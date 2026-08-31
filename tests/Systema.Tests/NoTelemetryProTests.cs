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
    [Fact]
    public void CoversTheIntelUsageReportPairAndMicrosoftInsights()
    {
        var src = Service();
        int list = src.IndexOf("ExtraTelemetryServices =", StringComparison.Ordinal);
        var block = src[list..(src.IndexOf("};", list, StringComparison.Ordinal))];

        Assert.Contains("SystemUsageReportSvc", block);   // Intel System Usage Report
        Assert.Contains("SUR QC SAM", block);             // its asset-manager companion
        Assert.Contains("wuqisvc", block);                // Microsoft Usage and Quality Insights
    }

    // The SystemUsageReportSvc suffix is an Intel platform codename and differs by machine, so a
    // literal match would silently miss the service on other Intel PCs.
    [Fact]
    public void TheUsageReportServiceIsMatchedByPrefix()
    {
        var src = Service();
        Assert.Contains("EffectiveExtraTelemetryServices", src);

        int m = src.IndexOf("private static IEnumerable<string> EffectiveExtraTelemetryServices", StringComparison.Ordinal);
        Assert.True(m > 0);
        var body = src[m..(m + 1200)];
        Assert.Contains("StartsWith(\"SystemUsageReportSvc\"", body);

        // ...and the expanded list is what actually gets used.
        int setter = src.IndexOf("private static void SetExtraTelemetryServices", StringComparison.Ordinal);
        Assert.Contains("EffectiveExtraTelemetryServices()", src[setter..(setter + 400)]);
    }

    // With no captured value, restore must land on Manual: a service that starts on demand is
    // the safe unknown, never Automatic.
    [Fact]
    public void RestoreFallsBackToManualWhenNothingWasCaptured()
    {
        var src = Service();
        int m = src.IndexOf("internal static int GetDefaultStart", StringComparison.Ordinal);
        Assert.True(m > 0);
        Assert.Contains(": 3", src[m..(m + 200)]);
    }


    // NVIDIA's telemetry ships as two DLLs loaded on demand by driver processes, with no service
    // and no scheduled task, so the opt-out registry value is the only lever that exists. Absent
    // means "never asked", which the driver treats as opted in.
    [Fact]
    public void CoversTheNvidiaTelemetryOptOut()
    {
        var src = Service();
        Assert.Contains("OptInOrOutPreference", src);
        Assert.Contains(@"SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client", src);
    }

    // A Microsoft task under \Microsoft\Windows\Sustainability\, not an NVIDIA one despite turning
    // up while auditing GPU telemetry.
    [Fact]
    public void CoversWindowsSustainabilityTelemetry()
    {
        var src = Service();
        Assert.Contains(@"\Microsoft\Windows\Sustainability\SustainabilityTelemetry", src);
    }

    // Restore used to run /Enable on every task unconditionally, which is not a restore: a task
    // the user had disabled themselves came back the first time they toggled No Telemetry Pro off.
    // The services path already captured each original Start value; tasks now match that standard.
    [Fact]
    public void TaskRestorePutsBackWhatTheUserHadNotABlanketEnable()
    {
        var src = Service();
        Assert.Contains("CaptureTaskDefault", src);
        Assert.Contains("TaskWasUserDisabled", src);
        Assert.Contains("IsTaskAlreadyDisabled", src);

        int m = src.IndexOf("private static void SetTelemetryTasks", StringComparison.Ordinal);
        Assert.True(m > 0);
        var body = src[m..(m + 2500)];

        // Capture happens on the way IN, and the way OUT skips tasks that were already off.
        Assert.Contains("CaptureTaskDefault(task, IsTaskAlreadyDisabled(task))", body);
        Assert.Contains("TaskWasUserDisabled(task)", body);
    }

    // Reading the pipe after WaitForExit can deadlock when the buffer fills, which is the same
    // mistake documented elsewhere in this file for schtasks calls.
    [Fact]
    public void TheTaskStateQueryReadsItsPipeBeforeWaiting()
    {
        var src = Service();
        int m = src.IndexOf("private static bool IsTaskAlreadyDisabled", StringComparison.Ordinal);
        Assert.True(m > 0);
        var body = src[m..(m + 1200)];

        int read = body.IndexOf("ReadToEnd()", StringComparison.Ordinal);
        int wait = body.IndexOf("WaitForExit", StringComparison.Ordinal);
        Assert.True(read > 0 && wait > read, "must read stdout before WaitForExit or the pipe can deadlock");
    }
}
