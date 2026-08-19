using System;
using System.Linq;
using System.IO;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Core parking is driven by SIX power settings, not one. Setting only min-cores does not park
/// deeply, because the other knobs re-float or hold up the very cores it just released.
/// </summary>
public class CoreParkingTests
{
    private static string Read(params string[] parts)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(new[] { dir }.Concat2(parts));
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    private static readonly string[] AllSettingNames =
    {
        "CpMinCoresGuid", "CpMinCoresClass1Guid",
        "CpLatencyHintMinUnparkedGuid", "CpLatencyHintMinUnparkedClass1Guid",
        "ProcThrottleMinGuid", "ProcThrottleMinClass1Guid",
        "CpParkedPerfStateGuid", "CpParkedPerfStateClass1Guid",
    };

    private static string Service() => Read("src", "Systema", "Services", "CoreParkingService.cs");
    private static string App()     => Read("src", "Systema", "App.xaml.cs");

    [Fact]
    public void ParkingDrivesEveryFloorToZero()
    {
        var src = Service();

        // The floor is 0: nothing has to stay awake, nothing is held ready, clocks may drop fully.
        Assert.Contains("minCoresPercent: 0", src);
        Assert.DoesNotContain("minCoresPercent: 10", src);

        // E-core class, the latency-hint "ready" pool, and the clock floor all move together.
        Assert.Contains("CpMinCoresClass1Guid", src);
        Assert.Contains("CpLatencyHintMinUnparkedGuid", src);
        Assert.Contains("ProcThrottleMinGuid", src);
    }

    // Apply and remove used to enumerate different setting lists, so disabling left the clock
    // floor and the latency-hint pool pinned at 0 permanently. One list now feeds both.
    [Fact]
    public void ApplyAndRemoveUseTheSameSettingList()
    {
        var src = Service();

        // Apply writes (guid, value) pairs; remove only needs the guids. Different shapes, but
        // they must name the SAME settings, or a disable leaves some of them written forever.
        string[] Names(string declStart)
        {
            int i = src.IndexOf(declStart, StringComparison.Ordinal);
            Assert.True(i > 0, declStart + " not found");
            int open = src.IndexOf('{', i);
            int close = src.IndexOf("};", open);
            var block = src[open..close];
            return AllSettingNames.Where(n => block.Contains(n, StringComparison.Ordinal))
                                  .OrderBy(n => n).ToArray();
        }

        var applied = Names("private static (string Guid, int Value)[] ParkingSettings");
        var removed = Names("private static readonly string[] ParkingSettingGuids");

        Assert.NotEmpty(applied);
        Assert.Equal(applied, removed);
    }

    // The boot task used to run a hardcoded "CPMINCORES 10" powercfg string. It kept enforcing
    // that stale value after the code moved on, so the task must call back into the service.
    [Fact]
    public void TheBootTaskCannotEnforceAStaleValue()
    {
        var src = Service();
        Assert.DoesNotContain("SUB_PROCESSOR CPMINCORES 10", src);
        Assert.Contains("--reapply-parking", src);

        // ...and the flag has to actually be handled, or the task is a no-op.
        Assert.Contains("--reapply-parking", App());
        Assert.Contains("ReapplyCoreParkingAsync", App());
    }
    // Parked performance state is an ENUM, not a percentage: 0 = No Preference, 1 = Deepest,
    // 2 = Lightest. Writing the shared 0 floor here would silently mean "no preference" and do
    // nothing at all, which is the same trap the old CPMINCORES = 0 comment warned about.
    [Fact]
    public void ParkedPerformanceStateUsesItsOwnEnumNotTheFloor()
    {
        var src = Service();
        Assert.Contains("CpParkedPerfStateGuid", src);
        Assert.Contains("ParkedPerfDeepest", src);
        Assert.Contains("ParkedPerfDeepest           = 1", src);

        // It must be paired with its own value, never handed the floor.
        Assert.Contains("(CpParkedPerfStateGuid,              ParkedPerfDeepest)", src);
        Assert.DoesNotContain("(CpParkedPerfStateGuid,              floorPercent)", src);
    }

    // Removal still has to cover it, or disabling leaves parked cores pinned to Deepest.
    [Fact]
    public void RemovalCoversTheParkedPerformanceState()
    {
        var src = Service();
        int decl = src.IndexOf("private static readonly string[] ParkingSettingGuids", StringComparison.Ordinal);
        Assert.True(decl > 0);
        Assert.Contains("CpParkedPerfStateGuid", src[decl..(decl + 500)]);
    }

    // Off does NOT mean "Windows default" for min cores. Balanced ships AC=100, and 100 means
    // nothing is ever parked while plugged in. 5 keeps light parking instead of none.
    [Fact]
    public void TurningItOffLeavesMinCoresAtFivePercent()
    {
        var src = Service();
        Assert.Contains("MinCoresWhenDisabled       = 5", src);

        int off = src.IndexOf("DisableForcedCoreParking", StringComparison.Ordinal);
        Assert.True(off > 0);
        Assert.Contains("SetMinCoresEverywhere(MinCoresWhenDisabled)", src[off..(off + 1500)]);
    }

    // Both AC and DC, on the active plan and every scheme.
    [Fact]
    public void MinCoresIsWrittenForBothAcAndDcEverywhere()
    {
        var src = Service();
        int m = src.IndexOf("public static void SetMinCoresEverywhere", StringComparison.Ordinal);
        Assert.True(m > 0);

        var body = src[m..(m + 2000)];
        Assert.Contains("setacvalueindex", body);
        Assert.Contains("setdcvalueindex", body);
        Assert.Contains("SCHEME_CURRENT", body);      // the active plan
        Assert.Contains("GetSubKeyNames", body);      // ...and all of them
    }

    // Max Life is a battery mode, not a parking mode, so it has to drive min cores itself
    // rather than depending on the Core Efficiency toggle being on.
    [Fact]
    public void MaxBatteryLifeParksIndependentlyOfTheCoreEfficiencyToggle()
    {
        var vm = Read("src", "Systema", "ViewModels", "VisualViewModel.cs");

        // Every Max Life path must set it, not just the button.
        int calls = 0;
        for (int i = vm.IndexOf("SetMinCoresEverywhere(0)", StringComparison.Ordinal); i >= 0;
                 i = vm.IndexOf("SetMinCoresEverywhere(0)", i + 1, StringComparison.Ordinal)) calls++;
        Assert.True(calls >= 2, "expected every Max Life path to park; found " + calls);
    }

    // Deleting the registry override looks like the way to restore a default, but writes to
    // PowerSchemes are refused even elevated ("Requested registry access is not allowed" on all
    // 2020 schemes in the live log). The deletes silently failed, so the clock floor and parked
    // P-state stayed pinned after a disable. Restoration has to go through powercfg.
    [Fact]
    public void DisableRestoresDefaultsThroughPowercfgNotRegistryDeletes()
    {
        var src = Service();
        Assert.Contains("RestoreDefaultsViaPowercfg", src);

        int off = src.IndexOf("DisableForcedCoreParking", StringComparison.Ordinal);
        Assert.Contains("RestoreDefaultsViaPowercfg();", src[off..(off + 2000)]);

        int m = src.IndexOf("private static void RestoreDefaultsViaPowercfg", StringComparison.Ordinal);
        Assert.True(m > 0);
        var body = src[m..(m + 2500)];

        // Reads the real default rather than inventing one, and writes both rails.
        Assert.Contains("WindowsDefault(", body);
        Assert.Contains("setacvalueindex", body);
        Assert.Contains("setdcvalueindex", body);
    }

    // Every setting apply touches must be restorable, or "off" leaves something behind.
    [Fact]
    public void RestorationCoversEverySettingApplyWrites()
    {
        var src = Service();
        int m = src.IndexOf("private static void RestoreDefaultsViaPowercfg", StringComparison.Ordinal);
        var body = src[m..(m + 2500)];

        // It iterates the same list apply uses, rather than a hand-copied subset that can drift.
        Assert.Contains("ParkingSettings(", body);

        // Min cores is the one deliberate exception: the caller sets it to 5 instead.
        Assert.Contains("MinCoresWhenDisabled", src);
    }

    // Windows ships min cores asymmetric on hybrid: class 0 (E-cores) AC=100, class 1 (P-cores)
    // AC=0. Keep the cheap cores available, park the expensive ones. Driving BOTH to 0 parks the
    // E-cores too, so background work lands on a P-core instead and draws MORE power for the same
    // work. The floor stays small (10, about one core on an 8-E-core chip) so the rest still park.
    [Fact]
    public void HybridKeepsSomeEcoresAwakeWhilePcoresParkFreely()
    {
        var src = Service();
        Assert.Contains("HybridEcoreMinCores", src);
        Assert.Contains("IsHybridCpu", src);

        int m = src.IndexOf("ParkingSettings(int floorPercent)", StringComparison.Ordinal);
        Assert.True(m > 0);
        var body = src[m..(m + 1400)];

        // Class 0 gets the hybrid-aware value; class 1 always gets the full floor.
        Assert.Contains("IsHybridCpu()) ? HybridEcoreMinCores : floorPercent", body);
        Assert.Contains("(CpMinCoresClass1Guid,               floorPercent)", body);

        // Non-hybrid must be unaffected: every core is class 0 there.
        Assert.Contains("floorPercent == 0 && IsHybridCpu()", body);
    }

}

internal static class PathExt
{
    public static string[] Concat2(this string[] head, string[] tail)
    {
        var all = new string[head.Length + tail.Length];
        head.CopyTo(all, 0);
        tail.CopyTo(all, head.Length);
        return all;
    }
}
