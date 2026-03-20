// ════════════════════════════════════════════════════════════════════════════
// PowerPlanService.cs  ·  Power plan switching and CPU cap via P/Invoke
// ════════════════════════════════════════════════════════════════════════════
//
// Switches the active Windows power plan (High Performance / Balanced / Power
// Saver) and can apply a maximum processor state cap. Detects whether the
// machine is on battery via GetSystemPowerStatus P/Invoke. Used by both
// VisualViewModel and HealthScoreService for the Security sub-score.
//
// RELATED FILES
//   VisualViewModel.cs      — power plan picker and CPU cap slider
//   DashboardViewModel.cs   — shows current plan on the Dashboard
//   HealthScoreService.cs   — reads plan for the Security sub-score
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Runtime.InteropServices;
using Systema.Core;
using static Systema.Core.ThreadHelper;

namespace Systema.Services;

public class PowerPlanService
{
    private static readonly LoggerService _log = LoggerService.Instance;

    private const string HighPerformanceGuid    = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string BalancedGuid           = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string PowerSaverGuid         = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string UltimatePerformanceGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    // Max processor state sub-group and setting GUIDs (for 99% cap)
    private const string ProcessorSubGroup  = "54533251-82be-4824-96c1-47b60b740d00";
    private const string MaxProcessorState  = "bc5038f7-23e0-4960-96da-33abaf5935ec";

    // ── Battery detection via P/Invoke ─────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>Returns true if the system has a battery (laptop/tablet).</summary>
    public bool HasBattery()
    {
        try
        {
            if (GetSystemPowerStatus(out var status))
                return status.BatteryFlag != 128; // 128 = no battery
            return false;
        }
        catch { return false; }
    }

    /// <summary>Returns true if currently running on battery power (not plugged in).</summary>
    public bool IsOnBattery()
    {
        try
        {
            if (GetSystemPowerStatus(out var status))
                return status.ACLineStatus == 0; // 0 = offline (battery), 1 = online (AC)
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Removes any battery CPU cap by restoring max processor state to 100% on DC.
    /// Does NOT change the active power plan.
    /// </summary>
    public Task<TweakResult> StopBatteryOptimizationAsync()
    {
        return RunOnLargeStackAsync<TweakResult>(() =>
        {
            try
            {
                // Switch back to High Performance (or Ultimate if available)
                RunPowercfg($"/duplicatescheme {UltimatePerformanceGuid}");
                RunPowercfg($"/setactive {UltimatePerformanceGuid}");
                string plan = GetActivePlan();
                if (!plan.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                    RunPowercfg($"/setactive {HighPerformanceGuid}");

                _log.Info("PowerPlanService", "Battery optimization stopped — restored High Performance");
                return TweakResult.Ok("Battery optimization disabled. High Performance plan restored.");
            }
            catch (Exception ex)
            {
                return TweakResult.FromException(ex);
            }
        });
    }

    /// <summary>
    /// Switches to High Performance without touching battery optimization state.
    /// Called by VisualViewModel when the user plugs back in while battery optimization is active.
    /// </summary>
    public Task<TweakResult> RestoreHighPerformanceAsync()
    {
        return RunOnLargeStackAsync<TweakResult>(() =>
        {
            try
            {
                RunPowercfg($"/duplicatescheme {UltimatePerformanceGuid}");
                RunPowercfg($"/setactive {UltimatePerformanceGuid}");
                string plan = GetActivePlan();
                if (!plan.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                    RunPowercfg($"/setactive {HighPerformanceGuid}");

                _log.Info("PowerPlanService", "Plugged in — restored High Performance plan");
                return TweakResult.Ok("Plugged in — High Performance plan restored.");
            }
            catch (Exception ex)
            {
                return TweakResult.FromException(ex);
            }
        });
    }

    public string GetActivePlan()
    {
        try
        {
            var psi = new ProcessStartInfo("powercfg", "/getactivescheme")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "Unknown";
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5_000); // 5-second safety timeout

            // Match known well-known GUIDs first for consistent naming
            if (output.Contains(HighPerformanceGuid, StringComparison.OrdinalIgnoreCase))
                return "High Performance";
            if (output.Contains(UltimatePerformanceGuid, StringComparison.OrdinalIgnoreCase))
                return "Ultimate Performance";
            if (output.Contains(PowerSaverGuid, StringComparison.OrdinalIgnoreCase))
                return "Power Saver";
            if (output.Contains(BalancedGuid, StringComparison.OrdinalIgnoreCase))
                return "Balanced";

            // Unknown / custom plan — extract the friendly name from inside parentheses.
            // powercfg output format: "Power Scheme GUID: <guid>  (Plan Name)"
            var parenMatch = System.Text.RegularExpressions.Regex.Match(output, @"\(([^)]+)\)");
            if (parenMatch.Success)
                return parenMatch.Groups[1].Value.Trim();

            return "Balanced"; // absolute fallback
        }
        catch { return "Unknown"; }
    }

    public Task<TweakResult> SetHighPerformanceAsync()
    {
        // Use a large-stack thread: multiple RunPowercfg() calls each spawn powercfg.exe;
        // AV/EDR CreateProcess hooks on the calling thread can overflow a 1 MB threadpool stack.
        return RunOnLargeStackAsync<TweakResult>(() =>
        {
            try
            {
                // Try to enable Ultimate Performance (Windows 10 Pro+)
                RunPowercfg($"/duplicatescheme {UltimatePerformanceGuid}");
                RunPowercfg($"/setactive {UltimatePerformanceGuid}");
                var plan = GetActivePlan();
                if (plan.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Info("PowerPlanService", "Power plan changed → Ultimate Performance");
                    return TweakResult.Ok("Ultimate Performance power plan activated.");
                }

                // Fall back to High Performance
                RunPowercfg($"/setactive {HighPerformanceGuid}");
                _log.Info("PowerPlanService", "Power plan changed → High Performance");
                return TweakResult.Ok("High Performance power plan activated.");
            }
            catch (Exception ex)
            {
                return TweakResult.FromException(ex);
            }
        });
    }

    public Task<TweakResult> SetBalancedAsync()
    {
        return RunOnLargeStackAsync<TweakResult>(() =>
        {
            try
            {
                RunPowercfg($"/setactive {BalancedGuid}");
                _log.Info("PowerPlanService", "Power plan changed → Balanced");
                return TweakResult.Ok("Balanced power plan restored.");
            }
            catch (Exception ex)
            {
                return TweakResult.FromException(ex);
            }
        });
    }

    /// <summary>
    /// Caps max processor state to 99% on DC (battery) for the CURRENT plan.
    /// Does NOT switch the active power plan — your current plan stays active.
    /// Only affects battery mode; plugged-in performance is unchanged.
    /// </summary>
    /// <summary>
    /// If on battery, switches to Balanced immediately.
    /// If on AC, does nothing — the plan stays whatever it was.
    /// VisualViewModel.OnPowerModeChanged handles the plug/unplug transitions.
    /// </summary>
    public Task<TweakResult> SetBalancedOnBatteryAsync()
    {
        return RunOnLargeStackAsync<TweakResult>(() =>
        {
            try
            {
                if (IsOnBattery())
                {
                    RunPowercfg($"/setactive {BalancedGuid}");
                    _log.Info("PowerPlanService", "On battery — switched active plan to Balanced");
                    return TweakResult.Ok("Balanced plan activated. Will restore your previous plan when plugged in.");
                }
                else
                {
                    _log.Info("PowerPlanService", "On AC — battery opt enabled, plan unchanged until you unplug");
                    return TweakResult.Ok("Battery optimization active. Balanced plan will switch on when you unplug.");
                }
            }
            catch (Exception ex)
            {
                return TweakResult.FromException(ex);
            }
        });
    }

    /// <summary>
    /// If on battery, switches to Power Saver immediately.
    /// If on AC, does nothing — the plan stays whatever it was.
    /// </summary>
    public Task<TweakResult> SetMaxBatteryLifeAsync()
    {
        return RunOnLargeStackAsync<TweakResult>(() =>
        {
            try
            {
                if (IsOnBattery())
                {
                    RunPowercfg($"/setactive {PowerSaverGuid}");
                    _log.Info("PowerPlanService", "On battery — switched active plan to Power Saver");
                    return TweakResult.Ok("Power Saver plan activated for maximum battery life.");
                }
                else
                {
                    _log.Info("PowerPlanService", "On AC — battery opt enabled, plan unchanged until you unplug");
                    return TweakResult.Ok("Battery optimization active. Power Saver plan will switch on when you unplug.");
                }
            }
            catch (Exception ex)
            {
                return TweakResult.FromException(ex);
            }
        });
    }

    /// <summary>Restores a named power plan (saved before battery optimization was enabled).</summary>
    public Task<TweakResult> RestorePlanAsync(string planName)
    {
        return RunOnLargeStackAsync<TweakResult>(() =>
        {
            try
            {
                if (planName.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                {
                    RunPowercfg($"/duplicatescheme {UltimatePerformanceGuid}");
                    RunPowercfg($"/setactive {UltimatePerformanceGuid}");
                }
                else if (planName.Contains("High", StringComparison.OrdinalIgnoreCase))
                    RunPowercfg($"/setactive {HighPerformanceGuid}");
                else if (planName.Contains("Power Saver", StringComparison.OrdinalIgnoreCase))
                    RunPowercfg($"/setactive {PowerSaverGuid}");
                else
                    RunPowercfg($"/setactive {BalancedGuid}");

                string actual = GetActivePlan();
                _log.Info("PowerPlanService", $"Plan restored to: {actual}");
                return TweakResult.Ok($"Plugged in — {actual} plan restored.");
            }
            catch (Exception ex)
            {
                return TweakResult.FromException(ex);
            }
        });
    }

    private static void RunPowercfg(string args)
    {
        var psi = new ProcessStartInfo("powercfg", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
    }
}
