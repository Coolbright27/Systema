// ════════════════════════════════════════════════════════════════════════════
// CoreParkingService.cs  ·  Controls CPU core parking across all power schemes
// ════════════════════════════════════════════════════════════════════════════
//
// CPMINCORES is the key power setting: it defines the MINIMUM percentage of
// logical cores that must remain unparked at all times.
//
//   CPMINCORES = 10   → allow parking; keep at least 10 % of cores active
//                         (Enable path — efficient/optimized parking)
//   CPMINCORES = 100  → keep ALL cores active; no cores can be parked
//                         (Disable path — maximum performance, no parking)
//
// Setting CPMINCORES = 0 (old disable behaviour) is wrong — it means "park
// everything", which is MORE aggressive parking, not less.
//
// Creates a Task Scheduler startup task (Enable only) so the setting survives
// reboots and power-plan resets by third-party tools or Windows updates.
//
// RELATED FILES
//   ToolsViewModel.cs  — Core Parking toggle button on the Tools tab
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

/// <summary>
/// Manages CPU core parking enforcement on Windows.
///
/// CPMINCORES controls the minimum fraction of logical cores that must remain
/// unparked. The two states this service enforces:
///
///   Enable  (optimized parking)  — CPMINCORES = 10 %
///     Allows the OS to park idle cores for power and thermal efficiency, while
///     keeping at least 10 % of cores always active for responsiveness.
///     A startup scheduled task keeps this value after power-plan resets.
///
///   Disable (force unpark)       — CPMINCORES = 100 %
///     Forces all cores to remain active; no cores can be parked. This gives
///     maximum single-threaded burst performance at the cost of higher idle power.
///     The task is removed on disable; the registry value persists on its own.
/// </summary>
public class CoreParkingService
{
    // GUID constants for the power-scheme settings hierarchy
    // Processor power management sub-group
    private const string ProcessorPowerSubGroupGuid = "54533251-82be-4824-96c1-47b60b740d00";
    // Core parking minimum cores setting
    private const string CpMinCoresGuid = "0cc5b647-c1df-4637-891a-dec35c318583";

    private const string PowerSchemesRoot =
        @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    private const string TaskName = "SystemaCoreParking";

    private static readonly LoggerService _log = LoggerService.Instance;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the SystemaCoreParking scheduled task exists, which is the
    /// definitive indicator that Systema is actively enforcing core parking.
    /// </summary>
    public bool IsCoreParkingEnforced()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "schtasks.exe",
                Arguments       = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow  = true
                // No output redirection — we only need the exit code.
                // Redirecting stdout/stderr without reading them fills the pipe buffers,
                // which blocks the child process so WaitForExit times out, then
                // accessing ExitCode on the still-running process throws InvalidOperationException.
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(10_000);
            // Guard: WaitForExit can return (timeout elapsed) while the process is still alive.
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log.Warn("CoreParkingService", $"IsCoreParkingEnforced check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Enables optimized core parking:
    ///   - Sets CPMINCORES = 10 % (keep at least 10 % of cores active; OS can park the rest)
    ///     across all user power schemes via registry and powercfg.
    ///   - Creates (or replaces) the SystemaCoreParking scheduled task so the setting
    ///     survives reboots and power-plan resets by third-party tools.
    /// </summary>
    public Task<TweakResult> EnableForcedCoreParking() => Task.Run(() =>
    {
        try
        {
            int schemesUpdated = ApplyCoreParking(minCoresPercent: 10);

            TweakResult taskResult = CreateScheduledTask();

            string msg = $"Core parking enforced on {schemesUpdated} power scheme(s). " +
                         $"Startup task: {(taskResult.Success ? "created" : taskResult.Message)}.";

            // Consider success if the scheduled task was created successfully.
            // schemesUpdated can be 0 when registry schemes aren't directly writable,
            // but powercfg (called in ApplyCoreParking) still applies the setting
            // to the active scheme immediately.
            return taskResult.Success ? TweakResult.Ok(msg) : TweakResult.Fail(msg);
        }
        catch (Exception ex)
        {
            _log.Error("CoreParkingService", "EnableForcedCoreParking failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    /// <summary>
    /// Disables core parking by forcing all cores to stay unparked:
    ///   - Sets CPMINCORES = 100 % so the OS must keep every core active.
    ///   - Deletes the SystemaCoreParking scheduled task (no longer needed).
    ///
    /// Note: CPMINCORES = 0 would be wrong here — that means "minimum 0 cores
    /// must stay active", which allows maximum parking. We want 100 (no parking).
    /// </summary>
    public Task<TweakResult> DisableForcedCoreParking() => Task.Run(() =>
    {
        try
        {
            // 100 % = keep all cores unparked. Apply before deleting the task so
            // the change takes effect immediately via powercfg /setactive.
            int schemesUpdated = ApplyCoreParking(minCoresPercent: 100);

            DeleteScheduledTask();

            string msg = $"Core parking disabled — all cores forced unparked across {schemesUpdated} scheme(s). Startup task removed.";
            return TweakResult.Ok(msg);
        }
        catch (Exception ex)
        {
            _log.Error("CoreParkingService", "DisableForcedCoreParking failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    // ── Registry helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Iterates every user power scheme in the registry and writes the CPMINCORES
    /// AC and DC values. Returns the number of schemes successfully updated.
    /// </summary>
    private static int ApplyCoreParking(int minCoresPercent)
    {
        int updated = 0;

        try
        {
            using var schemesKey = Registry.LocalMachine.OpenSubKey(PowerSchemesRoot, writable: false);
            if (schemesKey == null)
            {
                LoggerService.Instance.Warn("CoreParkingService",
                    $"Power schemes root key not found: {PowerSchemesRoot}");
                return 0;
            }

            foreach (string schemeGuid in schemesKey.GetSubKeyNames())
            {
                string settingPath =
                    $@"{PowerSchemesRoot}\{schemeGuid}\{ProcessorPowerSubGroupGuid}\{CpMinCoresGuid}";

                try
                {
                    using var settingKey = Registry.LocalMachine.CreateSubKey(settingPath, writable: true);
                    if (settingKey == null) continue;

                    settingKey.SetValue("ACSettingIndex", minCoresPercent, RegistryValueKind.DWord);
                    settingKey.SetValue("DCSettingIndex", minCoresPercent, RegistryValueKind.DWord);
                    updated++;
                }
                catch (Exception ex)
                {
                    LoggerService.Instance.Warn("CoreParkingService",
                        $"Could not update scheme '{schemeGuid}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warn("CoreParkingService",
                $"ApplyCoreParking enumeration failed: {ex.Message}");
        }

        // Also apply to the currently active scheme via powercfg so changes take
        // effect immediately without requiring a reboot.
        ApplyViaPowercfg(minCoresPercent);

        return updated;
    }

    /// <summary>
    /// Calls powercfg to apply the setting to the active scheme immediately.
    /// </summary>
    private static void ApplyViaPowercfg(int minCoresPercent)
    {
        try
        {
            string percentStr = minCoresPercent.ToString();

            RunPowercfg(
                $"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES {percentStr}");
            RunPowercfg(
                $"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES {percentStr}");
            RunPowercfg("/setactive SCHEME_CURRENT");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warn("CoreParkingService",
                $"ApplyViaPowercfg failed: {ex.Message}");
        }
    }

    private static void RunPowercfg(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "powercfg.exe",
            Arguments              = args,
            UseShellExecute        = false,
            CreateNoWindow         = true
            // No output redirection — we don't use the output, and redirecting
            // without reading both streams can deadlock if buffers fill.
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(10_000);
    }

    // ── Scheduled task helpers ────────────────────────────────────────────────

    /// <summary>
    /// Creates the SystemaCoreParking startup task that re-applies core parking
    /// settings each time the system boots, running as SYSTEM.
    /// The /F flag forces creation even if the task already exists.
    /// </summary>
    private TweakResult CreateScheduledTask()
    {
        try
        {
            // The task action runs powercfg to enforce the AC and DC parking values and
            // then re-activates the current scheme so the change takes effect immediately.
            // Note: no inner quotes around the cmd /c body — powercfg args have no spaces
            // and inner quotes would prematurely close the schtasks /TR quoted string.
            const string taskAction =
                "cmd /c powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 10 " +
                "&& powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES 10 " +
                "&& powercfg /setactive SCHEME_CURRENT";

            var psi = new ProcessStartInfo
            {
                FileName               = "schtasks.exe",
                Arguments              = $"/Create /TN \"{TaskName}\" /TR \"{taskAction}\" " +
                                         $"/SC ONSTART /RU SYSTEM /RL HIGHEST /F",
                UseShellExecute        = false,
                CreateNoWindow         = true
                // No output redirection — exit code alone determines success.
                // Redirecting without reading both streams can deadlock if buffers fill.
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return TweakResult.Fail("Failed to start schtasks.exe process.");

            bool exited = proc.WaitForExit(20_000);
            // Guard: WaitForExit returns false on timeout; ExitCode on a running process throws.
            if (!exited || !proc.HasExited)
                return TweakResult.Fail("Task creation timed out — schtasks.exe did not exit within 20 s.");

            if (proc.ExitCode == 0)
            {
                _log.Info("CoreParkingService", $"Scheduled task '{TaskName}' created.");
                return TweakResult.Ok($"Startup task '{TaskName}' created.");
            }

            _log.Warn("CoreParkingService", $"schtasks /Create exited {proc.ExitCode}");
            return TweakResult.Fail($"Task creation failed (exit code {proc.ExitCode}).");
        }
        catch (Exception ex)
        {
            _log.Error("CoreParkingService", "CreateScheduledTask failed", ex);
            return TweakResult.FromException(ex);
        }
    }

    /// <summary>
    /// Deletes the SystemaCoreParking scheduled task. Silently succeeds when the
    /// task does not exist.
    /// </summary>
    private void DeleteScheduledTask()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "schtasks.exe",
                Arguments              = $"/Delete /TN \"{TaskName}\" /F",
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);

            _log.Info("CoreParkingService", $"Scheduled task '{TaskName}' deletion attempted.");
        }
        catch (Exception ex)
        {
            _log.Warn("CoreParkingService", $"DeleteScheduledTask failed: {ex.Message}");
        }
    }
}
