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
    /// Disables Fast Startup (sets HiberbootEnabled = 0). Windows will perform a full
    /// power-off on shutdown rather than saving a kernel hibernation snapshot.
    /// </summary>
    public Task<TweakResult> DisableFastStartupAsync() => Task.Run(() =>
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HiberbootKey, writable: true);
            if (key == null)
                return TweakResult.Fail(
                    "Could not open the Fast Startup registry key (access denied?).");

            key.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
            Log.Info("SystemStability", "Fast Startup disabled (HiberbootEnabled=0)");
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
    /// Re-enables Fast Startup (sets HiberbootEnabled = 1).
    /// </summary>
    public Task<TweakResult> EnableFastStartupAsync() => Task.Run(() =>
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HiberbootKey, writable: true);
            if (key == null)
                return TweakResult.Fail(
                    "Could not open the Fast Startup registry key (access denied?).");

            key.SetValue("HiberbootEnabled", 1, RegistryValueKind.DWord);
            Log.Info("SystemStability", "Fast Startup re-enabled (HiberbootEnabled=1)");
            return TweakResult.Ok("Fast Startup re-enabled.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "EnableFastStartup failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    // ── NTFS Last-Access Timestamps ───────────────────────────────────────────

    private const string NtfsKey =
        @"SYSTEM\CurrentControlSet\Control\FileSystem";

    /// <summary>
    /// Returns true when NTFS last-access timestamp updates are disabled.
    /// Values 1 (user-managed off) and 2 (system-managed off) both count as disabled.
    /// </summary>
    public bool IsNtfsLastAccessDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(NtfsKey);
            if (key?.GetValue("NtfsDisableLastAccessUpdate") is int v)
                return v == 1 || v == 2; // user- or system-managed disabled
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("SystemStability", $"IsNtfsLastAccessDisabled read failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Disables NTFS last-access timestamp updates (NtfsDisableLastAccessUpdate = 1).
    /// Windows will no longer update the "Date Accessed" field on every file read,
    /// reducing unnecessary disk writes and improving SSD longevity.
    /// </summary>
    public Task<TweakResult> DisableNtfsLastAccessAsync() => Task.Run(() =>
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(NtfsKey, writable: true);
            if (key == null)
                return TweakResult.Fail(
                    "Could not open the NTFS FileSystem registry key (access denied?).");

            key.SetValue("NtfsDisableLastAccessUpdate", 1, RegistryValueKind.DWord);
            Log.Info("SystemStability", "NTFS last-access timestamps disabled (NtfsDisableLastAccessUpdate=1)");
            return TweakResult.Ok(
                "NTFS last-access timestamps disabled. Windows will no longer write to every " +
                "file on read, reducing unnecessary disk activity and SSD wear.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "DisableNtfsLastAccess failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    /// <summary>
    /// Re-enables NTFS last-access timestamp updates (NtfsDisableLastAccessUpdate = 0).
    /// </summary>
    public Task<TweakResult> EnableNtfsLastAccessAsync() => Task.Run(() =>
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(NtfsKey, writable: true);
            if (key == null)
                return TweakResult.Fail(
                    "Could not open the NTFS FileSystem registry key (access denied?).");

            key.SetValue("NtfsDisableLastAccessUpdate", 0, RegistryValueKind.DWord);
            Log.Info("SystemStability", "NTFS last-access timestamps re-enabled (NtfsDisableLastAccessUpdate=0)");
            return TweakResult.Ok("NTFS last-access timestamps re-enabled.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "EnableNtfsLastAccess failed", ex);
            return TweakResult.FromException(ex);
        }
    });
}
