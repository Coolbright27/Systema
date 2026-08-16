// NvidiaPowerModeTests.cs
//
// "Power Management Mode" in the NVIDIA app is the driver PROFILE setting PREFERRED_PSTATE,
// written through NVAPI DRS — the same mechanism as Max Frame Rate. It is NOT the PowerMizer
// registry values in NvidiaGpuService.
//
// The distinction matters for a reason a user can feel: DRS applies IMMEDIATELY, PowerMizer needs
// a reboot. A separate on-battery mode is only possible because of that. The driver keeps ONE
// global value, so "a different mode on battery" exists purely because Systema swaps the value on
// the power-source transition. Implement it on PowerMizer instead and the battery selector becomes
// something that only takes effect after a restart, by which point you are on a different power
// source anyway.
//
// Source-scan (not reflection) because Application Control blocks loading Systema.dll into the
// test host on this machine — same constraint as the other *SourceTests.

using System.IO;
using System.Linq;
using Xunit;

namespace Systema.Tests;

public class NvidiaPowerModeTests
{
    private static string RepoRoot()
    {
        var asmDir = Path.GetDirectoryName(typeof(NvidiaPowerModeTests).Assembly.Location)!;
        // tests/Systema.Tests/bin/<Cfg>/<TFM>/  →  ../../../../../
        return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
    }

    private static string Read(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));
        Assert.True(File.Exists(path), $"Expected file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string Nvapi()     => Read("src", "Systema", "Services", "NvapiService.cs");
    private static string GpuService()=> Read("src", "Systema", "Services", "NvidiaGpuService.cs");
    private static string ViewModel() => Read("src", "Systema", "ViewModels", "NvidiaGpuViewModel.cs");
    private static string View()      => Read("src", "Systema", "Views", "NvidiaView.xaml");

    [Fact]
    public void TheModeListMatchesTheNvidiaAppsOwnDropdown()
    {
        var vm = ViewModel();

        // Verified against the NVIDIA app's Power management mode dropdown on a T1200. All five
        // it offers must be present — "Optimal power" was wrongly dropped once, on the mistaken
        // conclusion that it wasn't exposed.
        Assert.Contains("\"Optimal power\"", vm);
        Assert.Contains("\"Prefer maximum performance\"", vm);
        Assert.Contains("\"Adaptive\"", vm);
        Assert.Contains("\"NVIDIA driver-controlled (Default)\"", vm);
        Assert.Contains("\"Prefer consistent performance\"", vm);

        // Value 4 (PREFER_MIN) exists in NVAPI's headers but NVIDIA does not expose it, so it
        // must not appear — offering a mode the vendor's own UI hides is how a user ends up on a
        // pstate nobody can name.
        Assert.DoesNotContain("PStateMinPower", vm);
        Assert.DoesNotContain("Prefer minimum power", vm);
    }

    [Fact]
    public void PreTuringCardsDoNotGetTheTuringOnlyModes()
    {
        var vm = ViewModel();
        var nv = Nvapi();

        // NVIDIA's own app gates two of the five on Turing and newer — discovered by reading its
        // localisation file, which ships separate help text keyed "typicalUsageScenariosTuring".
        // Architecture is read from the driver, not parsed out of the card's name, because names
        // misfile rebadged and mobile parts.
        Assert.Contains("public uint GetGpuArchitecture()", nv);
        Assert.Contains("public const uint ArchTuring = 0x160;", nv);

        Assert.Contains("arch >= NvapiService.ArchTuring", vm);
        Assert.Contains("m.Value != NvapiService.PStateDriverManaged", vm);
        Assert.Contains("m.Value != NvapiService.PStateConsistentPerf", vm);

        // Unknown architecture must keep the full list — hiding a mode the user has is the worse
        // failure, and it is the one that already happened once.
        Assert.Contains("arch == 0 || arch >= NvapiService.ArchTuring", vm);
    }

    [Fact]
    public void AFallbackDefaultCannotDefeatTheArchitectureFilter()
    {
        var vm = ViewModel();
        var nv = Nvapi();

        // Observed on a GTX 1060 (Pascal, arch 0x130): the filter correctly excluded mode 2, then
        // the "current mode must stay selectable" branch put it straight back — because the
        // not-present fallback WAS 2. A default is not evidence of what the card is set to.
        Assert.Contains("public uint GetPowerMode(out bool present)", nv);
        Assert.Contains("present = true;", nv);
        Assert.Contains("if (settingPresent && shown.All(m => m.Value != live))", vm);
    }

    [Fact]
    public void TheUnsetFallbackIsNvidiasOwnDefault()
    {
        // PREFERRED_PSTATE_DEFAULT is OPTIMAL_POWER. It is also valid on every architecture, so
        // it cannot re-introduce a Turing-only mode on an older card the way DriverManaged did.
        var nv = Nvapi();
        Assert.DoesNotContain("return PStateDriverManaged;", nv);
        Assert.Contains("return PStateOptimalPower;", nv);
    }

    [Fact]
    public void AMismatchReportArrivesWithTheDataNeededToActOnIt()
    {
        // The list mirrors NVIDIA's own gating rule, so "my app shows something different" is a
        // data point about their rule. Without the card, architecture and driver in the log, that
        // report costs another round of guessing.
        var vm = ViewModel();
        Assert.Contains("arch=0x", vm);
        Assert.Contains("driver=", vm);
        Assert.Contains("shown=[", vm);
    }

    [Fact]
    public void TheModeIsTheDrsSettingTheNvidiaAppWrites()
    {
        var src = Nvapi();
        Assert.Contains("PREFERRED_PSTATE_ID = 0x1057EB71", src);
        Assert.Contains("public uint GetPowerMode()", src);
        Assert.Contains("public TweakResult SetPowerMode(uint pstate)", src);
    }

    [Fact]
    public void PowerMizerIsNotUsedForTheMode()
    {
        // Two mechanisms writing the same user-facing choice would fight, and the PowerMizer one
        // needs a reboot — which defeats the whole point of a live battery switch.
        var src = GpuService();
        Assert.DoesNotContain("public string GetPowerMode(", src);
        Assert.DoesNotContain("public TweakResult SetPowerMode(", src);
    }

    [Fact]
    public void TheBatteryModeIsAppliedOnThePowerSourceChange()
    {
        var vm = ViewModel();

        // The driver holds one global value, so this swap IS the feature.
        Assert.Contains("SystemEvents.PowerModeChanged", vm);
        Assert.Contains("private void ApplyModeForCurrentPowerSource()", vm);
        Assert.Contains("_power.IsOnBattery()", vm);

        // And it must be unhooked, or the static event keeps the view-model alive for the life of
        // the process.
        Assert.Contains("SystemEvents.PowerModeChanged -= _powerModeChanged;", vm);
    }

    [Fact]
    public void ChangingTheInactiveSourceDoesNotApplyImmediately()
    {
        var vm = ViewModel();

        // Picking a battery mode while plugged in must SAVE the choice without changing the
        // driver — otherwise setting up your battery preference would degrade the machine you are
        // sitting at right now.
        Assert.Contains("if (!IsLaptop || !_power.IsOnBattery())", vm);
        Assert.Contains("if (IsLaptop && _power.IsOnBattery())", vm);
    }

    [Fact]
    public void AFreshInstallShowsTheDriversRealValue()
    {
        var vm = ViewModel();
        Assert.Contains("uint live = _nvapi.GetPowerMode(out bool settingPresent);", vm);
        // Whichever source we are on must mirror the driver exactly, not a saved guess.
        Assert.Contains("PowerModeBattery = live;", vm);
        Assert.Contains("PowerModeAc      = live;", vm);
    }

    [Fact]
    public void TheBatterySelectorIsLaptopOnlyAndBothAreBound()
    {
        var view = View();
        Assert.Contains("{Binding IsLaptop, Converter={StaticResource BoolToVis}}", view);
        Assert.Contains("{Binding PowerModeAc, Mode=TwoWay}", view);
        Assert.Contains("{Binding PowerModeBattery, Mode=TwoWay}", view);
        Assert.Contains("{Binding PowerModeOptions}", view);
    }

    [Fact]
    public void MaxPerformanceIsRecommendedOnlyAndDesktopOnly()
    {
        var dash = Read("src", "Systema", "ViewModels", "DashboardViewModel.cs");

        // Offered as a suggestion the user can take...
        Assert.Contains("\"NVIDIA power mode: maximum performance\"", dash);
        Assert.Contains("SetPowerMode(NvapiService.PStateMaxPerf)", dash);

        // ...on desktops with an NVIDIA card only. Holding full clocks on a laptop mostly makes
        // heat and drains the battery.
        Assert.Contains("if (!_powerPlan.HasBattery() && _nvapi.IsAvailable())", dash);

        // ...and NEVER applied by Auto-Pilot. Running a GPU at full clocks around the clock is a
        // trade to opt into, not something to do to someone silently. The apply-all pass runs
        // between step 14 and "Auto-Pilot completed" — the setter must not appear in it.
        int start = dash.IndexOf("// 14. Disable MPO — steadier frame timing where the GPU driver integrates",
                                 System.StringComparison.Ordinal);
        int end   = dash.IndexOf("Auto-Pilot completed successfully", System.StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "could not locate the Auto-Pilot apply-all pass");
        Assert.DoesNotContain("PStateMaxPerf", dash[start..end]);
    }

    [Fact]
    public void ReDetectDoesNotResetTheSelectedMode()
    {
        var vm = ViewModel();

        // Clearing a collection a ComboBox is bound to nulls its selection, and that null goes
        // straight back through the TwoWay binding. Re-detect rebuilt the list every time, so it
        // looked like it was resetting the power mode.
        Assert.Contains("SequenceEqual(shown.Select(o => o.Value))", vm);
    }

    [Fact]
    public void DesktopsMirrorTheDriverAndLaptopsKeepBothChoices()
    {
        var vm = ViewModel();

        // Desktop: one mode, driver is the only truth, nothing re-applies it afterwards.
        Assert.Contains("if (!IsLaptop)", vm);

        // Laptop: show the LIVE value for the source we are on and the SAVED choice for the one
        // we cannot observe. Reading `live` into both overwrote the other source's choice on
        // every load, Re-detect included.
        Assert.DoesNotContain("if (onBattery) PowerModeBattery = live; else PowerModeAc = live;", vm);
        Assert.Contains("ParseSavedMode(_settings.NvidiaPowerModeBattery, live)", vm);
        Assert.Contains("ParseSavedMode(_settings.NvidiaPowerModeAc, live)", vm);
    }
}
