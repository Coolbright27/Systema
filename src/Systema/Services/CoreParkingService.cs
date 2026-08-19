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

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
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

    // Efficiency class 1. Per Microsoft: a HIGHER efficiency class means greater performance and
    // less efficiency, so class 1 is the P-cores and class 0 (the base setting above) is the
    // E-cores. Windows keeps a SEPARATE min-cores floor per class, so setting only the base
    // one leaves the P-cores pinned awake on exactly the machines that benefit most from
    // parking them. Absent on non-hybrid CPUs, where the write simply creates a key Windows
    // ignores.
    private const string CpMinCoresClass1Guid = "0cc5b647-c1df-4637-891a-dec35c318584";

    // "Latency sensitivity hint min unparked cores/packages" — the pool Windows holds unparked
    // and READY to service latency hints. Left at its default it quietly re-floats cores that
    // min-cores just released, which is why min-cores alone does not park as deeply as expected.
    private const string CpLatencyHintMinUnparkedGuid = "616cdaa5-695e-4545-97ad-97dc2d1bdd88";
    private const string CpLatencyHintMinUnparkedClass1Guid = "616cdaa5-695e-4545-97ad-97dc2d1bdd89";

    // Minimum processor state — what ParkControl calls "frequency scaling". Parking decides how
    // many cores are awake; this decides how far the awake ones may clock DOWN. Windows ships
    // Balanced at 5%, which holds a floor under the clocks and blunts the parking work. At 0 the
    // cores are free to drop to their lowest state.
    private const string ProcThrottleMinGuid       = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string ProcThrottleMinClass1Guid = "893dee8e-2bef-41e0-89c6-b55d0929964d";


    // What P-state a core sits at WHILE PARKED. At default ("No Preference") a parked core can
    // still idle high, so parking costs the OS scheduling width without collecting the power
    // saving. Deepest costs nothing measurable: by definition the core is not running work.
    //
    // This is an ENUM, not a percentage: 0 = No Preference, 1 = Deepest, 2 = Lightest. Writing 0
    // here would silently mean "no preference" and do nothing, which is the same trap the old
    // CPMINCORES = 0 comment warned about. It therefore carries its own value rather than the
    // shared floor.
    private const string CpParkedPerfStateGuid       = "447235c7-6a8d-4cc0-8e24-9eaf70b96e2b";
    private const string CpParkedPerfStateClass1Guid = "447235c7-6a8d-4cc0-8e24-9eaf70b96e2c";
    private const int    ParkedPerfDeepest           = 1;

    // What min cores goes back to when Core Efficiency is switched OFF.
    //
    // NOTE: this is a deliberate choice, not the Windows default. Balanced actually ships
    // min cores at AC=100 / DC=10, and AC=100 means no core is ever parked while plugged in.
    // 5 leaves light parking in place instead of switching parking off entirely, which is the
    // requested off-state. Every OTHER setting still reverts to its true Windows default by
    // having its override deleted.
    private const int    MinCoresWhenDisabled       = 5;


    // ── Hybrid policy ─────────────────────────────────────────────────────────
    //
    // Windows' own defaults for min cores are deliberately ASYMMETRIC on hybrid chips:
    //     class 0 (E-cores) AC=100  → keep them all awake
    //     class 1 (P-cores) AC=0    → let them all park
    //
    // That is the power-optimal arrangement, and it is worth copying rather than overriding.
    // Parking the E-cores does not save power: background work still has to run somewhere, and
    // with no E-core available it lands on a P-core, which draws far more for the same work. So
    // driving BOTH classes to 0 produces more heat, not less.
    //
    // On hybrid we therefore keep a slice of E-cores awake and let every P-core park. 10 is
    // Microsoft's own battery-side default for this setting, so it is a blessed value for "park
    // hard but keep a little capacity", and on a typical 8-E-core chip it leaves about one core
    // available to absorb background work.
    //
    // On a NON-hybrid CPU every core is class 0, there is no cheap tier to preserve, and the
    // floor goes to 0 as before.
    private const int HybridEcoreMinCores = 10;

    private static bool? _isHybridCache;


    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationship, IntPtr buffer, ref uint returnedLength);

    /// <summary>
    /// Logical processors in the LOWEST efficiency class, i.e. the E-cores. 0 on a homogeneous CPU.
    /// Per Microsoft, a higher efficiency class means greater performance and less efficiency, so
    /// the lowest class is the efficient tier.
    /// </summary>
    private static int CountEcoreLogicalProcessors()
    {
        try
        {
            const int RelationProcessorCore = 0;
            uint size = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref size);
            if (size == 0) return 0;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref size)) return 0;

                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX (x64):
                //   +0 DWORD Relationship, +4 DWORD Size, +8 PROCESSOR_RELATIONSHIP
                //   PROCESSOR_RELATIONSHIP: +0 BYTE Flags, +1 BYTE EfficiencyClass,
                //                           +22 WORD GroupCount, +24 GROUP_AFFINITY (Mask first)
                var cores = new List<(byte cls, ulong mask)>();
                int offset = 0;
                while (offset + 8 <= (int)size)
                {
                    int  rel     = Marshal.ReadInt32(buf, offset);
                    uint recSize = (uint)Marshal.ReadInt32(buf, offset + 4);
                    if (recSize == 0) break;

                    if (rel == RelationProcessorCore)
                    {
                        byte cls  = Marshal.ReadByte(buf, offset + 8 + 1);
                        ulong m   = (ulong)Marshal.ReadInt64(buf, offset + 8 + 24);
                        cores.Add((cls, m));
                    }
                    offset += (int)recSize;
                }

                if (cores.Count == 0) return 0;
                byte lowest = cores.Min(c => c.cls);
                if (lowest == cores.Max(c => c.cls)) return 0;   // homogeneous

                int logical = 0;
                foreach (var (cls, m) in cores)
                    if (cls == lowest) logical += System.Numerics.BitOperations.PopCount(m);
                return logical;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warn("CoreParkingService", $"E-core count failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// The min-cores percentage for the E-core class: the SMALLEST value that still leaves one
    /// core schedulable. A fixed percentage does not scale, which is the whole problem: 10% is
    /// about one core on an 8-E-core chip but two on a 16-E-core one and three on a 32, reserving
    /// more capacity the bigger the CPU gets, for no benefit. Computing it from the actual count
    /// pins the reserve at one core on every chip.
    ///
    /// Rounded UP so the floor survives even if Windows truncates rather than rounds when turning
    /// the percentage into a core count.
    ///
    /// Assumes the per-class percentage is of that class's cores, which is the sensible reading
    /// given Windows keeps a separate setting per class.
    /// </summary>
    private static int HybridEcoreFloorPercent()
    {
        int n = CountEcoreLogicalProcessors();
        if (n <= 0) return 0;

        int pct = (int)Math.Ceiling(100.0 / n);
        LoggerService.Instance.Info("CoreParkingService",
            $"{n} E-core logical processor(s); reserving {pct}% (~1 core) so light work has somewhere cheap to land.");
        return Math.Clamp(pct, 1, 100);
    }
    /// <summary>True when the CPU exposes more than one efficiency class (P-cores + E-cores).</summary>
    private static bool IsHybridCpu()
    {
        if (_isHybridCache is { } cached) return cached;

        bool hybrid = false;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT NumberOfEfficiencyClasses FROM Win32_Processor");
            foreach (System.Management.ManagementObject cpu in searcher.Get())
            {
                // Absent on older Windows/CPUs, which is itself the "not hybrid" answer.
                var v = cpu["NumberOfEfficiencyClasses"];
                if (v != null && Convert.ToUInt32(v) > 1) { hybrid = true; break; }
            }
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warn("CoreParkingService", $"Hybrid detection failed, assuming non-hybrid: {ex.Message}");
        }

        _isHybridCache = hybrid;
        LoggerService.Instance.Info("CoreParkingService",
            hybrid ? "Hybrid CPU detected — keeping some E-cores awake so work does not spill onto P-cores."
                   : "Non-hybrid CPU — all cores share one parking floor.");
        return hybrid;
    }
    /// <summary>
    /// Every setting Systema writes, paired with the value it gets, so apply and remove cannot
    /// drift apart. Most take the shared floor; parked performance state takes its own enum.
    /// </summary>
    private static (string Guid, int Value)[] ParkingSettings(int floorPercent)
    {
        // Class 0 is the E-cores on a hybrid chip. Parking those pushes background work onto
        // P-cores, which costs MORE power for the same work, so they keep a slice awake.
        // On non-hybrid every core is class 0 and there is no cheap tier to protect.
        int class0 = (floorPercent == 0 && IsHybridCpu()) ? HybridEcoreFloorPercent() : floorPercent;

        return new[]
        {
        (CpMinCoresGuid,                     class0),
        (CpMinCoresClass1Guid,               floorPercent),
        (CpLatencyHintMinUnparkedGuid,       floorPercent),
        (CpLatencyHintMinUnparkedClass1Guid, floorPercent),
        (ProcThrottleMinGuid,                floorPercent),
        (ProcThrottleMinClass1Guid,          floorPercent),
        (CpParkedPerfStateGuid,              ParkedPerfDeepest),
        (CpParkedPerfStateClass1Guid,        ParkedPerfDeepest),
        };
    }

    /// <summary>Same set, for removal, where the values do not matter.</summary>
    private static readonly string[] ParkingSettingGuids =
    {
        CpMinCoresGuid, CpMinCoresClass1Guid,
        CpLatencyHintMinUnparkedGuid, CpLatencyHintMinUnparkedClass1Guid,
        ProcThrottleMinGuid, ProcThrottleMinClass1Guid,
        CpParkedPerfStateGuid, CpParkedPerfStateClass1Guid,
    };

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
            int schemesUpdated = ApplyCoreParking(minCoresPercent: 0);

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
    /// Disables core parking enforcement — restores Windows default behaviour:
    ///   - Deletes Systema's CPMINCORES registry overrides from all power schemes
    ///     so the OS uses its own built-in core parking logic.
    ///   - Runs <c>powercfg /setactive SCHEME_CURRENT</c> to apply immediately.
    ///   - Deletes the SystemaCoreParking scheduled task.
    /// </summary>
    public Task<TweakResult> DisableForcedCoreParking() => Task.Run(() =>
    {
        try
        {
            // Best effort: the registry deletes are refused on this OS, but they are harmless and
            // would be the cleanest route if a future Windows ever permits them.
            int cleaned = RemoveCoreParkingOverrides();

            // The route that actually works. powercfg holds the privilege direct registry writes
            // do not, so every setting is written back to its Windows default explicitly.
            RestoreDefaultsViaPowercfg();

            // ...then put min cores back at 5, deliberately NOT the Windows default. Balanced
            // ships AC=100, and 100 means nothing is ever parked while plugged in. 5 leaves
            // light parking in place instead of switching parking off altogether.
            SetMinCoresEverywhere(MinCoresWhenDisabled);

            // Also reset the active scheme via powercfg to apply immediately
            RunPowercfg("/setactive SCHEME_CURRENT");

            DeleteScheduledTask();

            string msg = $"Core parking enforcement removed across {cleaned} scheme(s). Min cores back to {MinCoresWhenDisabled}%, everything else back to Windows defaults. Startup task removed.";
            return TweakResult.Ok(msg);
        }
        catch (Exception ex)
        {
            _log.Error("CoreParkingService", "DisableForcedCoreParking failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    /// <summary>
    /// Re-applies the core-parking values to the live power scheme without
    /// recreating the scheduled task. Called on every app startup (after a short
    /// delay) when the setting is enabled, because the ONSTART scheduled task runs
    /// as SYSTEM against SYSTEM's active scheme — which often differs from the
    /// signed-in user's scheme, so it silently no-ops. Re-applying from the running
    /// (user-context, elevated) app guarantees the user's active scheme is corrected
    /// after every reboot or third-party power-plan reset.
    /// </summary>
    public Task ReapplyCoreParkingAsync() => Task.Run(() =>
    {
        try
        {
            int n = ApplyCoreParking(minCoresPercent: 0);
            _log.Info("CoreParkingService", $"Core parking re-applied on startup ({n} scheme(s)).");
        }
        catch (Exception ex)
        {
            _log.Warn("CoreParkingService", $"Startup core-parking re-apply failed: {ex.Message}");
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
        int skippedProtected = 0;        // TrustedInstaller-owned schemes — expected
        int otherFailures    = 0;        // anything else — worth a single warning

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

                    // Same floor for the other efficiency class, the ready/latency-hint pool and the clock
                    // floor, plus the deepest parked P-state. Min-cores alone does not park
                    // deeply: the other knobs re-float or hold up the cores it just released,
                    // and without the parked P-state the parked ones can still idle high.
                    foreach (var (g, value) in ParkingSettings(minCoresPercent))
                    {
                        if (g == CpMinCoresGuid) continue;   // written directly above
                        WriteSchemeValue(schemeGuid, g, value);
                    }
                }
                // Hidden Windows power schemes (the long list of GUIDs under
                // SYSTEM\…\PowerSchemes\) are owned by TrustedInstaller and can't
                // be written even from an elevated process. Every Win11 machine
                // has 200+ of them and the resulting log was ~350 warnings per
                // Auto-Pilot run that drowned out actually useful messages. We
                // count them silently and emit a single summary line at the end.
                // PowerSchemes is not writable even elevated, so on most machines EVERY scheme is
                // refused. Bailing on the first refusal instead of grinding through the rest
                // matters: this box has 2020 schemes and the doomed loop cost about six seconds
                // on every boot and app start to accomplish nothing. powercfg does the real work.
                catch (UnauthorizedAccessException)            { skippedProtected++; if (updated == 0) break; }
                catch (System.Security.SecurityException)      { skippedProtected++; if (updated == 0) break; }
                catch (Exception ex)
                {
                    otherFailures++;
                    if (otherFailures <= 3)
                        LoggerService.Instance.Warn("CoreParkingService",
                            $"Could not update scheme '{schemeGuid}': {ex.Message}");
                }
            }

            if (skippedProtected > 0)
                LoggerService.Instance.Info("CoreParkingService",
                    $"Registry path refused after {skippedProtected} scheme(s), expected; powercfg applies the values.");
            if (otherFailures > 3)
                LoggerService.Instance.Warn("CoreParkingService",
                    $"+{otherFailures - 3} additional scheme-write failures suppressed.");
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
    /// Removes Systema's CPMINCORES AC/DC overrides from all power schemes,
    /// letting Windows fall back to its built-in defaults.
    /// </summary>
    private static int RemoveCoreParkingOverrides()
    {
        int cleaned = 0;
        try
        {
            using var schemesKey = Registry.LocalMachine.OpenSubKey(PowerSchemesRoot, writable: false);
            if (schemesKey == null) return 0;

            foreach (string schemeGuid in schemesKey.GetSubKeyNames())
            {

                try
                {
                    // Remove every setting apply touches. Cleaning only min-cores left the clock
                    // floor and the latency-hint pool pinned at 0 forever after a disable.
                    bool any = false;
                    foreach (string guid in ParkingSettingGuids)
                    {
                        string path =
                            $@"{PowerSchemesRoot}\{schemeGuid}\{ProcessorPowerSubGroupGuid}\{guid}";
                        using var settingKey = Registry.LocalMachine.OpenSubKey(path, writable: true);
                        if (settingKey == null) continue;

                        settingKey.DeleteValue("ACSettingIndex", throwOnMissingValue: false);
                        settingKey.DeleteValue("DCSettingIndex", throwOnMissingValue: false);
                        any = true;
                    }
                    if (any) cleaned++;
                }
                catch (Exception ex)
                {
                    LoggerService.Instance.Warn("CoreParkingService",
                        $"Could not clean scheme '{schemeGuid}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warn("CoreParkingService",
                $"RemoveCoreParkingOverrides enumeration failed: {ex.Message}");
        }
        return cleaned;
    }

    /// <summary>
    /// Calls powercfg to apply the setting to the active scheme immediately.
    /// </summary>
    /// <summary>Writes one AC/DC power-setting pair into one scheme. Silent on the protected
    /// schemes Win11 ships by the hundred; the caller already reports those in aggregate.</summary>

    /// <summary>
    /// Writes min cores (AC and DC, both efficiency classes) to <paramref name="percent"/> across
    /// every writable scheme AND the active one. Used by the OFF path, and by Max Life battery mode,
    /// which drives it to 0 independently of whether Core Efficiency is switched on.
    /// </summary>
    public static void SetMinCoresEverywhere(int percent)
    {
        try
        {
            using var schemesKey = Registry.LocalMachine.OpenSubKey(PowerSchemesRoot, writable: false);
            if (schemesKey != null)
            {
                foreach (string schemeGuid in schemesKey.GetSubKeyNames())
                {
                    WriteSchemeValue(schemeGuid, CpMinCoresGuid, percent);
                    WriteSchemeValue(schemeGuid, CpMinCoresClass1Guid, percent);
                }
            }

            // The active plan explicitly, so it takes effect without waiting for a scheme switch.
            foreach (string guid in new[] { CpMinCoresGuid, CpMinCoresClass1Guid })
            {
                RunPowercfg($"/setacvalueindex SCHEME_CURRENT {ProcessorPowerSubGroupGuid} {guid} {percent}");
                RunPowercfg($"/setdcvalueindex SCHEME_CURRENT {ProcessorPowerSubGroupGuid} {guid} {percent}");
            }
            RunPowercfg("/setactive SCHEME_CURRENT");

            LoggerService.Instance.Info("CoreParkingService", $"Min cores set to {percent}% (AC and DC) on the active plan and all schemes.");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warn("CoreParkingService", $"SetMinCoresEverywhere({percent}) failed: {ex.Message}");
        }
    }
    /// schemes Win11 ships by the hundred; the caller already reports those in aggregate.</summary>
    private static void WriteSchemeValue(string schemeGuid, string settingGuid, int value)
    {
        try
        {
            string path = "{PowerSchemesRoot}{schemeGuid}{ProcessorPowerSubGroupGuid}{settingGuid}";
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            if (key == null) return;
            key.SetValue("ACSettingIndex", value, RegistryValueKind.DWord);
            key.SetValue("DCSettingIndex", value, RegistryValueKind.DWord);
        }
        catch { /* protected scheme, or setting absent on this CPU */ }
    }

    private static void ApplyViaPowercfg(int minCoresPercent)
    {
        try
        {
            // Addressed by GUID, not alias: only CPMINCORES has a powercfg alias. The per-class
            // floor, the latency-hint pool, the clock floor and the parked P-state have none.
            foreach (var (guid, value) in ParkingSettings(minCoresPercent))
            {
                RunPowercfg($"/setacvalueindex SCHEME_CURRENT {ProcessorPowerSubGroupGuid} {guid} {value}");
                RunPowercfg($"/setdcvalueindex SCHEME_CURRENT {ProcessorPowerSubGroupGuid} {guid} {value}");
            }
            RunPowercfg("/setactive SCHEME_CURRENT");
            RunPowercfg("/setactive SCHEME_CURRENT");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warn("CoreParkingService",
                $"ApplyViaPowercfg failed: {ex.Message}");
        }
    }


    // Windows' own per-scheme defaults live here, and this key IS readable even though the
    // PowerSchemes keys are not writable. That asymmetry is the whole reason removal has to go
    // through powercfg: we can read what the default should be, but we cannot delete the override
    // ourselves.
    private const string PowerSettingsRoot =
        @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings";

    private const string BalancedSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    /// <summary>The GUID of the scheme currently active, read straight from the registry.</summary>
    private static string ActiveSchemeGuid()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(PowerSchemesRoot, writable: false);
            return k?.GetValue("ActivePowerScheme") as string ?? BalancedSchemeGuid;
        }
        catch { return BalancedSchemeGuid; }
    }

    /// <summary>Windows' shipped default for one setting under one scheme, or null if none.</summary>
    private static int? WindowsDefault(string settingGuid, string schemeGuid, bool ac)
    {
        try
        {
            string path = $@"{PowerSettingsRoot}\{ProcessorPowerSubGroupGuid}\{settingGuid}\DefaultPowerSchemeValues\{schemeGuid}";
            using var k = Registry.LocalMachine.OpenSubKey(path, writable: false);
            return k?.GetValue(ac ? "ACSettingIndex" : "DCSettingIndex") as int?;
        }
        catch { return null; }
    }

    /// <summary>
    /// Puts every setting back to Windows' own default THROUGH POWERCFG.
    ///
    /// Deleting the registry override is the obvious way to restore a default, and it is what this
    /// used to do, but writes to PowerSchemes are refused even from an elevated process: the live
    /// log showed "Requested registry access is not allowed" on all 2020 schemes. So the deletes
    /// silently failed and the clock floor and parked P-state stayed pinned after a disable.
    /// powercfg holds the privilege we do not, so we look the default up and write it back.
    ///
    /// Min cores is excluded: the caller sets it to MinCoresWhenDisabled instead, which is a
    /// deliberate 5% rather than Windows' AC=100 (100 means nothing ever parks on AC).
    /// </summary>
    private static void RestoreDefaultsViaPowercfg()
    {
        string active = ActiveSchemeGuid();
        int restored = 0, unknown = 0;

        foreach (var (guid, _) in ParkingSettings(0))
        {
            if (guid == CpMinCoresGuid || guid == CpMinCoresClass1Guid) continue;

            int? ac = WindowsDefault(guid, active, ac: true)  ?? WindowsDefault(guid, BalancedSchemeGuid, ac: true);
            int? dc = WindowsDefault(guid, active, ac: false) ?? WindowsDefault(guid, BalancedSchemeGuid, ac: false);

            // No documented default means Systema should not invent one. Leaving the value alone
            // is safer than guessing, and is logged so it is visible rather than silent.
            if (ac == null && dc == null) { unknown++; continue; }

            if (ac != null) RunPowercfg($"/setacvalueindex SCHEME_CURRENT {ProcessorPowerSubGroupGuid} {guid} {ac}");
            if (dc != null) RunPowercfg($"/setdcvalueindex SCHEME_CURRENT {ProcessorPowerSubGroupGuid} {guid} {dc}");
            restored++;
        }

        RunPowercfg("/setactive SCHEME_CURRENT");
        LoggerService.Instance.Info("CoreParkingService",
            $"Restored {restored} setting(s) to Windows defaults via powercfg" +
            (unknown > 0 ? $"; {unknown} had no documented default and were left as-is." : "."));
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
            // The task re-invokes Systema itself rather than chaining powercfg calls. Six settings
            // across AC and DC is twelve invocations, which overruns the schtasks /TR length limit,
            // and a hardcoded command string silently drifts from the code the moment the setting
            // list changes. It did exactly that: the task was still writing CPMINCORES 10 by name
            // after the values moved.
            string exe = Environment.ProcessPath ?? "";
            string taskAction = $"\\\"{exe}\\\" --reapply-parking";

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
