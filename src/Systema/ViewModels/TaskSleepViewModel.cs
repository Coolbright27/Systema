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

namespace Systema.ViewModels;

public partial class TaskSleepViewModel : ObservableObject
{
    private static readonly LoggerService _log = LoggerService.Instance;

    private const string RegKey = @"SOFTWARE\Systema\TaskSleep";

    private static readonly string RulesFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Systema", "tasksleep_rules.json");

    private readonly TaskSleepService _service;

    // ── Observable properties ─────────────────────────────────────────────────

    // ── Automatic Controls ────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isEnabled               = true;
    [ObservableProperty] private bool   _lowerCpuPriority        = true;
    [ObservableProperty] private bool   _ignoreForeground        = true;
    [ObservableProperty] private bool   _actOnForegroundChildren = false;
    [ObservableProperty] private bool   _excludeSystemServices   = true;
    [ObservableProperty] private bool   _enableEfficiencyMode    = true;

    // ── CPU Thresholds ────────────────────────────────────────────────────────
    [ObservableProperty] private int _systemCpuTriggerPercent = 12;
    [ObservableProperty] private int _processCpuStartPercent  = 7;
    [ObservableProperty] private int _processCpuStopPercent   = 3;
    [ObservableProperty] private int _timeOverQuotaMs         = 1000;
    [ObservableProperty] private int _minAdjustmentDurationMs = 1000;
    [ObservableProperty] private int _maxAdjustmentDurationMs = 3000;

    // ── GPU, I/O & Core Affinity ──────────────────────────────────────────────
    [ObservableProperty] private bool _lowerGpuPriority = true;
    [ObservableProperty] private bool _lowerIoPriority  = true;
    [ObservableProperty] private bool _detectECores     = true;
    [ObservableProperty] private bool _moveToECores     = true;

    // ── Persistent Nap ────────────────────────────────────────────────────────
    [ObservableProperty] private bool _persistentNapEnabled = true;

    // ── Minimize Nap ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _minimizeNapEnabled          = true;
    [ObservableProperty] private int  _minimizedBriefWakeIntervalMs = 60_000;
    [ObservableProperty] private int  _minimizedBriefWakeDurationMs = 10_000;

    // ── Advanced: Memory / Tick ────────────────────────────────────────────────
    [ObservableProperty] private bool _lowerMemoryPriority = true;
    [ObservableProperty] private bool _trimWorkingSet      = true;
    [ObservableProperty] private bool _adaptiveTick        = true;

    // ── Whitelist (apps that are never napped) ────────────────────────────────
    /// <summary>Process names that Task Sleep will never touch, shown as the whitelist in the UI.</summary>
    public ObservableCollection<string> Whitelist { get; } = new();

    [ObservableProperty] private string _whitelistNewApp = "";
    [ObservableProperty] private string? _selectedRunningProcess;
    [ObservableProperty] private List<string> _runningProcessNames = new();

    // ── Tray Nap ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _trayNapEnabled          = true;
    [ObservableProperty] private int  _trayBriefWakeIntervalMs = 300_000;
    [ObservableProperty] private int  _trayBriefWakeDurationMs = 10_000;

    // ── Monitoring & Enforcement ──────────────────────────────────────────────
    [ObservableProperty] private bool   _enforceSettings  = true;
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

        LoadSettings();
        LoadWhitelist();

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

    partial void OnLowerCpuPriorityChanged(bool value)        => PushSettings();
    partial void OnIgnoreForegroundChanged(bool value)        => PushSettings();
    partial void OnActOnForegroundChildrenChanged(bool value) => PushSettings();
    partial void OnExcludeSystemServicesChanged(bool value)   => PushSettings();
    partial void OnEnableEfficiencyModeChanged(bool value)    => PushSettings();

    partial void OnSystemCpuTriggerPercentChanged(int value)  => PushSettings();
    partial void OnProcessCpuStartPercentChanged(int value)   => PushSettings();
    partial void OnProcessCpuStopPercentChanged(int value)    => PushSettings();
    partial void OnTimeOverQuotaMsChanged(int value)          => PushSettings();
    partial void OnMinAdjustmentDurationMsChanged(int value)  => PushSettings();
    partial void OnMaxAdjustmentDurationMsChanged(int value)  => PushSettings();

    partial void OnLowerGpuPriorityChanged(bool value)      => PushSettings();
    partial void OnLowerIoPriorityChanged(bool value)       => PushSettings();
    partial void OnDetectECoresChanged(bool value)          => PushSettings();
    partial void OnMoveToECoresChanged(bool value)          => PushSettings();
    partial void OnPersistentNapEnabledChanged(bool value)  => PushSettings();

    partial void OnMinimizeNapEnabledChanged(bool value)           => PushSettings();
    partial void OnMinimizedBriefWakeIntervalMsChanged(int value)  => PushSettings();
    partial void OnMinimizedBriefWakeDurationMsChanged(int value)  => PushSettings();

    partial void OnTrayNapEnabledChanged(bool value)           => PushSettings();
    partial void OnTrayBriefWakeIntervalMsChanged(int value)   => PushSettings();
    partial void OnTrayBriefWakeDurationMsChanged(int value)   => PushSettings();

    partial void OnLowerMemoryPriorityChanged(bool value) => PushSettings();
    partial void OnTrimWorkingSetChanged(bool value)      => PushSettings();
    partial void OnAdaptiveTickChanged(bool value)        => PushSettings();

    partial void OnEnforceSettingsChanged(bool value)  => PushSettings();

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
        catch { }
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
        LowerCpuPriority        = LowerCpuPriority,
        IgnoreForeground        = IgnoreForeground,
        ActOnForegroundChildren = ActOnForegroundChildren,
        ExcludeSystemServices   = ExcludeSystemServices,
        EnableEfficiencyMode    = EnableEfficiencyMode,
        SystemCpuTriggerPercent = SystemCpuTriggerPercent,
        ProcessCpuStartPercent  = ProcessCpuStartPercent,
        ProcessCpuStopPercent   = ProcessCpuStopPercent,
        TimeOverQuotaMs         = TimeOverQuotaMs,
        MinAdjustmentDurationMs = MinAdjustmentDurationMs,
        MaxAdjustmentDurationMs = MaxAdjustmentDurationMs,
        LowerGpuPriority        = LowerGpuPriority,
        LowerIoPriority         = LowerIoPriority,
        DetectECores            = DetectECores,
        MoveToECores            = MoveToECores,
        PersistentNapEnabled    = PersistentNapEnabled,
        MinimizeNapEnabled           = MinimizeNapEnabled,
        MinimizedBriefWakeIntervalMs = MinimizedBriefWakeIntervalMs,
        MinimizedBriefWakeDurationMs = MinimizedBriefWakeDurationMs,
        TrayNapEnabled           = TrayNapEnabled,
        TrayBriefWakeIntervalMs  = TrayBriefWakeIntervalMs,
        TrayBriefWakeDurationMs  = TrayBriefWakeDurationMs,
        LowerMemoryPriority     = LowerMemoryPriority,
        TrimWorkingSet          = TrimWorkingSet,
        AdaptiveTick            = AdaptiveTick,
        // Whitelist entries are stored as blacklist (IsBlacklisted=true) AppRules internally
        AppRules                = Whitelist.Select(name => new TaskSleepAppRule
                                  {
                                      ProcessName  = name,
                                      IsBlacklisted = true
                                  }).ToList(),
        EnforceSettings         = EnforceSettings,
        IsGameModeActive        = _isGameModeActive,
    };

    // ── Registry persistence (scalar settings) ────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: false);
            if (key == null) return;

            IsEnabled               = ReadBool(key, "IsEnabled",               true);
            LowerCpuPriority        = ReadBool(key, "LowerCpuPriority",        true);
            IgnoreForeground        = ReadBool(key, "IgnoreForeground",        true);
            ActOnForegroundChildren = ReadBool(key, "ActOnForegroundChildren", false);
            ExcludeSystemServices   = ReadBool(key, "ExcludeSystemServices",   true);
            EnableEfficiencyMode    = ReadBool(key, "EnableEfficiencyMode",    true);
            SystemCpuTriggerPercent = ReadInt(key, "SystemCpuTriggerPercent",  12);
            ProcessCpuStartPercent  = ReadInt(key, "ProcessCpuStartPercent",    7);
            ProcessCpuStopPercent   = ReadInt(key, "ProcessCpuStopPercent",     3);
            TimeOverQuotaMs         = ReadInt(key, "TimeOverQuotaMs",        1500);
            MinAdjustmentDurationMs = ReadInt(key, "MinAdjustmentDurationMs", 5000);
            MaxAdjustmentDurationMs = ReadInt(key, "MaxAdjustmentDurationMs", 30000);
            LowerGpuPriority        = ReadBool(key, "LowerGpuPriority",      true);
            LowerIoPriority         = ReadBool(key, "LowerIoPriority",       true);
            DetectECores            = ReadBool(key, "DetectECores",          true);
            MoveToECores            = ReadBool(key, "MoveToECores",          true);
            PersistentNapEnabled    = ReadBool(key, "PersistentNapEnabled",  true);
            MinimizeNapEnabled           = ReadBool(key, "MinimizeNapEnabled",          true);
            MinimizedBriefWakeIntervalMs = ReadInt (key, "MinimizedBriefWakeIntervalMs", 60_000);
            MinimizedBriefWakeDurationMs = ReadInt (key, "MinimizedBriefWakeDurationMs", 10_000);
            TrayNapEnabled           = ReadBool(key, "TrayNapEnabled",          true);
            TrayBriefWakeIntervalMs  = ReadInt (key, "TrayBriefWakeIntervalMs",  300_000);
            TrayBriefWakeDurationMs  = ReadInt (key, "TrayBriefWakeDurationMs",  10_000);
            LowerMemoryPriority     = ReadBool(key, "LowerMemoryPriority",   true);
            TrimWorkingSet          = ReadBool(key, "TrimWorkingSet",        true);
            AdaptiveTick            = ReadBool(key, "AdaptiveTick",          true);
            EnforceSettings         = ReadBool(key, "EnforceSettings",       true);
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepViewModel", $"LoadSettings failed: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
            if (key == null) return;

            key.SetValue("IsEnabled",               IsEnabled               ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("LowerCpuPriority",        LowerCpuPriority        ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("IgnoreForeground",        IgnoreForeground        ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ActOnForegroundChildren", ActOnForegroundChildren ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ExcludeSystemServices",   ExcludeSystemServices   ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("EnableEfficiencyMode",    EnableEfficiencyMode    ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("SystemCpuTriggerPercent", SystemCpuTriggerPercent,          RegistryValueKind.DWord);
            key.SetValue("ProcessCpuStartPercent",  ProcessCpuStartPercent,           RegistryValueKind.DWord);
            key.SetValue("ProcessCpuStopPercent",   ProcessCpuStopPercent,            RegistryValueKind.DWord);
            key.SetValue("TimeOverQuotaMs",         TimeOverQuotaMs,                  RegistryValueKind.DWord);
            key.SetValue("MinAdjustmentDurationMs", MinAdjustmentDurationMs,          RegistryValueKind.DWord);
            key.SetValue("MaxAdjustmentDurationMs", MaxAdjustmentDurationMs,          RegistryValueKind.DWord);
            key.SetValue("LowerGpuPriority",        LowerGpuPriority     ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("LowerIoPriority",         LowerIoPriority      ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("DetectECores",            DetectECores         ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("MoveToECores",            MoveToECores         ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("PersistentNapEnabled",    PersistentNapEnabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("MinimizeNapEnabled",           MinimizeNapEnabled      ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("MinimizedBriefWakeIntervalMs", MinimizedBriefWakeIntervalMs,     RegistryValueKind.DWord);
            key.SetValue("MinimizedBriefWakeDurationMs", MinimizedBriefWakeDurationMs,     RegistryValueKind.DWord);
            key.SetValue("TrayNapEnabled",           TrayNapEnabled       ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("TrayBriefWakeIntervalMs",  TrayBriefWakeIntervalMs,      RegistryValueKind.DWord);
            key.SetValue("TrayBriefWakeDurationMs",  TrayBriefWakeDurationMs,      RegistryValueKind.DWord);
            key.SetValue("LowerMemoryPriority",     LowerMemoryPriority  ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("TrimWorkingSet",          TrimWorkingSet       ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("AdaptiveTick",            AdaptiveTick         ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("EnforceSettings",         EnforceSettings      ? 1 : 0, RegistryValueKind.DWord);
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
            if (json.TrimStart().StartsWith("[\""))
            {
                // New format: simple string list
                var names = JsonSerializer.Deserialize<List<string>>(json);
                if (names != null)
                    foreach (var n in names)
                        if (!string.IsNullOrWhiteSpace(n)) Whitelist.Add(n.ToLowerInvariant());
            }
            else
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
