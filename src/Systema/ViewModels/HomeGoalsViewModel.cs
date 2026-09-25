// ════════════════════════════════════════════════════════════════════════════
// HomeGoalsViewModel.cs  ·  Home › "I want…" goals
// ════════════════════════════════════════════════════════════════════════════
//
// A goal is a plain-words shortcut ("Smoother games") for a handful of settings that already
// exist on other pages. Picking one opens a preview: every setting it would touch, what it is
// now and what it would become. Nothing changes until "Apply" is pressed.
//
// Applying a step sets the SAME ViewModel property (or runs the same command) that the page's
// own toggle is bound to, so it goes down the identical code path: the same service call, the
// same saved setting, the same revert-on-failure. A goal can never do anything its page can't.
//
// Only Systema's own reversible toggles are used. Deliberately left out:
//   • Dell thermal profile and charging mode: BIOS settings with user-specific values (a custom
//     75-80% charge window would be silently overwritten).
//   • Anything Auto Pilot manages while Auto Pilot is on: shown as locked, never applied.
//
// RELATED FILES
//   Views/DashboardView.xaml  — the chips and the preview panel
//   App.xaml.cs               — builds this after every other ViewModel exists
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Services;

namespace Systema.ViewModels;

/// <summary>One setting a goal would change, read live from the page's ViewModel.</summary>
public sealed class GoalStep : ObservableObject
{
    private readonly Func<bool> _isVisible;
    private readonly Func<bool> _isDone;
    private readonly Func<bool> _isLocked;
    private readonly Func<string> _currentText;
    private readonly Func<Task> _apply;

    public GoalStep(string title, string targetText, Func<bool> isVisible, Func<bool> isDone,
                    Func<bool> isLocked, Func<string> currentText, Func<Task> apply)
    {
        Title        = title;
        TargetText   = targetText;
        _isVisible   = isVisible;
        _isDone      = isDone;
        _isLocked    = isLocked;
        _currentText = currentText;
        _apply       = apply;
    }

    /// <summary>The setting's title, exactly as its page shows it.</summary>
    public string Title { get; }
    public string TargetText { get; }

    public bool IsVisible => _isVisible();
    public bool IsDone    => _isDone();
    public bool IsLocked  => !IsDone && _isLocked();
    public bool IsTodo    => IsVisible && !IsDone && !IsLocked;

    public string ChangeText =>
        IsDone   ? "Already set" :
        IsLocked ? "Auto Pilot manages this" :
                   $"{_currentText()} → {TargetText}";

    internal Task ApplyAsync() => _apply();

    /// <summary>Re-read everything from the page's ViewModel.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One "I want…" chip.</summary>
public sealed partial class HomeGoal : ObservableObject
{
    private readonly Func<bool> _isAvailable;

    public HomeGoal(string id, string label, IReadOnlyList<GoalStep> steps, Func<bool>? isAvailable = null)
    {
        Id           = id;
        Label        = label;
        Steps        = steps;
        _isAvailable = isAvailable ?? (() => true);
    }

    public string Id { get; }
    public string Label { get; }
    public IReadOnlyList<GoalStep> Steps { get; }

    /// <summary>Hidden when it doesn't apply to this PC (battery goal on a desktop) or has nothing to show.
    /// Live, because some of what it reads (the Tools page's battery check) loads in the background.</summary>
    public bool IsAvailable => _isAvailable() && Steps.Any(s => s.IsVisible);

    public int TodoCount => Steps.Count(s => s.IsTodo);

    [ObservableProperty] private bool _isSelected;

    public void RefreshAvailability() => OnPropertyChanged(nameof(IsAvailable));
}

public sealed partial class HomeGoalsViewModel : ObservableObject
{
    // Injected rather than LoggerService.Instance, so tests can build this without touching the real log.
    private readonly Action<string>? _logInfo;
    private readonly Action<string>? _logWarn;

    public ObservableCollection<HomeGoal> Goals { get; } = new();

    /// <summary>The open goal's visible steps, in order.</summary>
    public ObservableCollection<GoalStep> SelectedSteps { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private HomeGoal? _selectedGoal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(ApplyText))]
    private bool _isApplying;

    public bool HasSelection => SelectedGoal != null;

    public bool CanApply => !IsApplying && (SelectedGoal?.TodoCount ?? 0) > 0;

    public string ApplyText
    {
        get
        {
            if (IsApplying) return "Applying…";
            int n = SelectedGoal?.TodoCount ?? 0;
            return n == 0 ? "All set" : n == 1 ? "Apply 1 change" : $"Apply {n} changes";
        }
    }

    // Runs work on the UI thread (null = run inline, as in tests).
    private readonly Action<Action>? _toUi;
    private int _refreshQueued;

    public HomeGoalsViewModel(IEnumerable<HomeGoal> goals, IEnumerable<INotifyPropertyChanged> sources,
                              Action<string>? logInfo = null, Action<string>? logWarn = null,
                              Action<Action>? toUi = null)
    {
        _logInfo = logInfo;
        _logWarn = logWarn;
        _toUi    = toUi;
        foreach (var g in goals) Goals.Add(g);

        // Any change on a page the goals read from (a toggle flipped there, a failed apply
        // reverting, Auto Pilot turning on) re-reads the open preview.
        foreach (var src in sources) src.PropertyChanged += (_, _) => OnSourceChanged();
    }

    /// <summary>
    /// Pages sometimes raise PropertyChanged from a background thread, and this handler runs
    /// inside their setter. So the refresh hops to the UI thread (where SelectedSteps is edited),
    /// and a burst of changes collapses into one refresh instead of flooding the dispatcher.
    /// </summary>
    private void OnSourceChanged()
    {
        if (_toUi == null) { Refresh(); return; }
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _toUi(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    [RelayCommand]
    private void Select(HomeGoal? goal)
    {
        if (goal == null || ReferenceEquals(goal, SelectedGoal)) { Close(); return; }

        if (SelectedGoal != null) SelectedGoal.IsSelected = false;
        goal.IsSelected = true;
        SelectedGoal = goal;

        SelectedSteps.Clear();
        foreach (var s in goal.Steps.Where(s => s.IsVisible)) SelectedSteps.Add(s);
        Refresh();
    }

    [RelayCommand]
    private void Close()
    {
        if (SelectedGoal != null) SelectedGoal.IsSelected = false;
        SelectedGoal = null;
        SelectedSteps.Clear();
        Refresh();
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var goal = SelectedGoal;
        if (goal == null || IsApplying) return;

        IsApplying = true;
        try
        {
            foreach (var step in goal.Steps.Where(s => s.IsTodo).ToList())
            {
                _logInfo?.Invoke($"Goal '{goal.Label}': {step.Title} → {step.TargetText}");
                try { await step.ApplyAsync(); }
                catch (Exception ex) { _logWarn?.Invoke($"Goal step '{step.Title}' failed: {ex.Message}"); }
                Refresh();
                await Task.Delay(90);   // one change at a time, so each row visibly ticks over
            }
        }
        finally
        {
            IsApplying = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        foreach (var g in Goals) g.RefreshAvailability();
        foreach (var s in SelectedSteps) s.Refresh();
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ApplyText));
    }

    // ── The goals ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the four goals over the real page ViewModels. Titles match each page's own
    /// card title, so the preview names settings exactly the way the pages do.
    /// </summary>
    public static HomeGoalsViewModel Create(ToolsViewModel tools, VisualViewModel visual,
                                            GameBoosterViewModel games, ServicesViewModel services,
                                            SettingsService settings)
    {
        bool AutoPilot() => settings.AutoPilotModeEnabled;
        static string OnOff(bool v) => v ? "On" : "Off";
        static bool Always() => true;

        GoalStep coreParking() => new(
            "CPU Core Efficiency", "On", Always,
            () => tools.CoreParkingEnforced, AutoPilot,
            () => OnOff(tools.CoreParkingEnforced),
            () => { tools.CoreParkingEnforced = true; return Task.CompletedTask; });

        GoalStep hibernateOnBattery() => new(
            // Visual's battery check is synchronous; Tools' arrives a moment later.
            "Sleep → Hibernate (Battery)", "On", () => visual.HasBattery || tools.HasBattery,
            () => tools.SleepToHibernateEnabled, () => false,
            () => OnOff(tools.SleepToHibernateEnabled),
            () => { tools.SleepToHibernateEnabled = true; return Task.CompletedTask; });

        var quiet = new HomeGoal("quiet", "A quieter, cooler PC", new[]
        {
            coreParking(),
            new GoalStep("Performance mode", "Off", Always,
                () => !visual.PerformanceModeEnabled, () => visual.IsPerformanceModeAutoPiloted,
                () => OnOff(visual.PerformanceModeEnabled),
                () => { visual.PerformanceModeEnabled = false; return Task.CompletedTask; }),
        });

        var battery = new HomeGoal("battery", "Longer battery life", new[]
        {
            coreParking(),
            // Max life already saves more than Balanced, so it counts as done rather than
            // being stepped back down.
            new GoalStep("Battery mode", "Balanced no turbo", () => visual.HasBattery,
                () => visual.ActiveBatteryMode is "balanced" or "max", AutoPilot,
                () => BatteryModeName(visual.ActiveBatteryMode),
                () => visual.SetBatteryModeCommand.ExecuteAsync("balanced")),
            hibernateOnBattery(),
        }, isAvailable: () => visual.HasBattery || tools.HasBattery);

        var smooth = new HomeGoal("games", "Smoother games", new[]
        {
            new GoalStep("Boost games automatically", "On", Always,
                () => games.GameBoosterEnabled, AutoPilot,
                () => OnOff(games.GameBoosterEnabled),
                () => { games.GameBoosterEnabled = true; return Task.CompletedTask; }),
            new GoalStep("Use the High performance power plan", "On", Always,
                () => games.HighPerfPowerPlan, () => false,
                () => OnOff(games.HighPerfPowerPlan),
                () => { games.HighPerfPowerPlan = true; return Task.CompletedTask; }),
            new GoalStep("Keep the PC awake", "On", Always,
                () => games.PreventSleepOnBoost, () => false,
                () => OnOff(games.PreventSleepOnBoost),
                () => { games.PreventSleepOnBoost = true; return Task.CompletedTask; }),
        });

        var privacy = new HomeGoal("privacy", "More privacy", new[]
        {
            new GoalStep("No Telemetry Pro", "On", Always,
                () => services.NoTelemetryPro, AutoPilot,
                () => OnOff(services.NoTelemetryPro),
                () => { services.NoTelemetryPro = true; return Task.CompletedTask; }),
            new GoalStep("Disable Web Search in Start", "On", Always,
                () => tools.DisableWebSearch, () => false,
                () => OnOff(tools.DisableWebSearch),
                () => { tools.DisableWebSearch = true; return Task.CompletedTask; }),
        });

        var log = LoggerService.Instance;
        return new HomeGoalsViewModel(
            new[] { quiet, battery, smooth, privacy },
            new INotifyPropertyChanged[] { tools, visual, games, services },
            logInfo: m => log.Info("HomeGoals", m),
            logWarn: m => log.Warn("HomeGoals", m),
            toUi: work =>
            {
                var ui = System.Windows.Application.Current?.Dispatcher;
                if (ui == null) { work(); return; }   // shutting down; nothing is on screen
                ui.BeginInvoke(work, System.Windows.Threading.DispatcherPriority.Background);
            });
    }

    internal static string BatteryModeName(string mode) => mode switch
    {
        "balanced"    => "Balanced no turbo",
        "max"         => "Max life",
        "performance" => "Performance",
        _             => "Off",
    };
}
