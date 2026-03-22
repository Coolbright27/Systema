// ════════════════════════════════════════════════════════════════════════════
// GameBoosterViewModel.cs  ·  Game auto-detection and per-game boost toggle
// ════════════════════════════════════════════════════════════════════════════
//
// Shows the list of known game processes from GameBoosterService, lets the user
// enable or disable boost mode per game, and monitors for running game processes
// to reflect live boost state. User preferences (auto-boost on/off) are persisted
// via SettingsService. Implements IAutoRefreshable.
//
// RELATED FILES
//   GameBoosterService.cs     — auto-detection logic, service kill list, boost apply
//   SettingsService.cs        — persists auto-boost enabled preference
//   Views/GameBoosterView.xaml — game list, boost toggle, active-game indicator
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Core;
using Systema.Models;
using Systema.Services;
using Systema.Views;

namespace Systema.ViewModels;

public partial class GameBoosterViewModel : ObservableObject, IAutoRefreshable
{
    private readonly GameBoosterService _gameBooster;
    private readonly SettingsService    _settings;
    private static readonly LoggerService _log = LoggerService.Instance;

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty] private bool   _boostActive;
    [ObservableProperty] private bool   _manualBoostEnabled;
    [ObservableProperty] private string _manualBoostTimeRemaining = "";
    [ObservableProperty] private string _statusMessage   = "Ready.";
    [ObservableProperty] private string _activeGameName  = "—";
    [ObservableProperty] private bool   _gamesInstalled;
    [ObservableProperty] private int    _checkIntervalMinutes = 2;
    [ObservableProperty] private bool   _xboxOverride;

    // Kill list as structured items
    [ObservableProperty] private ObservableCollection<KillListEntry> _killListItems = new();

    // ── Master switch ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _gameBoosterEnabled;

    // ── Boost Options ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _freeMemoryOnBoost;
    [ObservableProperty] private bool _suppressNotifications;
    [ObservableProperty] private bool _highPerfPowerPlan;
    [ObservableProperty] private bool _timerResolutionOnBoost;
    [ObservableProperty] private bool _disableGameBar;
    [ObservableProperty] private bool _gpuProfileOnBoost;
    [ObservableProperty] private bool _disableNagleOnBoost;
    [ObservableProperty] private bool _flushDnsOnBoost;
    [ObservableProperty] private bool _nicPowerSavingOnBoost;
    [ObservableProperty] private bool _disableWifiOnEthernet;
    [ObservableProperty] private bool _disableBluetoothOnBoost;
    [ObservableProperty] private bool _preventSleepOnBoost;

    /// <summary>Persists and applies the master switch immediately — no Save click needed.</summary>
    partial void OnGameBoosterEnabledChanged(bool value)
    {
        _settings.GameBoosterEnabled = value;
        _gameBooster.SetEnabled(value);
        if (!value) { BoostActive = false; ActiveGameName = "—"; }
        StatusMessage = value ? "Game Booster enabled." : "Game Booster disabled — no games will be detected.";
    }

    // ── Expander state ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _showServiceSettings;
    [RelayCommand] private void ToggleServiceSettings() => ShowServiceSettings = !ShowServiceSettings;

    // ── Well-known service descriptions ──────────────────────────────────────
    private static readonly Dictionary<string, string> KnownDescriptions =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "Spooler",           "Print Spooler — manages printer jobs" },
        { "Fax",               "Fax service — send/receive faxes" },
        { "TabletInputService","Touch keyboard & handwriting panel" },
        { "WSearch",           "Windows Search indexing service" },
        { "SysMain",           "SuperFetch — preloads apps into RAM" },
        { "DiagTrack",         "Connected User Experiences & Telemetry" },
        { "WerSvc",            "Windows Error Reporting service" },
        { "MapsBroker",        "Downloaded Maps Manager" },
        { "RemoteRegistry",    "Allows remote registry editing" },
        { "XboxGipSvc",        "Xbox Accessory Management service" },
        { "xbgm",              "Xbox Game Monitoring service" },
        { "XblAuthManager",    "Xbox Live authentication manager" },
        { "XblGameSave",       "Xbox Live game save service" },
        { "XboxNetApiSvc",     "Xbox Live networking service" },
        { "lfsvc",             "Geolocation service" },
        { "WbioSrvc",          "Windows Biometric service" },
        { "RetailDemo",        "Retail Demo offline content" },
    };

    public GameBoosterViewModel(GameBoosterService gameBooster, SettingsService settings)
    {
        _gameBooster = gameBooster;
        _settings    = settings;

        // Wire service events -> UI updates (always marshal to UI thread)
        _gameBooster.BoostActivated += gameName =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnBoostActivated(gameName));
        _gameBooster.BoostDeactivated +=
            () => Application.Current?.Dispatcher.BeginInvoke(OnBoostDeactivated);
        _gameBooster.GamesInstalledChanged += v =>
            Application.Current?.Dispatcher.BeginInvoke(() => { GamesInstalled = v; });
        _gameBooster.ManualBoostTimedOut +=
            () => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ManualBoostEnabled = false;
                StatusMessage = "Manual boost auto-disabled after 6 hours.";
            });

        LoadSettings();
    }

    // ── IAutoRefreshable ──────────────────────────────────────────────────────

    public Task RefreshAsync()
    {
        // Called by MainViewModel's DispatcherTimer — already on UI thread
        BoostActive        = _gameBooster.BoostActive;
        ManualBoostEnabled = _gameBooster.ManualBoostActive;
        if (_gameBooster.ManualBoostActive)
        {
            var elapsed  = DateTime.UtcNow - _gameBooster.ManualBoostStartedAt;
            var remaining = TimeSpan.FromHours(6) - elapsed;
            if (remaining <= TimeSpan.Zero)
                ManualBoostTimeRemaining = "auto-off soon";
            else if (remaining.TotalMinutes < 1)
                ManualBoostTimeRemaining = $"{(int)remaining.TotalSeconds}s remaining";
            else if (remaining.TotalHours < 1)
                ManualBoostTimeRemaining = $"{(int)remaining.TotalMinutes}m remaining";
            else
                ManualBoostTimeRemaining = $"{(int)remaining.TotalHours}h {remaining.Minutes}m remaining";
        }
        else
        {
            ManualBoostTimeRemaining = "";
        }
        GamesInstalled     = _gameBooster.GamesInstalled;
        ActiveGameName     = _gameBooster.ActiveGameName ?? "—";
        StatusMessage      = _gameBooster.BoostActive
            ? $"Boosting: {ActiveGameName}"
            : (GamesInstalled ? "Games detected — monitoring." : "No games detected.");
        return Task.CompletedTask;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleManualBoost()
    {
        if (ManualBoostEnabled)
        {
            await _gameBooster.DisableManualBoostAsync();
            ManualBoostEnabled = false;
            StatusMessage = "Manual boost disabled.";
        }
        else
        {
            StatusMessage = "Activating boost...";
            await _gameBooster.EnableManualBoostAsync();
            ManualBoostEnabled = true;
            StatusMessage = "Manual boost enabled — auto-disables after 6 hours.";
        }
    }

    [RelayCommand]
    private async Task ScanNowAsync()
    {
        StatusMessage = "Scanning for games...";
        try
        {
            await _gameBooster.ForceCheckAsync();
            GamesInstalled = _gameBooster.GamesInstalled;
            StatusMessage  = GamesInstalled ? "Games detected on this system." : "No games detected.";
        }
        catch (Exception ex)
        {
            _log.Error("GameBoosterViewModel", "Force scan failed", ex);
            StatusMessage = $"Scan error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.GameCheckIntervalMinutes = CheckIntervalMinutes;
        _gameBooster.UpdateCheckInterval(CheckIntervalMinutes);

        var lines = KillListItems.Select(i => i.ServiceName)
                                  .Where(s => !string.IsNullOrWhiteSpace(s))
                                  .ToList();

        _settings.GameBoosterKillList = lines.Count > 0 ? lines : null;
        _settings.XboxServicesUserOverride = XboxOverride;

        // Boost options
        _settings.GameBoosterFreeMemory            = FreeMemoryOnBoost;
        _settings.GameBoosterSuppressNotifications = SuppressNotifications;
        _settings.GameBoosterHighPerfPowerPlan     = HighPerfPowerPlan;
        _settings.GameBoosterTimerResolution       = TimerResolutionOnBoost;
        _settings.GameBoosterDisableGameBar        = DisableGameBar;
        _settings.GameBoosterGpuProfile            = GpuProfileOnBoost;
        _settings.GameBoosterDisableNagle          = DisableNagleOnBoost;
        _settings.GameBoosterFlushDns              = FlushDnsOnBoost;
        _settings.GameBoosterNicPowerSaving        = NicPowerSavingOnBoost;
        _settings.GameBoosterDisableWifiOnEthernet = DisableWifiOnEthernet;
        _settings.GameBoosterDisableBluetooth      = DisableBluetoothOnBoost;
        _settings.GameBoosterPreventSleep          = PreventSleepOnBoost;

        StatusMessage = "Settings saved.";
        _log.Info("GameBoosterViewModel", $"Settings saved — interval={CheckIntervalMinutes}min, killList={lines.Count} entries");
    }

    [RelayCommand]
    private void ResetKillList()
    {
        _settings.GameBoosterKillList = null;
        LoadSettings();
        StatusMessage = "Kill list reset to defaults.";
    }

    [RelayCommand]
    private void OpenServicePicker()
    {
        var dialog = new ServicePickerDialog
        {
            Owner            = Application.Current?.MainWindow,
            ExistingServices = new HashSet<string>(
                KillListItems.Select(i => i.ServiceName), StringComparer.OrdinalIgnoreCase)
        };

        if (dialog.ShowDialog() != true) return;

        int added = 0;
        foreach (var name in dialog.SelectedServices)
        {
            if (KillListItems.Any(i => i.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            KillListItems.Add(new KillListEntry
            {
                ServiceName = name,
                Description = KnownDescriptions.TryGetValue(name, out var desc) ? desc : "Windows service"
            });
            added++;
        }

        StatusMessage = added > 0
            ? $"Added {added} service(s) to kill list. Click Save Settings to persist."
            : "No new services added.";
    }

    [RelayCommand]
    private void RemoveKillService(KillListEntry entry)
    {
        KillListItems.Remove(entry);
        StatusMessage = $"Removed {entry.ServiceName} from kill list. Click Save Settings to persist.";
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void LoadSettings()
    {
        // Load all values via the public property setters first.
        CheckIntervalMinutes    = _settings.GameCheckIntervalMinutes;
        XboxOverride            = _settings.XboxServicesUserOverride;
        GamesInstalled          = _gameBooster.GamesInstalled;
        BoostActive             = _gameBooster.BoostActive;
        ActiveGameName          = _gameBooster.ActiveGameName ?? "—";
        FreeMemoryOnBoost       = _settings.GameBoosterFreeMemory;
        SuppressNotifications   = _settings.GameBoosterSuppressNotifications;
        HighPerfPowerPlan       = _settings.GameBoosterHighPerfPowerPlan;
        TimerResolutionOnBoost  = _settings.GameBoosterTimerResolution;
        DisableGameBar          = _settings.GameBoosterDisableGameBar;
        GpuProfileOnBoost       = _settings.GameBoosterGpuProfile;
        DisableNagleOnBoost     = _settings.GameBoosterDisableNagle;
        FlushDnsOnBoost         = _settings.GameBoosterFlushDns;
        NicPowerSavingOnBoost   = _settings.GameBoosterNicPowerSaving;
        DisableWifiOnEthernet   = _settings.GameBoosterDisableWifiOnEthernet;
        DisableBluetoothOnBoost = _settings.GameBoosterDisableBluetooth;
        PreventSleepOnBoost     = _settings.GameBoosterPreventSleep;
        GameBoosterEnabled      = _settings.GameBoosterEnabled;

        // Force every binding to re-evaluate unconditionally. CommunityToolkit.Mvvm's setter
        // skips OnPropertyChanged when the new value equals the current field value. This is
        // normally fine, but WPF's ToggleSwitch custom style relies on IsChecked triggers that
        // only fire on PropertyChanged notifications. If a saved value matches the C# field
        // default (e.g. both false), no notification is sent and the visual state can be
        // wrong. Raising here guarantees the toggle always renders the persisted state.
        OnPropertyChanged(nameof(CheckIntervalMinutes));
        OnPropertyChanged(nameof(XboxOverride));
        OnPropertyChanged(nameof(GamesInstalled));
        OnPropertyChanged(nameof(BoostActive));
        OnPropertyChanged(nameof(ActiveGameName));
        OnPropertyChanged(nameof(FreeMemoryOnBoost));
        OnPropertyChanged(nameof(SuppressNotifications));
        OnPropertyChanged(nameof(HighPerfPowerPlan));
        OnPropertyChanged(nameof(TimerResolutionOnBoost));
        OnPropertyChanged(nameof(DisableGameBar));
        OnPropertyChanged(nameof(GpuProfileOnBoost));
        OnPropertyChanged(nameof(DisableNagleOnBoost));
        OnPropertyChanged(nameof(FlushDnsOnBoost));
        OnPropertyChanged(nameof(NicPowerSavingOnBoost));
        OnPropertyChanged(nameof(DisableWifiOnEthernet));
        OnPropertyChanged(nameof(DisableBluetoothOnBoost));
        OnPropertyChanged(nameof(PreventSleepOnBoost));
        OnPropertyChanged(nameof(GameBoosterEnabled));

        var killList = _gameBooster.GetKillList();
        KillListItems.Clear();
        foreach (var name in killList)
        {
            KillListItems.Add(new KillListEntry
            {
                ServiceName = name,
                Description = KnownDescriptions.TryGetValue(name, out var desc) ? desc : "Windows service"
            });
        }
    }

    private void OnBoostActivated(string gameName)
    {
        BoostActive    = true;
        ActiveGameName = gameName;
        StatusMessage  = $"Game Boosting Active — {gameName}";
    }

    private void OnBoostDeactivated()
    {
        BoostActive    = false;
        ActiveGameName = "—";
        StatusMessage  = "Game session ended. Services restored.";
    }
}
