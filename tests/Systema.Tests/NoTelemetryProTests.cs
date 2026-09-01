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

    // Edge is baked into Windows and its policies also govern WebView2, which Discord, Teams and
    // Office embed, so this reaches well past browsing.
    [Fact]
    public void CoversEdgeTelemetryPolicies()
    {
        var src = Service();
        foreach (var p in new[]
                 { "MetricsReportingEnabled", "SendSiteInfoToImproveServices",
                   "PersonalizationReportingEnabled", "DiagnosticData", "UserFeedbackAllowed",
                   "AlternateErrorPagesEnabled", "ResolveNavigationErrorsUseWebService",
                   "SpotlightExperiencesAndRecommendationsEnabled", "ShowRecommendationsEnabled",
                   "EdgeShoppingAssistantEnabled", "WebWidgetAllowed" })
            Assert.Contains(p, src);
    }

    // SmartScreen is SECURITY, not telemetry. Every "debloat Edge" guide turns it off, which makes
    // it a standing temptation — on a machine that already fights Smart App Control over unsigned
    // binaries, disabling it trades a real protection for nothing. It must never become a policy row.
    [Fact]
    public void SmartScreenIsNeverDisabled()
    {
        var src = Service();
        int list = src.IndexOf("TelemetryRegistry =", StringComparison.Ordinal);
        Assert.True(list > 0);
        int end = src.IndexOf("};", list, StringComparison.Ordinal);

        // Only actual policy rows count; the comment explaining the exclusion is fine.
        var rows = src[list..end]
            .Split('\n')
            .Where(l => l.TrimStart().StartsWith("(true", StringComparison.Ordinal) ||
                        l.TrimStart().StartsWith("(false", StringComparison.Ordinal));

        Assert.DoesNotContain(rows, l => l.Contains("SmartScreen", StringComparison.Ordinal));
    }

    // DiagTrack and dmwappushservice were the last pair still restored to a HARDCODED default
    // rather than the captured original, so a user who had already disabled DiagTrack got it
    // switched back to Automatic the first time they toggled No Telemetry Pro off.
    [Fact]
    public void TelemetryServicesRestoreTheCapturedValue()
    {
        var src = Service();

        int off = src.IndexOf("public async Task<TweakResult> RestoreTelemetryServicesAsync", StringComparison.Ordinal);
        Assert.True(off > 0);
        var restore = src[off..(off + 1400)];

        Assert.Contains("ResolveRestoreStart(svcName)", restore);
        Assert.DoesNotContain("GetDefaultStart(svcName)", restore);

        // ...and the capture happens on the way in, or there is nothing to resolve.
        int on = src.IndexOf("DisableAllTelemetryServicesAsync", StringComparison.Ordinal);
        Assert.Contains("CaptureServiceDefault(svcName, cur)", src[on..(on + 3000)]);
    }

    // If the capture is missing or unusable, restore must fall back to the documented Windows
    // default rather than leaving the service disabled. For DiagTrack that default is 2
    // (Automatic), i.e. back ON, which is the safe direction: never leave something off that
    // Windows ships on because a capture went missing.
    [Fact]
    public void AMissingCaptureFallsBackToTheWindowsDefault()
    {
        var src = Service();
        int m = src.IndexOf("private static int ResolveRestoreStart", StringComparison.Ordinal);
        Assert.True(m > 0);
        var body = src[m..(m + 1200)];

        Assert.Contains("return GetDefaultStart(serviceName);", body);   // the fallback
        Assert.Contains("saved is 2 or 3", body);                        // only real values accepted
        Assert.Contains("[\"DiagTrack\"] = 2", src);                     // and that default is ON
    }

    // Windows feature updates re-enable DiagTrack and reset the DataCollection policies. The
    // toggle reads LIVE state, so it would quietly show OFF again and stay that way until the
    // user noticed. Intent has to be persisted separately from current state for anything to
    // know it should re-apply.
    [Fact]
    public void TheUsersIntentIsPersistedSeparatelyFromLiveState()
    {
        var svc = Service();
        Assert.Contains("NoTelemetryProEnabled = on", svc);

        // Written BEFORE the work, so a crash midway still leaves startup able to finish it.
        int m = svc.IndexOf("SetNoTelemetryProAsync(bool on)", StringComparison.Ordinal);
        Assert.True(m > 0);
        var body = svc[m..(m + 900)];
        int intent = body.IndexOf("NoTelemetryProEnabled = on", StringComparison.Ordinal);
        int work   = body.IndexOf("SetTelemetryRegistry(off: true)", StringComparison.Ordinal);
        Assert.True(intent > 0 && work > intent, "intent must be persisted before the work starts");
    }

    [Fact]
    public void StartupReAppliesOnlyWhenWantedAndOnlyOnDrift()
    {
        string dir = AppContext.BaseDirectory;
        string? app = null;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(dir, "src", "Systema", "App.xaml.cs");
            if (File.Exists(p)) { app = File.ReadAllText(p); break; }
            dir = Directory.GetParent(dir)?.FullName!;
        }
        Assert.NotNull(app);

        int m = app!.IndexOf("NoTelemetryProEnabled", StringComparison.Ordinal);
        Assert.True(m > 0, "startup does not check the persisted intent");
        var body = app[m..(m + 1200)];

        // Gated on intent AND on drift, so a normal launch does no work at all.
        Assert.Contains("IsNoTelemetryProEnabled()", body);
        Assert.Contains("SetNoTelemetryProAsync(true)", body);
    }

    // Found by auditing the machine rather than working from a list. whesvc was the only
    // uncovered telemetry service actually RUNNING; wercplsupport is WerSvc's direct companion
    // and was covered only by luck on this box.
    [Fact]
    public void CoversWindowsHealthAndProblemReportsSupport()
    {
        var src = Service();
        int list = src.IndexOf("ExtraTelemetryServices =", StringComparison.Ordinal);
        var block = src[list..src.IndexOf("};", list, StringComparison.Ordinal)];

        Assert.Contains("whesvc", block);
        Assert.Contains("wercplsupport", block);
    }

    // A whole usage-data pipeline (collect, flush, receive, report) was Ready on a machine with
    // telemetry explicitly off. Windows ships BootstrapUsageDataReporting disabled itself, which
    // is a decent signal it considers these optional.
    [Fact]
    public void CoversTheFlightingUsageDataPipeline()
    {
        var src = Service();
        foreach (var t in new[] { "UsageDataReporting", "UsageDataFlushing", "UsageDataReceiver",
                                  "GovernedFeatureUsageProcessing" })
            Assert.Contains(@"\Microsoft\Windows\Flighting\FeatureConfig\" + t, src);
    }

    // NumberOfSIUFInPeriod=0 was already set, so without the client tasks the job was half done.
    [Fact]
    public void CoversTheWindowsFeedbackClient()
    {
        var src = Service();
        Assert.Contains(@"\Microsoft\Windows\Feedback\Siuf\DmClient", src);
        Assert.Contains("NumberOfSIUFInPeriod", src);   // the registry half, already present
    }

    // These power the network and audio troubleshooters. They are diagnostics, not reporting, and
    // disabling them means "Windows cannot diagnose this" the next time something breaks.
    [Fact]
    public void DiagnosticInfrastructureIsLeftAlone()
    {
        var src = Service();
        int list = src.IndexOf("ExtraTelemetryServices =", StringComparison.Ordinal);
        var block = src[list..src.IndexOf("};", list, StringComparison.Ordinal)];

        foreach (var keep in new[] { "\"DPS\"", "\"WdiServiceHost\"", "\"WdiSystemHost\"",
                                     "\"QWAVE\"", "\"LxpSvc\"" })
            Assert.DoesNotContain(keep, block);
    }

    // Drift detection used to check ONE registry value (AllowTelemetry) out of roughly
    // twenty-nine, and two services out of twelve. A Windows update could wipe every Edge policy
    // and the NVIDIA opt-out and re-enable whesvc, and the feature still reported itself enabled,
    // so the startup re-apply never fired. Re-enforcement is only as good as the check that
    // triggers it.
    [Fact]
    public void DriftDetectionChecksEveryValueNotJustOne()
    {
        var src = Service();
        int m = src.IndexOf("private static bool IsTelemetryRegistryOff", StringComparison.Ordinal);
        Assert.True(m > 0);
        var body = src[m..(m + 1400)];

        // Iterates the whole table rather than reading a single named value.
        Assert.Contains("foreach (var (hklm, path, name, offVal) in TelemetryRegistry)", body);
    }

    [Fact]
    public void DriftDetectionCoversTheExtraServicesToo()
    {
        var src = Service();
        Assert.Contains("AreExtraTelemetryServicesDisabled", src);

        int m = src.IndexOf("public bool IsNoTelemetryProEnabled", StringComparison.Ordinal);
        Assert.True(m > 0);
        var body = src[m..(m + 400)];

        Assert.Contains("IsTelemetryRegistryOff()", body);
        Assert.Contains("AreTelemetryServicesDisabled()", body);
        Assert.Contains("AreExtraTelemetryServicesDisabled()", body);
    }

    // A service that is not installed (the Intel entries on an AMD machine) must count as
    // "nothing to do", not as drift, or the feature would re-apply forever on those boxes.
    [Fact]
    public void AMissingServiceIsNotTreatedAsDrift()
    {
        var src = Service();
        int m = src.IndexOf("private static bool AreExtraTelemetryServicesDisabled", StringComparison.Ordinal);
        Assert.True(m > 0);
        Assert.Contains("if (key == null) continue;", src[m..(m + 900)]);
    }
}
