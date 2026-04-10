// ════════════════════════════════════════════════════════════════════════════
// SystemStabilityService.cs  ·  Fast Startup and NTFS last-access tweaks
// ════════════════════════════════════════════════════════════════════════════
//
// Provides two stability-oriented system tweaks:
//
//   Fast Startup (HiberbootEnabled)
//     Windows Fast Startup is a hybrid shutdown that saves a kernel hibernation
//     snapshot on shutdown so the next boot is faster. The downside: it can
//     cause driver state corruption, prevent BIOS/UEFI updates from applying,
//     and interfere with dual-boot setups.
//     HiberbootEnabled = 0 → full shutdown (safer, more stable)
//     HiberbootEnabled = 1 → fast startup (default on most OEM systems)
//
//   NTFS Last-Access Timestamps (NtfsDisableLastAccessUpdate)
//     By default, NTFS updates the "last accessed" timestamp on every file
//     read. On HDDs and SSDs this creates unnecessary write amplification.
//     NtfsDisableLastAccessUpdate = 1 → user-managed disabled (timestamps off)
//     NtfsDisableLastAccessUpdate = 0 → user-managed enabled  (timestamps on)
//
// RELATED FILES
//   ToolsViewModel.cs  — Fast Startup and NTFS toggle on the Tools tab
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

/// <summary>
/// Controls system stability tweaks: Fast Startup and NTFS last-access timestamp writes.
/// All registry writes target HKLM (requires admin — Systema always runs elevated).
/// </summary>
public class SystemStabilityService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    // ── Fast Startup ──────────────────────────────────────────────────────────

    private const string HiberbootKey =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    /// <summary>
    /// Returns true when Fast Startup is disabled (HiberbootEnabled = 0).
    /// Returns false when it is enabled or the value is absent (default = enabled).
    /// </summary>
    public bool IsFastStartupDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HiberbootKey);
            if (key?.GetValue("HiberbootEnabled") is int v)
                return v == 0;
            return false; // missing = default = fast startup on
        }
        catch (Exception ex)
        {
            Log.Warn("SystemStability", $"IsFastStartupDisabled read failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Disables Fast Startup by both setting the registry value AND running
    /// <c>powercfg /hibernate off</c>. Fast Startup requires hibernation — disabling
    /// hibernation is the most reliable way to ensure Fast Startup stays off.
    /// The registry write alone is not enough on many OEM systems.
    /// </summary>
    public Task<TweakResult> DisableFastStartupAsync() => Task.Run(() =>
    {
        try
        {
            // 1. Registry write (belt)
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(HiberbootKey, writable: true);
                key?.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Log.Warn("SystemStability", $"HiberbootEnabled registry write failed: {ex.Message}");
            }

            // 2. powercfg /hibernate off (suspenders) — this is the authoritative command
            var psi = new ProcessStartInfo
            {
                FileName        = "powercfg.exe",
                Arguments       = "/hibernate off",
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);

            Log.Info("SystemStability", "Fast Startup disabled (HiberbootEnabled=0 + powercfg /hibernate off)");
            return TweakResult.Ok(
                "Fast Startup disabled. Windows will perform a full shutdown each time, " +
                "improving driver stability and ensuring firmware updates apply correctly.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "DisableFastStartup failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    /// <summary>
    /// Re-enables Fast Startup by setting the registry value and re-enabling hibernation.
    /// </summary>
    public Task<TweakResult> EnableFastStartupAsync() => Task.Run(() =>
    {
        try
        {
            // 1. Re-enable hibernation first (required for fast startup)
            var psi = new ProcessStartInfo
            {
                FileName        = "powercfg.exe",
                Arguments       = "/hibernate on",
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);

            // 2. Set the registry value
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(HiberbootKey, writable: true);
                key?.SetValue("HiberbootEnabled", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Log.Warn("SystemStability", $"HiberbootEnabled registry write failed: {ex.Message}");
            }

            Log.Info("SystemStability", "Fast Startup re-enabled (HiberbootEnabled=1 + powercfg /hibernate on)");
            return TweakResult.Ok("Fast Startup re-enabled.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "EnableFastStartup failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    // ── NTFS Last-Access Timestamps ───────────────────────────────────────────
    //
    // Modern Windows manages NtfsDisableLastAccessUpdate via fsutil, not the
    // registry directly. Writing the registry value alone gets overridden at
    // boot. We use `fsutil behavior set disablelastaccess 1` which persists
    // correctly across reboots.
    //
    // fsutil values: 0 = enabled, 1 = user-disabled, 2 = system-disabled, 3 = system-enabled
    // Values 1 and 2 both mean disabled.

    /// <summary>
    /// Returns true when NTFS last-access timestamp updates are disabled.
    /// Queries fsutil which is the authoritative source on modern Windows.
    /// </summary>
    public bool IsNtfsLastAccessDisabled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "fsutil.exe",
                Arguments              = "behavior query disablelastaccess",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);
            // Output: "DisableLastAccess = 1 (User Managed, Disabled)"
            // Extract the digit after "= "
            int idx = output.IndexOf('=');
            if (idx >= 0 && idx + 2 < output.Length && int.TryParse(output.AsSpan(idx + 2, 1), out int v))
                return v == 1 || v == 2;
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("SystemStability", $"IsNtfsLastAccessDisabled read failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Disables NTFS last-access timestamp updates via fsutil.
    /// This is the correct approach on modern Windows — the registry value alone
    /// gets overridden at boot by the volume manager.
    /// </summary>
    public Task<TweakResult> DisableNtfsLastAccessAsync() => Task.Run(() =>
    {
        try
        {
            var (exitCode, output) = RunFsutil("behavior set disablelastaccess 1");
            if (exitCode == 0)
            {
                Log.Info("SystemStability", "NTFS last-access timestamps disabled via fsutil");
                return TweakResult.Ok(
                    "NTFS last-access timestamps disabled. Takes effect immediately for new file access.");
            }

            Log.Warn("SystemStability", $"fsutil disablelastaccess=1 exited {exitCode}: {output}");
            return TweakResult.Fail($"fsutil failed (exit code {exitCode}). Make sure Systema is running as Administrator.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "DisableNtfsLastAccess failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    /// <summary>
    /// Re-enables NTFS last-access timestamp updates via fsutil.
    /// </summary>
    public Task<TweakResult> EnableNtfsLastAccessAsync() => Task.Run(() =>
    {
        try
        {
            var (exitCode, output) = RunFsutil("behavior set disablelastaccess 0");
            if (exitCode == 0)
            {
                Log.Info("SystemStability", "NTFS last-access timestamps re-enabled via fsutil");
                return TweakResult.Ok("NTFS last-access timestamps re-enabled.");
            }

            Log.Warn("SystemStability", $"fsutil disablelastaccess=0 exited {exitCode}: {output}");
            return TweakResult.Fail($"fsutil failed (exit code {exitCode}). Make sure Systema is running as Administrator.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "EnableNtfsLastAccess failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    private static (int ExitCode, string Output) RunFsutil(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "fsutil.exe",
            Arguments              = args,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
        };
        using var proc = Process.Start(psi)!;
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10_000);
        return (proc.HasExited ? proc.ExitCode : -1, output.Trim());
    }
}
