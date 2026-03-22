// ════════════════════════════════════════════════════════════════════════════
// StartupService.cs  ·  Enumerates and toggles registry + Task Scheduler startup items
// ════════════════════════════════════════════════════════════════════════════
//
// Collects startup entries from HKCU/HKLM Run and RunOnce registry keys as
// well as Task Scheduler tasks marked to run at logon. Exposes Enable/Disable
// per entry. Used both by MemoryViewModel (display) and HealthScoreService
// (startup count sub-score).
//
// IMPACT CLASSIFICATION
//   Each item is classified Low / Medium / High based on substring patterns
//   in its Command string. Unknown means we don't recognise the executable.
//   This is a best-effort hint — not a precise measurement.
//
// RELATED FILES
//   Models/StartupItem.cs   — startup entry data shape
//   MemoryViewModel.cs      — displays startup list with enable/disable commands
//   HealthScoreService.cs   — counts enabled startup items for the Startup sub-score
// ════════════════════════════════════════════════════════════════════════════

using Microsoft.Win32;
using Systema.Core;
using Systema.Models;
using Microsoft.Win32.TaskScheduler;

namespace Systema.Services;

public class StartupService
{
    private static readonly string[] _registryPaths =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"
    };

    // ── Impact classification tables ──────────────────────────────────────────
    // Checked in order: first match wins.

    /// <summary>Executables / paths that cause a noticeable delay on boot.</summary>
    private static readonly string[] _highImpact =
    {
        "steam", "epicgameslauncher", "eadesktop", "eaapp", "origin.exe",
        "battlenet", "battle.net", "gog galaxy", "ubisoft connect", "riotclient",
        "discord", "spotify", "zoom", "msteams", "teams.exe",
        "googledrivefs", "googledrive.exe", "dropbox",
        "icloudservices", "icloud.exe",
        "adobeupdatedaemon", "creativecloud", "acrord32",
        "onedrive.exe",   // OneDrive can be heavy on older machines
    };

    /// <summary>Executables with a moderate boot cost.</summary>
    private static readonly string[] _mediumImpact =
    {
        "nvdisplay", "nvbackend", "shadowplay", "nvshare", "geforce",
        "amdnotification", "radeon", "ccc.exe",
        "realtekHD", "rtkhd", "rkastray",
        "lghub", "logitech",
        "razercentralservice", "razer synapse", "rzsd.exe",
        "virtualbox", "vmware",
        "megasync", "boxsync", "onedriveupdater",
        "1password", "bitwarden", "dashlane", "lastpass", "nordpass",
        "greenshot", "snagit",
        "malwarebytes", "mbam",
    };

    /// <summary>Executables with a small / negligible boot cost.</summary>
    private static readonly string[] _lowImpact =
    {
        "securityhealthsystray", "windowsdefender", "sgrmbroker",
        "inteldriverassist", "inteldcu",
        "igfxtray", "igfxsrvc",
        "nvtray", "amdtray",
        "mousocoreworker",
        "sniptool", "snippingtool",
        "dellsupportassistremediationservice",
        "hpsusd", "lenovo",
    };

    // ── Public API ────────────────────────────────────────────────────────────

    public List<StartupItem> GetStartupItems()
    {
        var items = new List<StartupItem>();
        items.AddRange(GetRegistryStartupItems(Registry.CurrentUser,  "HKCU"));
        items.AddRange(GetRegistryStartupItems(Registry.LocalMachine,  "HKLM"));
        items.AddRange(GetTaskSchedulerStartupItems());
        return items;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    // Paths that indicate a built-in Windows OS component rather than a user app.
    // Entries whose command expands to one of these directories are skipped when
    // reading the HKLM (machine-wide) Run key so the list stays user-relevant.
    private static readonly string[] _systemDirFragments =
    {
        @"\windows\system32\",
        @"\windows\syswow64\",
        @"\windows\systemapps\",
        @"\windowsapps\",
        @"\windows\immersivecontrolpanel\",
    };

    private IEnumerable<StartupItem> GetRegistryStartupItems(RegistryKey hive, string hiveName)
    {
        bool isMachine = hiveName.Equals("HKLM", StringComparison.OrdinalIgnoreCase);

        foreach (var path in _registryPaths)
        {
            using var key = hive.OpenSubKey(path, false);
            if (key == null) continue;
            foreach (var name in key.GetValueNames())
            {
                var cmd = key.GetValue(name)?.ToString() ?? "";

                // For machine-wide entries, skip anything that lives inside
                // core Windows directories — those are OS components, not user apps.
                if (isMachine)
                {
                    var lower = cmd.ToLowerInvariant();
                    bool isSystem = false;
                    foreach (var frag in _systemDirFragments)
                        if (lower.Contains(frag)) { isSystem = true; break; }
                    if (isSystem) continue;
                }

                yield return new StartupItem
                {
                    Name        = name,
                    Command     = cmd,
                    IsEnabled   = true,
                    Source      = StartupItemSource.Registry,
                    RegistryKey = $"{hiveName}\\{path}",
                    Impact      = ClassifyImpact(cmd),
                };
            }
        }
    }

    private IEnumerable<StartupItem> GetTaskSchedulerStartupItems()
    {
        var items = new List<StartupItem>();
        try
        {
            using var ts = new TaskService();
            foreach (var task in ts.RootFolder.AllTasks)
            {
                try
                {
                    // Skip all built-in Windows / Microsoft system tasks — they are not
                    // user-managed startup programs and would flood the list.
                    if (task.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool hasLogonTrigger = false;
                    foreach (var trigger in task.Definition.Triggers)
                    {
                        if (trigger is LogonTrigger) { hasLogonTrigger = true; break; }
                    }
                    if (!hasLogonTrigger) continue;

                    var cmd = task.Definition.Actions.Count > 0
                        ? task.Definition.Actions[0].ToString() ?? ""
                        : "";

                    items.Add(new StartupItem
                    {
                        Name        = task.Name,
                        Command     = cmd,
                        IsEnabled   = task.Enabled,
                        Source      = StartupItemSource.TaskScheduler,
                        RegistryKey = task.Path,
                        Impact      = ClassifyImpact(cmd),
                    });
                }
                catch { }
            }
        }
        catch { }
        return items;
    }

    /// <summary>Classifies a startup command's boot impact from known exe name patterns.</summary>
    private static StartupImpact ClassifyImpact(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return StartupImpact.Unknown;
        var lower = command.ToLowerInvariant();

        foreach (var h in _highImpact)
            if (lower.Contains(h)) return StartupImpact.High;

        foreach (var m in _mediumImpact)
            if (lower.Contains(m)) return StartupImpact.Medium;

        foreach (var l in _lowImpact)
            if (lower.Contains(l)) return StartupImpact.Low;

        return StartupImpact.Unknown;
    }

    // ── Enable / Disable ──────────────────────────────────────────────────────

    public TweakResult SetStartupItemEnabled(StartupItem item, bool enabled)
    {
        try
        {
            if (item.Source == StartupItemSource.Registry)
            {
                // Registry items: "disable" by moving the value to a sibling -Disabled key
                var hive     = item.RegistryKey.StartsWith("HKCU") ? Registry.CurrentUser : Registry.LocalMachine;
                var basePath = item.RegistryKey.Substring(item.RegistryKey.IndexOf('\\') + 1);
                var disabled = basePath.Replace("\\Run", "\\Run-Disabled");

                if (enabled)
                {
                    using var disabledKey = hive.OpenSubKey(disabled, true);
                    using var runKey      = hive.OpenSubKey(basePath, true);
                    if (disabledKey != null && runKey != null)
                    {
                        var value = disabledKey.GetValue(item.Name);
                        if (value != null)
                        {
                            runKey.SetValue(item.Name, value);
                            disabledKey.DeleteValue(item.Name);
                        }
                    }
                }
                else
                {
                    using var runKey      = hive.OpenSubKey(basePath, true);
                    using var disabledKeyObj = hive.CreateSubKey(disabled);
                    if (runKey != null && disabledKeyObj != null)
                    {
                        var value = runKey.GetValue(item.Name);
                        if (value != null)
                        {
                            disabledKeyObj.SetValue(item.Name, value);
                            runKey.DeleteValue(item.Name);
                        }
                    }
                }
            }
            else
            {
                using var ts   = new TaskService();
                var task       = ts.GetTask(item.RegistryKey);
                if (task != null) task.Enabled = enabled;
            }

            return TweakResult.Ok($"{item.Name} {(enabled ? "enabled" : "disabled")}.");
        }
        catch (Exception ex)
        {
            return TweakResult.FromException(ex);
        }
    }
}
