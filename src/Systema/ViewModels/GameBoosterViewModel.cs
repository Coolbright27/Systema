// ════════════════════════════════════════════════════════════════════════════
// GameBoosterViewModel.cs  ·  Backs the Game Booster tab
// ════════════════════════════════════════════════════════════════════════════
//
// Three things: the master auto-boost switch, the manual boost switch, and the
// list of options describing what a boost actually does. Every option persists the
// moment it changes (see PersistBoostOptions) — the tab has no Save button.
// Live boost state is mirrored from GameBoosterService events. Implements
// IAutoRefreshable.
//
// RELATED FILES
//   GameBoosterService.cs      — game detection and everything a boost applies/restores
//   SettingsService.cs         — persists every option below
//   Views/GameBoosterView.xaml — the tab itself
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

public partial class GameBoosterViewModel : ObservableObject, IAutoRefreshable, IDisposable
{
    private readonly GameBoosterService _gameBooster;
    private readonly SettingsService    _settings;
    private static readonly LoggerService _log = LoggerService.Instance;

    /// <summary>True when Auto-Pilot Mode is on — master Game Boost switch is grayed out.</summary>
    public bool IsAutoPilotActive => _settings.AutoPilotModeEnabled;

    // Event handlers stored for cleanup in Dispose()
    private readonly Action<string> _onBoostActivated;
    private readonly Action         _onBoostDeactivated;
    private readonly Action<bool>   _onGamesInstalledChanged;
    private readonly Action         _onManualBoostTimedOut;

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty] private bool   _boostActive;
    [ObservableProperty] private bool   _manualBoostEnabled;
    [ObservableProperty] private string _manualBoostTimeRemaining = "";
    [ObservableProperty] private string _statusMessage   = "Ready.";
    [ObservableProperty] private string _activeGameName  = "—";
    [ObservableProperty] private bool   _gamesInstalled;
    [ObservableProperty] private int    _checkIntervalMinutes = 2;

    // ── Master switch ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _gameBoosterEnabled;

    // ── Boost Options ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _freeMemoryOnBoost;
    [ObservableProperty] private bool _suppressNotifications;
    [ObservableProperty] private bool _highPerfPowerPlan;
    [ObservableProperty] private bool _disableGameBar;
    [ObservableProperty] private bool _gpuProfileOnBoost;
    [ObservableProperty] private bool _pauseIndexing = true;
    [ObservableProperty] private bool _disableNagleOnBoost;
    [ObservableProperty] private bool _flushDnsOnBoost;
    [ObservableProperty] private bool _nicPowerSavingOnBoost;
    [ObservableProperty] private bool _disableWifiOnEthernet;
    [ObservableProperty] private bool _disableBluetoothOnBoost;
    [ObservableProperty] private bool _preventSleepOnBoost;

    // ── Battery Pause (vendor-specific charge control) ────────────────────────
    [ObservableProperty] private bool   _pauseChargingOnBoost;
    [ObservableProperty] private string _batteryPauseStatus    = "Detecting hardware support…";
    [ObservableProperty] private bool   _batteryPauseAvailable;
    /// <summary>True when a battery is detected — hides the entire section on desktops.</summary>
    [ObservableProperty] private bool   _isBatteryPresent = true; // default true so section shows during detection

    // ── Live session (the boosting card) ───────────────────────────────────────
    // Derived from what the service already did — nothing here applies anything.
    [ObservableProperty] private string _sessionElapsedText = "";

    private void RefreshSessionInfo()
    {
        if (!_gameBooster.BoostActive || _gameBooster.BoostStartedAt is not { } started)
        {
            SessionElapsedText = "";
            return;
        }

        var span = DateTime.UtcNow - started;
        SessionElapsedText = span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min" : "just started";
    }

    /// <summary>Persists and applies the master switch immediately — no Save click needed.</summary>
    partial void OnGameBoosterEnabledChanged(bool value)
    {
        _settings.GameBoosterEnabled = value;
        _gameBooster.SetEnabled(value);
        if (!value) { BoostActive = false; ActiveGameName = "—"; }
        StatusMessage = value ? "Game Booster enabled." : "Game Booster disabled — no games will be detected.";
    }

    partial void OnHighPerfPowerPlanChanged(bool value) => PersistBoostOptions();

    // ── Auto-persist ──────────────────────────────────────────────────────────
    // The tab has no Save button any more. Every switch writes through the moment it
    // flips, like the rest of the app — the old two-Save-button layout was how a
    // flipped toggle could be lost by navigating away.

    /// <summary>Suppresses write-back while LoadSettings is populating the properties.</summary>
    private bool _loadingSettings;

    private void PersistBoostOptions()
    {
        if (_loadingSettings) return;

        _settings.GameBoosterFreeMemory            = FreeMemoryOnBoost;
        _settings.GameBoosterSuppressNotifications = SuppressNotifications;
        _settings.GameBoosterHighPerfPowerPlan     = HighPerfPowerPlan;
        _settings.GameBoosterDisableGameBar        = DisableGameBar;
        _settings.GameBoosterGpuProfile            = GpuProfileOnBoost;
        _settings.GameBoosterPauseIndexing         = PauseIndexing;
        _settings.GameBoosterDisableNagle          = DisableNagleOnBoost;
        _settings.GameBoosterFlushDns              = FlushDnsOnBoost;
        _settings.GameBoosterNicPowerSaving        = NicPowerSavingOnBoost;
        _settings.GameBoosterDisableWifiOnEthernet = DisableWifiOnEthernet;
        _settings.GameBoosterDisableBluetooth      = DisableBluetoothOnBoost;
        _settings.GameBoosterPreventSleep          = PreventSleepOnBoost;
        _settings.GameBoosterPauseCharging         = PauseChargingOnBoost;
    }

    partial void OnFreeMemoryOnBoostChanged(bool value)            => PersistBoostOptions();
    partial void OnSuppressNotificationsChanged(bool value)        => PersistBoostOptions();
    partial void OnDisableGameBarChanged(bool value)               => PersistBoostOptions();
    partial void OnGpuProfileOnBoostChanged(bool value)            => PersistBoostOptions();
    partial void OnPauseIndexingChanged(bool value)                => PersistBoostOptions();
    partial void OnDisableNagleOnBoostChanged(bool value)          => PersistBoostOptions();
    partial void OnFlushDnsOnBoostChanged(bool value)              => PersistBoostOptions();
    partial void OnNicPowerSavingOnBoostChanged(bool value)        => PersistBoostOptions();
    partial void OnDisableWifiOnEthernetChanged(bool value)        => PersistBoostOptions();
    partial void OnDisableBluetoothOnBoostChanged(bool value)      => PersistBoostOptions();
    partial void OnPreventSleepOnBoostChanged(bool value)          => PersistBoostOptions();
    partial void OnPauseChargingOnBoostChanged(bool value)         => PersistBoostOptions();

    partial void OnCheckIntervalMinutesChanged(int value)
    {
        if (_loadingSettings) return;
        if (value < 1 || value > 60) return;   // ignore half-typed values from the text box
        _settings.GameCheckIntervalMinutes = value;
        _gameBooster.UpdateCheckInterval(value);
    }

    /// <summary>Backs the one collapsed "Advanced" section at the bottom of the tab.</summary>
    [ObservableProperty] private bool _showDetection;
    /// <summary>
    /// "Stop boosting" on the live session card. Calls the service DIRECTLY instead of flipping
    /// <see cref="ManualBoostEnabled"/>: an auto-started boost leaves that toggle false, so setting
    /// it false again raises no PropertyChanged and the callback would never run — the same dead-UI
    /// trap documented on OnManualBoostEnabledChanged. DisableManualBoostAsync also records which
    /// game was opted out of, so the monitor doesn't simply re-boost it on the next tick.
    /// </summary>
    [RelayCommand]
    private async Task StopBoostAsync()
    {
        StatusMessage = "Stopping boost…";
        await _gameBooster.DisableManualBoostAsync();
        // Keep the manual toggle visually in sync without re-entering the callback.
        _suppressManualBoostSideEffect = true;
        ManualBoostEnabled = false;
        _suppressManualBoostSideEffect = false;
        BoostActive = _gameBooster.BoostActive;
        RefreshSessionInfo();
        StatusMessage = "Boost stopped. Services and settings restored.";
    }

    [RelayCommand] private void ToggleDetection() => ShowDetection = !ShowDetection;

    public GameBoosterViewModel(GameBoosterService gameBooster, SettingsService settings)
    {
        _gameBooster = gameBooster;
        _settings    = settings;

        // Wire service events -> UI updates (always marshal to UI thread).
        // Handlers are stored as fields so Dispose() can unsubscribe them.
        _onBoostActivated = gameName =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnBoostActivated(gameName));
        _onBoostDeactivated =
            () => Application.Current?.Dispatcher.BeginInvoke(OnBoostDeactivated);
        _onGamesInstalledChanged = v =>
            Application.Current?.Dispatcher.BeginInvoke(() => { GamesInstalled = v; });
        _onManualBoostTimedOut =
            () => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                // The service already deactivated the boost on timeout — just sync
                // the toggle. Suppress the side effect so OnManualBoostEnabledChanged
                // doesn't call DisableManualBoostAsync a second time.
                _suppressManualBoostSideEffect = true;
                ManualBoostEnabled = false;
                _suppressManualBoostSideEffect = false;
                StatusMessage = "Manual boost auto-disabled after 6 hours.";
            });

        _gameBooster.BoostActivated        += _onBoostActivated;
        _gameBooster.BoostDeactivated      += _onBoostDeactivated;
        _gameBooster.GamesInstalledChanged += _onGamesInstalledChanged;
        _gameBooster.ManualBoostTimedOut   += _onManualBoostTimedOut;
        SettingsService.AutoPilotModeChanged += OnAutoPilotModeChanged;

        try
        {
            LoadSettings();
        }
        catch (Exception ex)
        {
            // Unsubscribe on failure so handlers don't leak on a half-constructed VM
            Dispose();
            LoggerService.Instance.Error("GameBoosterViewModel", "LoadSettings failed in constructor", ex);
            throw;
        }

        // Run vendor detection on a worker thread — first WMI hit can take 50-300ms
        // on cold cache and we don't want to stall the UI thread while the user
        // navigates to the Game Booster panel.
        _ = Task.Run(() =>
        {
            try
            {
                var support = _gameBooster.BatteryPause.DetectSupport();
                var msg     = _gameBooster.BatteryPause.StatusMessage;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    BatteryPauseStatus    = msg;
                    BatteryPauseAvailable = support == BatteryPauseSupport.Supported;
                    IsBatteryPresent      = support != BatteryPauseSupport.NotALaptop;
                    if (!BatteryPauseAvailable && PauseChargingOnBoost)
                    {
                        // User had toggle on but device no longer supports it (e.g.
                        // vendor utility uninstalled). Force toggle off.
                        PauseChargingOnBoost = false;
                        _settings.GameBoosterPauseCharging = false;
                    }
                });
            }
            catch (Exception ex)
            {
                _log.Warn("GameBoosterViewModel", $"Battery pause detection failed: {ex.Message}");
            }
        });
    }

    // ── IAutoRefreshable ──────────────────────────────────────────────────────

    public Task RefreshAsync()
    {
        // Called by MainViewModel's DispatcherTimer — already on UI thread.
        // Re-sync GameBoosterEnabled from settings so the master switch reflects
        // the persisted value even when Auto-Pilot toggled it in this session (M-3 fix).
        // Use SetProperty directly on the backing field to raise OnPropertyChanged
        // without triggering OnGameBoosterEnabledChanged (which calls SetEnabled +
        // writes a status message and must not fire during a background refresh).
        // MVVMTK0034 suppressed intentionally — bypassing the partial callback is the goal.
#pragma warning disable MVVMTK0034
        SetProperty(ref _gameBoosterEnabled, _settings.GameBoosterEnabled,
            nameof(GameBoosterEnabled));
#pragma warning restore MVVMTK0034

        BoostActive = _gameBooster.BoostActive;
        RefreshSessionInfo();
        // Sync the toggle from the service WITHOUT triggering OnManualBoostEnabledChanged —
        // a background refresh tick is not a user click and must not call the service.
        _suppressManualBoostSideEffect = true;
        ManualBoostEnabled = _gameBooster.ManualBoostActive;
        _suppressManualBoostSideEffect = false;
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

    // Re-entrancy guard so a rapid double-click can't fire two overlapping
    // enable/disable calls into the service.
    private bool _manualBoostBusy;

    // Set true around PROGRAMMATIC writes to ManualBoostEnabled (RefreshAsync
    // sync, timeout handler) so OnManualBoostEnabledChanged doesn't mistake a
    // state sync for a user click and call the service again.
    private bool _suppressManualBoostSideEffect;

    /// <summary>
    /// Fires whenever the Manual Boost toggle changes.
    ///
    /// The CheckBox in GameBoosterView.xaml is two-way bound to
    /// <see cref="ManualBoostEnabled"/> with NO Command — so this callback is
    /// the only thing that actually activates / deactivates the boost.
    ///
    /// Bug history: earlier builds declared a <c>[RelayCommand] ToggleManualBoost</c>
    /// that nothing in the XAML or VM ever referenced. The toggle was dead UI —
    /// clicking it flipped the bool but never called the service, and the next
    /// auto-refresh tick reset the bool from <c>_gameBooster.ManualBoostActive</c>
    /// (still false), so the switch "toggled right back off" with no boost.
    /// </summary>
    partial void OnManualBoostEnabledChanged(bool value)
    {
        if (_suppressManualBoostSideEffect) return;   // programmatic sync, not a user click
        _ = ApplyManualBoostAsync(value);
    }

    private async Task ApplyManualBoostAsync(bool enable)
    {
        if (_manualBoostBusy) return;
        _manualBoostBusy = true;
        try
        {
            if (enable)
            {
                StatusMessage = "Activating boost...";
                await _gameBooster.EnableManualBoostAsync();
                StatusMessage = "Manual boost enabled — auto-disables after 6 hours.";
            }
            else
            {
                await _gameBooster.DisableManualBoostAsync();
                StatusMessage = "Manual boost disabled.";
            }
        }
        catch (Exception ex)
        {
            _log.Error("GameBoosterViewModel",
                $"Manual boost {(enable ? "enable" : "disable")} failed", ex);
            StatusMessage = $"Manual boost error: {ex.Message}";
            // Snap the toggle back to the service's real state without re-firing
            // this handler (otherwise a failed enable would leave the UI lying).
            _suppressManualBoostSideEffect = true;
            ManualBoostEnabled = _gameBooster.ManualBoostActive;
            _suppressManualBoostSideEffect = false;
        }
        finally { _manualBoostBusy = false; }
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

    // ── Private helpers ───────────────────────────────────────────────────────

    private void LoadSettings()
    {
        // Guard the auto-persist write-back: without this, setting the first property
        // would flush the remaining (still-default) properties over the saved values.
        _loadingSettings = true;
        try
        {
        // Load all values via the public property setters first.
        CheckIntervalMinutes    = _settings.GameCheckIntervalMinutes;
        GamesInstalled          = _gameBooster.GamesInstalled;
        BoostActive             = _gameBooster.BoostActive;
        ActiveGameName          = _gameBooster.ActiveGameName ?? "—";
        FreeMemoryOnBoost       = _settings.GameBoosterFreeMemory;
        SuppressNotifications   = _settings.GameBoosterSuppressNotifications;
        HighPerfPowerPlan       = _settings.GameBoosterHighPerfPowerPlan;
        DisableGameBar          = _settings.GameBoosterDisableGameBar;
        GpuProfileOnBoost       = _settings.GameBoosterGpuProfile;
        PauseIndexing           = _settings.GameBoosterPauseIndexing;
        DisableNagleOnBoost     = _settings.GameBoosterDisableNagle;
        FlushDnsOnBoost         = _settings.GameBoosterFlushDns;
        NicPowerSavingOnBoost   = _settings.GameBoosterNicPowerSaving;
        DisableWifiOnEthernet   = _settings.GameBoosterDisableWifiOnEthernet;
        DisableBluetoothOnBoost = _settings.GameBoosterDisableBluetooth;
        PreventSleepOnBoost     = _settings.GameBoosterPreventSleep;
        PauseChargingOnBoost    = _settings.GameBoosterPauseCharging;
        GameBoosterEnabled      = _settings.GameBoosterEnabled;

        // Force every binding to re-evaluate unconditionally. CommunityToolkit.Mvvm's setter
        // skips OnPropertyChanged when the new value equals the current field value. This is
        // normally fine, but WPF's ToggleSwitch custom style relies on IsChecked triggers that
        // only fire on PropertyChanged notifications. If a saved value matches the C# field
        // default (e.g. both false), no notification is sent and the visual state can be
        // wrong. Raising here guarantees the toggle always renders the persisted state.
        OnPropertyChanged(nameof(CheckIntervalMinutes));
        OnPropertyChanged(nameof(GamesInstalled));
        OnPropertyChanged(nameof(BoostActive));
        OnPropertyChanged(nameof(ActiveGameName));
        OnPropertyChanged(nameof(FreeMemoryOnBoost));
        OnPropertyChanged(nameof(SuppressNotifications));
        OnPropertyChanged(nameof(HighPerfPowerPlan));
        OnPropertyChanged(nameof(DisableGameBar));
        OnPropertyChanged(nameof(GpuProfileOnBoost));
        OnPropertyChanged(nameof(PauseIndexing));
        OnPropertyChanged(nameof(DisableNagleOnBoost));
        OnPropertyChanged(nameof(FlushDnsOnBoost));
        OnPropertyChanged(nameof(NicPowerSavingOnBoost));
        OnPropertyChanged(nameof(DisableWifiOnEthernet));
        OnPropertyChanged(nameof(DisableBluetoothOnBoost));
        OnPropertyChanged(nameof(PreventSleepOnBoost));
        OnPropertyChanged(nameof(PauseChargingOnBoost));
        OnPropertyChanged(nameof(GameBoosterEnabled));
        }
        finally { _loadingSettings = false; }
    }

    private void OnAutoPilotModeChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            () => OnPropertyChanged(nameof(IsAutoPilotActive)));

    public void Dispose()
    {
        _gameBooster.BoostActivated        -= _onBoostActivated;
        _gameBooster.BoostDeactivated      -= _onBoostDeactivated;
        _gameBooster.GamesInstalledChanged -= _onGamesInstalledChanged;
        _gameBooster.ManualBoostTimedOut   -= _onManualBoostTimedOut;
        SettingsService.AutoPilotModeChanged -= OnAutoPilotModeChanged;
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
