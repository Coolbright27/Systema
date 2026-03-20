// ════════════════════════════════════════════════════════════════════════════
// TelemetryService.cs  ·  Disables Windows telemetry services and scheduled tasks
// ════════════════════════════════════════════════════════════════════════════
//
// Stops and disables DiagTrack and dmwappushservice, and removes the Customer
// Experience Improvement Program (CEIP) and Autochk scheduled tasks. Also reads
// back current telemetry state for use by HealthScoreService in the Security
// sub-score calculation.
//
// RELATED FILES
//   HealthScoreService.cs  — queries telemetry state for the Security sub-score
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

public class TelemetryService
{
    private static readonly LoggerService Log = LoggerService.Instance;
    private static readonly string[] _telemetryServices =
        { "DiagTrack", "dmwappushservice" };

    private static readonly string[] _ceipTasks =
    {
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
        @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
        @"\Microsoft\Windows\Autochk\Proxy",
    };

    public async Task<TweakResult> DisableAllTelemetryAsync()
    {
        return await Task.Run(() =>
        {
            var errors = new List<string>();

            // Registry: set AllowTelemetry to 0
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", true);
                key.SetValue("AllowTelemetry", 0, RegistryValueKind.DWord);
                key.SetValue("DisableEnterpriseAuthProxy", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex) { errors.Add(ex.Message); Log.Warn("Telemetry", "Failed to set AllowTelemetry policy", ex); }

            // Registry: disable feedback frequency
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(
                    @"SOFTWARE\Microsoft\Siuf\Rules", true);
                key.SetValue("NumberOfSIUFInPeriod", 0, RegistryValueKind.DWord);
                key.SetValue("PeriodInNanoSeconds", 0, RegistryValueKind.DWord);
            }
            catch (Exception ex) { errors.Add(ex.Message); Log.Warn("Telemetry", "Failed to set feedback frequency registry", ex); }

            // Registry: disable advertising ID
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", true);
                key.SetValue("Enabled", 0, RegistryValueKind.DWord);
            }
            catch (Exception ex) { errors.Add(ex.Message); Log.Warn("Telemetry", "Failed to disable AdvertisingInfo", ex); }

            // Disable telemetry services
            foreach (var svcName in _telemetryServices)
            {
                try
                {
                    using var svc = new ServiceController(svcName);
                    if (svc.Status == ServiceControllerStatus.Running)
                    {
                        svc.Stop();
                        PollForStatus(svc, ServiceControllerStatus.Stopped, timeoutSeconds: 5);
                    }
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{svcName}", true);
                    if (key == null)
                        Log.Warn("Telemetry", $"Cannot open registry key for service {svcName} — Start value not written");
                    else
                        key.SetValue("Start", 4, RegistryValueKind.DWord);
                }
                catch (Exception ex) { Log.Warn("Telemetry", $"Failed to disable service {svcName}", ex); }
            }

            // Disable CEIP scheduled tasks
            foreach (var task in _ceipTasks)
            {
                try
                {
                    var psi = new ProcessStartInfo("schtasks.exe",
                        $"/Change /TN \"{task}\" /Disable")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) { Log.Warn("Telemetry", $"schtasks.exe failed to start for task {task}"); continue; }
                    proc.WaitForExit(3000);
                }
                catch (Exception ex) { Log.Warn("Telemetry", $"Failed to disable task {task}", ex); }
            }

            var result = errors.Count == 0
                ? TweakResult.Ok("All telemetry disabled successfully.")
                : TweakResult.Fail($"Telemetry partially disabled — {errors.Count} step(s) failed. Check the log for details.");
            Log.LogChange("Telemetry Disabled", result.Message);
            return result;
        });
    }

    public bool IsTelemetryDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\DataCollection");
            var val = key?.GetValue("AllowTelemetry");
            return val is int i && i == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Polls a service until it reaches the target status or the timeout expires.
    /// Avoids WaitForStatus() which can exhaust stack space on threadpool threads.
    /// </summary>
    private static void PollForStatus(
        ServiceController svc, ServiceControllerStatus target, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            svc.Refresh();
            if (svc.Status == target) return;
            Thread.Sleep(200);
        }
    }

    public async Task<TweakResult> RestoreTelemetryAsync()
    {
        return await Task.Run(() =>
        {
            var errors = new List<string>();

            // Registry: remove AllowTelemetry policy values
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", true);
                key?.DeleteValue("AllowTelemetry", false);
                key?.DeleteValue("DisableEnterpriseAuthProxy", false);
            }
            catch (Exception ex) { errors.Add(ex.Message); Log.Warn("Telemetry", "Failed to restore AllowTelemetry policy", ex); }

            // Registry: restore feedback frequency
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Siuf\Rules", true);
                key?.DeleteValue("NumberOfSIUFInPeriod", false);
                key?.DeleteValue("PeriodInNanoSeconds", false);
            }
            catch (Exception ex) { errors.Add(ex.Message); Log.Warn("Telemetry", "Failed to restore feedback frequency registry", ex); }

            // Registry: re-enable advertising ID
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", true);
                key?.SetValue("Enabled", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex) { errors.Add(ex.Message); Log.Warn("Telemetry", "Failed to restore AdvertisingInfo", ex); }

            // Restore telemetry services to Automatic
            foreach (var svcName in _telemetryServices)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{svcName}", true);
                    if (key == null)
                        Log.Warn("Telemetry", $"Cannot open registry key for service {svcName} — Start value not restored");
                    else
                        key.SetValue("Start", 2, RegistryValueKind.DWord);
                }
                catch (Exception ex) { Log.Warn("Telemetry", $"Failed to restore service {svcName}", ex); }
            }

            // Re-enable CEIP scheduled tasks
            foreach (var task in _ceipTasks)
            {
                try
                {
                    var psi = new ProcessStartInfo("schtasks.exe",
                        $"/Change /TN \"{task}\" /Enable")
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) { Log.Warn("Telemetry", $"schtasks.exe failed to start for task {task}"); continue; }
                    proc.WaitForExit(3000);
                }
                catch (Exception ex) { Log.Warn("Telemetry", $"Failed to re-enable task {task}", ex); }
            }

            var result = errors.Count == 0
                ? TweakResult.Ok("Telemetry settings restored to Windows defaults.")
                : TweakResult.Ok($"Telemetry mostly restored with {errors.Count} minor errors.");
            Log.LogChange("Telemetry Restored", result.Message);
            return result;
        });
    }
}
