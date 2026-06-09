// GameBoosterManualBoostTests.cs
//
// Regression guard for the v0.7.9 incident where the Manual Boost toggle was
// dead UI. The CheckBox in GameBoosterView.xaml is two-way bound to
// ManualBoostEnabled with NO Command. An earlier build had a
// [RelayCommand] ToggleManualBoost that nothing referenced — so clicking the
// toggle flipped the bool but never called the service, and the next
// auto-refresh tick reset the bool, making the switch "toggle right back off"
// with no boost.
//
// The fix: an OnManualBoostEnabledChanged partial method is the single thing
// that activates/deactivates the boost. These source-scan tests pin that:
//   1. The partial method must exist.
//   2. The dead [RelayCommand] ToggleManualBoost must NOT come back.
//   3. The XAML CheckBox stays bound TwoWay to ManualBoostEnabled.
//
// Source-scan (not reflection) because Application Control blocks loading
// Systema.dll into the test host on this machine — see other *SourceTests
// for the same constraint.

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Systema.Tests;

public class GameBoosterManualBoostTests
{
    private static string RepoRoot()
    {
        var asmDir = Path.GetDirectoryName(typeof(GameBoosterManualBoostTests).Assembly.Location)!;
        // tests/Systema.Tests/bin/<Cfg>/<TFM>/  →  ../../../../../
        return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
    }

    private static string ReadFile(params string[] relativeParts)
    {
        var path = Path.GetFullPath(Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray()));
        Assert.True(File.Exists(path), $"Expected file not found: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The Manual Boost toggle has no Command in XAML, so the ONLY thing that can
    /// actually activate the boost is the OnManualBoostEnabledChanged partial
    /// method. If it disappears, the toggle is dead UI again.
    /// </summary>
    [Fact]
    public void GameBoosterViewModel_Has_OnManualBoostEnabledChanged_PartialMethod()
    {
        var src = ReadFile("src", "Systema", "ViewModels", "GameBoosterViewModel.cs");

        var hasPartial = Regex.IsMatch(
            src,
            @"partial\s+void\s+OnManualBoostEnabledChanged\s*\(\s*bool\s+\w+\s*\)");
        Assert.True(hasPartial,
            "GameBoosterViewModel must declare 'partial void OnManualBoostEnabledChanged(bool value)' — " +
            "it is the only code path that activates/deactivates manual boost (the XAML CheckBox " +
            "has no Command binding).");

        // It must actually do something — call ApplyManualBoostAsync (or the service directly).
        var callsApply = Regex.IsMatch(src, @"ApplyManualBoostAsync\s*\(");
        Assert.True(callsApply,
            "OnManualBoostEnabledChanged must drive ApplyManualBoostAsync — otherwise the toggle " +
            "still does nothing.");
    }

    /// <summary>
    /// Guards against re-introducing the dead [RelayCommand] ToggleManualBoost.
    /// If someone wires that command back up alongside the partial method, the
    /// boost would fire twice (or cancel itself). Keep exactly one activation path.
    /// </summary>
    [Fact]
    public void GameBoosterViewModel_DoesNotResurrect_DeadToggleManualBoostCommand()
    {
        var src = ReadFile("src", "Systema", "ViewModels", "GameBoosterViewModel.cs");

        // Strip // line comments so the explanatory comment text above the method
        // (which legitimately mentions "ToggleManualBoost") doesn't trip the check.
        var noComments = Regex.Replace(src, @"//.*?$", "", RegexOptions.Multiline);

        var hasDeadCommand = Regex.IsMatch(
            noComments,
            @"\[RelayCommand\][^\]]*?\n\s*(private|public)\s+(async\s+)?Task\s+ToggleManualBoost\b");
        Assert.False(hasDeadCommand,
            "The [RelayCommand] ToggleManualBoost was removed — nothing referenced it and it " +
            "duplicated the activation path. Do not bring it back; OnManualBoostEnabledChanged " +
            "is the single source of truth.");
    }

    /// <summary>
    /// The XAML CheckBox must stay TwoWay-bound to ManualBoostEnabled. If the
    /// binding is removed or changed to OneWay, the partial method never fires.
    /// </summary>
    [Fact]
    public void GameBoosterView_CheckBox_IsTwoWayBoundToManualBoostEnabled()
    {
        var xaml = ReadFile("src", "Systema", "Views", "GameBoosterView.xaml");

        var hasTwoWayBinding = Regex.IsMatch(
            xaml,
            @"IsChecked\s*=\s*""\{Binding\s+ManualBoostEnabled\s*,\s*Mode\s*=\s*TwoWay\s*\}""");
        Assert.True(hasTwoWayBinding,
            "GameBoosterView.xaml must keep the Manual Boost CheckBox bound " +
            "IsChecked=\"{Binding ManualBoostEnabled, Mode=TwoWay}\" — the partial-method " +
            "activation path depends on the TwoWay write-back.");
    }
}
