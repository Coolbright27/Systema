// ════════════════════════════════════════════════════════════════════════════
// ServiceAndAudioLogicTests.cs
// Unit tests for the pure decision logic in the most heavily-edited services:
//   • ServiceControlService — service restore-to-default map, start-mode arg
//     mapping, and telemetry edition clamping.
//   • AudioService          — effect-list name matching across BOTH FX property
//     sets (the "not all enhancements disabled" root cause).
//
// These are the pieces that decide what actually gets written to Windows, so a
// silent change here (e.g. a service defaulting to the wrong Start value on OFF,
// or the composite-FX GUID dropping out of IsFxListName) would regress real
// behaviour with no compiler error. Registry-touching code isn't exercised here;
// only the pure logic that feeds it.
// ════════════════════════════════════════════════════════════════════════════

using Systema.Services;

namespace Systema.Tests;

public class ServiceAndAudioLogicTests
{
    // ── ServiceControlService.GetDefaultStart ────────────────────────────────
    // Start values: 2 = Automatic, 3 = Manual (demand), 4 = Disabled.
    // These are the Windows defaults a service is restored to when Service Cleanup
    // is toggled OFF and no captured original exists. Getting one wrong means a
    // service that should come back on (e.g. Spooler → printing) stays down.

    [Theory]
    [InlineData("SysMain")]
    [InlineData("WSearch")]
    [InlineData("Spooler")]
    [InlineData("MapsBroker")]
    [InlineData("PcaSvc")]
    [InlineData("TrkWks")]
    [InlineData("DiagTrack")]
    [InlineData("DoSvc")]
    [InlineData("BITS")]
    public void GetDefaultStart_AutomaticServices_ReturnAuto(string service)
        => Assert.Equal(2, ServiceControlService.GetDefaultStart(service));

    [Theory]
    [InlineData("RemoteRegistry")]
    [InlineData("RemoteAccess")]
    [InlineData("NetTcpPortSharing")]
    public void GetDefaultStart_DisabledByDefaultServices_ReturnDisabled(string service)
        => Assert.Equal(4, ServiceControlService.GetDefaultStart(service));

    [Theory]
    [InlineData("SomeUnknownService")]
    [InlineData("Fax")]
    [InlineData("")]
    public void GetDefaultStart_UnknownServices_FallBackToManual(string service)
        => Assert.Equal(3, ServiceControlService.GetDefaultStart(service));

    [Fact]
    public void GetDefaultStart_IsCaseInsensitive()
    {
        // The map is built with StringComparer.OrdinalIgnoreCase — sc.exe / registry
        // names arrive in mixed case, so "sysmain" must resolve like "SysMain".
        Assert.Equal(2, ServiceControlService.GetDefaultStart("sysmain"));
        Assert.Equal(4, ServiceControlService.GetDefaultStart("remoteregistry"));
    }

    // ── ServiceControlService.StartModeArg ───────────────────────────────────
    // Maps a numeric Start value to the sc.exe "config start=" argument. Anything
    // that isn't Auto(2)/Disabled(4) must fall through to demand (Manual).

    [Theory]
    [InlineData(2, "auto")]
    [InlineData(4, "disabled")]
    [InlineData(3, "demand")]
    [InlineData(0, "demand")]
    [InlineData(1, "demand")]
    [InlineData(99, "demand")]
    public void StartModeArg_MapsStartValueToScArgument(int start, string expected)
        => Assert.Equal(expected, ServiceControlService.StartModeArg(start));

    // ── ServiceControlService.IsFullTelemetryOffEdition ──────────────────────
    // Only Enterprise / Education / IoT / Server honour AllowTelemetry=0 as the
    // full "Security" off level. Home and Pro floor at "Required", so the toggle
    // must NOT claim a full-off on those editions.

    [Theory]
    [InlineData("Enterprise")]
    [InlineData("EnterpriseS")]
    [InlineData("Education")]
    [InlineData("ProfessionalEducation")]
    [InlineData("IoTEnterprise")]
    [InlineData("ServerStandard")]
    public void IsFullTelemetryOffEdition_TrueForHonouringEditions(string edition)
        => Assert.True(ServiceControlService.IsFullTelemetryOffEdition(edition));

    [Theory]
    [InlineData("Professional")]
    [InlineData("Core")]
    [InlineData("CoreSingleLanguage")]
    [InlineData("CoreN")]
    [InlineData("")]
    public void IsFullTelemetryOffEdition_FalseForHomeAndPro(string edition)
        => Assert.False(ServiceControlService.IsFullTelemetryOffEdition(edition));

    [Fact]
    public void IsFullTelemetryOffEdition_NullEdition_IsFalse()
        => Assert.False(ServiceControlService.IsFullTelemetryOffEdition(null!));

    // ── AudioService.IsFxListName ────────────────────────────────────────────
    // The "not all enhancements disabled" bug: Realtek/Waves park APOs in the
    // composite-FX property set {d3993a3f...}, not just the plain {d04e05a6...}
    // set. IsFxListName must recognise BOTH, and only when the fmtid is followed
    // by the "," slot separator.

    [Theory]
    [InlineData("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},13")]   // plain FX set, speaker slot
    [InlineData("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},17")]   // plain FX set, mic slot
    [InlineData("{d3993a3f-99c2-4402-b5ec-a92a0367664b},5")]    // composite FX set (Realtek/Waves)
    [InlineData("{D3993A3F-99C2-4402-B5EC-A92A0367664B},9")]    // uppercase — must still match
    public void IsFxListName_MatchesBothFxPropertySets(string name)
        => Assert.True(AudioService.IsFxListName(name));

    [Theory]
    [InlineData("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}")]      // no "," slot separator
    [InlineData("{00000000-0000-0000-0000-000000000000},5")]    // NullClsid fmtid, not an FX set
    [InlineData("SomethingElse,13")]
    [InlineData("")]
    public void IsFxListName_RejectsNonFxNames(string name)
        => Assert.False(AudioService.IsFxListName(name));

    // ── AudioService.VendorAudioServices allowlist safety ────────────────────
    // "Disable all audio enhancements" stops these services. If a core audio service ever slipped
    // into this list it would kill all sound, so this test is a hard guard against that mistake.

    [Fact]
    public void VendorAudioServices_NeverIncludeCoreAudioServices()
    {
        // Stopping any of these breaks or destabilises all audio — they must never be in the allowlist.
        // (IntelAudioService is deliberately NOT here: it's an included, tested-safe vendor DSP service.)
        string[] forbidden =
        {
            "Audiosrv", "AudioSrv", "AudioEndpointBuilder",
            "RpcSs", "DcomLaunch", "MMCSS", "AudioSes",
        };
        foreach (var svc in AudioService.VendorAudioServices)
            foreach (var bad in forbidden)
                Assert.False(string.Equals(svc, bad, StringComparison.OrdinalIgnoreCase),
                    $"'{svc}' is a core audio service and must not be in VendorAudioServices");
    }

    [Fact]
    public void VendorAudioServices_AreOnlyKnownVendorEnhancementServices()
    {
        // Every entry must be a recognised vendor DSP/enhancement service. Adding anything else
        // (especially a driver/core service) requires updating this list deliberately.
        string[] allowed =
        {
            "WavesSysSvc", "WavesAudioService", "RtkAudioUniversalService",
            "RtkAudioService", "NahimicService", "IntelAudioService",
            // Waves/MaxxAudio naming variants seen on other OEM builds. WavesSysSvc64 is the
            // same Waves Audio Service under a 64-bit name; MaxxAudioAnalytics is Dell's
            // MaxxAudio companion/telemetry service. Both are enhancement-layer only — neither
            // carries the audio path, so stopping them cannot silence a device.
            "WavesSysSvc64", "MaxxAudioAnalytics",
        };
        foreach (var svc in AudioService.VendorAudioServices)
            Assert.Contains(svc, allowed);
    }

    [Fact]
    public void VendorAudioAgentProcesses_NeverIncludeCoreOrSystemProcesses()
    {
        // "Disable all audio enhancements" kills these processes. A core/system process here would be
        // catastrophic (audiodg = the audio engine; svchost hosts core services), so guard against it.
        string[] forbidden =
        {
            "audiodg", "svchost", "Audiosrv", "csrss", "wininit", "winlogon",
            "services", "lsass", "explorer", "System", "smss",
        };
        foreach (var p in AudioService.VendorAudioAgentProcesses)
            foreach (var bad in forbidden)
                Assert.False(string.Equals(p, bad, StringComparison.OrdinalIgnoreCase),
                    $"'{p}' is a core/system process and must not be in VendorAudioAgentProcesses");
    }

    // ── GraphicsTweaksService.GraphicsMmcssTasks separation guard ────────────
    [Fact]
    public void GraphicsMmcssTasks_NeverIncludeAudioTasks()
    {
        // The Audio / Pro Audio MMCSS tasks belong to the audio tab's "Priority audio scheduling"
        // toggle. The graphics scheduling feature must never write them, or the two toggles would
        // fight over the same registry keys. ("Window Manager"/DWM IS included, by owner's request.)
        string[] forbidden = { "Audio", "Pro Audio" };
        foreach (var task in GraphicsTweaksService.GraphicsMmcssTasks)
            foreach (var bad in forbidden)
                Assert.False(string.Equals(task, bad, StringComparison.OrdinalIgnoreCase),
                    $"'{task}' is an audio task and must not be in GraphicsMmcssTasks");
    }
}
