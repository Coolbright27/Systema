// ════════════════════════════════════════════════════════════════════════════
// MainViewModel.cs  ·  Central shell ViewModel — navigation hub
// ════════════════════════════════════════════════════════════════════════════
//
// Holds references to every tab ViewModel and drives the auto-refresh cycle.
// CurrentView is set by the nav commands and data-bound in MainWindow.xaml
// so the ContentControl swaps the visible view.
//
// REFRESH CYCLE
//   DispatcherTimer fires every 1 s (focused) or 5 s (unfocused).
//   Only the active tab is refreshed: if CurrentView implements IAutoRefreshable
//   its RefreshAsync() is called each tick.
//   → To make a new tab auto-refresh: implement IAutoRefreshable in its ViewModel.
//   → To change the intervals: edit FocusedInterval / UnfocusedInterval below.
//   → Focus tracking: MainWindow.xaml.cs calls SetFocused(true/false) on Activated/Deactivated.
//
// NAV COMMAND PATTERN
//   Each "Navigate_Xxx" RelayCommand sets CurrentView = XxxVm.
//   The XAML nav button binds to that command; ActiveSection drives the
//   "selected" highlight on the sidebar.
//   → To add a new tab: add a property + NavigateXxx RelayCommand + set ActiveSection string.
//
// RELATED FILES
//   App.xaml.cs           — instantiates all VMs and passes them to this constructor
//   Views/MainWindow.xaml — ContentControl bound to CurrentView; nav buttons bound to commands
//   Core/IAutoRefreshable.cs — interface VMs implement to receive periodic refresh calls
//   Core/CrashGuard.cs    — Heartbeat() called every tick to prove the UI thread is alive
// ════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Core;
using Systema.Services;
using Systema.Views;

namespace Systema.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private string  _activeSection = "Dashboard";
    [ObservableProperty] private bool    _isBusy;
    [ObservableProperty] private string  _statusMessage = string.Empty;

    public DashboardViewModel   DashboardVm   { get; }
    public MemoryViewModel      MemoryVm      { get; }
    public ServicesViewModel    ServicesVm    { get; }
    public VisualViewModel      VisualVm      { get; }
    public GameBoosterViewModel GameBoosterVm { get; }
    public SettingsViewModel    SettingsVm    { get; }
    public ToolsViewModel       ToolsVm       { get; }
    public TaskSleepViewModel   TaskSleepVm   { get; }
    public BloatwareViewModel   BloatwareVm   { get; }

    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _heartbeatTimer;   // dedicated 1-second CrashGuard heartbeat
    private static readonly TimeSpan FocusedInterval   = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UnfocusedInterval = TimeSpan.FromSeconds(5);

    public MainViewModel(
        DashboardViewModel   dashboardVm,
        MemoryViewModel      memoryVm,
        ServicesViewModel    servicesVm,
        VisualViewModel      visualVm,
        GameBoosterViewModel gameBoosterVm,
        SettingsViewModel    settingsVm,
        ToolsViewModel       toolsVm,
        TaskSleepViewModel   taskSleepVm,
        BloatwareViewModel   bloatwareVm)
    {
        DashboardVm   = dashboardVm;
        MemoryVm      = memoryVm;
        ServicesVm    = servicesVm;
        VisualVm      = visualVm;
        GameBoosterVm = gameBoosterVm;
        SettingsVm    = settingsVm;
        ToolsVm       = toolsVm;
        TaskSleepVm   = taskSleepVm;
        BloatwareVm   = bloatwareVm;
        CurrentView   = dashboardVm;

        // Dedicated 1-second heartbeat — always ticks regardless of app focus or refresh rate.
        // The refresh timer slows to 5 s when unfocused, which would race the 5-second watchdog
        // timeout and produce false crash reports whenever boost runs in the background.
        _heartbeatTimer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _heartbeatTimer.Tick += (_, _) => CrashGuard.Heartbeat();
        _heartbeatTimer.Start();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = FocusedInterval
        };
        _refreshTimer.Tick += async (_, _) => await SafeRefreshAsync();
        _refreshTimer.Start();

        _ = SafeRefreshAsync();
    }

    public void SetFocused(bool focused)
    {
        _refreshTimer.Interval = focused ? FocusedInterval : UnfocusedInterval;
    }

    private async Task SafeRefreshAsync()
    {
        try
        {
            if (CurrentView is IAutoRefreshable refreshable)
                await refreshable.RefreshAsync();
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error("MainViewModel", $"Refresh crashed in {ActiveSection}", ex);
            CrashReportWindow.ShowError(ex, $"{ActiveSection} — Auto Refresh");
        }
    }

    [RelayCommand]
    private void Navigate(string section)
    {
        try
        {
            CrashGuard.Mark($"Navigating to {section}");

            ActiveSection = section;
            var next = section switch
            {
                "Dashboard"   => (object)DashboardVm,
                "Memory"      => MemoryVm,
                "Services"    => ServicesVm,
                "Visual"      => VisualVm,
                "GameBooster" => GameBoosterVm,
                "Settings"    => SettingsVm,
                "Tools"       => ToolsVm,
                "TaskSleep"   => TaskSleepVm,
                "Bloatware"   => BloatwareVm,
                _             => (object)DashboardVm
            };
            CurrentView = next;

            CrashGuard.Mark($"View loaded for {section}, starting refresh");

            if (next is IAutoRefreshable refreshable)
                _ = SafeRefreshOnNavigateAsync(refreshable, section);
            else
                CrashGuard.Clear();
        }
        catch (Exception ex)
        {
            CrashGuard.Mark($"NAVIGATE CRASH: {section} — {ex.GetType().Name}: {ex.Message}");
            LoggerService.Instance.Error("MainViewModel", $"Navigation to {section} failed", ex);
            CrashReportWindow.ShowError(ex, $"{section} — Navigation Error");
        }
    }

    private async Task SafeRefreshOnNavigateAsync(IAutoRefreshable refreshable, string section)
    {
        try
        {
            CrashGuard.Mark($"{section} — RefreshAsync starting");
            await refreshable.RefreshAsync();
            CrashGuard.Clear();
        }
        catch (Exception ex)
        {
            CrashGuard.Mark($"{section} REFRESH CRASH: {ex.GetType().Name}: {ex.Message}");
            LoggerService.Instance.Error("MainViewModel", $"Navigation refresh crashed for {section}", ex);
            CrashReportWindow.ShowError(ex, $"{section} — Load Error");
            CrashGuard.Clear();
        }
    }
}
