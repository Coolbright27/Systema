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
