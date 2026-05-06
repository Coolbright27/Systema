// ════════════════════════════════════════════════════════════════════════════
// DashboardViewModel.cs  ·  Live status hub + Auto-Pilot optimizer
// ════════════════════════════════════════════════════════════════════════════
//
// The new dashboard shows three live status indicators (Task Sleep, Game Boost,
// Privacy), a list of apps currently being napped, an Auto-Pilot card that
// checks and applies the full recommended optimisation set in one click, and a
// system info footer (power plan, RAM usage).
//
// Auto-Pilot checks (and applies if needed):
//   1. Page file size         — configured to recommended MB based on installed RAM
//   2. Data collection        — telemetry services disabled
//   3. Power plan             — High Performance
//   4. Battery power (if any) — Balanced / 99% DC cap on battery
//   5. Game Boost             — master switch on
//   6. DNS                    — Cloudflare 1.1.1.1
//   7. Preview updates        — blocked
//   8. CPU core efficiency    — forced core parking enabled
//   9. Launch on startup      — Systema starts with Windows
//  10. SMBv1 removed          — uninstalls the insecure legacy SMBv1 protocol if present
//
// The button is disabled (greyed) once all items are already applied.
// It shows "Optimizing…" with loading state while running.
//
// RELATED FILES
//   Views/DashboardView.xaml      — binds to all properties here
//   Services/GameBoosterService   — game boost status
//   ViewModels/TaskSleepViewModel — napped process list
//   Services/ServiceControlService — telemetry status + disable
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Core;
using Systema.Services;
using static Systema.Core.ThreadHelper;
using System.IO;

namespace Systema.ViewModels;

/// <summary>Single item in the Auto-Pilot checklist.</summary>
public class AutoPilotItem
{
    public string Label  { get; set; } = "";
    public bool   IsDone { get; set; }
    public string Detail { get; set; } = "";
}

public partial class DashboardViewModel : ObservableObject, IAutoRefreshable
{
    // ── Services ──────────────────────────────────────────────────────────────
    private readonly GameBoosterService         _gameBooster;
    private readonly TaskSleepViewModel         _taskSleepVm;
    private readonly ServiceControlService      _serviceControl;
    private readonly MemoryService              _memoryService;
    private readonly DnsService                 _dnsService;
    private readonly PowerPlanService           _powerPlan;
    private readonly WindowsUpdateTweaksService _wuTweaks;
    private readonly CoreParkingService         _corePark;
    private readonly SettingsService            _settings;
    private readonly OptionalFeaturesService    _optFeatures;
    private readonly SystemStabilityService     _stability;
    private static readonly LoggerService _log = LoggerService.Instance;

    // ── Status pills ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _taskSleepActive;
    [ObservableProperty] private int    _nappedAppCount;
    [ObservableProperty] private string _taskSleepStatus = "Off";

    [ObservableProperty] private bool   _gameBoostActive;
    [ObservableProperty] private bool   _gamesDetected;
    [ObservableProperty] private bool   _gameBoostEnabled;
    [ObservableProperty] private string _gameBoostStatus = "Idle";

    [ObservableProperty] private bool   _dataCollectionBlocked;
    [ObservableProperty] private string _dataCollectionStatus = "Checking…";

    // ── Napping list ──────────────────────────────────────────────────────────
    /// <summary>Names of processes currently napped by Task Sleep (top 8).</summary>
    public ObservableCollection<string> NappedApps { get; } = new();

    // ── System info footer ────────────────────────────────────────────────────
    [ObservableProperty] private string _activePlan   = "—";
    [ObservableProperty] private string _ramUsageText = "—";
    [ObservableProperty] private string _statusMessage = "Loading…";

    // ── Auto-Pilot ────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isAutoPilotRunning;
    [ObservableProperty] private bool   _isAutoPilotApplied;
    [ObservableProperty] private int    _autoPilotPendingCount;
    [ObservableProperty] private string _autoPilotButtonText = "Checking…";

    /// <summary>
    /// Auto-Pilot Mode — when true, all Auto-Pilot-managed settings across every
    /// tab are locked (grayed out) and auto-healed every 30 s if they drift.
    /// Persisted to HKCU so the mode survives app updates and restarts.
    /// </summary>
    [ObservableProperty] private bool _autoPilotModeEnabled;

    // Throttle: re-check auto-pilot status at most once every 30s during refresh ticks
    private DateTime _lastAutoPilotCheck = DateTime.MinValue;
    private int      _autoPilotCheckInFlight; // Interlocked flag

    /// <summary>Live checklist shown inside the Auto-Pilot card.</summary>
    public ObservableCollection<AutoPilotItem> AutoPilotChecklist { get; } = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public DashboardViewModel(
        GameBoosterService         gameBooster,
        TaskSleepViewModel         taskSleepVm,
        ServiceControlService      serviceControl,
        MemoryService              memoryService,
        DnsService                 dnsService,
        PowerPlanService           powerPlan,
        WindowsUpdateTweaksService wuTweaks,
        CoreParkingService         corePark,
        SettingsService            settings,
        OptionalFeaturesService    optFeatures,
        SystemStabilityService     stability)
    {
        _gameBooster    = gameBooster;
        _taskSleepVm    = taskSleepVm;
        _serviceControl = serviceControl;
        _memoryService  = memoryService;
        _dnsService     = dnsService;
        _powerPlan      = powerPlan;
        _wuTweaks       = wuTweaks;
        _corePark       = corePark;
        _settings       = settings;
        _optFeatures    = optFeatures;
        _stability      = stability;

        // Restore persisted mode — no PropertyChanged callback fires on field-init.
        _autoPilotModeEnabled = _settings.AutoPilotModeEnabled;

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        await RefreshAsync();
        await CheckAutoPilotStatusAsync();
    }

    // ── IAutoRefreshable — called every 1 s / 5 s by MainViewModel timer ─────

    public Task RefreshAsync()
    {
        // Task Sleep ─────────────────────────────────────────────────
        TaskSleepActive = _taskSleepVm.IsEnabled;

        var throttled = _taskSleepVm.LiveProcesses
            .Where(p => p.IsThrottled)
            .Take(8)
            .Select(p => p.Name)
            .ToList();
        NappedAppCount = throttled.Count;

        TaskSleepStatus = TaskSleepActive
            ? (NappedAppCount > 0
                ? $"Active · Napping {NappedAppCount} app{(NappedAppCount == 1 ? "" : "s")}"
                : "Active · All apps behaving")
            : "Off — enable in Task Sleep tab";

        // Sync the NappedApps observable collection (add/remove only what changed)
        var toRemove = NappedApps.Except(throttled).ToList();
        var toAdd    = throttled.Except(NappedApps).ToList();
        foreach (var r in toRemove) NappedApps.Remove(r);
        foreach (var a in toAdd)    NappedApps.Add(a);

        // Game Boost ─────────────────────────────────────────────────
        GameBoostEnabled = _gameBooster.IsEnabled;
        GameBoostActive  = _gameBooster.BoostActive;
        GamesDetected    = _gameBooster.GamesInstalled;
        GameBoostStatus  = !GameBoostEnabled
            ? "Disabled"
            : GameBoostActive
                ? $"Boosting: {_gameBooster.ActiveGameName ?? "Game"}"
                : GamesDetected ? "Ready · Games detected" : "Idle · No game running";

        // Privacy ────────────────────────────────────────────────────
        try
        {
            DataCollectionBlocked = _serviceControl.AreTelemetryServicesDisabled();
            DataCollectionStatus  = DataCollectionBlocked
                ? "Protected"
                : "Collecting data";
        }
        catch (Exception ex) { _log.Warn("DashboardViewModel", $"Telemetry status check failed: {ex.Message}"); DataCollectionStatus = "Unknown"; }

        // RAM ────────────────────────────────────────────────────────
        try
        {
            var (total, avail) = _memoryService.GetRamStats();
            long used   = total - avail;
            RamUsageText = $"{used / 1024.0:F1} / {total / 1024.0:F1} GB";
        }
        catch (Exception ex) { _log.Warn("DashboardViewModel", $"RAM stats failed: {ex.Message}"); RamUsageText = "—"; }

        StatusMessage = $"Systema is running · {DateTime.Now:HH:mm}";

        // Re-check Auto-Pilot status every 30 s so changes made in other tabs are
        // reflected as soon as the user navigates back to the Dashboard.
        // Acquire the Interlocked gate FIRST, then check the time inside the gate
        // to avoid a race where two ticks both pass the >= 30 s check.
        if (!IsAutoPilotRunning &&
            Interlocked.CompareExchange(ref _autoPilotCheckInFlight, 1, 0) == 0)
        {
            if ((DateTime.Now - _lastAutoPilotCheck).TotalSeconds >= 30)
            {
                _lastAutoPilotCheck = DateTime.Now;
                _ = CheckAutoPilotStatusAsync().ContinueWith(_ =>
                    Interlocked.Exchange(ref _autoPilotCheckInFlight, 0));
            }
            else
            {
                Interlocked.Exchange(ref _autoPilotCheckInFlight, 0);
            }
        }

        return Task.CompletedTask;
    }

    // ── Auto-Pilot status check ───────────────────────────────────────────────

    private async Task CheckAutoPilotStatusAsync()
    {
        try
        {
            var items   = new List<AutoPilotItem>();
            int pending = 0;

            // Run all checks on a background thread (some hit the registry / powercfg)
            await RunOnLargeStackAsync(() =>
            {
                // 1. Page file
                var (initMb, _, isManaged) = _memoryService.GetPagefileSettings();
                var (recommended, ramMb)   = _memoryService.GetRecommendedPagefileWithRam();
                bool pgOk = !isManaged && initMb >= recommended - 512;
                if (!pgOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Page file",
                    IsDone = pgOk,
                    Detail = pgOk
                        ? $"Optimized — {initMb / 1024} GB (based on your {ramMb / 1024} GB RAM)"
                        : $"Set to {recommended / 1024} GB (based on your {ramMb / 1024} GB RAM)",
                });

                // 2. Data collection
                bool telOk = _serviceControl.AreTelemetryServicesDisabled();
                if (!telOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Data collection",
                    IsDone = telOk,
                    Detail = telOk ? "Blocked" : "Telemetry services are active",
                });

                // 3. Power plan
                string plan  = _powerPlan.GetActivePlan();
                bool hasBattery = _powerPlan.HasBattery();
                bool batteryOptConfigured = !string.IsNullOrEmpty(_settings.BatteryOptimizationMode);
                bool isHighPerf = plan.Contains("High Performance", StringComparison.OrdinalIgnoreCase)
                               || plan.Contains("Ultimate", StringComparison.OrdinalIgnoreCase);
                // On a laptop with battery optimization configured, the plan switching
                // to Balanced on battery is expected behaviour — not a problem.
                bool planOk = isHighPerf
                           || (hasBattery && batteryOptConfigured &&
                               plan.Contains("Balanced", StringComparison.OrdinalIgnoreCase));
                if (!planOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Power plan",
                    IsDone = planOk,
                    Detail = isHighPerf ? "High Performance"
                           : planOk    ? "Balanced on battery (auto-switching enabled)"
                                       : $"Currently: {plan}",
                });
                ActivePlan = plan;

                // 4. Balanced on battery (only if battery present)
                if (hasBattery)
                {
                    bool battOk = batteryOptConfigured;
                    if (!battOk) pending++;
                    items.Add(new AutoPilotItem
                    {
                        Label  = "Balanced on battery",
                        IsDone = battOk,
                        Detail = battOk ? "Balanced on battery, High Performance on AC" : "Not configured — click Optimize to enable",
                    });
                }

                // 5. Game Boost
                bool gbOk = _settings.GameBoosterEnabled;
                if (!gbOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Game Boost",
                    IsDone = gbOk,
                    Detail = gbOk ? "Enabled" : "Disabled",
                });

                // 6. DNS — Cloudflare
                string dns   = _dnsService.GetCurrentDns();
                bool   dnsOk = dns.Contains("1.1.1.1");
                if (!dnsOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "DNS",
                    IsDone = dnsOk,
                    Detail = dnsOk
                        ? "Cloudflare (1.1.1.1)"
                        : $"Current: {(string.IsNullOrWhiteSpace(dns) ? "System Default" : dns)}",
                });

                // 7. Preview updates
                bool prevOk = _wuTweaks.IsPreviewUpdatesBlocked();
                if (!prevOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Preview updates",
                    IsDone = prevOk,
                    Detail = prevOk ? "Blocked" : "Preview builds allowed",
                });

                // 8. CPU core efficiency
                bool coreOk = _corePark.IsCoreParkingEnforced();
                if (!coreOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "CPU core efficiency",
                    IsDone = coreOk,
                    Detail = coreOk ? "Active" : "Not enforced",
                });

                // 9. Launch on startup
                bool startOk = _settings.StartWithWindows;
                if (!startOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Launch on startup",
                    IsDone = startOk,
                    Detail = startOk ? "Enabled" : "Disabled",
                });

                // 10. SMBv1 removed — insecure legacy protocol
                bool smb1Gone = !_optFeatures.IsSMBv1Present();
                if (!smb1Gone) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "SMBv1 removed",
                    IsDone = smb1Gone,
                    Detail = smb1Gone
                        ? "Removed — not installed"
                        : "Present — insecure legacy protocol (will be removed by Optimize)",
                });

                // 11. NTFS last-access timestamps disabled
                bool ntfsOk = _stability.IsNtfsLastAccessDisabled();
                if (!ntfsOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "NTFS last-access timestamps",
                    IsDone = ntfsOk,
                    Detail = ntfsOk ? "Disabled — reduces unnecessary disk writes" : "Enabled (default) — click Optimize to disable",
                });
            });

            // All registry/powercfg calls are done.
            // RunOnLargeStackAsync continuations run on a ThreadPool thread, so ALL
            // ObservableCollection mutations and UI-property writes must be marshalled
            // back to the UI thread — otherwise WPF raises InvalidOperationException.
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                AutoPilotPendingCount = pending;
                IsAutoPilotApplied    = pending == 0;
                AutoPilotButtonText   = pending == 0
                    ? "✓  All settings applied"
                    : $"Apply settings once  ({pending} item{(pending == 1 ? "" : "s")})";

                AutoPilotChecklist.Clear();
                foreach (var item in items)
                    AutoPilotChecklist.Add(item);

                RunAutoPilotCommand.NotifyCanExecuteChanged();

                // Auto-Pilot Mode enforcement: if the mode is on and any setting drifted
                // (e.g. a Windows update reverted a policy), re-apply automatically.
                if (_settings.AutoPilotModeEnabled && pending > 0 && !IsAutoPilotRunning)
                {
                    _log.Info("DashboardViewModel",
                        $"Auto-Pilot Mode: {pending} setting(s) drifted — re-applying automatically");
                    _ = RunAutoPilotAsync();
                }
            });
        }
        catch (Exception ex)
        {
            _log.Warn("DashboardViewModel", $"CheckAutoPilotStatus: {ex.Message}");
        }
    }

    // ── Auto-Pilot Mode toggle ────────────────────────────────────────────────

    /// <summary>
    /// Fires when the user toggles Auto-Pilot Mode on or off.
    /// ON  → persists, broadcasts to all ViewModels, immediately runs a full AutoPilot
    ///        pass so settings are applied right away.
    /// OFF → persists and broadcasts; no further action (settings stay as-is, just unlocked).
    /// </summary>
    partial void OnAutoPilotModeEnabledChanged(bool value)
    {
        _settings.AutoPilotModeEnabled = value;
        // The SettingsService.AutoPilotModeEnabled setter fires AutoPilotModeChanged automatically,
        // which propagates IsAutoPilotActive to VisualVm / ToolsVm / GameBoosterVm / SettingsVm.
        _log.Info("DashboardViewModel",
            value ? "Auto-Pilot Mode ON — applying settings and locking controls"
                  : "Auto-Pilot Mode OFF — controls unlocked");

        if (value)
            _ = RunAutoPilotAsync();
    }

    // ── Auto-Pilot run ────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRunAutoPilot))]
    private async Task RunAutoPilotAsync()
    {
        // Guard against concurrent invocations (relay command CanExecute + direct calls).
        if (IsAutoPilotRunning) return;
        IsAutoPilotRunning  = true;
        AutoPilotButtonText = "Optimizing…";
        RunAutoPilotCommand.NotifyCanExecuteChanged();

        try
        {
            _log.Info("DashboardViewModel", "Auto-Pilot started");

            // 1. Page file — set to recommended size based on installed RAM
            var (recommended, ramMb) = _memoryService.GetRecommendedPagefileWithRam();
            await _memoryService.ConfigurePagefileAsync(recommended, recommended);
            _log.Info("DashboardViewModel", $"Page file set to {recommended / 1024} GB (RAM: {ramMb / 1024} GB)");

            // 2. Disable data collection (telemetry services + tasks)
            await _serviceControl.DisableAllTelemetryServicesAsync();
            _log.Info("DashboardViewModel", "Data collection disabled");

            // 3. High Performance power plan
            await _powerPlan.SetHighPerformanceAsync();
            // Persist the toggle so VisualViewModel shows it as ON and re-applies
            // HP at every subsequent startup (hibernate-resume, app restart after
            // update, etc.). Without this line the plan reverts to Balanced on
            // restart because VisualViewModel only reads this setting at init.
            _settings.PerformanceModeEnabled = true;
            _log.Info("DashboardViewModel", "Power plan → High Performance");

            // 4. Balanced on battery (if applicable) — set the persisted setting and apply
            //    immediately if currently on battery. VisualViewModel's PowerModeChanged
            //    handler reads BatteryOptimizationMode from settings on plug/unplug, so
            //    it will auto-switch plans even though Auto-Pilot bypasses VisualViewModel.
            if (_powerPlan.HasBattery())
            {
                _settings.BatteryOptimizationMode = "balanced";
                if (_powerPlan.IsOnBattery())
                    await _powerPlan.SetBalancedOnBatteryAsync(); // switch to Balanced right now
                _log.Info("DashboardViewModel", "Battery optimization enabled (Balanced on battery)");
            }

            // 5. Game Boost master switch on
            _settings.GameBoosterEnabled = true;
            _gameBooster.SetEnabled(true);
            _log.Info("DashboardViewModel", "Game Boost enabled");

            // 6. Cloudflare DNS
            var cloudflare = DnsService.Profiles.FirstOrDefault(p => p.Primary == "1.1.1.1");
            if (cloudflare != null)
            {
                await _dnsService.ApplyProfileAsync(cloudflare);
                _log.Info("DashboardViewModel", "DNS → Cloudflare");
            }

            // 7. Block Windows preview updates
            await _wuTweaks.BlockPreviewUpdatesAsync();
            _log.Info("DashboardViewModel", "Preview updates blocked");

            // 8. CPU core efficiency (forced core parking)
            await _corePark.EnableForcedCoreParking();
            _log.Info("DashboardViewModel", "CPU core efficiency enabled");

            // 9. Launch on startup
            _settings.StartWithWindows = true;
            _log.Info("DashboardViewModel", "Start with Windows enabled");

            // 10. Remove SMBv1 if present (DISM — may take 1-3 minutes)
            if (_optFeatures.IsSMBv1Present())
            {
                StatusMessage = "Removing SMBv1 (insecure legacy protocol)… this may take a few minutes.";
                _log.Info("DashboardViewModel", "SMBv1 present — removing via DISM");
                var smb1Result = await _optFeatures.RemoveSMBv1Async();
                _log.Info("DashboardViewModel", $"SMBv1 removal: {smb1Result.Message}");
            }
            else
            {
                _log.Info("DashboardViewModel", "SMBv1 not present — skipping removal");
            }

            // 11. Disable NTFS last-access timestamps
            if (!_stability.IsNtfsLastAccessDisabled())
            {
                await _stability.DisableNtfsLastAccessAsync();
                _log.Info("DashboardViewModel", "NTFS last-access timestamps disabled");
            }

            _log.Info("DashboardViewModel", "Auto-Pilot completed successfully");
            StatusMessage = "Auto-Pilot complete — your PC is fully optimized.";
        }
        catch (Exception ex)
        {
            _log.Error("DashboardViewModel", "Auto-Pilot failed", ex);
            StatusMessage = $"Auto-Pilot partially applied — one or more steps failed: {ex.Message}";
        }
        finally
        {
            IsAutoPilotRunning = false;
            // Re-check all settings so the checklist and button state update
            await CheckAutoPilotStatusAsync();
        }
    }

    // "Apply settings once" is always available — never gated on IsAutoPilotApplied.
    private bool CanRunAutoPilot() => !IsAutoPilotRunning;

    // The only gate property is IsAutoPilotRunning — notify when it changes.
    partial void OnIsAutoPilotRunningChanged(bool value) =>
        RunAutoPilotCommand.NotifyCanExecuteChanged();
}
