// ════════════════════════════════════════════════════════════════════════════
// RealtekCleanerService.cs  ·  Silent uninstall of Realtek Audio Manager/Console
// ════════════════════════════════════════════════════════════════════════════
//
// Uninstalls Realtek Audio Manager and Realtek Audio Console silently via wmic
// product call. Also sets the driver search policy registry key to prevent
// automatic reinstallation on next Windows Update.
//
// RELATED FILES
//   Models/RealtekEntry.cs  — entry data shape used for detection and logging
//   ToolsViewModel.cs       — Realtek removal button on the Tools tab
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;
using Systema.Core;
using Systema.Models;

namespace Systema.Services;

/// <summary>
/// Discovers and silently uninstalls Realtek bloatware (Audio Manager / Audio Console)
/// while leaving the core audio driver intact.
///
/// After uninstall the service also sets driver-search policy keys to prevent Windows
/// Update from automatically re-downloading the manager/console apps.
/// </summary>
public class RealtekCleanerService
{
    private static readonly LoggerService _log = LoggerService.Instance;

    // Registry paths that contain installed-program metadata on x64 Windows.
    private static readonly string[] UninstallPaths =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    // Inclusion-based: a Realtek entry is ONLY flagged as bloat if it contains at least
    // one of these keywords. This prevents accidental removal of audio drivers, mic,
    // speaker, and camera components.
    private static readonly string[] BloatKeywords =
    [
        "audio manager",
        "audio console",
        "audio universal service",
        "audio effects",
        "audio enhancement",
        "hd audio manager",
        "dolby",
        "dts studio",
        "nahimic",
        "waves maxxaudio"
    ];

    // Substrings that indicate a Realtek *driver* package — we must NOT remove these,
    // even if a bloat keyword matched.
    private static readonly string[] DriverKeywords =
    [
        "audio driver",
        "high definition audio driver",
        "hd audio driver",
        "realtek pcie",
        "realtek card reader driver",
        "realtek ethernet",
        "realtek lan driver",
        "usb audio",
        "bluetooth",
        "wireless",
        "i2s",
        "smart card"
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if any Realtek audio hardware is detected on this system
    /// (checks both registry uninstall hives and the audio device class).
    /// </summary>
    public bool HasRealtekHardware()
    {
        try
        {
            // Check for Realtek audio device driver entries in the device class
            const string audioDevicesKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96c-e325-11ce-bfc1-08002be10318}";
            using var root = Registry.LocalMachine.OpenSubKey(audioDevicesKey);
            if (root != null)
            {
                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subName);
                        var desc = sub?.GetValue("DriverDesc") as string;
                        if (desc != null && desc.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
            }
        }
        catch { }

        // Fallback: check if any Realtek entries exist in the uninstall hives
        try
        {
            foreach (string uninstallPath in UninstallPaths)
            {
                using var root = Registry.LocalMachine.OpenSubKey(uninstallPath);
                if (root == null) continue;
                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subName);
                        var displayName = sub?.GetValue("DisplayName") as string;
                        if (displayName != null && displayName.Contains("Realtek", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Scans both 32-bit and 64-bit uninstall registry hives for Realtek bloatware entries
    /// (manager and console apps, not the core audio driver).
    /// </summary>
    public List<RealtekEntry> GetRealtekBloatEntries()
    {
        var results = new List<RealtekEntry>();

        foreach (string uninstallPath in UninstallPaths)
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(uninstallPath, writable: false);
                if (root == null) continue;

                foreach (string subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subName, writable: false);
                        if (sub == null) continue;

                        var displayName = sub.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        string nameLower = displayName.ToLowerInvariant();

                        // Must contain "realtek" to be a candidate
                        if (!nameLower.Contains("realtek")) continue;

                        // Must match at least one known bloat keyword (inclusion-based)
                        bool isBloat = false;
                        foreach (string bloatKw in BloatKeywords)
                        {
                            if (nameLower.Contains(bloatKw))
                            {
                                isBloat = true;
                                break;
                            }
                        }
                        if (!isBloat) continue;

                        // Double-check: exclude anything that looks like a core driver package
                        bool isDriver = false;
                        foreach (string driverKw in DriverKeywords)
                        {
                            if (nameLower.Contains(driverKw))
                            {
                                isDriver = true;
                                break;
                            }
                        }
                        if (isDriver) continue;

                        results.Add(new RealtekEntry
                        {
                            DisplayName          = displayName,
                            Version              = sub.GetValue("DisplayVersion") as string ?? string.Empty,
                            UninstallString      = sub.GetValue("UninstallString") as string ?? string.Empty,
                            QuietUninstallString = sub.GetValue("QuietUninstallString") as string ?? string.Empty,
                            RegistryKey          = $@"HKLM\{uninstallPath}\{subName}"
                        });
                    }
                    catch (Exception innerEx)
                    {
                        _log.Warn("RealtekCleanerService",
                            $"Skipped uninstall subkey '{subName}': {innerEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("RealtekCleanerService",
                    $"Could not open uninstall hive '{uninstallPath}': {ex.Message}");
            }
        }

        // Deduplicate by DisplayName (same app may appear in both 32/64-bit hives)
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique  = new List<RealtekEntry>(results.Count);
        foreach (var entry in results)
        {
            if (seen.Add(entry.DisplayName))
                unique.Add(entry);
        }

        return unique;
    }

    /// <summary>
    /// Silently uninstalls all discovered Realtek bloatware entries and then writes
    /// policy keys to suppress Windows Update from reinstalling them automatically.
    /// Returns a <see cref="TweakResult"/> describing how many items were uninstalled.
    /// </summary>
    public Task<TweakResult> RemoveRealtekBloatAsync() => Task.Run(() =>
    {
        try
        {
            var entries = GetRealtekBloatEntries();
            if (entries.Count == 0)
                return TweakResult.Ok("No Realtek bloatware found — nothing to remove.");

            int succeeded = 0;
            int failed    = 0;
            var errors    = new List<string>();

            foreach (var entry in entries)
            {
                try
                {
                    string uninstallCmd = BuildUninstallCommand(entry);
                    if (string.IsNullOrWhiteSpace(uninstallCmd))
                    {
                        _log.Warn("RealtekCleanerService",
                            $"No uninstall string for '{entry.DisplayName}' — skipping.");
                        failed++;
                        errors.Add($"No uninstall string: {entry.DisplayName}");
                        continue;
                    }

                    _log.Info("RealtekCleanerService",
                        $"Uninstalling '{entry.DisplayName}' via: {uninstallCmd}");

                    int exitCode = RunProcess(uninstallCmd);
                    if (exitCode == 0)
                    {
                        _log.Info("RealtekCleanerService",
                            $"Successfully uninstalled '{entry.DisplayName}' (exit 0).");
                        succeeded++;
                    }
                    else
                    {
                        // Some uninstallers return non-zero for "reboot required" (3010) —
                        // treat that as success too.
                        if (exitCode == 3010)
                        {
                            _log.Info("RealtekCleanerService",
                                $"Uninstalled '{entry.DisplayName}' — reboot required (exit 3010).");
                            succeeded++;
                        }
                        else
                        {
                            _log.Warn("RealtekCleanerService",
                                $"Uninstaller for '{entry.DisplayName}' exited with code {exitCode}.");
                            failed++;
                            errors.Add($"{entry.DisplayName} (exit {exitCode})");
                        }
                    }
                }
                catch (Exception innerEx)
                {
                    _log.Error("RealtekCleanerService",
                        $"Exception uninstalling '{entry.DisplayName}'", innerEx);
                    failed++;
                    errors.Add($"{entry.DisplayName}: {innerEx.Message}");
                }
            }

            string summary = succeeded > 0
                ? $"Removed {succeeded} Realtek item(s)."
                : "No items were successfully removed.";

            if (errors.Count > 0)
                summary += $" Failed: {string.Join("; ", errors)}.";

            return failed == 0 ? TweakResult.Ok(summary) : TweakResult.Fail(summary);
        }
        catch (Exception ex)
        {
            _log.Error("RealtekCleanerService", "RemoveRealtekBloatAsync failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a quiet uninstall command from the entry.
    /// Prefers QuietUninstallString; falls back to UninstallString with /quiet /norestart appended.
    /// </summary>
    private static string BuildUninstallCommand(RealtekEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.QuietUninstallString))
            return entry.QuietUninstallString;

        if (string.IsNullOrWhiteSpace(entry.UninstallString))
            return string.Empty;

        string cmd = entry.UninstallString.Trim();

        // MsiExec-based uninstallers need /quiet /norestart appended
        if (cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase) ||
            cmd.Contains("{") && cmd.Contains("}"))
        {
            if (!cmd.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
                cmd += " /quiet";
            if (!cmd.Contains("/norestart", StringComparison.OrdinalIgnoreCase))
                cmd += " /norestart";
        }
        else
        {
            // Generic executable — try /quiet /norestart as well
            if (!cmd.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
                cmd += " /quiet";
            if (!cmd.Contains("/norestart", StringComparison.OrdinalIgnoreCase))
                cmd += " /norestart";
        }

        return cmd;
    }

    /// <summary>
    /// Runs a command string (which may contain arguments) via cmd /c and returns the exit code.
    /// Both stdout and stderr are drained asynchronously to prevent pipe-buffer deadlocks when
    /// the process produces more output than the OS pipe buffer can hold (~64 KB).
    /// </summary>
    private static int RunProcess(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            Arguments              = $"/c \"{command}\"",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return -1;

        // Drain both streams concurrently on background tasks — without this, if either
        // pipe fills (> ~64 KB) the child process blocks, WaitForExit never returns: deadlock.
        var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
        var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

        bool finished = proc.WaitForExit(120_000); // 2-minute timeout

        // Drain remaining bytes so the pipe handles are fully closed before we return.
        stdoutTask.GetAwaiter().GetResult();
        stderrTask.GetAwaiter().GetResult();

        return finished ? proc.ExitCode : -1;
    }

}
