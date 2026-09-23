using System;
using System.IO;
using Systema.ViewModels;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// The Dell charging-mode card writes the same BIOS attribute (PrimaryBattChargeCfg) that Game
/// Boost's battery pause uses. These lock the pieces that are easy to break silently: the custom
/// threshold math, the friendly labels, and the "grey out while a boost owns charging" behaviour.
/// </summary>
public class DellChargingTests
{
    private static string Read(params string[] parts)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(dir, Path.Combine(parts));
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    // ── Custom threshold math ──────────────────────────────────────────────────

    [Theory]
    [InlineData(40, 50)]   // below floor -> 50
    [InlineData(73, 70)]   // snaps to 5% step
    [InlineData(75, 75)]
    [InlineData(98, 95)]   // above ceiling -> 95
    public void StartClampsIntoRangeAndStep(int input, int expected)
        => Assert.Equal(expected, DellViewModel.ClampStart(input));

    [Theory]
    [InlineData(75, 76, 80)]   // stop must sit at least 5 above start
    [InlineData(75, 80, 80)]
    [InlineData(75, 200, 100)] // clamped to 100
    [InlineData(90, 90, 95)]   // gap enforced near the top
    public void StopKeepsFivePercentGap(int start, int input, int expected)
        => Assert.Equal(expected, DellViewModel.ClampStop(start, input));

    [Fact]
    public void StopNeverExceedsHundredEvenWhenStartIsHigh()
        => Assert.True(DellViewModel.ClampStop(95, 99) <= 100);

    // ── Friendly labels match Dell's own wording ───────────────────────────────

    [Theory]
    [InlineData("Adaptive", "Adaptive", true)]
    [InlineData("PrimAcUse", "Always AC", false)]
    [InlineData("Express", "ExpressCharge", false)]
    [InlineData("Standard", "Standard", false)]
    [InlineData("Custom", "Custom", false)]
    public void FriendlyLabelsAndRecommendedFlag(string raw, string label, bool recommended)
    {
        var (l, _, rec) = DellViewModel.ChargeFriendly(raw);
        Assert.Equal(label, l);
        Assert.Equal(recommended, rec);
    }

    [Fact]
    public void OnlyAdaptiveIsRecommended()
    {
        Assert.True(DellViewModel.ChargeFriendly("Adaptive").Recommended);
        foreach (var m in new[] { "Standard", "Express", "PrimAcUse", "Custom" })
            Assert.False(DellViewModel.ChargeFriendly(m).Recommended);
    }

    // ── The card greys out and never writes while Game Boost owns charging ──────

    [Fact]
    public void CardGreysOutAndNeverWritesDuringBoost()
    {
        var vm = Read("src", "Systema", "ViewModels", "DellViewModel.cs");

        // The apply path bails while a boost owns charging, so a stray property change
        // during a boost cannot clobber the pause value.
        int apply = vm.IndexOf("private void ApplyChargeMode", StringComparison.Ordinal);
        Assert.True(apply > 0);
        Assert.Contains("IsBatteryPauseActive", vm[apply..(apply + 400)]);

        // Boost start/stop toggles the flag, and stop re-reads the BIOS value the boost restored.
        Assert.Contains("BoostActivated", vm);
        Assert.Contains("BoostDeactivated", vm);
        Assert.Contains("ReloadChargeFromBios", vm);

        // Custom is written in the BIOS "Custom:start:stop" shape.
        Assert.Contains("$\"Custom:{CustomStart}:{CustomStop}\"", vm);
    }

    [Fact]
    public void XamlShowsBatteryPauseBannerAndDisablesInput()
    {
        var xaml = Read("src", "Systema", "Views", "DellView.xaml");

        // The exact copy the user asked for.
        Assert.Contains("Battery Pause active", xaml);
        Assert.Contains("Battery pause is on in Game Boost.", xaml);

        // The interactive area disables (not just dims) so clicks do nothing during a boost.
        int trig = xaml.IndexOf("IsBatteryPauseActive", StringComparison.Ordinal);
        Assert.True(trig > 0);
        Assert.Contains("IsEnabled", xaml);

        // Card is hidden entirely on machines without the charging attribute.
        Assert.Contains("ChargingSupported", xaml);
    }

    // ── The 5% gap moves the OTHER end, never the value just picked ─────────────

    // The reported bug: start 80, then pick stop 80. The BIOS silently rewrites that to
    // 80/85, so the app must resolve it first — by dropping start to 75, keeping the 80
    // the user actually chose.
    [Fact]
    public void PickingStopEqualToStartLowersStartNotStop()
    {
        Assert.Equal(75, DellViewModel.StartForStop(stop: 80, currentStart: 80));
        // The stop the user picked is untouched, so 75/80 reaches the BIOS.
        Assert.Equal(80, DellViewModel.StopForStart(start: 75, currentStop: 80));
    }

    [Theory]
    [InlineData(80, 80, 75)]   // equal -> start drops
    [InlineData(80, 78, 75)]   // too close -> start drops
    [InlineData(80, 75, 75)]   // exactly 5 apart -> untouched
    [InlineData(80, 60, 60)]   // already wider -> untouched
    [InlineData(55, 90, 50)]   // never below the 50 floor
    public void StartForStopKeepsTheGap(int stop, int currentStart, int expected)
        => Assert.Equal(expected, DellViewModel.StartForStop(stop, currentStart));

    [Theory]
    [InlineData(80, 80, 85)]   // equal -> stop rises
    [InlineData(80, 82, 85)]   // too close -> stop rises
    [InlineData(80, 85, 85)]   // exactly 5 apart -> untouched
    [InlineData(80, 100, 100)] // already wider -> untouched
    [InlineData(95, 95, 100)]  // never above the 100 ceiling
    public void StopForStartKeepsTheGap(int start, int currentStop, int expected)
        => Assert.Equal(expected, DellViewModel.StopForStart(start, currentStop));

    // Whichever end the user picks, the resulting pair is always BIOS-legal and always
    // lands on a value the combo boxes actually offer (5% steps, 50-95 and 55-100).
    [Fact]
    public void EveryResolvedPairIsLegalAndSelectable()
    {
        for (int start = 50; start <= 95; start += 5)
        for (int stop = 55; stop <= 100; stop += 5)
        {
            int fixedStop = DellViewModel.StopForStart(start, stop);
            Assert.True(fixedStop - start >= 5, $"start {start} stop {fixedStop}");
            Assert.InRange(fixedStop, 55, 100);
            Assert.Equal(0, fixedStop % 5);

            int fixedStart = DellViewModel.StartForStop(stop, start);
            Assert.True(stop - fixedStart >= 5, $"start {fixedStart} stop {stop}");
            Assert.InRange(fixedStart, 50, 95);
            Assert.Equal(0, fixedStart % 5);
        }
    }

    // Coercing the property a TwoWay binding is mid-push leaves the combo box showing a
    // stale value, which is how 80/80 displayed while 80/85 went to firmware.
    [Fact]
    public void HandlersAdjustTheOppositeEnd()
    {
        var vm = Read("src", "Systema", "ViewModels", "DellViewModel.cs");

        int onStart = vm.IndexOf("partial void OnCustomStartChanged", StringComparison.Ordinal);
        Assert.True(onStart > 0);
        Assert.Contains("StopForStart", vm[onStart..(onStart + 500)]);

        int onStop = vm.IndexOf("partial void OnCustomStopChanged", StringComparison.Ordinal);
        Assert.True(onStop > 0);
        Assert.Contains("StartForStop", vm[onStop..(onStop + 500)]);
    }

    // Thermal Profile sits above Charging Mode, and neither card is flush against the other.
    [Fact]
    public void ThermalCardComesFirstAndCardsAreSpaced()
    {
        var xaml = Read("src", "Systema", "Views", "DellView.xaml");
        int thermal  = xaml.IndexOf("Dell Thermal Profile Card", StringComparison.Ordinal);
        int charging = xaml.IndexOf("Dell Charging Mode Card", StringComparison.Ordinal);
        Assert.True(thermal > 0 && charging > 0);
        Assert.True(thermal < charging, "Thermal Profile must render above Charging Mode");

        // The Card style carries no margin, so the wrappers have to supply the gap.
        // Match the binding itself, not the prose in the surrounding comment.
        foreach (var binding in new[] { "Binding ThermalCardVisible", "Binding ChargingSupported" })
        {
            int at = xaml.IndexOf(binding, StringComparison.Ordinal);
            Assert.True(at > 0, $"missing {binding}");
            Assert.Contains("Margin=\"0,0,0,16\"", xaml[Math.Max(0, at - 220)..at]);
        }
    }

    // ── The card only appears when the BIOS can actually drive it ───────────────

    [Fact]
    public void NoCapabilityMeansNoCardAndNoOptions()
    {
        var none = Systema.Services.ChargingCapability.None;
        Assert.False(none.Available);
        Assert.Empty(none.Modes);
        Assert.False(none.SupportsCustom);
        Assert.Null(none.CurrentMode);
    }

    // DetectSupport only proves SetAttribute exists, which is all the blind pause path
    // needs. The card additionally needs the charge attribute to enumerate and read back,
    // so it must gate on DescribeCharging instead.
    [Fact]
    public void CardGatesOnTheCapabilityProbeNotJustDetectSupport()
    {
        var vm = Read("src", "Systema", "ViewModels", "DellViewModel.cs");
        Assert.Contains("DescribeCharging()", vm);
        Assert.Contains("ChargingSupported = cap.Available", vm);
        Assert.Contains("if (!cap.Available) return;", vm);

        // The old loose gate must not creep back.
        Assert.DoesNotContain("ChargingSupported = supported", vm);
    }

    [Fact]
    public void CapabilityRequiresEnumeratedModesAndAReadableCurrentValue()
    {
        var svc = Read("src", "Systema", "Services", "BatteryPauseService.cs");
        int at = svc.IndexOf("public ChargingCapability DescribeCharging", StringComparison.Ordinal);
        Assert.True(at > 0);
        string body = svc[at..(at + 2200)];

        // No vendor method -> nothing to drive.
        Assert.Contains("_activeMethod == null", body);
        // An empty list or an unreadable current value hides the card.
        Assert.Contains("modes.Count == 0", body);
        Assert.Contains("string.IsNullOrEmpty(current)", body);
        Assert.Contains("ChargingCapability.None", body);
    }

    // Picking Custom without the threshold attributes would switch the BIOS mode and then
    // silently fail to set the percents, so the option is removed when they are missing.
    [Fact]
    public void CustomIsHiddenWhenThresholdAttributesAreMissing()
    {
        var svc = Read("src", "Systema", "Services", "BatteryPauseService.cs");
        int at = svc.IndexOf("public ChargingCapability DescribeCharging", StringComparison.Ordinal);
        string body = svc[at..(at + 2200)];

        Assert.Contains("HasCustomThresholds()", body);
        Assert.Contains("\"Custom\"", body);

        // Both Dell paths must be able to answer the question.
        Assert.Contains("CustomChargeStart", svc);
        Assert.Contains("CustomChargeStop", svc);
    }

    // The old helper handed back a hardcoded five-mode list whenever the BIOS did not
    // enumerate, which is exactly how a dead card would have been shown.
    [Fact]
    public void NoHardcodedModeListSurvives()
    {
        var svc = Read("src", "Systema", "Services", "BatteryPauseService.cs");
        Assert.DoesNotContain("GetChargeModes", svc);
        Assert.DoesNotContain("new List<string> { \"Adaptive\", \"Standard\", \"Express\", \"PrimAcUse\", \"Custom\" }", svc);
    }

    [Fact]
    public void XamlStillHidesTheWholeCardWhenUnsupported()
    {
        var xaml = Read("src", "Systema", "Views", "DellView.xaml");
        int at = xaml.IndexOf("Binding ChargingSupported", StringComparison.Ordinal);
        Assert.True(at > 0);
        // The visibility binding drives the outer wrapper, so nothing inside renders.
        Assert.Contains("Visibility", xaml[Math.Max(0, at - 160)..at]);
    }
}
