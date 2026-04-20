// ════════════════════════════════════════════════════════════════════════════
// TaskSleepViewModel.cs  ·  Task Sleep settings UI and whitelist management
// ════════════════════════════════════════════════════════════════════════════
//
// Creates and owns a TaskSleepService instance internally. Exposes all settings
// as [ObservableProperty] fields; OnChanged callbacks call SaveSettings and push
// a rebuilt TaskSleepSettings to the running service. Manages the whitelist
// ObservableCollection (apps that are never napped) and displays the live monitor
// feed (throttled processes, recent events) via MonitorSnapshot.
//
// QUICK EDIT GUIDE
//   To add a new setting → add [ObservableProperty] field + OnChanged callback
//                          + wire into BuildSettings / LoadSettings / SaveSettings
//
// RELATED FILES
//   TaskSleepService.cs            — background throttle monitor (owns the thread)
//   Models/TaskSleepSettings.cs    — all config fields with default values
//   Models/TaskSleepAppRule.cs     — per-app rule data record
//   Views/TaskSleepView.xaml       — binds all settings controls and live monitor
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Systema.Core;
using Systema.Models;
using Systema.Services;
// Aliases avoid ambiguity with System.Windows.Forms.TextBox (WinForms is referenced via UseWindowsForms).
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfKeyboard = System.Windows.Input.Keyboard;
using WpfWindow = System.Windows.Window;
using WpfVisualTreeHelper = System.Windows.Media.VisualTreeHelper;
using WpfDependencyObject = System.Windows.DependencyObject;

namespace Systema.ViewModels;

public partial class TaskSleepViewModel : ObservableObject, IDisposable
{
    private static readonly LoggerService _log = LoggerService.Instance;

    private const string RegKey = @"SOFTWARE\Systema\TaskSleep";

    private static readonly string RulesFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Systema", "tasksleep_rules.json");

    private readonly TaskSleepService _service;

    // ── Observable properties ─────────────────────────────────────────────────

    // ── Core Controls ────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isEnabled               = true;

    [ObservableProperty] private bool _napChildrenEnabled     = false;

    // ── CPU Thresholds (fixed defaults — preset selector removed in 1.7.32) ──
    [ObservableProperty] private int _systemCpuTriggerPercent = 12;
    [ObservableProperty] private int _processCpuStartPercent  = 7;
    [ObservableProperty] private int _processCpuStopPercent   = 3;
    [ObservableProperty] private int _timeOverQuotaMs         = 1500;
    [ObservableProperty] private int _minAdjustmentDurationMs = 5000;
    [ObservableProperty] private int _maxAdjustmentDurationMs = 30000;

    // ── Minimize Nap ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _minimizeNapEnabled              = true;
    [ObservableProperty] private int  _minimizedBriefWakeIntervalMs    = 60_000;
    [ObservableProperty] private int  _minimizedBriefWakeDurationMs    = 10_000;
    [ObservableProperty] private int  _minimizeDeepSleepThresholdMs    = 600_000;
    [ObservableProperty] private int  _minimizeDeepSleepWakeIntervalMs = 300_000;

    // (LowerMemoryPriority, TrimWorkingSet, AdaptiveTick always on — hardcoded in BuildSettings)

    // ── Whitelist (apps that are never napped) ────────────────────────────────
    /// <summary>Process names that Task Sleep will never touch, shown as the whitelist in the UI.</summary>
    public ObservableCollection<string> Whitelist { get; } = new();

    [ObservableProperty] private string _whitelistNewApp = "";
    [ObservableProperty] private string? _selectedRunningProcess;
    [ObservableProperty] private List<string> _runningProcessNames = new();

    // ── Tray Nap ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _trayNapEnabled              = true;
    [ObservableProperty] private int  _trayBriefWakeIntervalMs     = 300_000;
    [ObservableProperty] private int  _trayBriefWakeDurationMs     = 10_000;
    [ObservableProperty] private bool _trayDeepSleepEnabled        = true;
    [ObservableProperty] private int  _trayDeepSleepThresholdMs    = 600_000;
    [ObservableProperty] private int  _trayDeepSleepWakeIntervalMs = 600_000;

    // ── CPU Cap ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _nappedCpuCapEnabled     = true;
    [ObservableProperty] private int  _nappedCpuCapPercent     = 3;
    [ObservableProperty] private int  _briefWakeCpuCapPercent  = 7;

    // ── Game Mode interaction ────────────────────────────────────────────────
    [ObservableProperty] private bool _suppressBriefWakesDuringGameMode = true;

    // ── Brief Wake Concurrency ────────────────────────────────────────────────
    [ObservableProperty] private int _maxConcurrentBriefWakes = 3;

    // ── Advanced Features ────────────────────────────────────────────────────
    [ObservableProperty] private bool _elevatedProcessGuardEnabled   = true;
    [ObservableProperty] private bool _multiMonitorAwarenessEnabled  = true;
    [ObservableProperty] private bool _processGroupAwarenessEnabled  = true;

    // ── Background / Idle Nap ────────────────────────────────────────────────
    [ObservableProperty] private bool _backgroundNapEnabled          = true;
    [ObservableProperty] private int  _backgroundNapAfterMs          = 180_000;
    [ObservableProperty] private bool _idleNapEnabled                = true;
    [ObservableProperty] private int  _idleNapAfterMs                = 120_000;

    // ── Beta Features ────────────────────────────────────────────────────────
    [ObservableProperty] private bool _networkActivityGuardEnabled   = false;
    [ObservableProperty] private int  _networkActivityThresholdKBps  = 50;
    [ObservableProperty] private bool _diskIoGuardEnabled            = false;
    [ObservableProperty] private int  _diskIoThresholdKBps           = 100;
    [ObservableProperty] private bool _smartAggressiveNapEnabled     = false;
    [ObservableProperty] private int  _smartAggressiveCpuThresholdPercent = 1;
    [ObservableProperty] private int  _smartAggressiveTickCount      = 5;
    [ObservableProperty] private bool _notificationGracePeriodEnabled = false;
    [ObservableProperty] private int  _notificationGracePeriodMs     = 15_000;

    // ── UI Display ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _cpuFreedDisplay  = "";
    [ObservableProperty] private bool   _cpuFreedVisible  = false;
    [ObservableProperty] private bool   _showAllProcesses = false;
    [ObservableProperty] private string _systemCpuDisplay      = "System CPU: —";
    [ObservableProperty] private string _throttledCountDisplay = "0 napping";

    // ── UI State ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isAdvancedExpanded = false;

    public ObservableCollection<ProcessSnapshot> LiveProcesses { get; } = new();
    public ObservableCollection<MonitorEvent>    RecentEvents  { get; } = new();

    private readonly DispatcherTimer _monitorTimer;
    private readonly DispatcherTimer _processRefreshTimer;
    private bool _isGameModeActive;

    [ObservableProperty] private string _statusMessage = "Task Sleep is off.";

    // ── Constructor ───────────────────────────────────────────────────────────

    public TaskSleepViewModel()
    {
        // Create the service first so property-change callbacks triggered by
        // LoadSettings() can safely call _service methods.
        _service = new TaskSleepService(BuildSettings());
        _service.StatusChanged += msg =>
            Application.Current?.Dispatcher.BeginInvoke(() => StatusMessage = msg);
        _service.ProcessAutoWhitelisted += name =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!Whitelist.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    Whitelist.Add(name);
                    SaveAndPushWhitelist();
                }
            });

        LoadSettings();
        LoadWhitelist();

        // Explicitly sync service state after loading settings.
        // CommunityToolkit.Mvvm skips OnIsEnabledChanged when the loaded value
        // equals the field default (both true), so the service would never start
        // on subsequent launches without this explicit call.
        if (IsEnabled) _service.Start();

        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _monitorTimer.Tick += (_, _) => RefreshMonitor();
        _monitorTimer.Start();

        // Auto-refresh the running process picker every 15 s so newly-launched
        // apps appear without the user having to click the refresh button.
        RefreshRunning();
        _processRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _processRefreshTimer.Tick += (_, _) => RefreshRunning();
        _processRefreshTimer.Start();

        _log.Info("TaskSleepViewModel", $"Initialized — enabled={IsEnabled}");
    }

    /// <summary>
    /// Called by App.xaml.cs when GameBoosterService activates or deactivates boost.
    /// While game mode is active, brief idle wakes are suppressed so background processes
    /// stay napped and the CPU stays fully available to the game.
    /// </summary>
    public void SetGameMode(bool active)
    {
        _isGameModeActive = active;
        _service.UpdateSettings(BuildSettings());
        _log.Info("TaskSleepViewModel", $"Game mode {(active ? "activated" : "deactivated")} — brief wakes {(active ? "suppressed" : "restored")}");
    }

    // ── Property change callbacks ─────────────────────────────────────────────

    partial void OnIsEnabledChanged(bool value)
    {
        _service.UpdateSettings(BuildSettings());
        SaveSettings();
        if (value) _service.Start();
        else       _service.Stop();
    }

    partial void OnNapChildrenEnabledChanged(bool value)    => PushSettings();

    partial void OnSystemCpuTriggerPercentChanged(int value)  => PushSettings();
    partial void OnProcessCpuStartPercentChanged(int value)   => PushSettings();
    partial void OnProcessCpuStopPercentChanged(int value)    => PushSettings();
    partial void OnTimeOverQuotaMsChanged(int value)          => PushSettings();
    partial void OnMinAdjustmentDurationMsChanged(int value)  => PushSettings();
    partial void OnMaxAdjustmentDurationMsChanged(int value)  => PushSettings();

    partial void OnMinimizeNapEnabledChanged(bool value)              => PushSettings();
    partial void OnMinimizedBriefWakeIntervalMsChanged(int value)    => PushSettings();
    partial void OnMinimizedBriefWakeDurationMsChanged(int value)    => PushSettings();
    partial void OnMinimizeDeepSleepThresholdMsChanged(int value)    => PushSettings();
    partial void OnMinimizeDeepSleepWakeIntervalMsChanged(int value) => PushSettings();

    partial void OnTrayNapEnabledChanged(bool value)              => PushSettings();
    partial void OnTrayBriefWakeIntervalMsChanged(int value)    => PushSettings();
    partial void OnTrayBriefWakeDurationMsChanged(int value)    => PushSettings();
    partial void OnTrayDeepSleepEnabledChanged(bool value)      => PushSettings();
    partial void OnTrayDeepSleepThresholdMsChanged(int value)   => PushSettings();
    partial void OnTrayDeepSleepWakeIntervalMsChanged(int value) => PushSettings();

    partial void OnNappedCpuCapEnabledChanged(bool value)    => PushSettings();
    partial void OnNappedCpuCapPercentChanged(int value)
    {
        _log.Info("TaskSleepViewModel", $"OnNappedCpuCapPercentChanged fired: value={value}");
        int c = Math.Clamp(value, 1, 100);
        if (c != value) { NappedCpuCapPercent = c; return; }
        _log.Info("TaskSleepViewModel", $"OnNappedCpuCapPercentChanged calling PushSettings (clamped={c})");
        PushSettings();
    }
    partial void OnBriefWakeCpuCapPercentChanged(int value)
    {
        int c = Math.Clamp(value, 1, 100);
        if (c != value) { BriefWakeCpuCapPercent = c; return; }
        PushSettings();
    }
    partial void OnSuppressBriefWakesDuringGameModeChanged(bool value) => PushSettings();


    partial void OnMaxConcurrentBriefWakesChanged(int value)
    {
        int c = Math.Clamp(value, 1, 10);
        if (c != value) { MaxConcurrentBriefWakes = c; return; }
        PushSettings();
    }

    // ── Advanced + Beta feature callbacks ────────────────────────────────────
    partial void OnElevatedProcessGuardEnabledChanged(bool value)        => PushSettings();
    partial void OnMultiMonitorAwarenessEnabledChanged(bool value)       => PushSettings();
    partial void OnNetworkActivityGuardEnabledChanged(bool value)        => PushSettings();
    partial void OnNetworkActivityThresholdKBpsChanged(int value)
    {
        int c = Math.Clamp(value, 1, 10_000);
        if (c != value) { NetworkActivityThresholdKBps = c; return; }
        PushSettings();
    }
    partial void OnProcessGroupAwarenessEnabledChanged(bool value)       => PushSettings();
    partial void OnDiskIoGuardEnabledChanged(bool value)                 => PushSettings();
    partial void OnDiskIoThresholdKBpsChanged(int value)
    {
        int c = Math.Clamp(value, 1, 10_000);
        if (c != value) { DiskIoThresholdKBps = c; return; }
        PushSettings();
    }
    partial void OnSmartAggressiveNapEnabledChanged(bool value)          => PushSettings();
    partial void OnSmartAggressiveCpuThresholdPercentChanged(int value)  => PushSettings();
    partial void OnSmartAggressiveTickCountChanged(int value)            => PushSettings();
    partial void OnNotificationGracePeriodEnabledChanged(bool value)     => PushSettings();
    partial void OnNotificationGracePeriodMsChanged(int value)           => PushSettings();
    partial void OnBackgroundNapEnabledChanged(bool value)               => PushSettings();
    partial void OnBackgroundNapAfterMsChanged(int value)                => PushSettings();
    partial void OnIdleNapEnabledChanged(bool value)                     => PushSettings();
    partial void OnIdleNapAfterMsChanged(int value)                      => PushSettings();

    // (LowerMemoryPriority, TrimWorkingSet, AdaptiveTick, EnforceSettings, SoftNap — hardcoded)

    // ── Human-friendly helpers for XAML bindings (convert ms ↔ seconds/minutes) ─

    /// <summary>MinimizedBriefWakeIntervalMs in whole seconds for the UI text box.</summary>
    public int MinimizedBriefWakeIntervalSeconds
    {
        get => MinimizedBriefWakeIntervalMs / 1000;
        set { MinimizedBriefWakeIntervalMs = Math.Max(value, 1) * 1000; OnPropertyChanged(); }
    }

    /// <summary>MinimizedBriefWakeDurationMs in whole seconds for the UI text box.</summary>
    public int MinimizedBriefWakeDurationSeconds
    {
        get => MinimizedBriefWakeDurationMs / 1000;
        set { MinimizedBriefWakeDurationMs = Math.Max(value, 1) * 1000; OnPropertyChanged(); }
    }

    /// <summary>MinimizeDeepSleepThresholdMs in whole minutes for the UI text box.</summary>
    public int MinimizeDeepSleepThresholdMinutes
    {
        get => MinimizeDeepSleepThresholdMs / 60_000;
        set { MinimizeDeepSleepThresholdMs = Math.Max(value, 1) * 60_000; OnPropertyChanged(); }
    }

    /// <summary>TrayBriefWakeIntervalMs in whole minutes for the UI text box.</summary>
    public int TrayBriefWakeIntervalMinutes
    {
        get => TrayBriefWakeIntervalMs / 60_000;
        set { TrayBriefWakeIntervalMs = Math.Max(value, 1) * 60_000; OnPropertyChanged(); }
    }

    /// <summary>TrayBriefWakeDurationMs in whole seconds for the UI text box.</summary>
    public int TrayBriefWakeDurationSeconds
    {
        get => TrayBriefWakeDurationMs / 1000;
        set { TrayBriefWakeDurationMs = Math.Max(value, 1) * 1000; OnPropertyChanged(); }
    }

    /// <summary>TrayDeepSleepThresholdMs in whole minutes for the UI text box.</summary>
    public int TrayDeepSleepThresholdMinutes
    {
        get => TrayDeepSleepThresholdMs / 60_000;
        set { TrayDeepSleepThresholdMs = Math.Max(value, 1) * 60_000; OnPropertyChanged(); }
    }

    /// <summary>TrayDeepSleepWakeIntervalMs in whole minutes for the UI text box.</summary>
    public int TrayDeepSleepWakeIntervalMinutes
    {
        get => TrayDeepSleepWakeIntervalMs / 60_000;
        set { TrayDeepSleepWakeIntervalMs = Math.Max(value, 1) * 60_000; OnPropertyChanged(); }
    }

    /// <summary>NotificationGracePeriodMs in whole seconds for the UI.</summary>
    public int NotificationGracePeriodSeconds
    {
        get => NotificationGracePeriodMs / 1000;
        set { NotificationGracePeriodMs = Math.Max(value, 1) * 1000; OnPropertyChanged(); }
    }


    public int TimeOverQuotaSeconds
    {
        get => TimeOverQuotaMs / 1000;
        set { TimeOverQuotaMs = Math.Max(value, 1) * 1000; OnPropertyChanged(); }
    }
    public int MinAdjustmentDurationSeconds
    {
        get => MinAdjustmentDurationMs / 1000;
        set { MinAdjustmentDurationMs = Math.Max(value, 1) * 1000; OnPropertyChanged(); }
    }
    public int MaxAdjustmentDurationSeconds
    {
        get => MaxAdjustmentDurationMs / 1000;
        set { MaxAdjustmentDurationMs = Math.Max(value, 1) * 1000; OnPropertyChanged(); }
    }

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    partial void OnShowAllProcessesChanged(bool value) => RefreshMonitor();

    partial void OnSelectedRunningProcessChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            WhitelistNewApp = value;
    }

    private void PushSettings()
    {
        _service.UpdateSettings(BuildSettings());
        SaveSettings();
    }

    private void RefreshMonitor()
    {
        var snapshot = _service.GetLatestSnapshot();

        SystemCpuDisplay = snapshot != null ? $"System CPU: {snapshot.SystemCpuPercent:F0}%" : "System CPU: —";
        int nSleeping = snapshot?.TotalThrottled ?? 0;
        int nPending  = snapshot?.Processes.Count(p => p.IsPendingNap) ?? 0;
        ThrottledCountDisplay = nSleeping > 0
            ? (nPending > 0 ? $"{nSleeping} napping, {nPending} pending" : $"{nSleeping} napping")
            : (nPending > 0 ? $"{nPending} pending nap" : "all awake");
        CpuFreedDisplay = IsEnabled ? "Task Sleep is improving system responsiveness" : "";
        CpuFreedVisible = IsEnabled;

        if (snapshot == null || !IsEnabled)
        {
            LiveProcesses.Clear();
            return;
        }

        // Update live process list — default view shows throttled AND pending-nap processes
        var procs = ShowAllProcesses
            ? snapshot.Processes
            : (IReadOnlyList<ProcessSnapshot>)snapshot.Processes
                .Where(p => p.IsThrottled || p.IsPendingNap).ToList();

        LiveProcesses.Clear();
        foreach (var p in procs)
            LiveProcesses.Add(p);

        // Update recent events — newest first, cap at 30
        var events = snapshot.RecentEvents;
        RecentEvents.Clear();
        for (int i = events.Count - 1; i >= Math.Max(0, events.Count - 30); i--)
            RecentEvents.Add(events[i]);
    }

    // ── Whitelist commands ────────────────────────────────────────────────────

    [RelayCommand]
    private void AddToWhitelist()
    {
        string name = WhitelistNewApp.Trim()
            .Replace(".exe", "", StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();

        if (string.IsNullOrEmpty(name)) return;
        if (Whitelist.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) return;

        Whitelist.Add(name);
        WhitelistNewApp = "";
        SelectedRunningProcess = null;
        SaveAndPushWhitelist();
    }

    [RelayCommand]
    private void RemoveFromWhitelist(string name)
    {
        var existing = Whitelist.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Whitelist.Remove(existing);
            SaveAndPushWhitelist();
        }
    }

    /// <summary>Immediately wake a napping process (one-time, without whitelisting).</summary>
    [RelayCommand]
    private void WakeProcess(ProcessSnapshot? snapshot)
    {
        if (snapshot == null) return;
        _service.WakeProcess(snapshot.Name);
        StatusMessage = $"Woke up {snapshot.Name} — it may nap again if it stays over the CPU threshold.";
    }

    /// <summary>Add a process to the whitelist from the live monitor (permanent protection).</summary>
    [RelayCommand]
    private void WhitelistProcess(ProcessSnapshot? snapshot)
    {
        if (snapshot == null) return;
        string name = snapshot.Name.ToLowerInvariant();
        if (!Whitelist.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Whitelist.Add(name);
            SaveAndPushWhitelist();
        }
        _service.WakeProcess(name); // also wake it immediately
        StatusMessage = $"Whitelisted {snapshot.Name} — it will never be napped again.";
    }

    [RelayCommand]
    private void RefreshRunning()
    {
        try
        {
            RunningProcessNames = Process.GetProcesses()
                .Select(p => p.ProcessName.ToLowerInvariant())
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
        }
        catch (Exception ex) { LoggerService.Instance.Warn("TaskSleepViewModel", $"RefreshRunning failed: {ex.Message}"); }
    }

    private void SaveAndPushWhitelist()
    {
        SaveWhitelist();
        _service.UpdateSettings(BuildSettings());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private TaskSleepSettings BuildSettings() => new()
    {
        IsEnabled               = IsEnabled,
        // Always-on core behavior (hardcoded — no UI toggle needed)
        LowerCpuPriority        = true,
        IgnoreForeground        = true,
        ActOnForegroundChildren = false,
        ExcludeSystemServices   = true,
        EnableEfficiencyMode    = true,
        PersistentNapEnabled    = true,
        EnforceSettings         = true,
        SoftNapEnabled          = false,
        // LowerGpuPriority intentionally disabled: D3DKMT Idle tier disrupts the shared
        // HAGS flip queue and can break VSync for foreground games. See TaskSleepSettings.
        LowerGpuPriority        = false,
        LowerIoPriority         = true,
        DetectECores            = true,
        MoveToECores            = true,
        LowerMemoryPriority     = true,
        TrimWorkingSet          = true,
        AdaptiveTick            = true,
        // User-configurable
        // CpuTriggeredNapEnabled is hardcoded false — MinimizeNap/TrayNap/BackgroundNap/IdleNap
        // already cover every path that needs to nap something. A plain CPU trigger risked
        // throttling visible apps the user was actively using, which is the wrong default.
        CpuTriggeredNapEnabled  = false,
        NapChildrenEnabled      = NapChildrenEnabled,
        SystemCpuTriggerPercent = SystemCpuTriggerPercent,
        ProcessCpuStartPercent  = ProcessCpuStartPercent,
        ProcessCpuStopPercent   = ProcessCpuStopPercent,
        TimeOverQuotaMs         = TimeOverQuotaMs,
        MinAdjustmentDurationMs = MinAdjustmentDurationMs,
        MaxAdjustmentDurationMs = MaxAdjustmentDurationMs,
        MinimizeNapEnabled              = MinimizeNapEnabled,
        MinimizedBriefWakeIntervalMs    = MinimizedBriefWakeIntervalMs,
        MinimizedBriefWakeDurationMs    = MinimizedBriefWakeDurationMs,
        MinimizeDeepSleepThresholdMs    = Math.Max(MinimizeDeepSleepThresholdMs, 60_000),
        MinimizeDeepSleepWakeIntervalMs = Math.Max(MinimizeDeepSleepWakeIntervalMs, 10_000),
        TrayNapEnabled              = TrayNapEnabled,
        TrayBriefWakeIntervalMs     = TrayBriefWakeIntervalMs,
        TrayBriefWakeDurationMs     = TrayBriefWakeDurationMs,
        TrayDeepSleepEnabled        = TrayDeepSleepEnabled,
        // Tray deep sleep threshold is unified with the minimize deep sleep threshold
        // (one "Deep sleep after" setting in the UI). One knob, no divergence.
        TrayDeepSleepThresholdMs    = Math.Max(MinimizeDeepSleepThresholdMs, 60_000),
        TrayDeepSleepWakeIntervalMs = Math.Max(TrayDeepSleepWakeIntervalMs, 60_000),
        NappedCpuCapEnabled         = NappedCpuCapEnabled,
        NappedCpuCapPercent         = Math.Clamp(NappedCpuCapPercent, 1, 100),
        BriefWakeCpuCapPercent      = Math.Clamp(BriefWakeCpuCapPercent, 1, 100),
        SuppressBriefWakesDuringGameMode = SuppressBriefWakesDuringGameMode,
        MaxConcurrentBriefWakes     = Math.Clamp(MaxConcurrentBriefWakes, 1, 10),
        // Advanced features
        ElevatedProcessGuardEnabled        = ElevatedProcessGuardEnabled,
        MultiMonitorAwarenessEnabled       = MultiMonitorAwarenessEnabled,
        NetworkActivityGuardEnabled        = NetworkActivityGuardEnabled,
        NetworkActivityThresholdKBps       = Math.Clamp(NetworkActivityThresholdKBps, 1, 10_000),
        ProcessGroupAwarenessEnabled       = ProcessGroupAwarenessEnabled,
        DiskIoGuardEnabled                 = DiskIoGuardEnabled,
        DiskIoThresholdKBps                = Math.Clamp(DiskIoThresholdKBps, 1, 10_000),
        SmartAggressiveNapEnabled          = SmartAggressiveNapEnabled,
        SmartAggressiveCpuThresholdPercent = Math.Clamp(SmartAggressiveCpuThresholdPercent, 1, 50),
        SmartAggressiveTickCount           = Math.Clamp(SmartAggressiveTickCount, 2, 30),
        NotificationGracePeriodEnabled     = NotificationGracePeriodEnabled,
        NotificationGracePeriodMs          = Math.Clamp(NotificationGracePeriodMs, 1_000, 120_000),
        BackgroundNapEnabled               = BackgroundNapEnabled,
        BackgroundNapAfterMs               = Math.Clamp(BackgroundNapAfterMs, 30_000, 600_000),
        IdleNapEnabled                     = IdleNapEnabled,
        IdleNapCpuThreshold                = 0.5,
        IdleNapAfterMs                     = Math.Clamp(IdleNapAfterMs, 30_000, 600_000),
        // Whitelist entries are stored as blacklist (IsBlacklisted=true) AppRules internally
        AppRules                = Whitelist.Select(name => new TaskSleepAppRule
                                  {
                                      ProcessName  = name,
                                      IsBlacklisted = true
                                  }).ToList(),
        IsGameModeActive        = _isGameModeActive,
    };

    // ── Registry persistence (scalar settings) ────────────────────────────────

    private void LoadSettings()
    {
        _log.Info("TaskSleepViewModel", "LoadSettings starting — reading registry");
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: false);
            if (key == null) { _log.Warn("TaskSleepViewModel", "LoadSettings: registry key not found — using defaults"); return; }

            IsEnabled               = ReadBool(key, "IsEnabled",               true);
            NapChildrenEnabled      = ReadBool(key, "NapChildrenEnabled",      false);
            SystemCpuTriggerPercent = Math.Clamp(ReadInt(key, "SystemCpuTriggerPercent",  12), 1, 100);
            ProcessCpuStartPercent  = Math.Clamp(ReadInt(key, "ProcessCpuStartPercent",    7), 1, 100);
            ProcessCpuStopPercent   = Math.Clamp(ReadInt(key, "ProcessCpuStopPercent",     3), 0, 100);
            TimeOverQuotaMs         = Math.Clamp(ReadInt(key, "TimeOverQuotaMs",        1500), 100, 60_000);
            MinAdjustmentDurationMs = Math.Clamp(ReadInt(key, "MinAdjustmentDurationMs", 5000), 500, 300_000);
            MaxAdjustmentDurationMs = Math.Clamp(ReadInt(key, "MaxAdjustmentDurationMs", 30000), 1000, 3_600_000);
            MinimizeNapEnabled              = ReadBool(key, "MinimizeNapEnabled",              true);
            MinimizedBriefWakeIntervalMs    = Math.Clamp(ReadInt(key, "MinimizedBriefWakeIntervalMs",    60_000), 1_000, 3_600_000);
            MinimizedBriefWakeDurationMs    = Math.Clamp(ReadInt(key, "MinimizedBriefWakeDurationMs",    10_000), 500, 300_000);
            MinimizeDeepSleepThresholdMs    = Math.Clamp(ReadInt(key, "MinimizeDeepSleepThresholdMs",   600_000), 60_000, 3_600_000);
            MinimizeDeepSleepWakeIntervalMs = Math.Clamp(ReadInt(key, "MinimizeDeepSleepWakeIntervalMs", 300_000), 10_000, 3_600_000);
            TrayNapEnabled              = ReadBool(key, "TrayNapEnabled",          true);
            TrayBriefWakeIntervalMs     = Math.Clamp(ReadInt(key, "TrayBriefWakeIntervalMs",  300_000), 1_000, 3_600_000);
            TrayBriefWakeDurationMs     = Math.Clamp(ReadInt(key, "TrayBriefWakeDurationMs",  10_000), 500, 300_000);
            TrayDeepSleepEnabled        = ReadBool(key, "TrayDeepSleepEnabled",        true);
            TrayDeepSleepThresholdMs    = Math.Clamp(ReadInt(key, "TrayDeepSleepThresholdMs",    600_000), 60_000, 3_600_000);
            TrayDeepSleepWakeIntervalMs = Math.Clamp(ReadInt(key, "TrayDeepSleepWakeIntervalMs", 600_000), 60_000, 3_600_000);
            NappedCpuCapEnabled         = ReadBool(key, "NappedCpuCapEnabled",         true);
            NappedCpuCapPercent         = Math.Clamp(ReadInt(key, "NappedCpuCapPercent", 3), 1, 100);
            BriefWakeCpuCapPercent      = Math.Clamp(ReadInt(key, "BriefWakeCpuCapPercent", 7), 1, 100);
            SuppressBriefWakesDuringGameMode = ReadBool(key, "SuppressBriefWakesDuringGameMode", true);
            MaxConcurrentBriefWakes     = Math.Clamp(ReadInt(key, "MaxConcurrentBriefWakes",  3), 1, 10);
            // Advanced + Beta features
            ElevatedProcessGuardEnabled        = ReadBool(key, "ElevatedProcessGuardEnabled",        true);
            MultiMonitorAwarenessEnabled       = ReadBool(key, "MultiMonitorAwarenessEnabled",       true);
            NetworkActivityGuardEnabled        = ReadBool(key, "NetworkActivityGuardEnabled",        false);
            NetworkActivityThresholdKBps       = Math.Clamp(ReadInt(key, "NetworkActivityThresholdKBps",  50), 1, 10_000);
            ProcessGroupAwarenessEnabled       = ReadBool(key, "ProcessGroupAwarenessEnabled",       true);
            DiskIoGuardEnabled                 = ReadBool(key, "DiskIoGuardEnabled",                 false);
            DiskIoThresholdKBps                = Math.Clamp(ReadInt(key, "DiskIoThresholdKBps",          100), 1, 10_000);
            SmartAggressiveNapEnabled          = ReadBool(key, "SmartAggressiveNapEnabled",          false);
            SmartAggressiveCpuThresholdPercent = Math.Clamp(ReadInt(key, "SmartAggressiveCpuThresholdPercent", 1), 1, 50);
            SmartAggressiveTickCount           = Math.Clamp(ReadInt(key, "SmartAggressiveTickCount",          5), 2, 30);
            NotificationGracePeriodEnabled     = ReadBool(key, "NotificationGracePeriodEnabled",     false);
            NotificationGracePeriodMs          = Math.Clamp(ReadInt(key, "NotificationGracePeriodMs",    15_000), 1_000, 120_000);
            BackgroundNapEnabled               = ReadBool(key, "BackgroundNapEnabled",               true);
            BackgroundNapAfterMs               = Math.Clamp(ReadInt(key, "BackgroundNapAfterMs",         180_000), 30_000, 600_000);
            IdleNapEnabled                     = ReadBool(key, "IdleNapEnabled",                     true);
            IdleNapAfterMs                     = Math.Clamp(ReadInt(key, "IdleNapAfterMs",               120_000), 30_000, 600_000);
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepViewModel", $"LoadSettings failed: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        var caller = new System.Diagnostics.StackTrace(1, false).ToString().Split('\n')[0].Trim();
        _log.Info("TaskSleepViewModel", $"SaveSettings called — NappedCpuCapPercent={NappedCpuCapPercent} — caller: {caller}");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
            if (key == null) { _log.Warn("TaskSleepViewModel", "SaveSettings: registry key creation failed (null)"); return; }

            key.SetValue("IsEnabled",               IsEnabled               ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("NapChildrenEnabled",      NapChildrenEnabled      ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("SystemCpuTriggerPercent", SystemCpuTriggerPercent,          RegistryValueKind.DWord);
            key.SetValue("ProcessCpuStartPercent",  ProcessCpuStartPercent,           RegistryValueKind.DWord);
            key.SetValue("ProcessCpuStopPercent",   ProcessCpuStopPercent,            RegistryValueKind.DWord);
            key.SetValue("TimeOverQuotaMs",         TimeOverQuotaMs,                  RegistryValueKind.DWord);
            key.SetValue("MinAdjustmentDurationMs", MinAdjustmentDurationMs,          RegistryValueKind.DWord);
            key.SetValue("MaxAdjustmentDurationMs", MaxAdjustmentDurationMs,          RegistryValueKind.DWord);
            key.SetValue("MinimizeNapEnabled",              MinimizeNapEnabled      ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("MinimizedBriefWakeIntervalMs",    MinimizedBriefWakeIntervalMs,     RegistryValueKind.DWord);
            key.SetValue("MinimizedBriefWakeDurationMs",    MinimizedBriefWakeDurationMs,     RegistryValueKind.DWord);
            key.SetValue("MinimizeDeepSleepThresholdMs",    MinimizeDeepSleepThresholdMs,     RegistryValueKind.DWord);
            key.SetValue("MinimizeDeepSleepWakeIntervalMs", MinimizeDeepSleepWakeIntervalMs,  RegistryValueKind.DWord);
            key.SetValue("TrayNapEnabled",              TrayNapEnabled       ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("TrayBriefWakeIntervalMs",     TrayBriefWakeIntervalMs,      RegistryValueKind.DWord);
            key.SetValue("TrayBriefWakeDurationMs",     TrayBriefWakeDurationMs,      RegistryValueKind.DWord);
            key.SetValue("TrayDeepSleepEnabled",        TrayDeepSleepEnabled  ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("TrayDeepSleepThresholdMs",    TrayDeepSleepThresholdMs,     RegistryValueKind.DWord);
            key.SetValue("TrayDeepSleepWakeIntervalMs", TrayDeepSleepWakeIntervalMs,  RegistryValueKind.DWord);
            key.SetValue("NappedCpuCapEnabled",         NappedCpuCapEnabled   ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("NappedCpuCapPercent",         NappedCpuCapPercent,          RegistryValueKind.DWord);
            key.SetValue("BriefWakeCpuCapPercent",      BriefWakeCpuCapPercent,       RegistryValueKind.DWord);
            key.SetValue("SuppressBriefWakesDuringGameMode", SuppressBriefWakesDuringGameMode ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("MaxConcurrentBriefWakes",     MaxConcurrentBriefWakes,      RegistryValueKind.DWord);
            // Advanced + Beta features
            key.SetValue("ElevatedProcessGuardEnabled",        ElevatedProcessGuardEnabled        ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("MultiMonitorAwarenessEnabled",       MultiMonitorAwarenessEnabled       ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("NetworkActivityGuardEnabled",        NetworkActivityGuardEnabled        ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("NetworkActivityThresholdKBps",       NetworkActivityThresholdKBps,                RegistryValueKind.DWord);
            key.SetValue("ProcessGroupAwarenessEnabled",       ProcessGroupAwarenessEnabled       ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("DiskIoGuardEnabled",                 DiskIoGuardEnabled                 ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("DiskIoThresholdKBps",                DiskIoThresholdKBps,                         RegistryValueKind.DWord);
            key.SetValue("SmartAggressiveNapEnabled",          SmartAggressiveNapEnabled          ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("SmartAggressiveCpuThresholdPercent", SmartAggressiveCpuThresholdPercent,          RegistryValueKind.DWord);
            key.SetValue("SmartAggressiveTickCount",           SmartAggressiveTickCount,                    RegistryValueKind.DWord);
            key.SetValue("NotificationGracePeriodEnabled",     NotificationGracePeriodEnabled     ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("NotificationGracePeriodMs",          NotificationGracePeriodMs,                   RegistryValueKind.DWord);
            key.SetValue("BackgroundNapEnabled",               BackgroundNapEnabled               ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("BackgroundNapAfterMs",               BackgroundNapAfterMs,                        RegistryValueKind.DWord);
            key.SetValue("IdleNapEnabled",                     IdleNapEnabled                     ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("IdleNapAfterMs",                     IdleNapAfterMs,                              RegistryValueKind.DWord);
            _log.Info("TaskSleepViewModel", $"SaveSettings completed successfully — NappedCpuCapPercent={NappedCpuCapPercent}");
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepViewModel", $"SaveSettings failed: {ex.Message}");
        }
    }

    // ── JSON persistence (whitelist) ──────────────────────────────────────────

    private void LoadWhitelist()
    {
        try
        {
            if (!File.Exists(RulesFilePath)) return;
            // Support both old format (List<TaskSleepAppRule>) and new format (List<string>)
            var json = File.ReadAllText(RulesFilePath);
            // Try new format first (string list), fall back to old object format
            try
            {
                var names = JsonSerializer.Deserialize<List<string>>(json);
                if (names != null)
                    foreach (var n in names)
                        if (!string.IsNullOrWhiteSpace(n)) Whitelist.Add(n.ToLowerInvariant());
            }
            catch
            {
                // Old format: migrate — only keep entries marked as blacklisted (never-nap)
                var models = JsonSerializer.Deserialize<List<TaskSleepAppRule>>(json);
                if (models != null)
                    foreach (var rule in models.Where(r => r.IsBlacklisted))
                        Whitelist.Add(rule.ProcessName.ToLowerInvariant());
                SaveWhitelist(); // re-save in new format
            }
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepViewModel", $"LoadWhitelist failed: {ex.Message}");
        }
    }

    private void SaveWhitelist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RulesFilePath)!);
            File.WriteAllText(RulesFilePath,
                JsonSerializer.Serialize(Whitelist.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepViewModel", $"SaveWhitelist failed: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Forces every pending TextBox edit (LostFocus binding) in every live WPF
    /// window to commit its source, then unconditionally persists every setting.
    /// Safe to call from any Dispose path (window close, tray Exit, OS shutdown).
    ///
    /// Fixes the "type a number then restart → value resets" bug: our XAML uses
    /// UpdateSourceTrigger=LostFocus, so a typed value in a still-focused
    /// TextBox never reaches the VM property unless we force the commit here.
    ///
    /// We walk the visual tree (not Keyboard.FocusedElement) because:
    ///   • A window hidden to tray loses focus tracking but its TextBoxes live on.
    ///   • Keyboard.FocusedElement returns null/wrong element for hidden windows.
    ///   • Tree walk catches every pending binding regardless of focus state.
    /// </summary>
    public void CommitPendingEditsAndSave()
    {
        try
        {
            var app = Application.Current;
            if (app != null)
            {
                Action commitAll = () =>
                {
                    foreach (WpfWindow w in app.Windows)
                        CommitAllTextBoxBindings(w);
                };
                if (app.Dispatcher.CheckAccess()) commitAll();
                else app.Dispatcher.Invoke(commitAll);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepViewModel", $"CommitPendingEdits (tree walk) failed: {ex.Message}");
        }

        // Unconditional safety-net save — idempotent and cheap, even if no
        // setter fired above. Covers every edge case (app crash mid-shutdown,
        // OS session-end, user clicks Exit without ever blurring a TextBox).
        try { SaveSettings();  } catch { /* SaveSettings logs its own failures */ }
        try { SaveWhitelist(); } catch { /* SaveWhitelist logs its own failures */ }
    }

    /// <summary>
    /// Recursively walks the visual tree rooted at <paramref name="parent"/> and
    /// calls <see cref="System.Windows.Data.BindingExpression.UpdateSource"/> on
    /// every TextBox's Text binding. Commits any pending LostFocus edits.
    /// </summary>
    private static void CommitAllTextBoxBindings(WpfDependencyObject? parent)
    {
        if (parent == null) return;
        if (parent is WpfTextBox tb)
        {
            try
            {
                var exp = tb.GetBindingExpression(WpfTextBox.TextProperty);
                exp?.UpdateSource();
            }
            catch { /* one bad binding shouldn't stop the rest */ }
        }
        int count = WpfVisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
            CommitAllTextBoxBindings(WpfVisualTreeHelper.GetChild(parent, i));
    }

    public void Dispose()
    {
        // Flush any pending user edits and persist before stopping timers / disposing service.
        CommitPendingEditsAndSave();

        _monitorTimer.Stop();
        _processRefreshTimer.Stop();
        _service.Dispose();
    }

    private static bool ReadBool(RegistryKey key, string name, bool defaultValue)
    {
        var val = key.GetValue(name);
        return val is int i ? i != 0 : defaultValue;
    }

    private static int ReadInt(RegistryKey key, string name, int defaultValue)
    {
        var val = key.GetValue(name);
        return val is int i ? i : defaultValue;
    }
}
