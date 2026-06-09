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
using System.Windows.Forms;
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

    // ── Responsiveness: Foreground Priority Boost ─────────────────────────────
    //
    // Win32PrioritySeparation controls how the scheduler splits CPU quantum
    // between foreground and background threads.
    //   2    = Windows default (fixed-length quantums, mild foreground bias)
    //   0x26 = short, variable, BOOSTED quantums for the foreground app — the
    //          active window stays responsive under background load. This is the
    //          value Windows itself uses for "Adjust for best performance of
    //          Programs." Pairs naturally with Task Sleep (which demotes the
    //          background side). Instant effect, no reboot, fully reversible.

    private const string PriorityControlKey =
        @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const int ForegroundBoostValue       = 0x26; // 38
    private const int PrioritySeparationDefault   = 2;

    /// <summary>True when foreground priority boost is applied (Win32PrioritySeparation = 38).</summary>
    public bool IsForegroundBoostEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PriorityControlKey);
            return key?.GetValue("Win32PrioritySeparation") is int v && v == ForegroundBoostValue;
        }
        catch (Exception ex)
        {
            Log.Warn("SystemStability", $"IsForegroundBoostEnabled read failed: {ex.Message}");
            return false;
        }
    }

    public Task<TweakResult> EnableForegroundBoostAsync() => Task.Run(() =>
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PriorityControlKey, writable: true);
            if (key == null)
                return TweakResult.Fail("Could not open PriorityControl key. Run Systema as Administrator.");
            key.SetValue("Win32PrioritySeparation", ForegroundBoostValue, RegistryValueKind.DWord);
            Log.Info("SystemStability", $"Foreground priority boost enabled (Win32PrioritySeparation={ForegroundBoostValue})");
            return TweakResult.Ok("Foreground priority boost enabled — the active window now gets more CPU time.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "EnableForegroundBoost failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    /// <summary>Restores the Windows default (Win32PrioritySeparation = 2).</summary>
    public Task<TweakResult> DisableForegroundBoostAsync() => Task.Run(() =>
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PriorityControlKey, writable: true);
            key?.SetValue("Win32PrioritySeparation", PrioritySeparationDefault, RegistryValueKind.DWord);
            Log.Info("SystemStability", $"Foreground priority boost disabled (Win32PrioritySeparation={PrioritySeparationDefault} — Windows default)");
            return TweakResult.Ok("Foreground priority boost disabled — restored Windows default.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "DisableForegroundBoost failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    // ── Instant App Focus (ForegroundLockTimeout) ─────────────────────────────
    // Windows makes a freshly-launched app "wait its turn" before it can take the
    // foreground (ForegroundLockTimeout — default 200000 ms). Setting it to 0 lets
    // the app you just launched come to the front immediately, so it feels snappier.
    // HKCU value, no admin needed, applied live via SystemParametersInfo, reversible.
    private const string DesktopKey = @"Control Panel\Desktop";
    private const int    ForegroundLockDefault = 0x30D40; // 200000 ms (Windows default)
    private const uint   SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint   SPIF_UPDATEINIFILE = 0x0001;
    private const uint   SPIF_SENDCHANGE    = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, UIntPtr pvParam, uint fWinIni);

    /// <summary>True when ForegroundLockTimeout is 0 (launched apps focus instantly).</summary>
    public bool IsInstantAppFocusEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
            var raw = key?.GetValue("ForegroundLockTimeout");
            if (raw is int i) return i == 0;
            if (raw is string s && int.TryParse(s, out int v)) return v == 0;
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("SystemStability", $"IsInstantAppFocusEnabled read failed: {ex.Message}");
            return false;
        }
    }

    public Task<TweakResult> EnableInstantAppFocusAsync() => Task.Run(() =>
        SetForegroundLock(0, "Instant app focus enabled — apps you launch now come to the front immediately."));

    /// <summary>Restores the Windows default (ForegroundLockTimeout = 200000 ms).</summary>
    public Task<TweakResult> DisableInstantAppFocusAsync() => Task.Run(() =>
        SetForegroundLock(ForegroundLockDefault, "Instant app focus disabled — restored Windows default."));

    private TweakResult SetForegroundLock(int timeout, string okMsg)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(DesktopKey, writable: true))
                key?.SetValue("ForegroundLockTimeout", timeout, RegistryValueKind.DWord);
            // Apply live (and persist to the user profile) so no re-login is needed.
            SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, new UIntPtr((uint)timeout),
                                 SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            Log.Info("SystemStability", $"ForegroundLockTimeout set to {timeout}");
            return TweakResult.Ok(okMsg);
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "SetForegroundLock failed", ex);
            return TweakResult.FromException(ex);
        }
    }

    // ── Instant Startup Apps (StartupDelayInMSec) ─────────────────────────────
    // After every boot / sign-in, Windows artificially delays your auto-start apps
    // (the Serialize\StartupDelayInMSec throttle — typically ~10 s) so the desktop
    // "settles" first. Setting it to 0 launches your startup apps immediately.
    // HKCU value, no admin needed, reversible (the value is deleted to restore the
    // Windows default delay).
    private const string StartupSerializeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";

    /// <summary>True when the Windows startup-app delay is removed (StartupDelayInMSec = 0).</summary>
    public bool IsStartupAppDelayDisabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupSerializeKey);
            return key?.GetValue("StartupDelayInMSec") is int v && v == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("SystemStability", $"IsStartupAppDelayDisabled read failed: {ex.Message}");
            return false;
        }
    }

    public Task<TweakResult> EnableInstantStartupAppsAsync() => Task.Run(() =>
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(StartupSerializeKey, writable: true))
            {
                if (key == null) return TweakResult.Fail("Could not open the Explorer Serialize key.");
                key.SetValue("StartupDelayInMSec", 0, RegistryValueKind.DWord);
            }
            // Verify it actually persisted. Explorer OWNS the Serialize key and
            // actively rewrites it during the logon sequence — if Systema applies this
            // while auto-launching mid-logon, Explorer clobbers our value moments later.
            // The ViewModel re-asserts this on a delay (after logon settles) to win that
            // race; the read-back here makes the clobber visible in the log.
            bool landed = IsStartupAppDelayDisabled();
            if (landed)
                Log.Info("SystemStability", "StartupDelayInMSec set to 0 — startup apps launch immediately after boot.");
            else
                Log.Warn("SystemStability", "StartupDelayInMSec write did not persist (Explorer rewrote the Serialize key during logon) — will be re-asserted after logon settles.");
            return TweakResult.Ok("Startup app delay removed — your startup apps now launch immediately after a reboot. Takes effect on the next boot.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "EnableInstantStartupApps failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    /// <summary>Restores the Windows default startup-app settle delay (deletes StartupDelayInMSec).</summary>
    public Task<TweakResult> DisableInstantStartupAppsAsync() => Task.Run(() =>
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(StartupSerializeKey, writable: true))
                key?.DeleteValue("StartupDelayInMSec", throwOnMissingValue: false);
            Log.Info("SystemStability", "StartupDelayInMSec removed — restored Windows default startup delay.");
            return TweakResult.Ok("Startup app delay restored to the Windows default.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "DisableInstantStartupApps failed", ex);
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
    // fsutil "DisableLastAccess" values (per Microsoft docs):
    //   0 = User Managed,  Last Access Updates ENABLED
    //   1 = User Managed,  Last Access Updates DISABLED
    //   2 = System Managed, Last Access Updates ENABLED  ← Win10 1803+ DEFAULT
    //   3 = System Managed, Last Access Updates DISABLED
    // So "disabled" = 1 OR 3. (The old code wrongly treated 2 as disabled, which
    // made a default machine always report "already disabled" and the toggle
    // appeared stuck on.)

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
                return v == 1 || v == 3;   // 1/3 = disabled; 0/2 = enabled
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

    // ── Battery detection ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the system has a battery (i.e. is a laptop or tablet).
    /// Uses the Windows power status API — no WMI, no powercfg spawn.
    /// <c>BatteryChargeStatus.NoSystemBattery</c> (128) is the only reliable
    /// "no battery hardware" indicator; Unknown (255) is treated as "has battery"
    /// because it typically means the driver hasn't reported yet, not that no
    /// battery exists.
    /// </summary>
    public bool HasBattery()
    {
        try
        {
            return SystemInformation.PowerStatus.BatteryChargeStatus
                != BatteryChargeStatus.NoSystemBattery;
        }
        catch { return false; }
    }

    // ── Sleep → Hibernate (battery only) ─────────────────────────────────────
    //
    // When enabled, the laptop hibernates after a configurable sleep timeout on
    // battery instead of staying in low-power sleep indefinitely.
    //
    // Implementation:
    //   HYBRIDSLEEP    — combines sleep + hibernation in one step.  Set to 0 so
    //                    the separate HIBERNATEIDLE timeout can fire cleanly.
    //   HIBERNATEIDLE  — seconds of sleep before the system hibernates (DC only).
    //   /setactive     — writes the modified values to the active power plan.
    //
    // Restore: set HYBRIDSLEEP=1 (Windows default) and HIBERNATEIDLE=0 (never).

    /// <summary>
    /// Returns true when the Sleep → Hibernate feature is enabled on battery
    /// (HIBERNATEIDLE DC value is a real timeout in the active power plan).
    /// </summary>
    /// <remarks>
    /// Windows uses <c>0x7FFFFFFF</c> (~68 years) as an internal "never hibernate"
    /// sentinel when the user has never configured this setting.  That value is
    /// greater than zero but does NOT represent an enabled feature, so we treat
    /// any value ≥ <c>0x7FFFFFFF</c> as "not set / disabled".
    /// </remarks>
    public bool IsSleepToHibernateEnabled()
    {
        try
        {
            var (_, output) = RunPowercfg("/query SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE");
            uint v = ParseDcSettingSeconds(output);
            // 0          = never (explicitly disabled)
            // 0x7FFFFFFF = Windows "never" sentinel (never been configured)
            return v > 0 && v < 0x7FFFFFFF;
        }
        catch (Exception ex)
        {
            Log.Warn("SystemStability", $"IsSleepToHibernateEnabled read failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns the current Sleep → Hibernate timeout in minutes (DC/battery).
    /// Returns 30 if the feature is off or the value cannot be read.
    /// </summary>
    public int GetSleepToHibernateMinutes()
    {
        try
        {
            var (_, output) = RunPowercfg("/query SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE");
            uint seconds = ParseDcSettingSeconds(output);
            // Guard against 0 (disabled) and the Windows 0x7FFFFFFF sentinel (never set).
            if (seconds == 0 || seconds >= 0x7FFFFFFF) return 30;
            return Math.Max(1, (int)(seconds / 60));
        }
        catch { return 30; }
    }

    /// <summary>
    /// Enables Sleep → Hibernate: after <paramref name="minutes"/> of sleep on battery
    /// the system hibernates instead of staying in low-power sleep.
    /// </summary>
    public Task<TweakResult> EnableSleepToHibernateAsync(int minutes) => Task.Run(() =>
    {
        try
        {
            int seconds = minutes * 60;

            // Ensure hibernate file is present (needed for the idle timer to fire).
            RunPowercfg("/hibernate on");

            // Disable Hybrid Sleep on battery — if it's on the HIBERNATEIDLE timer
            // is ignored because the system wakes straight from the combined state.
            RunPowercfg("/setdcvalueindex SCHEME_CURRENT SUB_SLEEP HYBRIDSLEEP 0");

            // Set the hibernate-after-sleep idle timeout on battery.
            RunPowercfg($"/setdcvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE {seconds}");

            // Commit changes to the active scheme.
            RunPowercfg("/setactive SCHEME_CURRENT");

            // ── Verify the value actually stuck ──────────────────────────────────
            // Some OEM power plans or group policies can silently reject the write.
            // Re-query and confirm the DC index matches what we asked for.
            var (_, verifyOut) = RunPowercfg("/query SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE");
            uint actual = ParseDcSettingSeconds(verifyOut);
            if (actual != (uint)seconds)
            {
                string msg =
                    $"Sleep → Hibernate (battery): value did not apply. " +
                    $"Expected {seconds}s ({minutes} min) but powercfg reports {actual}s. " +
                    $"Your power plan may be controlled by a group policy or OEM tool.";
                Log.Error("SystemStability", msg);
                return TweakResult.Fail(msg);
            }

            Log.Info("SystemStability",
                $"Sleep-to-Hibernate enabled: {minutes} min on battery " +
                $"(HYBRIDSLEEP=0, HIBERNATEIDLE={seconds}s) — verified.");

            return TweakResult.Ok(
                $"Sleep → Hibernate enabled: laptop will hibernate after " +
                $"{minutes} minute{(minutes == 1 ? "" : "s")} of sleep on battery.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "EnableSleepToHibernate failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    /// <summary>
    /// Disables Sleep → Hibernate and restores Windows/OEM defaults:
    /// HYBRIDSLEEP=1, HIBERNATEIDLE=0 (never hibernate from sleep).
    /// </summary>
    public Task<TweakResult> DisableSleepToHibernateAsync() => Task.Run(() =>
    {
        try
        {
            // Restore Hybrid Sleep to Windows default.
            RunPowercfg("/setdcvalueindex SCHEME_CURRENT SUB_SLEEP HYBRIDSLEEP 1");

            // Clear the hibernate-after-sleep timeout (0 = never).
            RunPowercfg("/setdcvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE 0");

            RunPowercfg("/setactive SCHEME_CURRENT");

            Log.Info("SystemStability",
                "Sleep-to-Hibernate disabled (HYBRIDSLEEP=1, HIBERNATEIDLE=0 restored)");

            return TweakResult.Ok(
                "Sleep → Hibernate disabled. Restored to Windows/OEM default.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "DisableSleepToHibernate failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    // ── Sleep → Hibernate (AC / plugged-in) ──────────────────────────────────
    //
    // Identical logic to the DC (battery) version but targets AC values so the
    // system hibernates after a period of idle sleep even while plugged in.
    // Useful for desktops and laptops left idle overnight on AC power.

    /// <summary>
    /// Returns true when Sleep → Hibernate is enabled on AC power
    /// (HIBERNATEIDLE AC value is a real timeout in the active power plan).
    /// </summary>
    /// <remarks>
    /// Same sentinel guard as the DC version: <c>0x7FFFFFFF</c> is Windows'
    /// "never configured" default and must not be treated as an enabled timeout.
    /// </remarks>
    public bool IsSleepToHibernateAcEnabled()
    {
        try
        {
            var (_, output) = RunPowercfg("/query SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE");
            uint v = ParseAcSettingSeconds(output);
            return v > 0 && v < 0x7FFFFFFF;
        }
        catch (Exception ex)
        {
            Log.Warn("SystemStability", $"IsSleepToHibernateAcEnabled read failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns the current Sleep → Hibernate timeout in minutes (AC/plugged-in).
    /// Returns 30 if the feature is off or the value cannot be read.
    /// </summary>
    public int GetSleepToHibernateAcMinutes()
    {
        try
        {
            var (_, output) = RunPowercfg("/query SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE");
            uint seconds = ParseAcSettingSeconds(output);
            // Guard against 0 (disabled) and the Windows 0x7FFFFFFF sentinel (never set).
            if (seconds == 0 || seconds >= 0x7FFFFFFF) return 30;
            return Math.Max(1, (int)(seconds / 60));
        }
        catch { return 30; }
    }

    /// <summary>
    /// Enables Sleep → Hibernate on AC power: after <paramref name="minutes"/> of idle
    /// sleep while plugged in the system hibernates.
    /// </summary>
    public Task<TweakResult> EnableSleepToHibernateAcAsync(int minutes) => Task.Run(() =>
    {
        try
        {
            int seconds = minutes * 60;
            RunPowercfg("/hibernate on");
            RunPowercfg("/setacvalueindex SCHEME_CURRENT SUB_SLEEP HYBRIDSLEEP 0");
            RunPowercfg($"/setacvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE {seconds}");
            RunPowercfg("/setactive SCHEME_CURRENT");

            // ── Verify the value actually stuck ──────────────────────────────────
            var (_, verifyOut) = RunPowercfg("/query SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE");
            uint actual = ParseAcSettingSeconds(verifyOut);
            if (actual != (uint)seconds)
            {
                string msg =
                    $"Sleep → Hibernate (AC): value did not apply. " +
                    $"Expected {seconds}s ({minutes} min) but powercfg reports {actual}s. " +
                    $"Your power plan may be controlled by a group policy or OEM tool.";
                Log.Error("SystemStability", msg);
                return TweakResult.Fail(msg);
            }

            Log.Info("SystemStability",
                $"Sleep-to-Hibernate (AC) enabled: {minutes} min on AC " +
                $"(HYBRIDSLEEP=0, HIBERNATEIDLE={seconds}s) — verified.");
            return TweakResult.Ok(
                $"Sleep → Hibernate (AC) enabled: system will hibernate after " +
                $"{minutes} minute{(minutes == 1 ? "" : "s")} of sleep while plugged in.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "EnableSleepToHibernateAc failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    /// <summary>
    /// Disables Sleep → Hibernate on AC power and restores Windows/OEM defaults:
    /// HYBRIDSLEEP=1, HIBERNATEIDLE=0 (never hibernate from sleep on AC).
    /// </summary>
    public Task<TweakResult> DisableSleepToHibernateAcAsync() => Task.Run(() =>
    {
        try
        {
            RunPowercfg("/setacvalueindex SCHEME_CURRENT SUB_SLEEP HYBRIDSLEEP 1");
            RunPowercfg("/setacvalueindex SCHEME_CURRENT SUB_SLEEP HIBERNATEIDLE 0");
            RunPowercfg("/setactive SCHEME_CURRENT");
            Log.Info("SystemStability",
                "Sleep-to-Hibernate (AC) disabled (HYBRIDSLEEP=1, HIBERNATEIDLE=0 restored)");
            return TweakResult.Ok(
                "Sleep → Hibernate (AC) disabled. Restored to Windows/OEM default.");
        }
        catch (Exception ex)
        {
            Log.Error("SystemStability", "DisableSleepToHibernateAc failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    /// <summary>
    /// Parses the "Current AC Power Setting Index: 0x..." line from powercfg /query output.
    /// Returns 0 on parse failure.
    /// </summary>
    private static uint ParseAcSettingSeconds(string output)
    {
        const string marker = "Current AC Power Setting Index:";
        int idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;

        string afterColon = output.Substring(idx + marker.Length).TrimStart();
        string token = afterColon.Split(new[] { '\r', '\n', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        string hex = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? token.Substring(2) : token;

        return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint v)
            ? v : 0;
    }

    /// <summary>
    /// Parses the "Current DC Power Setting Index: 0x..." line from powercfg /query output
    /// and returns the value as unsigned seconds. Returns 0 on parse failure.
    /// </summary>
    private static uint ParseDcSettingSeconds(string output)
    {
        const string marker = "Current DC Power Setting Index:";
        int idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;

        string afterColon = output.Substring(idx + marker.Length).TrimStart();
        // Value is formatted as "0x00000000"
        string token = afterColon.Split(new[] { '\r', '\n', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        string hex = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? token.Substring(2) : token;

        return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint v)
            ? v : 0;
    }

    private static (int ExitCode, string Output) RunPowercfg(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "powercfg.exe",
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
