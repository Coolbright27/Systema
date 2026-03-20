// ════════════════════════════════════════════════════════════════════════════
// SettingsService.cs  ·  User preferences persisted to HKCU\Software\Systema
// ════════════════════════════════════════════════════════════════════════════
//
// Loads and saves all user-facing preferences (e.g. skip-restore-point flag,
// auto-boost enabled) via the registry. Exposes strongly typed properties;
// callers set a property and call SaveSettings to persist immediately.
//
// QUICK EDIT GUIDE
//   To add a new preference → add a property + read in LoadSettings + write in SaveSettings
//
// RELATED FILES
//   SettingsViewModel.cs    — binds preferences to the Settings tab UI
//   GameBoosterService.cs   — reads AutoBoostEnabled preference
//   GameBoosterViewModel.cs — reads/writes AutoBoostEnabled via this service
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

namespace Systema.Services;

/// <summary>
/// Persists user preferences to HKCU\Software\Systema.
/// All reads return safe defaults when the key doesn't exist yet.
/// </summary>
public class SettingsService
{
    private const string RegistryKey = @"Software\Systema";

    // ── Restore Point ─────────────────────────────────────────────────────────

    public bool SkipRestorePoint
    {
        get => ReadBool(nameof(SkipRestorePoint), defaultValue: false);
        set => WriteBool(nameof(SkipRestorePoint), value);
    }

    // ── Game Booster ──────────────────────────────────────────────────────────

    /// <summary>How often (in minutes) to poll for running game processes. Default: 2.</summary>
    public int GameCheckIntervalMinutes
    {
        get => ReadInt(nameof(GameCheckIntervalMinutes), defaultValue: 2);
        set => WriteInt(nameof(GameCheckIntervalMinutes), Math.Max(1, value));
    }

    /// <summary>
    /// JSON-serialised list of service names to kill during a game session.
    /// Null means "use the built-in default list".
    /// </summary>
    public List<string>? GameBoosterKillList
    {
        get
        {
            var json = ReadString(nameof(GameBoosterKillList), defaultValue: null);
            if (json == null) return null;
            try { return JsonSerializer.Deserialize<List<string>>(json); }
            catch { return null; }
        }
        set
        {
            if (value == null)
                DeleteValue(nameof(GameBoosterKillList));
            else
                WriteString(nameof(GameBoosterKillList), JsonSerializer.Serialize(value));
        }
    }

    /// <summary>When true the user has manually chosen Xbox service state — auto-logic won't override.</summary>
    public bool XboxServicesUserOverride
    {
        get => ReadBool(nameof(XboxServicesUserOverride), defaultValue: false);
        set => WriteBool(nameof(XboxServicesUserOverride), value);
    }

    // ── Core Parking ────────────────────────────────────────────────────────

    /// <summary>Persists whether the user has enabled forced core parking enforcement.</summary>
    public bool CoreParkingEnabled
    {
        get => ReadBool(nameof(CoreParkingEnabled), defaultValue: false);
        set => WriteBool(nameof(CoreParkingEnabled), value);
    }

    // ── Windows Update tweaks ─────────────────────────────────────────────────

    /// <summary>Persists whether the user has enabled blocking of Windows preview updates.</summary>
    public bool BlockPreviewUpdatesEnabled
    {
        get => ReadBool(nameof(BlockPreviewUpdatesEnabled), defaultValue: false);
        set => WriteBool(nameof(BlockPreviewUpdatesEnabled), value);
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private const string TaskName    = "Systema";
    private const string RunKey       = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Systema";

    /// <summary>
    /// Gets or sets whether Systema launches automatically at Windows startup.
    /// Uses a Task Scheduler logon task with "Run with highest privileges" so
    /// the admin app starts silently without a UAC prompt. Falls back to the
    /// HKCU Run key if Task Scheduler is unavailable.
    /// </summary>
    public bool StartWithWindows
    {
        get
        {
            // Primary: Task Scheduler task
            try
            {
                using var ts   = new TaskService();
                var task = ts.GetTask(TaskName);
                if (task != null) return task.Enabled;
            }
            catch { }

            // Fallback: HKCU Run key
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }
        set
        {
            // Try Task Scheduler first (required for admin apps to start without UAC)
            try
            {
                using var ts = new TaskService();

                if (!value)
                {
                    ts.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
                    // Also remove any legacy Run key entry
                    try
                    {
                        using var runKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                        runKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
                    }
                    catch { }
                    return;
                }

                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;

                var td = ts.NewTask();
                td.RegistrationInfo.Description = "Starts Systema optimization suite at logon";
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries     = false;
                td.Settings.RunOnlyIfNetworkAvailable  = false;
                td.Settings.ExecutionTimeLimit         = TimeSpan.Zero; // never time out
                td.Settings.MultipleInstances          = TaskInstancesPolicy.IgnoreNew;

                // Run with highest privileges — this is what lets an admin app start
                // at logon without triggering a UAC elevation prompt.
                td.Principal.RunLevel  = TaskRunLevel.Highest;
                td.Principal.LogonType = TaskLogonType.InteractiveToken;

                // Trigger: when the current user logs on
                string currentUser = WindowsIdentity.GetCurrent().Name;
                td.Triggers.Add(new LogonTrigger { UserId = currentUser });

                // Action: launch the installed EXE in silent/tray mode
                string workDir = Path.GetDirectoryName(exePath) ?? "";
                td.Actions.Add(new ExecAction(exePath, "--autostart", workDir));

                ts.RootFolder.RegisterTaskDefinition(
                    TaskName, td,
                    TaskCreation.CreateOrUpdate,
                    null, null,
                    TaskLogonType.InteractiveToken);

                // Remove any legacy Run key entry to avoid double-launch
                try
                {
                    using var runKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                    runKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
                catch { }
                return;
            }
            catch { /* fall through to Run key */ }

            // Fallback: HKCU Run key (non-admin builds / Task Scheduler unavailable)
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key == null) return;
                if (value)
                {
                    string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                        key.SetValue(RunValueName, $"\"{exePath}\" --autostart", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
            }
            catch { }
        }
    }

    // ── Battery Optimization ──────────────────────────────────────────────────

    /// <summary>
    /// Persists the active battery optimization mode across sessions.
    /// "" = none, "balanced" = 99% DC cap, "max" = 80% DC cap.
    /// </summary>
    public string BatteryOptimizationMode
    {
        get => ReadString(nameof(BatteryOptimizationMode), defaultValue: "") ?? "";
        set => WriteString(nameof(BatteryOptimizationMode), value);
    }

    // ── Game Booster — per-session actions ────────────────────────────────────

    /// <summary>Free RAM from background processes when a game starts.</summary>
    public bool GameBoosterFreeMemory
    {
        get => ReadBool(nameof(GameBoosterFreeMemory), defaultValue: true);
        set => WriteBool(nameof(GameBoosterFreeMemory), value);
    }

    /// <summary>Enable Focus Assist (suppress notifications) during gaming.</summary>
    public bool GameBoosterSuppressNotifications
    {
        get => ReadBool(nameof(GameBoosterSuppressNotifications), defaultValue: true);
        set => WriteBool(nameof(GameBoosterSuppressNotifications), value);
    }

    /// <summary>Master switch — when false the game booster never activates.</summary>
    public bool GameBoosterEnabled
    {
        get => ReadBool(nameof(GameBoosterEnabled), defaultValue: true);
        set => WriteBool(nameof(GameBoosterEnabled), value);
    }

    /// <summary>Disable Game Bar while a game is active (re-enables on exit).</summary>
    public bool GameBoosterDisableGameBar
    {
        get => ReadBool(nameof(GameBoosterDisableGameBar), defaultValue: false);
        set => WriteBool(nameof(GameBoosterDisableGameBar), value);
    }

    /// <summary>Switch to the High Performance power plan for the duration of the game session.</summary>
    public bool GameBoosterHighPerfPowerPlan
    {
        get => ReadBool(nameof(GameBoosterHighPerfPowerPlan), defaultValue: false);
        set => WriteBool(nameof(GameBoosterHighPerfPowerPlan), value);
    }

    /// <summary>Set Windows timer resolution to 1 ms while a game is running (reduces frame jitter).</summary>
    public bool GameBoosterTimerResolution
    {
        get => ReadBool(nameof(GameBoosterTimerResolution), defaultValue: true);
        set => WriteBool(nameof(GameBoosterTimerResolution), value);
    }

    /// <summary>Tune the Windows Multimedia System Profile (GPU Priority, Scheduling Category) for gaming.</summary>
    public bool GameBoosterGpuProfile
    {
        get => ReadBool(nameof(GameBoosterGpuProfile), defaultValue: true);
        set => WriteBool(nameof(GameBoosterGpuProfile), value);
    }

    /// <summary>Disable Nagle's algorithm (TcpAckFrequency + TCPNoDelay) for lower online-game latency.</summary>
    public bool GameBoosterDisableNagle
    {
        get => ReadBool(nameof(GameBoosterDisableNagle), defaultValue: true);
        set => WriteBool(nameof(GameBoosterDisableNagle), value);
    }

    /// <summary>Flush the DNS resolver cache when a game session starts.</summary>
    public bool GameBoosterFlushDns
    {
        get => ReadBool(nameof(GameBoosterFlushDns), defaultValue: false);
        set => WriteBool(nameof(GameBoosterFlushDns), value);
    }

    /// <summary>Disable network adapter power management while gaming (restored on exit).</summary>
    public bool GameBoosterNicPowerSaving
    {
        get => ReadBool(nameof(GameBoosterNicPowerSaving), defaultValue: true);
        set => WriteBool(nameof(GameBoosterNicPowerSaving), value);
    }

    // ── Updates ───────────────────────────────────────────────────────────────

    /// <summary>
    /// When true (default), Systema checks for updates every 2 hours and installs
    /// silently when the CPU is idle and not in game mode.
    /// When false, all automatic update activity is suppressed (manual check still works).
    /// </summary>
    public bool AutoUpdateEnabled
    {
        get => ReadBool(nameof(AutoUpdateEnabled), defaultValue: true);
        set => WriteBool(nameof(AutoUpdateEnabled), value);
    }

    public bool KeepSystemaRunning
    {
        get => ReadBool(nameof(KeepSystemaRunning), defaultValue: false);
        set => WriteBool(nameof(KeepSystemaRunning), value);
    }

    // ── Generic helpers ───────────────────────────────────────────────────────

    // Single lock serializes all concurrent registry reads and writes to prevent
    // torn state when multiple threads (game check timer, UI thread) access simultaneously.
    private static readonly object _registryLock = new();

    private static bool ReadBool(string name, bool defaultValue)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
                if (key?.GetValue(name) is int v) return v != 0;
            }
            catch { }
            return defaultValue;
        }
    }

    private static void WriteBool(string name, bool value)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
                key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }
    }

    private static int ReadInt(string name, int defaultValue)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
                if (key?.GetValue(name) is int v) return v;
            }
            catch { }
            return defaultValue;
        }
    }

    private static void WriteInt(string name, int value)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
                key?.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch { }
        }
    }

    private static string? ReadString(string name, string? defaultValue)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
                return key?.GetValue(name) as string ?? defaultValue;
            }
            catch { return defaultValue; }
        }
    }

    private static void WriteString(string name, string value)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
                key?.SetValue(name, value, RegistryValueKind.String);
            }
            catch { }
        }
    }

    private static void DeleteValue(string name)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
                key?.DeleteValue(name, throwOnMissingValue: false);
            }
            catch { }
        }
    }

    // ── Export / Import ──────────────────────────────────────────────────────

    /// <summary>
    /// Serialises all values under HKCU\Software\Systema to an indented JSON string.
    /// DWORD values are exported as numbers; strings as strings.
    /// </summary>
    public string ExportToJson()
    {
        lock (_registryLock)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
                if (key != null)
                {
                    foreach (var name in key.GetValueNames())
                    {
                        var val = key.GetValue(name);
                        dict[name] = val;
                    }
                }
            }
            catch { }
            return JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>
    /// Imports settings from a JSON string produced by <see cref="ExportToJson"/>.
    /// Unknown keys are silently ignored. Returns true on success.
    /// </summary>
    public bool ImportFromJson(string json)
    {
        lock (_registryLock)
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (dict == null) return false;

                using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
                if (key == null) return false;

                foreach (var (name, element) in dict)
                {
                    switch (element.ValueKind)
                    {
                        case JsonValueKind.Number when element.TryGetInt32(out int iv):
                            key.SetValue(name, iv, RegistryValueKind.DWord);
                            break;
                        case JsonValueKind.String:
                            key.SetValue(name, element.GetString() ?? string.Empty, RegistryValueKind.String);
                            break;
                        case JsonValueKind.True:
                            key.SetValue(name, 1, RegistryValueKind.DWord);
                            break;
                        case JsonValueKind.False:
                            key.SetValue(name, 0, RegistryValueKind.DWord);
                            break;
                    }
                }
                return true;
            }
            catch { return false; }
        }
    }
}
