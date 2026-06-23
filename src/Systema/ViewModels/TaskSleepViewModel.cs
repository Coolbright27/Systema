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

    // Stateless registry-tweak service used for the engine's responsiveness boosts
    // (Foreground Priority Boost + Instant App Focus). Safe to instantiate freely —
    // App.xaml.cs constructs it the same way.
    private readonly SystemStabilityService _stability = new();

    // Settings store — used only to persist the Maximum System Responsiveness opt-in
    // (SystemResponsiveness=0) so GameBooster's VSync self-heal honours the user's
    // choice. Registry-backed, safe to instantiate alongside the shared instance.
    private readonly SettingsService _settings = new();

    // ── Observable properties ─────────────────────────────────────────────────

    // ── Core Controls ────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isEnabled               = true;

    [ObservableProperty] private bool _napChildrenEnabled     = false;

    // Compress memory in deep sleep — closest Windows equivalent to macOS's
    // compressed-memory behaviour. ON by default. When a napped process crosses
    // the deep-sleep threshold (default ~10 min idle), trim its working set so
    // Windows can compress those pages on the standby list. Re-trim after each
    // brief wake while the process is still in deep sleep.
    //
    // Replaces the v0.7.9 "Aggressive re-trim after brief wakes",
    // "Max RAM per napped app", and "Also cap foreground app's helpers" toggles.
    // See TaskSleepSettings.CompressDeepSleep.
    [ObservableProperty] private bool _compressDeepSleep = true;

    // ── CPU Thresholds (fixed defaults — preset selector removed in 1.7.32) ──
    [ObservableProperty] private int _systemCpuTriggerPercent = 12;
    [ObservableProperty] private int _processCpuStopPercent   = 3;
    [ObservableProperty] private int _timeOverQuotaMs         = 1500;
    [ObservableProperty] private int _minAdjustmentDurationMs = 5000;
    [ObservableProperty] private int _maxAdjustmentDurationMs = 30000;

    // ── Minimize Nap ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _minimizeNapEnabled              = true;
    [ObservableProperty] private bool _skipBusyMinimizedApps           = true;   // ON by default
    [ObservableProperty] private int  _busyMinimizedCpuThresholdPercent = 30;
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
    [ObservableProperty] private int  _nappedCpuCapPercent     = 1;
    [ObservableProperty] private int  _briefWakeCpuCapPercent  = 5;

    // Preset dropdown options for the CPU Cap + Wake Timing selectors. (Legacy
    // defaults 3% / 7% are kept in the lists so an existing saved value still
    // shows instead of going blank after the textbox→dropdown switch.)
    public int[] CpuCapWhileSleepingOptions   { get; } = { 1, 2, 3, 5, 8, 10 };
    public int[] CpuCapBriefWakeOptions        { get; } = { 3, 5, 7, 8, 10, 15, 20 };
    public int[] MaxConcurrentBriefWakeOptions { get; } = { 1, 2, 3, 4, 5, 8 };
    public int[] MinimizedBriefWakeIntervalOptions { get; } = { 30, 45, 60, 90, 120, 180, 300 }; // seconds
    public int[] BriefWakeDurationOptions      { get; } = { 5, 10, 15, 20, 30 };                 // seconds
    public int[] TrayBriefWakeIntervalOptions  { get; } = { 1, 2, 5, 10, 15, 30 };               // minutes
    public int[] DeepSleepAfterOptions         { get; } = { 5, 10, 15, 20, 30, 45, 60 };         // minutes
    public int[] TrayDeepWakeOptions           { get; } = { 5, 10, 15, 20, 30, 60 };             // minutes

    // ── Launch Boost ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _launchBoostEnabled           = false;
    [ObservableProperty] private int  _launchBoostDurationSeconds    = 20;
    [ObservableProperty] private bool _launchBoostCpu               = true;
    [ObservableProperty] private bool _launchBoostIo                = true;
    [ObservableProperty] private bool _launchBoostDisableEfficiency = true;
    [ObservableProperty] private bool _launchBoostGpu               = false;
    /// <summary>Selectable boost durations (seconds) for the dropdown.</summary>
    public int[] LaunchBoostDurationOptions { get; } = { 5, 10, 20, 40 };

    // ── Responsiveness (engine-gated) ─────────────────────────────────────────
    // Foreground Priority Boost + Instant App Focus + Instant Startup Apps. All
    // belong to the Systema Engine: they run only while the engine is on, default
    // on when it is, and are forced off (and the cards gray out) when the engine is
    // turned off.
    [ObservableProperty] private bool _foregroundBoostEnabled = true;
    [ObservableProperty] private bool _instantAppFocusEnabled = true;
    [ObservableProperty] private bool _instantStartupApps     = true;

    // ── Maximum System Responsiveness (MMCSS SystemResponsiveness = 0) ──────────
    // Engine-gated like the boosts above: on when the engine is on (default), forced
    // off and grayed when the engine is off. Sets the registry value; takes effect on
    // the next restart. See OnMaxResponsivenessEnabledChanged.
    [ObservableProperty] private bool _maxResponsivenessEnabled = true;

    // ── Reinforcements (engine-gated, default on) ──────────────────────────────
    // Network Throttling off pairs with Maximum System Responsiveness; Power Throttling
    // off pairs with Foreground Priority Boost (AC-gated so battery is never hurt — see
    // OnPowerModeChangedForThrottling); Fast App Close trims the shutdown/sign-out wait.
    [ObservableProperty] private bool _networkThrottlingOffEnabled = true;
    [ObservableProperty] private bool _powerThrottlingOffEnabled   = true;
    [ObservableProperty] private bool _fastAppCloseEnabled         = true;

    // Keep Kernel in RAM defaults ON only on machines with >= 14 GB installed (it's
    // only recommended at 16 GB+). Computed once from physically-installed RAM.
    private static readonly bool _ramSupportsKeepKernel = SystemStabilityService.InstalledRamGb() >= 14;
    [ObservableProperty] private bool _keepKernelInRamEnabled = _ramSupportsKeepKernel;
    [ObservableProperty] private bool _fasterShutdownEnabled  = true;
    [ObservableProperty] private bool _inputHookTimeoutEnabled = true;
    [ObservableProperty] private bool _serviceShutdownFastEnabled = true;
    [ObservableProperty] private bool _fastStartupOffEnabled = true;
    [ObservableProperty] private bool _backgroundAppsOffEnabled = true;

    // ── Live display vs. user intent ───────────────────────────────────────────
    // The [ObservableProperty] toggles above DISPLAY the LIVE Windows state — they're set
    // from SystemStabilityService.Is*() on load and after every apply, so a switch reads
    // "On" only when the underlying registry value is actually set (never a stale saved
    // preference). The user's INTENT — what to apply on launch and default-on — is tracked
    // separately here and persisted. Keeping them apart stops a value Windows reset (e.g.
    // Explorer wiping StartupDelay during logon) from being mistaken for "the user turned
    // it off" and saved as such; instead it's re-applied (healed) on the next launch.
    private bool _wantForegroundBoost      = true;
    private bool _wantInstantAppFocus      = true;
    private bool _wantInstantStartupApps   = true;
    private bool _wantMaxResponsiveness    = true;
    private bool _wantNetworkThrottlingOff = true;
    private bool _wantPowerThrottlingOff   = true;
    private bool _wantFastAppClose         = true;
    private bool _wantKeepKernelInRam      = _ramSupportsKeepKernel;
    private bool _wantFasterShutdown       = true;
    private bool _wantInputHookTimeout     = true;
    private bool _wantServiceShutdownFast  = true;
    private bool _wantFastStartupOff       = true;
    private bool _wantBackgroundAppsOff    = true;

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

    // ── UI Display ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _cpuFreedDisplay  = "";
    [ObservableProperty] private bool   _cpuFreedVisible  = false;
    [ObservableProperty] private bool   _showAllProcesses = false;
    [ObservableProperty] private string _systemCpuDisplay      = "System CPU: —";
    [ObservableProperty] private string _throttledCountDisplay = "0 napping";

    // ── UI State ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isAdvancedExpanded = false;

    // ── Accordion sections (Design A) — collapsed by default for a clean page ──
    [ObservableProperty] private bool _showLaunchBoost;
    [ObservableProperty] private bool _showResponsiveness;
    [ObservableProperty] private bool _showLiveMonitor;
    [ObservableProperty] private bool _showSleepRules;
    [ObservableProperty] private bool _showAdvancedSection;
    [ObservableProperty] private bool _showNeverNap;
    [RelayCommand] private void ToggleLaunchBoostSection()    => ShowLaunchBoost     = !ShowLaunchBoost;
    [RelayCommand] private void ToggleResponsivenessSection() => ShowResponsiveness  = !ShowResponsiveness;
    [RelayCommand] private void ToggleLiveMonitorSection()    => ShowLiveMonitor     = !ShowLiveMonitor;
    [RelayCommand] private void ToggleSleepRulesSection()     => ShowSleepRules      = !ShowSleepRules;
    [RelayCommand] private void ToggleAdvancedSection()       => ShowAdvancedSection = !ShowAdvancedSection;
    [RelayCommand] private void ToggleNeverNapSection()       => ShowNeverNap        = !ShowNeverNap;

    // Hero status tiles: live counts of napping vs. awake apps.
    [ObservableProperty] private int _appsNappingCount;
    [ObservableProperty] private int _activeAppsCount;

    public ObservableCollection<ProcessSnapshot> LiveProcesses { get; } = new();
    public ObservableCollection<MonitorEvent>    RecentEvents  { get; } = new();

    private readonly DispatcherTimer _monitorTimer;
    private readonly DispatcherTimer _processRefreshTimer;
    private bool _isGameModeActive;

    // True only while LoadSettings() is populating properties from the registry.
    // Suppresses the OnChanged → SaveSettings round-trip so loading an early
    // non-default setting can't overwrite later, not-yet-loaded settings with
    // their defaults (which was resetting the Launch Boost toggles on restart).
    private bool _loadingSettings;

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

        // Push the fully-loaded settings to the service in one shot. During
        // LoadSettings the per-property OnChanged → PushSettings round-trips are
        // suppressed (see _loadingSettings), so this is what actually hands the
        // service the saved configuration after a restart.
        _service.UpdateSettings(BuildSettings());

        // Explicitly sync service state after loading settings.
        // CommunityToolkit.Mvvm skips OnIsEnabledChanged when the loaded value
        // equals the field default (both true), so the service would never start
        // on subsequent launches without this explicit call.
        if (IsEnabled) _service.Start();

        // Responsiveness boosts are engine-gated and default ON. On launch, APPLY each boost
        // the user wants (intent — default on; a boost they turned off stays off across
        // restarts), then the toggles reflect the LIVE Windows value so "On" always means the
        // registry value is actually set, and a value Windows reset gets re-applied (healed).
        if (IsEnabled)
        {
            _ = ApplyResponsivenessAsync();
        }
        else
        {
            _loadingSettings = true;
            ForegroundBoostEnabled = InstantAppFocusEnabled = InstantStartupApps = MaxResponsivenessEnabled =
                NetworkThrottlingOffEnabled = PowerThrottlingOffEnabled = FastAppCloseEnabled =
                KeepKernelInRamEnabled = FasterShutdownEnabled = InputHookTimeoutEnabled =
                ServiceShutdownFastEnabled = FastStartupOffEnabled = BackgroundAppsOffEnabled = false;
            _loadingSettings = false;
        }

        // Power Throttling off is AC-gated — re-apply it when the user plugs in / unplugs
        // so battery runtime is never sacrificed. The handler no-ops unless both the engine
        // and the toggle are on.
        SystemEvents.PowerModeChanged += OnPowerModeChangedForThrottling;

        // StartupDelayInMSec lives in Explorer's Serialize key, which Explorer itself
        // actively rewrites during the logon sequence. When Systema auto-launches
        // mid-logon (the common case — it starts with Windows), the apply above races
        // Explorer and gets clobbered (proven: the value vanishes despite logging
        // success, while the other two responsiveness tweaks land fine). Re-assert it
        // ~60 s later, once logon has settled, so it persists and is read on the NEXT
        // boot. Idempotent and respects an in-session toggle-off.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(60_000);
                if (_wantInstantStartupApps && IsEnabled && !_stability.IsStartupAppDelayDisabled())
                {
                    await _stability.EnableInstantStartupAppsAsync();
                    RefreshResponsivenessDisplay();
                }
            }
            catch (Exception ex) { _log.Warn("TaskSleepViewModel", $"Delayed StartupDelay re-assert failed: {ex.Message}"); }
        });

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

    /// <summary>
    /// Called just before an auto-update replaces Systema.exe. Restores EVERY napped process
    /// (priority / I-O / memory / GPU / affinity / CPU-cap) and stops the monitor, so nothing is
    /// left stuck at Idle / lowest-RAM priority once this process exits — those throttles persist
    /// on the target processes after Systema dies, and the freshly-installed version has no record
    /// of which processes the old one napped, so it can't undo them.
    /// <para>
    /// Deliberately does NOT change the persisted <c>IsEnabled</c> setting: if Task Sleep was on,
    /// the new version reads that setting on startup and re-enables itself automatically. If it
    /// was already off, this is a no-op.
    /// </para>
    /// </summary>
    public void PauseForUpdate()
    {
        try
        {
            if (!IsEnabled) return;   // off → nothing to restore; stays off after update
            _log.Info("TaskSleepViewModel", "Pre-update: restoring all napped processes and pausing Task Sleep (setting preserved for re-enable)");
            _service.Stop();          // RestoreAll() + stop monitor; leaves the IsEnabled setting untouched
        }
        catch (Exception ex) { _log.Warn("TaskSleepViewModel", $"PauseForUpdate failed: {ex.Message}"); }
    }

    /// <summary>
    /// Called from App's crash / process-exit handlers. Restores EVERY napped process and lifts their
    /// CPU caps (via the service's RestoreAll) before Systema's handles close, so a crash or force-quit
    /// doesn't leave them orphaned. Like <see cref="PauseForUpdate"/> it leaves the IsEnabled setting
    /// alone, and is a no-op when Task Sleep is off (nothing is napped).
    /// </summary>
    public void RestoreAllNaps()
    {
        try
        {
            if (!IsEnabled) return;   // off → nothing napped to restore
            _service.Stop();          // RestoreAll() (lifts caps + restores all throttles) + stop monitor
        }
        catch (Exception ex) { _log.Warn("TaskSleepViewModel", $"RestoreAllNaps failed: {ex.Message}"); }
    }

    // ── Property change callbacks ─────────────────────────────────────────────

    // Remembers whether Launch Boost was on before the engine was last switched off,
    // so flipping the engine off→on restores Launch Boost instead of losing it.
    private bool _launchBoostBeforeEngineOff;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_loadingSettings) return;   // load path syncs the service explicitly
        // Launch Boost depends on Task Sleep running. When the engine is turned off we
        // force Launch Boost off (it can't run on its own) but REMEMBER whether it was on,
        // so toggling the engine back on brings Launch Boost back with it.
        if (!value)
        {
            _launchBoostBeforeEngineOff = LaunchBoostEnabled;
            if (LaunchBoostEnabled)
                LaunchBoostEnabled = false;   // cascades through OnLaunchBoostEnabledChanged → PushSettings
        }

        // Responsiveness boosts (Foreground Priority Boost + Instant App Focus + Instant
        // Startup Apps + Maximum System Responsiveness) are engine-gated.
        if (value)
        {
            // Engine back ON → bring Launch Boost back to the state it had before the
            // engine was last turned off, so an off/on cycle doesn't silently lose it.
            if (_launchBoostBeforeEngineOff && !LaunchBoostEnabled)
                LaunchBoostEnabled = true;   // cascades → re-arms Launch Boost + persists

            // Engine turned ON → RESET intent: all boosts back on by design (the off→on
            // cycle re-enables them). Persist the reset intent, apply it to Windows, then the
            // toggles reflect the live result (see ApplyResponsivenessAsync).
            _wantForegroundBoost = _wantInstantAppFocus = _wantInstantStartupApps =
                _wantMaxResponsiveness = _wantNetworkThrottlingOff = _wantPowerThrottlingOff =
                _wantFastAppClose = _wantFasterShutdown = _wantInputHookTimeout =
                _wantServiceShutdownFast = _wantFastStartupOff = _wantBackgroundAppsOff = true;
            _wantKeepKernelInRam = _ramSupportsKeepKernel;   // default on only at >= 14 GB
            SaveSettings();
            _ = ApplyResponsivenessAsync();
        }
        else
        {
            // Engine turned OFF → gray the toggles off and revert the tweaks, but DON'T
            // route through the OnChanged handlers (suppressed via _loadingSettings) so
            // the grayed-off state isn't mistaken for the user's saved preference.
            _loadingSettings = true;
            ForegroundBoostEnabled      = false;
            InstantAppFocusEnabled      = false;
            InstantStartupApps          = false;
            MaxResponsivenessEnabled    = false;
            NetworkThrottlingOffEnabled = false;
            PowerThrottlingOffEnabled   = false;
            FastAppCloseEnabled         = false;
            KeepKernelInRamEnabled      = false;
            FasterShutdownEnabled       = false;
            InputHookTimeoutEnabled     = false;
            ServiceShutdownFastEnabled  = false;
            FastStartupOffEnabled       = false;
            BackgroundAppsOffEnabled    = false;
            _loadingSettings = false;
            _ = _stability.DisableForegroundBoostAsync();
            _ = _stability.DisableInstantAppFocusAsync();
            _ = _stability.DisableInstantStartupAppsAsync();
            _ = _stability.DisableMaxResponsivenessAsync();
            _ = _stability.DisableNetworkThrottlingOffAsync();
            _ = _stability.DisablePowerThrottlingOffAsync();
            _ = _stability.DisableFastAppCloseAsync();
            _ = _stability.DisableKeepKernelInRamAsync();
            _ = _stability.DisableFasterShutdownAsync();
            _ = _stability.DisableInputHookTimeoutAsync();
            _ = _stability.DisableServiceShutdownFastAsync();
            _ = _stability.EnableFastStartupAsync();                  // revert: Fast Startup back on
            _ = _stability.DisableBackgroundAppsOffAsync();           // revert: allow background apps again
            _settings.MaxResponsivenessEnabled = false;
        }

        OnPropertyChanged(nameof(CanUseLaunchBoost));
        OnPropertyChanged(nameof(CanUseResponsiveness));

        _service.UpdateSettings(BuildSettings());
        SaveSettings();
        if (value) _service.Start();
        else       _service.Stop();
    }

    partial void OnNapChildrenEnabledChanged(bool value)    => PushSettings();
    partial void OnCompressDeepSleepChanged(bool value)     => PushSettings();

    // ── Launch Boost ──────────────────────────────────────────────────────────
    partial void OnLaunchBoostEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLaunchBoostConfigEnabled)); // re-gray the sub-controls
        PushSettings();
    }
    partial void OnLaunchBoostDurationSecondsChanged(int value)
    {
        int c = Math.Clamp(value, 3, 120);
        if (c != value) { LaunchBoostDurationSeconds = c; return; }
        PushSettings();
    }
    partial void OnLaunchBoostCpuChanged(bool value)               => PushSettings();
    partial void OnLaunchBoostIoChanged(bool value)                => PushSettings();
    partial void OnLaunchBoostDisableEfficiencyChanged(bool value) => PushSettings();
    partial void OnLaunchBoostGpuChanged(bool value)               => PushSettings();

    /// <summary>True when the Launch Boost sub-settings should be interactive
    /// (i.e. the master toggle is on). XAML binds IsEnabled to this so everything
    /// below the toggle is grayed out until Launch Boost is enabled.</summary>
    public bool IsLaunchBoostConfigEnabled => LaunchBoostEnabled;

    /// <summary>Launch Boost can only be used while Task Sleep itself is on. When
    /// Task Sleep is off the whole card is grayed and the master toggle disabled.</summary>
    public bool CanUseLaunchBoost => IsEnabled;

    // ── Responsiveness (Foreground Priority Boost + Instant App Focus) ─────────
    // Each persists the user's choice via SaveSettings() so a boost turned off while the
    // engine is on stays off across restarts (until the engine is cycled off→on).
    partial void OnForegroundBoostEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantForegroundBoost = value;
        if (value) _ = _stability.EnableForegroundBoostAsync();
        else       _ = _stability.DisableForegroundBoostAsync();
        SaveSettings();
    }

    partial void OnInstantAppFocusEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantInstantAppFocus = value;
        if (value) _ = _stability.EnableInstantAppFocusAsync();
        else       _ = _stability.DisableInstantAppFocusAsync();
        SaveSettings();
    }

    partial void OnInstantStartupAppsChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantInstantStartupApps = value;
        if (value) _ = _stability.EnableInstantStartupAppsAsync();
        else       _ = _stability.DisableInstantStartupAppsAsync();
        SaveSettings();
    }

    // ── Maximum System Responsiveness (engine-gated) ───────────────────────────
    // Part of the engine like the boosts above. Writes MMCSS SystemResponsiveness
    // (0 = on, 20 = Windows default; takes effect on the next restart) and keeps the
    // opt-in flag in sync so GameBooster's VSync self-heal honours the choice.
    partial void OnMaxResponsivenessEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantMaxResponsiveness = value;
        if (value) _ = _stability.EnableMaxResponsivenessAsync();
        else       _ = _stability.DisableMaxResponsivenessAsync();
        _settings.MaxResponsivenessEnabled = value;
        SaveSettings();
    }

    partial void OnNetworkThrottlingOffEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantNetworkThrottlingOff = value;
        if (value) _ = _stability.EnableNetworkThrottlingOffAsync();
        else       _ = _stability.DisableNetworkThrottlingOffAsync();
        SaveSettings();
    }

    partial void OnPowerThrottlingOffEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantPowerThrottlingOff = value;
        if (value) _ = _stability.EnablePowerThrottlingOffAsync();
        else       _ = _stability.DisablePowerThrottlingOffAsync();
        SaveSettings();
    }

    partial void OnFastAppCloseEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantFastAppClose = value;
        if (value) _ = _stability.EnableFastAppCloseAsync();
        else       _ = _stability.DisableFastAppCloseAsync();
        SaveSettings();
    }

    partial void OnKeepKernelInRamEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantKeepKernelInRam = value;
        if (value) _ = _stability.EnableKeepKernelInRamAsync();
        else       _ = _stability.DisableKeepKernelInRamAsync();
        SaveSettings();
    }

    partial void OnFasterShutdownEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantFasterShutdown = value;
        if (value) _ = _stability.EnableFasterShutdownAsync();
        else       _ = _stability.DisableFasterShutdownAsync();
        SaveSettings();
    }

    partial void OnInputHookTimeoutEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantInputHookTimeout = value;
        if (value) _ = _stability.EnableInputHookTimeoutAsync();
        else       _ = _stability.DisableInputHookTimeoutAsync();
        SaveSettings();
    }

    partial void OnServiceShutdownFastEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantServiceShutdownFast = value;
        if (value) _ = _stability.EnableServiceShutdownFastAsync();
        else       _ = _stability.DisableServiceShutdownFastAsync();
        SaveSettings();
    }

    partial void OnFastStartupOffEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantFastStartupOff = value;
        if (value) _ = _stability.DisableFastStartupAsync();   // boost ON = Fast Startup OFF
        else       _ = _stability.EnableFastStartupAsync();
        SaveSettings();
    }

    partial void OnBackgroundAppsOffEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _wantBackgroundAppsOff = value;
        if (value) _ = _stability.EnableBackgroundAppsOffAsync();   // boost ON = background apps OFF
        else       _ = _stability.DisableBackgroundAppsOffAsync();
        SaveSettings();
    }

    /// <summary>
    /// Applies every responsiveness boost the user wants (intent — default on) to the live
    /// Windows state, then refreshes the toggles so they reflect what actually stuck. This is
    /// the "on by default + self-heal" path: a value Windows reset (e.g. an Explorer startup
    /// clobber or a feature update) is re-asserted here on the next launch / engine-on.
    /// </summary>
    private Task ApplyResponsivenessAsync() => Task.Run(async () =>
    {
        try
        {
            if (_wantForegroundBoost)      await _stability.EnableForegroundBoostAsync();      else await _stability.DisableForegroundBoostAsync();
            if (_wantInstantAppFocus)      await _stability.EnableInstantAppFocusAsync();      else await _stability.DisableInstantAppFocusAsync();
            if (_wantInstantStartupApps)   await _stability.EnableInstantStartupAppsAsync();   else await _stability.DisableInstantStartupAppsAsync();
            if (_wantMaxResponsiveness)  { await _stability.EnableMaxResponsivenessAsync();  _settings.MaxResponsivenessEnabled = true;  }
            else                         { await _stability.DisableMaxResponsivenessAsync(); _settings.MaxResponsivenessEnabled = false; }
            if (_wantNetworkThrottlingOff) await _stability.EnableNetworkThrottlingOffAsync(); else await _stability.DisableNetworkThrottlingOffAsync();
            if (_wantPowerThrottlingOff)   await _stability.EnablePowerThrottlingOffAsync();   else await _stability.DisablePowerThrottlingOffAsync();
            if (_wantFastAppClose)         await _stability.EnableFastAppCloseAsync();          else await _stability.DisableFastAppCloseAsync();
            if (_wantKeepKernelInRam)      await _stability.EnableKeepKernelInRamAsync();       else await _stability.DisableKeepKernelInRamAsync();
            if (_wantFasterShutdown)       await _stability.EnableFasterShutdownAsync();        else await _stability.DisableFasterShutdownAsync();
            if (_wantInputHookTimeout)     await _stability.EnableInputHookTimeoutAsync();      else await _stability.DisableInputHookTimeoutAsync();
            if (_wantServiceShutdownFast)  await _stability.EnableServiceShutdownFastAsync();   else await _stability.DisableServiceShutdownFastAsync();

            // Fast Startup off is heavier (powercfg), so only act when the live state isn't
            // already what we want — this avoids spawning powercfg on every launch.
            if (_wantFastStartupOff) { if (!_stability.IsFastStartupDisabled()) await _stability.DisableFastStartupAsync(); }
            else                     { if (_stability.IsFastStartupDisabled())  await _stability.EnableFastStartupAsync();  }
            if (_wantBackgroundAppsOff) await _stability.EnableBackgroundAppsOffAsync(); else await _stability.DisableBackgroundAppsOffAsync();
        }
        catch (Exception ex) { _log.Warn("TaskSleepViewModel", $"ApplyResponsivenessAsync failed: {ex.Message}"); }
        finally { RefreshResponsivenessDisplay(); }
    });

    /// <summary>
    /// Re-reads the LIVE Windows value for every responsiveness boost into its toggle, so a
    /// switch reads "On" only when the registry value is actually set (never a stale saved
    /// preference). The detectors are read on the CALLING thread (some spawn sc.exe, so this
    /// must not be the UI thread); only the property assignments are marshalled to the UI
    /// thread, where the per-toggle OnChanged handlers are suppressed (via
    /// <see cref="_loadingSettings"/>) so this never re-applies or persists anything.
    /// </summary>
    private void RefreshResponsivenessDisplay()
    {
        // Read live state here (off the UI thread when called from a background apply).
        bool fgBoost      = _stability.IsForegroundBoostEnabled();
        bool appFocus     = _stability.IsInstantAppFocusEnabled();
        bool startupApps  = _stability.IsStartupAppDelayDisabled();
        bool maxResp      = _stability.IsMaxResponsivenessEnabled();
        bool netThrottle  = _stability.IsNetworkThrottlingDisabled();
        bool pwrThrottle  = _stability.IsPowerThrottlingDisabled();
        bool fastClose    = _stability.IsFastAppCloseEnabled();
        bool keepKernel   = _stability.IsKeepKernelInRamEnabled();
        bool fastShutdown = _stability.IsFasterShutdownEnabled();
        bool inputHook    = _stability.IsInputHookTimeoutEnabled();
        bool svcShutdown  = _stability.IsServiceShutdownFastEnabled();
        bool fastStartOff = _stability.IsFastStartupDisabled();
        bool bgAppsOff    = _stability.IsBackgroundAppsDisabled();

        void Assign()
        {
            bool prev = _loadingSettings;
            _loadingSettings = true;
            try
            {
                ForegroundBoostEnabled      = fgBoost;
                InstantAppFocusEnabled      = appFocus;
                InstantStartupApps          = startupApps;
                MaxResponsivenessEnabled    = maxResp;
                NetworkThrottlingOffEnabled = netThrottle;
                PowerThrottlingOffEnabled   = pwrThrottle;
                FastAppCloseEnabled         = fastClose;
                KeepKernelInRamEnabled      = keepKernel;
                FasterShutdownEnabled       = fastShutdown;
                InputHookTimeoutEnabled     = inputHook;
                ServiceShutdownFastEnabled  = svcShutdown;
                FastStartupOffEnabled       = fastStartOff;
                BackgroundAppsOffEnabled    = bgAppsOff;
            }
            finally { _loadingSettings = prev; }
        }

        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.BeginInvoke(new Action(Assign));
        else Assign();
    }

    /// <summary>Re-applies the AC-gated Power Throttling state on plug-in / unplug so it
    /// only suppresses throttling on AC and never costs battery runtime.</summary>
    private void OnPowerModeChangedForThrottling(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        if (_wantPowerThrottlingOff && IsEnabled)
            _ = _stability.EnablePowerThrottlingOffAsync();
    }

    /// <summary>The responsiveness boosts can only be used while the engine is on.
    /// When it's off the cards gray out and the toggles are forced off.</summary>
    public bool CanUseResponsiveness => IsEnabled;

    /// <summary>Enables Launch Boost from Auto-Pilot. Launch Boost depends on Task
    /// Sleep running, so this turns Task Sleep on first (if needed) and then arms
    /// Launch Boost. Both changes persist and push to the service.</summary>
    public void EnableLaunchBoost()
    {
        if (!IsEnabled) IsEnabled = true;          // OnIsEnabledChanged starts the service + saves
        if (!LaunchBoostEnabled) LaunchBoostEnabled = true; // OnLaunchBoostEnabledChanged pushes + persists
    }

    partial void OnSystemCpuTriggerPercentChanged(int value)  => PushSettings();
    partial void OnProcessCpuStopPercentChanged(int value)    => PushSettings();
    partial void OnTimeOverQuotaMsChanged(int value)          => PushSettings();
    partial void OnMinAdjustmentDurationMsChanged(int value)  => PushSettings();
    partial void OnMaxAdjustmentDurationMsChanged(int value)  => PushSettings();

    partial void OnMinimizeNapEnabledChanged(bool value)              => PushSettings();
    partial void OnSkipBusyMinimizedAppsChanged(bool value)           => PushSettings();
    partial void OnBusyMinimizedCpuThresholdPercentChanged(int value) => PushSettings();
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

    // ── Advanced feature callbacks ───────────────────────────────────────────
    partial void OnElevatedProcessGuardEnabledChanged(bool value)        => PushSettings();
    partial void OnMultiMonitorAwarenessEnabledChanged(bool value)       => PushSettings();
    partial void OnProcessGroupAwarenessEnabledChanged(bool value)       => PushSettings();
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
        if (_loadingSettings) return;   // don't push/save mid-load
        _service.UpdateSettings(BuildSettings());
        SaveSettings();
    }

    private void RefreshMonitor()
    {
        var snapshot = _service.GetLatestSnapshot();

        SystemCpuDisplay = snapshot != null ? $"System CPU: {snapshot.SystemCpuPercent:F0}%" : "System CPU: —";
        int nSleeping = snapshot?.TotalThrottled ?? 0;
        int nPending  = snapshot?.Processes.Count(p => p.IsPendingNap) ?? 0;
        AppsNappingCount = nSleeping;
        ActiveAppsCount  = snapshot?.Processes.Count(p => !p.IsThrottled) ?? 0;
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
        // Foreground app's helper processes are protected from napping by default.
        // The v0.7.9 UI toggle for this is gone — it was paired with the hard
        // RAM cap, which has been removed. The underlying setting stays here as
        // a hardcoded safe default; expose a new toggle if future use cases need it.
        ActOnForegroundChildren = false,
        ExcludeSystemServices   = true,
        EnableEfficiencyMode    = true,
        PersistentNapEnabled    = true,
        EnforceSettings         = true,
        SoftNapEnabled          = false,
        // GPU priority is no longer touched at all — removed to avoid ever disrupting
        // the shared HAGS flip queue that games and DWM rely on for VSync.
        LowerIoPriority         = true,
        DetectECores            = true,
        MoveToECores            = true,
        LowerMemoryPriority     = true,
        TrimWorkingSet          = true,
        // Compress in deep sleep — on by default. Trim once when a process
        // crosses the deep-sleep threshold AND after every brief wake while
        // still in deep sleep. Replaces the v0.7.9 hard RAM cap + re-trim-
        // after-brief-wake + ActOnForegroundChildren toggles.
        CompressDeepSleep       = CompressDeepSleep,
        AdaptiveTick            = true,
        // User-configurable
        NapChildrenEnabled      = NapChildrenEnabled,
        SystemCpuTriggerPercent = SystemCpuTriggerPercent,
        ProcessCpuStopPercent   = ProcessCpuStopPercent,
        TimeOverQuotaMs         = TimeOverQuotaMs,
        MinAdjustmentDurationMs = MinAdjustmentDurationMs,
        MaxAdjustmentDurationMs = MaxAdjustmentDurationMs,
        MinimizeNapEnabled              = MinimizeNapEnabled,
        SkipBusyMinimizedApps           = SkipBusyMinimizedApps,
        BusyMinimizedCpuThresholdPercent = BusyMinimizedCpuThresholdPercent,
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
        ProcessGroupAwarenessEnabled       = ProcessGroupAwarenessEnabled,
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
        // Launch Boost
        LaunchBoostEnabled           = LaunchBoostEnabled,
        LaunchBoostDurationSeconds   = Math.Clamp(LaunchBoostDurationSeconds, 3, 120),
        LaunchBoostCpu               = LaunchBoostCpu,
        LaunchBoostIo                = LaunchBoostIo,
        LaunchBoostDisableEfficiency = LaunchBoostDisableEfficiency,
        LaunchBoostGpu               = LaunchBoostGpu,
    };

    // ── Registry persistence (scalar settings) ────────────────────────────────

    private void LoadSettings()
    {
        _log.Info("TaskSleepViewModel", "LoadSettings starting — reading registry");
        _loadingSettings = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: false);
            if (key == null) { _log.Warn("TaskSleepViewModel", "LoadSettings: registry key not found — using defaults"); return; }

            IsEnabled               = ReadBool(key, "IsEnabled",               true);
            NapChildrenEnabled      = ReadBool(key, "NapChildrenEnabled",      false);
            // CompressDeepSleep defaults to true — existing users who never had this
            // key (upgraders from < v0.7.9) get the new default automatically; users
            // who explicitly disabled it will see their preference preserved.
            CompressDeepSleep       = ReadBool(key, "CompressDeepSleep",       true);
            SystemCpuTriggerPercent = Math.Clamp(ReadInt(key, "SystemCpuTriggerPercent",  12), 1, 100);
            ProcessCpuStopPercent   = Math.Clamp(ReadInt(key, "ProcessCpuStopPercent",     3), 0, 100);
            TimeOverQuotaMs         = Math.Clamp(ReadInt(key, "TimeOverQuotaMs",        1500), 100, 60_000);
            MinAdjustmentDurationMs = Math.Clamp(ReadInt(key, "MinAdjustmentDurationMs", 5000), 500, 300_000);
            MaxAdjustmentDurationMs = Math.Clamp(ReadInt(key, "MaxAdjustmentDurationMs", 30000), 1000, 3_600_000);
            MinimizeNapEnabled              = ReadBool(key, "MinimizeNapEnabled",              true);
            SkipBusyMinimizedApps           = ReadBool(key, "SkipBusyMinimizedApps",           true);
            BusyMinimizedCpuThresholdPercent = Math.Clamp(ReadInt(key, "BusyMinimizedCpuThresholdPercent", 30), 1, 100);
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
            NappedCpuCapPercent         = Math.Clamp(ReadInt(key, "NappedCpuCapPercent", 1), 1, 100);
            BriefWakeCpuCapPercent      = Math.Clamp(ReadInt(key, "BriefWakeCpuCapPercent", 5), 1, 100);
            SuppressBriefWakesDuringGameMode = ReadBool(key, "SuppressBriefWakesDuringGameMode", true);
            MaxConcurrentBriefWakes     = Math.Clamp(ReadInt(key, "MaxConcurrentBriefWakes",  3), 1, 10);
            // Advanced features
            ElevatedProcessGuardEnabled        = ReadBool(key, "ElevatedProcessGuardEnabled",        true);
            MultiMonitorAwarenessEnabled       = ReadBool(key, "MultiMonitorAwarenessEnabled",       true);
            ProcessGroupAwarenessEnabled       = ReadBool(key, "ProcessGroupAwarenessEnabled",       true);
            BackgroundNapEnabled               = ReadBool(key, "BackgroundNapEnabled",               true);
            BackgroundNapAfterMs               = Math.Clamp(ReadInt(key, "BackgroundNapAfterMs",         180_000), 30_000, 600_000);
            IdleNapEnabled                     = ReadBool(key, "IdleNapEnabled",                     true);
            IdleNapAfterMs                     = Math.Clamp(ReadInt(key, "IdleNapAfterMs",               120_000), 30_000, 600_000);
            // Launch Boost (off by default — opt-in)
            LaunchBoostEnabled                 = ReadBool(key, "LaunchBoostEnabled",                 false);
            LaunchBoostDurationSeconds         = Math.Clamp(ReadInt(key, "LaunchBoostDurationSeconds", 20), 3, 120);
            LaunchBoostCpu                     = ReadBool(key, "LaunchBoostCpu",                      true);
            LaunchBoostIo                      = ReadBool(key, "LaunchBoostIo",                       true);
            LaunchBoostDisableEfficiency       = ReadBool(key, "LaunchBoostDisableEfficiency",       true);
            LaunchBoostGpu                     = ReadBool(key, "LaunchBoostGpu",                      false);
            // Responsiveness boosts — load the user's INTENT (persisted, default ON) into the
            // _want* fields. The toggles themselves are set from the LIVE Windows state after
            // the on-launch apply (see ApplyResponsivenessAsync / RefreshResponsivenessDisplay),
            // so a switch reads "On" only when the registry value is actually set. An engine
            // off→on cycle resets intent back on (see OnIsEnabledChanged).
            _wantForegroundBoost      = ReadBool(key, "ForegroundBoostEnabled",      true);
            _wantNetworkThrottlingOff = ReadBool(key, "NetworkThrottlingOffEnabled", true);
            _wantPowerThrottlingOff   = ReadBool(key, "PowerThrottlingOffEnabled",   true);
            _wantFastAppClose         = ReadBool(key, "FastAppCloseEnabled",         true);
            _wantKeepKernelInRam      = ReadBool(key, "KeepKernelInRamEnabled",      _ramSupportsKeepKernel);
            _wantFasterShutdown       = ReadBool(key, "FasterShutdownEnabled",       true);
            _wantInputHookTimeout     = ReadBool(key, "InputHookTimeoutEnabled",     true);
            _wantServiceShutdownFast  = ReadBool(key, "ServiceShutdownFastEnabled",  true);
            _wantFastStartupOff       = ReadBool(key, "FastStartupOffEnabled",       true);
            _wantBackgroundAppsOff    = ReadBool(key, "BackgroundAppsOffEnabled",    true);
            _wantInstantAppFocus      = ReadBool(key, "InstantAppFocusEnabled",      true);
            _wantInstantStartupApps   = ReadBool(key, "InstantStartupApps",          true);
            _wantMaxResponsiveness    = ReadBool(key, "MaxResponsivenessEnabled",    true);
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepViewModel", $"LoadSettings failed: {ex.Message}");
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SaveSettings()
    {
        if (_loadingSettings) return;   // never persist while loading from the registry
        var caller = new System.Diagnostics.StackTrace(1, false).ToString().Split('\n')[0].Trim();
        _log.Info("TaskSleepViewModel",
            $"SaveSettings called — NappedCpuCapPercent={NappedCpuCapPercent}, " +
            $"CompressDeepSleep={CompressDeepSleep} — caller: {caller}");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegKey, writable: true);
            if (key == null) { _log.Warn("TaskSleepViewModel", "SaveSettings: registry key creation failed (null)"); return; }

            key.SetValue("IsEnabled",               IsEnabled               ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("NapChildrenEnabled",      NapChildrenEnabled      ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("CompressDeepSleep",       CompressDeepSleep       ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("SystemCpuTriggerPercent", SystemCpuTriggerPercent,          RegistryValueKind.DWord);
            key.SetValue("ProcessCpuStopPercent",   ProcessCpuStopPercent,            RegistryValueKind.DWord);
            key.SetValue("TimeOverQuotaMs",         TimeOverQuotaMs,                  RegistryValueKind.DWord);
            key.SetValue("MinAdjustmentDurationMs", MinAdjustmentDurationMs,          RegistryValueKind.DWord);
            key.SetValue("MaxAdjustmentDurationMs", MaxAdjustmentDurationMs,          RegistryValueKind.DWord);
            key.SetValue("MinimizeNapEnabled",              MinimizeNapEnabled      ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("SkipBusyMinimizedApps",           SkipBusyMinimizedApps   ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("BusyMinimizedCpuThresholdPercent", BusyMinimizedCpuThresholdPercent, RegistryValueKind.DWord);
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
            // Advanced features
            key.SetValue("ElevatedProcessGuardEnabled",        ElevatedProcessGuardEnabled        ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("MultiMonitorAwarenessEnabled",       MultiMonitorAwarenessEnabled       ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ProcessGroupAwarenessEnabled",       ProcessGroupAwarenessEnabled       ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("BackgroundNapEnabled",               BackgroundNapEnabled               ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("BackgroundNapAfterMs",               BackgroundNapAfterMs,                        RegistryValueKind.DWord);
            key.SetValue("IdleNapEnabled",                     IdleNapEnabled                     ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("IdleNapAfterMs",                     IdleNapAfterMs,                              RegistryValueKind.DWord);
            // Launch Boost
            key.SetValue("LaunchBoostEnabled",                 LaunchBoostEnabled                 ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("LaunchBoostDurationSeconds",         LaunchBoostDurationSeconds,                  RegistryValueKind.DWord);
            key.SetValue("LaunchBoostCpu",                     LaunchBoostCpu                     ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("LaunchBoostIo",                      LaunchBoostIo                      ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("LaunchBoostDisableEfficiency",       LaunchBoostDisableEfficiency       ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("LaunchBoostGpu",                     LaunchBoostGpu                     ? 1 : 0, RegistryValueKind.DWord);
            // Responsiveness boosts — persist the user's INTENT (the _want* fields), NOT the
            // live-display toggles, so a value Windows reset isn't mistaken for "turned off".
            key.SetValue("ForegroundBoostEnabled",      _wantForegroundBoost      ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("NetworkThrottlingOffEnabled", _wantNetworkThrottlingOff ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("PowerThrottlingOffEnabled",   _wantPowerThrottlingOff   ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("FastAppCloseEnabled",         _wantFastAppClose         ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("KeepKernelInRamEnabled",      _wantKeepKernelInRam      ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("FasterShutdownEnabled",       _wantFasterShutdown       ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("InputHookTimeoutEnabled",     _wantInputHookTimeout     ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ServiceShutdownFastEnabled",  _wantServiceShutdownFast  ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("FastStartupOffEnabled",       _wantFastStartupOff       ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("BackgroundAppsOffEnabled",    _wantBackgroundAppsOff    ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("InstantAppFocusEnabled",      _wantInstantAppFocus      ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("InstantStartupApps",          _wantInstantStartupApps   ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("MaxResponsivenessEnabled",    _wantMaxResponsiveness    ? 1 : 0, RegistryValueKind.DWord);
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
