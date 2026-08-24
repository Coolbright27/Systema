using System;
using System.IO;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// The Dell BIOS diagnostics asked for properties that do not exist on those WMI classes, so both
/// queries returned "Invalid query" and neither ever produced output on any machine. Battery Pause
/// itself was unaffected (the queries are explicitly non-gating), but the diagnostic was dead.
/// </summary>
public class DellProbeTests
{
    private static string Service()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(dir, "src", "Systema", "Services", "BatteryPauseService.cs");
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new FileNotFoundException("BatteryPauseService.cs not found");
    }

    // Verified against the live schema: EnumerationAttribute exposes PossibleValue (singular),
    // IntegerAttribute exposes LowerBound/UpperBound rather than MinValue/MaxValue.
    [Fact]
    public void TheBiosQueriesUseRealPropertyNames()
    {
        var src = Service();

        Assert.Contains("SELECT CurrentValue, PossibleValue FROM EnumerationAttribute", src);
        Assert.DoesNotContain("PossibleValues FROM EnumerationAttribute", src);

        Assert.Contains("LowerBound, UpperBound FROM IntegerAttribute", src);
        Assert.DoesNotContain("MinValue, MaxValue FROM IntegerAttribute", src);

        // ...and the values are read back under those same names.
        Assert.Contains("item[\"PossibleValue\"]", src);
        Assert.Contains("item[\"LowerBound\"]", src);
        Assert.Contains("item[\"UpperBound\"]", src);
    }

    // Both failures are expected on BIOSes that do not expose these classes, and neither affects
    // whether Battery Pause works. Logging one at Warning made it the only warning in an otherwise
    // clean session, which reads as a fault in a working feature.
    [Fact]
    public void DiagnosticFailuresAreLoggedAtInfoNotWarning()
    {
        var src = Service();
        int probe = src.IndexOf("private sealed class DellModernMethod", StringComparison.Ordinal);
        Assert.True(probe > 0);

        int end = src.IndexOf("return BatteryPauseSupport.Supported;", probe, StringComparison.Ordinal);
        var body = src[probe..end];

        Assert.DoesNotContain("_log.Warn", body);
        Assert.Contains("IntegerAttribute enum skipped", body);
    }
}
