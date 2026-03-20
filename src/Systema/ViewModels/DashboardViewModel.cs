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
//
// The button is disabled (greyed) once all 9 items are already applied.
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
        SettingsService            settings)
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
        catch { DataCollectionStatus = "Unknown"; }

        // RAM ────────────────────────────────────────────────────────
        try
        {
            var (total, avail) = _memoryService.GetRamStats();
            long used   = total - avail;
            RamUsageText = $"{used / 1024.0:F1} / {total / 1024.0:F1} GB";
        }
        catch { RamUsageText = "—"; }

        StatusMessage = $"Systema is running · {DateTime.Now:HH:mm}";

        // Re-check Auto-Pilot status every 30 s so changes made in other tabs are
        // reflected as soon as the user navigates back to the Dashboard.
        if (!IsAutoPilotRunning &&
            (DateTime.Now - _lastAutoPilotCheck).TotalSeconds >= 30 &&
            Interlocked.CompareExchange(ref _autoPilotCheckInFlight, 1, 0) == 0)
        {
            _lastAutoPilotCheck = DateTime.Now;
            _ = CheckAutoPilotStatusAsync().ContinueWith(_ =>
                Interlocked.Exchange(ref _autoPilotCheckInFlight, 0));
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
                int  recommended = _memoryService.GetRecommendedPagefileMb();
                bool pgOk = !isManaged && initMb >= recommended - 512;
                if (!pgOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Page file",
                    IsDone = pgOk,
                    Detail = pgOk
                        ? $"Optimized ({initMb / 1024} GB)"
                        : $"Recommended: {recommended / 1024} GB",
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
                bool   planOk = plan.Contains("High Performance", StringComparison.OrdinalIgnoreCase)
                             || plan.Contains("Ultimate", StringComparison.OrdinalIgnoreCase);
                if (!planOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Power plan",
                    IsDone = planOk,
                    Detail = planOk ? "High Performance" : $"Currently: {plan}",
                });
                ActivePlan = plan;

                // 4. Balanced on battery (only if battery present)
                if (_powerPlan.HasBattery())
                {
                    bool battOk = !string.IsNullOrEmpty(_settings.BatteryOptimizationMode);
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
            });

            // All registry/powercfg calls are done — now update the UI thread properties
            AutoPilotPendingCount = pending;
            IsAutoPilotApplied    = pending == 0;
            AutoPilotButtonText   = IsAutoPilotApplied
                ? "✓  All Optimized"
                : $"Optimize Now  ({pending} item{(pending == 1 ? "" : "s")})";

            AutoPilotChecklist.Clear();
            foreach (var item in items)
                AutoPilotChecklist.Add(item);

            RunAutoPilotCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _log.Warn("DashboardViewModel", $"CheckAutoPilotStatus: {ex.Message}");
        }
    }

    // ── Auto-Pilot run ────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanRunAutoPilot))]
    private async Task RunAutoPilotAsync()
    {
        IsAutoPilotRunning  = true;
        AutoPilotButtonText = "Optimizing…";
        RunAutoPilotCommand.NotifyCanExecuteChanged();

        try
        {
            _log.Info("DashboardViewModel", "Auto-Pilot started");

            // 1. Page file — set to recommended size
            int recommended = _memoryService.GetRecommendedPagefileMb();
            await _memoryService.ConfigurePagefileAsync(recommended, recommended);
            _log.Info("DashboardViewModel", $"Page file set to {recommended / 1024} GB");

            // 2. Disable data collection (telemetry services + tasks)
            await _serviceControl.DisableAllTelemetryServicesAsync();
            _log.Info("DashboardViewModel", "Data collection disabled");

            // 3. High Performance power plan
            await _powerPlan.SetHighPerformanceAsync();
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

    private bool CanRunAutoPilot() => !IsAutoPilotRunning && !IsAutoPilotApplied;

    // Notify the command when the gate properties change
    partial void OnIsAutoPilotRunningChanged(bool value) =>
        RunAutoPilotCommand.NotifyCanExecuteChanged();

    partial void OnIsAutoPilotAppliedChanged(bool value) =>
        RunAutoPilotCommand.NotifyCanExecuteChanged();
}
