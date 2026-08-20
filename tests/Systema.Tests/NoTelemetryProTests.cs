using System;
using System.IO;
using Xunit;

namespace Systema.Tests;

public class NoTelemetryProTests
{
    private static string Service()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(dir, "src", "Systema", "Services", "ServiceControlService.cs");
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new FileNotFoundException("ServiceControlService.cs not found");
    }

    [Fact]
    public void CoversDataUsageAndIntelCollectors()
    {
        var src = Service();
        int list = src.IndexOf("ExtraTelemetryServices =", StringComparison.Ordinal);
        Assert.True(list > 0);
        var block = src[list..(src.IndexOf("};", list, StringComparison.Ordinal))];

        Assert.Contains("DusmSvc", block);                 // Data Usage
        Assert.Contains("IntelCollectorService", block);   // Intel(R) Collector Service
        Assert.Contains("IntelTelemetryAgent", block);     // Intel(R) Telemetry Agent Service
    }

    // The list used to be all demand-start services, so restore wrote a flat Manual (3). It no
    // longer is: Data Usage and the Intel telemetry agent ship as Automatic, and writing 3 back
    // would silently demote them instead of restoring them.
    [Fact]
    public void RestoreUsesTheCapturedValueNotAFlatManual()
    {
        var src = Service();
        int m = src.IndexOf("private static void SetExtraTelemetryServices", StringComparison.Ordinal);
        Assert.True(m > 0);
        var body = src[m..(m + 2000)];

        Assert.Contains("CaptureServiceDefault(name, current)", body);
        Assert.Contains("ResolveRestoreStart(name)", body);
        Assert.DoesNotContain("disable ? 4 : 3", body);
    }

    // Intel services do not exist on AMD machines; the routine must skip rather than throw.
    [Fact]
    public void MissingServicesAreSkippedNotFatal()
    {
        var src = Service();
        int m = src.IndexOf("private static void SetExtraTelemetryServices", StringComparison.Ordinal);
        Assert.Contains("if (key == null) continue;", src[m..(m + 2000)]);
    }
}
