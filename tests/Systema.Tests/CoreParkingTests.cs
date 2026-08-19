using System;
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
        int apply  = src.IndexOf("private static int ApplyCoreParking", StringComparison.Ordinal);
        int remove = src.IndexOf("private static int RemoveCoreParkingOverrides", StringComparison.Ordinal);
        Assert.True(apply > 0 && remove > 0);

        Assert.Contains("ParkingSettingGuids", src[apply..(apply + 3000)]);
        Assert.Contains("ParkingSettingGuids", src[remove..(remove + 3000)]);
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
