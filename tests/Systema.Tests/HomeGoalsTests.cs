using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Systema.ViewModels;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Home's "I want…" goals preview a set of changes and only apply the ones still needed.
/// These pin the preview wording, what counts as "to do", and that Apply never touches a
/// setting that is already right, hidden on this PC, or locked by Auto Pilot.
/// </summary>
public class HomeGoalsTests
{
    private static string Src(params string[] parts)
    {
        var asmDir = Path.GetDirectoryName(typeof(HomeGoalsTests).Assembly.Location)!;
        string root = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(new[] { root, "src", "Systema" }.Concat(parts).ToArray()));
    }

    /// <summary>A switch the test can flip, standing in for a page ViewModel property.</summary>
    private sealed class Switch
    {
        public bool On;
        public bool Visible = true;
        public bool Locked;
        public bool Fails;
        public int Applied;
    }

    private static GoalStep Step(string title, Switch s, List<string>? order = null) => new(
        title, "On", () => s.Visible, () => s.On, () => s.Locked,
        () => s.On ? "On" : "Off",
        () =>
        {
            order?.Add(title);
            s.Applied++;
            if (s.Fails) throw new InvalidOperationException("boom");
            s.On = true;
            return Task.CompletedTask;
        });

    private sealed class Source : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("X"));
    }

    [Fact]
    public void Preview_SaysWhatWouldChange_OrWhyItWont()
    {
        var off = new Switch();
        var done = new Switch { On = true };
        var locked = new Switch { Locked = true };

        Assert.Equal("Off → On", Step("A", off).ChangeText);
        Assert.Equal("Already set", Step("B", done).ChangeText);
        Assert.Equal("Auto Pilot manages this", Step("C", locked).ChangeText);
    }

    [Fact]
    public void AlreadySet_WinsOverLocked()
    {
        // Auto Pilot on, but the setting is already where the goal wants it: nothing to explain.
        var s = new Switch { On = true, Locked = true };
        var step = Step("A", s);
        Assert.False(step.IsLocked);
        Assert.Equal("Already set", step.ChangeText);
    }

    [Fact]
    public void Selecting_ShowsOnlyVisibleSteps_AndSelectingAgainCloses()
    {
        var hidden = new Switch { Visible = false };
        var goal = new HomeGoal("g", "Goal", new[] { Step("Shown", new Switch()), Step("Hidden", hidden) });
        var vm = new HomeGoalsViewModel(new[] { goal }, Array.Empty<INotifyPropertyChanged>());

        vm.SelectCommand.Execute(goal);
        Assert.True(vm.HasSelection);
        Assert.True(goal.IsSelected);
        Assert.Equal(new[] { "Shown" }, vm.SelectedSteps.Select(s => s.Title));

        vm.SelectCommand.Execute(goal);
        Assert.False(vm.HasSelection);
        Assert.False(goal.IsSelected);
        Assert.Empty(vm.SelectedSteps);
    }

    [Fact]
    public void SwitchingGoals_DeselectsTheOldOne()
    {
        var a = new HomeGoal("a", "A", new[] { Step("A1", new Switch()) });
        var b = new HomeGoal("b", "B", new[] { Step("B1", new Switch()) });
        var vm = new HomeGoalsViewModel(new[] { a, b }, Array.Empty<INotifyPropertyChanged>());

        vm.SelectCommand.Execute(a);
        vm.SelectCommand.Execute(b);
        Assert.False(a.IsSelected);
        Assert.True(b.IsSelected);
        Assert.Same(b, vm.SelectedGoal);
    }

    [Fact]
    public void ApplyButton_CountsOnlyWhatStillNeedsDoing()
    {
        var goal = new HomeGoal("g", "Goal", new[]
        {
            Step("Todo1", new Switch()),
            Step("Todo2", new Switch()),
            Step("Done", new Switch { On = true }),
            Step("Locked", new Switch { Locked = true }),
            Step("Hidden", new Switch { Visible = false }),
        });
        var vm = new HomeGoalsViewModel(new[] { goal }, Array.Empty<INotifyPropertyChanged>());
        vm.SelectCommand.Execute(goal);

        Assert.Equal("Apply 2 changes", vm.ApplyText);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task Apply_ChangesOnlyTodoSteps_InOrder_ThenSaysAllSet()
    {
        var order = new List<string>();
        var done = new Switch { On = true };
        var locked = new Switch { Locked = true };
        var hidden = new Switch { Visible = false };
        var goal = new HomeGoal("g", "Goal", new[]
        {
            Step("First", new Switch(), order),
            Step("Done", done, order),
            Step("Locked", locked, order),
            Step("Hidden", hidden, order),
            Step("Second", new Switch(), order),
        });
        var vm = new HomeGoalsViewModel(new[] { goal }, Array.Empty<INotifyPropertyChanged>());
        vm.SelectCommand.Execute(goal);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "First", "Second" }, order);
        Assert.Equal(0, done.Applied + locked.Applied + hidden.Applied);
        Assert.Equal("All set", vm.ApplyText);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public async Task OneFailingStep_DoesNotStopTheRest_AndIsLogged()
    {
        var warnings = new List<string>();
        var bad = new Switch { Fails = true };
        var good = new Switch();
        var goal = new HomeGoal("g", "Goal", new[] { Step("Bad", bad), Step("Good", good) });
        var vm = new HomeGoalsViewModel(new[] { goal }, Array.Empty<INotifyPropertyChanged>(),
                                        logWarn: warnings.Add);
        vm.SelectCommand.Execute(goal);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(good.On);
        Assert.Single(warnings);
        Assert.Contains("Bad", warnings[0]);
        Assert.Equal("Apply 1 change", vm.ApplyText);   // the failed one is still to do
    }

    [Fact]
    public void AChangeOnAPage_RefreshesTheOpenPreview()
    {
        var s = new Switch();
        var src = new Source();
        var goal = new HomeGoal("g", "Goal", new[] { Step("A", s) });
        var vm = new HomeGoalsViewModel(new[] { goal }, new INotifyPropertyChanged[] { src });
        vm.SelectCommand.Execute(goal);
        Assert.Equal("Apply 1 change", vm.ApplyText);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        s.On = true;                 // flipped on its own page
        src.Raise();

        Assert.Contains(nameof(HomeGoalsViewModel.ApplyText), raised);
        Assert.Equal("All set", vm.ApplyText);
    }

    [Fact]
    public void GoalIsHidden_WhenItDoesNotApplyToThisPC()
    {
        var laptopOnly = new HomeGoal("b", "Battery", new[] { Step("A", new Switch()) }, isAvailable: () => false);
        var nothingVisible = new HomeGoal("n", "None", new[] { Step("A", new Switch { Visible = false }) });
        var normal = new HomeGoal("ok", "Ok", new[] { Step("A", new Switch()) });

        Assert.False(laptopOnly.IsAvailable);
        Assert.False(nothingVisible.IsAvailable);
        Assert.True(normal.IsAvailable);
    }

    [Theory]
    [InlineData("", "Off")]
    [InlineData("balanced", "Balanced no turbo")]
    [InlineData("max", "Max life")]
    [InlineData("performance", "Performance")]
    public void BatteryMode_ReadsLikeItsPage(string mode, string label) =>
        Assert.Equal(label, HomeGoalsViewModel.BatteryModeName(mode));

    // ── The real goal definitions ────────────────────────────────────────────

    private static readonly Dictionary<string, string> PageOf = new()
    {
        ["CPU Core Efficiency"]                 = "ToolsView",
        ["Sleep → Hibernate (Battery)"]         = "ToolsView",
        ["Disable Web Search in Start"]         = "ToolsView",
        ["Performance mode"]                    = "VisualView",
        ["Battery mode"]                        = "VisualView",
        ["Boost games automatically"]           = "GameBoosterView",
        ["Use the High performance power plan"] = "GameBoosterView",
        ["Keep the PC awake"]                   = "GameBoosterView",
        ["No Telemetry Pro"]                    = "ServicesView",
    };

    [Fact]
    public void EveryGoalStep_IsNamedExactlyLikeItsPage()
    {
        string src = Src("ViewModels", "HomeGoalsViewModel.cs");
        var titles = Regex.Matches(src, @"""([^""]+)"",\s*""(?:On|Off|Balanced no turbo)""")
                          .Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.Equal(PageOf.Keys.OrderBy(t => t), titles.OrderBy(t => t));
        foreach (var (title, view) in PageOf)
        {
            string xaml = Src("Views", view + ".xaml");
            Assert.True(xaml.Contains($"Header=\"{title}\"") || xaml.Contains($"Text=\"{title}\""),
                $"'{title}' is not a card title on {view}");
        }
    }

    [Fact]
    public void Goals_NeverTouchDellBiosSettings()
    {
        // Thermal profile and charge mode are BIOS settings with values the user picked on
        // purpose (a custom charge window, say). A one-click goal must not overwrite them.
        string src = Src("ViewModels", "HomeGoalsViewModel.cs");
        string create = src[src.IndexOf("public static HomeGoalsViewModel Create", StringComparison.Ordinal)..];
        Assert.DoesNotContain("DellViewModel", create);
        Assert.DoesNotContain("ThermalMode", create);
        Assert.DoesNotContain("ChargeMode", create);
    }
}
