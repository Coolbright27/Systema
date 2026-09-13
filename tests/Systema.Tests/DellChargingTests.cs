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
}
