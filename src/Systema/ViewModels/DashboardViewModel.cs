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
using Microsoft.Win32;
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

/// <summary>A single "recommended change" card in the Auto Pilot feed. Built from a pending
/// checklist item; the Label is the join key to the apply meta in DashboardViewModel.</summary>
public class Recommendation
{
    public string Label  { get; init; } = "";
    public string Title  { get; init; } = "";
    public string Why    { get; init; } = "";
    public string Safety { get; init; } = "Safe · Reversible";

    /// <summary>The reasoning WITHOUT the trailing trade-off sentence.</summary>
    public string WhyBody    => Split(Why).Body;
    /// <summary>Just the trade-off / caveat, so the card can show it as its own callout
    /// instead of burying it at the end of a paragraph. Empty when there isn't one.</summary>
    public string Tradeoff   => Split(Why).Trade;
    public bool   HasTradeoff => Tradeoff.Length > 0;

    // The recommendation copy marks its caveat with one of these three openers.
    private static readonly string[] TradeoffMarkers = { "Cons:", "Tradeoff:", "Possible issue:" };

    private static (string Body, string Trade) Split(string why)
    {
        if (string.IsNullOrEmpty(why)) return ("", "");
        foreach (string marker in TradeoffMarkers)
        {
            int i = why.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return (why[..i].TrimEnd(), why[(i + marker.Length)..].Trim());
        }
        return (why, "");
    }
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
    private readonly GraphicsTweaksService      _graphics;
    private readonly ThermalManagementService   _thermal;
    private static readonly LoggerService _log = LoggerService.Instance;

    // ── Header badges ───────────────────────────────────────────────────────
    // Reflects the actual elevation state of the running process rather than a
    // hardcoded label. The app gates launch on elevation (see AdminCheckService),
    // so in practice this is true — but binding it keeps the badge honest.
    public bool   IsAdministrator   => AdminCheckService.IsAdmin();
    public string AdminBadgeText    => IsAdministrator ? "Administrator" : "Standard User";

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

    // System Health — single aggregate indicator across the modules. Optimal when the
    // app is elevated, telemetry is blocked, and Auto-Pilot's recommendations are applied.
    [ObservableProperty] private bool   _systemHealthOptimal = true;
    [ObservableProperty] private string _systemHealthStatus  = "Checking…";

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

    // ── Auto Pilot redesign: status card + one-at-a-time suggestion queue ──────
    /// <summary>Every pending suggestion for this PC. The view shows ONE at a time
    /// (CurrentRecommendation) and the user steps through them with the arrows.</summary>
    public ObservableCollection<Recommendation> Recommendations { get; } = new();
    [ObservableProperty] private bool   _hasRecommendations;

    // Which suggestion the focus card is showing.
    private int _recIndex;
    [ObservableProperty] private Recommendation? _currentRecommendation;
    [ObservableProperty] private string _recPositionText = "";
    [ObservableProperty] private bool   _canGoPrevRec;
    [ObservableProperty] private bool   _canGoNextRec;
    [ObservableProperty] private bool   _autoPilotActive;            // drives the status dot colour
    [ObservableProperty] private string _autoPilotStatusLine = "Checking…";
    [ObservableProperty] private bool   _seeWhatsOnExpanded;

    /// <summary>The recommendations the user dismissed, shown in the Dismissed popup.</summary>
    public ObservableCollection<Recommendation> Dismissed { get; } = new();
    [ObservableProperty] private int  _dismissedCount;
    [ObservableProperty] private bool _hasDismissed;

    // Recommendations the user dismissed (persisted), and the per-item apply meta.
    private readonly HashSet<string> _dismissed = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (string Title, string Why, Func<Task> Apply)> _recMeta = new();
    private const string DismissedRegKey = @"Software\Systema\AutoPilot";

    // Recommendation-ONLY items (Suggestions & nags, Start web search): surfaced in the feed so they
    // can be applied with one click, but deliberately NOT part of the Auto-Pilot checklist / Apply-all.
    private readonly Win11CleanupService _win11 = new();
    private readonly AudioService        _audio = new();
    private readonly NvapiService        _nvapi = new();
    private readonly NvidiaGpuService    _nvidiaGpu = new();
    private readonly IntelGpuService     _intelGpu = new();
    private readonly List<AutoPilotItem> _extraRecs = new();

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
        SystemStabilityService     stability,
        GraphicsTweaksService      graphics,
        ThermalManagementService   thermal)
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
        _graphics       = graphics;
        _thermal        = thermal;

        // Restore persisted mode — no PropertyChanged callback fires on field-init.
        _autoPilotModeEnabled = _settings.AutoPilotModeEnabled;

        _recMeta = BuildRecMeta();
        LoadDismissed();
        RebuildDismissed();

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

        // System Health — coarse aggregate the user can read at a glance.
        bool healthy = IsAdministrator && DataCollectionBlocked && IsAutoPilotApplied;
        SystemHealthOptimal = healthy;
        SystemHealthStatus  = healthy ? "All systems optimal" : "Some items need attention";

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

                // 2. Privacy & background services — merged check: telemetry services
                //    must be disabled AND every Recommended optional service for this PC
                //    (skips Xbox if games are installed, skips BITS, etc.).
                bool gamesInstalled = _gameBooster.GamesInstalled;
                bool telOk        = _serviceControl.AreTelemetryServicesDisabled();
                var  remaining    = _serviceControl.GetRemainingRecommendedServices(gamesInstalled);
                bool recOk        = remaining.Count == 0;
                bool privacyOk    = telOk && recOk;
                if (!privacyOk) pending++;

                // Build a useful detail line that names the offending services rather
                // than the old generic "Some recommended services are still running."
                // The list comes from the SAME helper the toggle uses, so the two
                // views can no longer disagree about whether the cleanup is complete.
                string detail;
                if (privacyOk)
                    detail = "Telemetry blocked, recommended services disabled";
                else if (!telOk && !recOk)
                    detail = $"Telemetry active and {remaining.Count} service(s) running: {Truncate(remaining)}";
                else if (!telOk)
                    detail = "Telemetry services are active";
                else
                    detail = $"Still running: {Truncate(remaining)}";

                items.Add(new AutoPilotItem
                {
                    Label  = "Privacy & background services",
                    IsDone = privacyOk,
                    Detail = detail,
                });

                // 2b. No Telemetry Pro — the maximal telemetry kill: the registry policy layer
                //     (tells Windows not to collect at all) on top of the telemetry services,
                //     error reporting, and scheduled data-collection tasks. Broader than the
                //     service cleanup above, so it stays a distinct item.
                bool noTelProOk = _serviceControl.IsNoTelemetryProEnabled();
                if (!noTelProOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "No Telemetry Pro",
                    IsDone = noTelProOk,
                    Detail = noTelProOk
                        ? "All Windows telemetry off (policies, services, error reporting, tasks)"
                        : "Windows telemetry policies are still active",
                });

                // 3. Power plan — Performance Mode (High Performance) is a DESKTOP-only item.
                //    Laptops get NO power item in Auto Pilot at all: forcing High Performance drains
                //    the battery, and battery optimization is a manual choice on the Performance tab.
                string plan  = _powerPlan.GetActivePlan();
                ActivePlan = plan;

                if (!_powerPlan.HasBattery())
                {
                    // Desktop: High Performance is the target plan.
                    bool isHighPerf = plan.Contains("High Performance", StringComparison.OrdinalIgnoreCase)
                                   || plan.Contains("Ultimate", StringComparison.OrdinalIgnoreCase);
                    if (!isHighPerf) pending++;
                    items.Add(new AutoPilotItem
                    {
                        Label  = "Power plan",
                        IsDone = isHighPerf,
                        Detail = isHighPerf ? "High Performance" : $"Currently: {plan}",
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

                // 12. Foreground priority boost
                bool fgBoostOk = _stability.IsForegroundBoostEnabled();
                if (!fgBoostOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Foreground priority boost",
                    IsDone = fgBoostOk,
                    Detail = fgBoostOk ? "Active window gets boosted CPU priority" : "Off (default) — click Optimize to enable",
                });

                // 13. Launch Boost (newly launched apps get a temporary priority boost)
                bool launchBoostOk = _taskSleepVm.IsEnabled && _taskSleepVm.LaunchBoostEnabled;
                if (!launchBoostOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Launch Boost",
                    IsDone = launchBoostOk,
                    Detail = launchBoostOk ? "Apps get a priority boost when they launch" : "Off (default) — click Optimize to enable",
                });

                // 14. Disable MPO — steadier frame timing on systems with poor MPO
                //     driver integration (fixes flicker / stutter). Restart to apply.
                //     Skipped entirely on NVIDIA, where it breaks VSync (see IsMpoAutoDisableUnsafe).
                //     There it becomes the opposite item: MPO must be ON.
                bool mpoUnsafe = _graphics.IsMpoAutoDisableUnsafe();
                bool mpoOk     = mpoUnsafe ? !_graphics.IsMpoDisabled() : _graphics.IsMpoDisabled();
                if (!mpoOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = mpoUnsafe ? "Keep Multi-Plane Overlay on" : "Disable MPO",
                    IsDone = mpoOk,
                    Detail = mpoUnsafe
                        ? (mpoOk
                            ? "On — NVIDIA needs it for VSync and Independent Flip"
                            : "Off — this breaks VSync on NVIDIA. Click Optimize to turn it back on (restart to apply)")
                        : (mpoOk
                            ? "Multi-Plane Overlay disabled — steadier frame timing"
                            : "Enabled (default) — click Optimize to disable (restart to apply)"),
                });

                // 15. Extend GPU recovery timeout (TdrDelay) — fewer "driver stopped
                //     responding" black-screen GPU resets.
                bool tdrOk = _graphics.IsTdrDelayExtended();
                if (!tdrOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Extend GPU recovery timeout",
                    IsDone = tdrOk,
                    Detail = tdrOk
                        ? "Extended to 10s — fewer black-screen GPU resets"
                        : "Default (~2s) — click Optimize to extend (restart to apply)",
                });

                // 16. Maximum system responsiveness (MMCSS SystemResponsiveness = 0) —
                //     hands the CPU quanta Windows reserves for background work to
                //     multimedia/foreground apps for steadier frame pacing. Restart to apply.
                bool maxRespOk = _stability.IsMaxResponsivenessEnabled();
                if (!maxRespOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Maximum system responsiveness",
                    IsDone = maxRespOk,
                    Detail = maxRespOk
                        ? "SystemResponsiveness = 0 — more CPU for foreground/multimedia"
                        : "Default (20) — click Optimize to maximize (restart to apply)",
                });

                // 17. Stable timer resolution — forces a global 0.5 ms timer, DESKTOPS ONLY. On a
                //     laptop a forced high-resolution timer keeps the CPU out of deep idle states,
                //     hurting battery and thermals, so it's not offered (checklist or feed) there.
                if (!_powerPlan.HasBattery())
                {
                    bool timerOk = _graphics.IsTimerResolutionForced();
                    if (!timerOk) pending++;
                    items.Add(new AutoPilotItem
                    {
                        Label  = "Stable timer resolution",
                        IsDone = timerOk,
                        Detail = timerOk
                            ? "Forced 0.5 ms system timer for steadier frame pacing"
                            : "Not forced — click Optimize to enable (restart to apply)",
                    });
                }

                // 18. Priority graphics scheduling — raise the MMCSS graphics/DWM tasks to high.
                bool gfxSchedOk = _graphics.IsGraphicsSchedulingBoosted();
                if (!gfxSchedOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Priority graphics scheduling",
                    IsDone = gfxSchedOk,
                    Detail = gfxSchedOk
                        ? "Graphics and desktop compositor run at high MMCSS priority"
                        : "Default priority — click Optimize to raise (restart to apply)",
                });

                // 19. Priority audio scheduling — raise the MMCSS Audio task to high.
                bool audioSchedOk = _audio.IsAudioSchedulingBoosted();
                if (!audioSchedOk) pending++;
                items.Add(new AutoPilotItem
                {
                    Label  = "Priority audio scheduling",
                    IsDone = audioSchedOk,
                    Detail = audioSchedOk
                        ? "Audio task runs at high MMCSS priority"
                        : "Default priority — click Optimize to raise (restart to apply)",
                });
            });

            // All registry/powercfg calls are done.
            // RunOnLargeStackAsync continuations run on a ThreadPool thread, so ALL
            // Recommendation-only checks (registry reads, still on the background thread). These never
            // touch `pending` — they're feed suggestions, not Auto-Pilot checklist items.
            var extras = new List<AutoPilotItem>
            {
                new() { Label = "Disable Suggestions & nags",  IsDone = _win11.IsConsumerContentDisabled() },
                new() { Label = "Disable web search in Start", IsDone = _win11.IsWebSearchDisabled() },
                new() { Label = "Turn off Game Bar capture",  IsDone = _graphics.IsGameDvrDisabled() },
                new() { Label = "GPU scheduling & windowed optimizations",
                        IsDone = !_graphics.IsHagsEnabled() && !_graphics.IsWindowedOptimizationsEnabled() },
            };

            // NVIDIA LAPTOPS ONLY: cap FPS to the monitor's refresh rate. On a laptop, every frame
            // rendered above the panel's refresh rate is thrown away before it's ever shown — pure
            // wasted GPU work that costs heat, fan noise, and battery. Desktops don't get this in
            // the feed (they're plugged in), and it's Recommended-only, never in the Apply-all pass.
            if (_powerPlan.HasBattery() && _nvapi.IsAvailable())
            {
                int target = NvapiService.GetRefreshRateFpsTarget();   // refresh Hz snapped to a clean cap
                if (target > 0)
                {
                    // Already "done" once any effective cap at or below the refresh rate is in place —
                    // the wasted above-refresh frames are gone, so there's nothing left to recommend.
                    int cap = _nvapi.GetMaxFrameRate();
                    extras.Add(new() { Label = "Cap FPS to monitor refresh", IsDone = cap > 0 && cap <= target });
                }
            }

            // NVIDIA DESKTOPS ONLY: turn OFF GPU power management (PowerMizer) so the dGPU holds full
            // clocks ("prefer maximum performance"). Desktops have the power and cooling headroom to
            // make that a free win; on a laptop it causes thermal throttling (which is why laptops keep
            // it On), so this is Recommended-only and desktop-only, never in the Apply-all pass.
            if (!_powerPlan.HasBattery())
            {
                var nvAdapters = _nvidiaGpu.DetectNvidiaAdapters();
                if (nvAdapters.Count > 0)
                    extras.Add(new() { Label = "GPU max performance",
                                       IsDone = _nvidiaGpu.IsMaxPerformance(nvAdapters[0].FullPath) });
            }

            // NVIDIA DESKTOPS ONLY: set the driver's Power management mode to Prefer maximum
            // performance. This is the NVIDIA app's own setting (DRS PREFERRED_PSTATE), applied
            // live with no restart, and it is SEPARATE from the PowerMizer registry item above.
            // Desktops have the cooling and the mains power to hold full clocks; on a laptop the
            // same change mostly makes heat and drains the battery, so it stays desktop-only.
            // Recommended-only, deliberately NOT in the Apply-all pass — holding full clocks
            // around the clock is a trade the user should opt into, not something Auto-Pilot does
            // to them.
            if (!_powerPlan.HasBattery() && _nvapi.IsAvailable())
            {
                extras.Add(new() { Label = "NVIDIA power mode: maximum performance",
                                   IsDone = _nvapi.GetPowerMode() == NvapiService.PStateMaxPerf });
            }

            // INTEL iGPU (ALL machines, laptop and desktop): recommend the Max Performance Power
            // Policy so the integrated graphics hold full clocks instead of the driver's power-saving
            // default. Writes ONLY the single documented PowerPolicy flag (=2), which Reset removes —
            // never any of the PSR2/DPST/DRRS/MSI values. Recommended-only, never in the Apply-all pass.
            var intelAdapters = _intelGpu.DetectIntelAdapters();
            if (intelAdapters.Count > 0)
            {
                string ip = intelAdapters[0].FullPath;
                var pp = _intelGpu.ResolveFeature(ip, new[] { IntelGpuService.PowerPolicy });
                extras.Add(new() { Label = "Intel GPU max performance", IsDone = pp.Value == 2 });

                // INTEL iGPU DESKTOPS ONLY: turn off the power-saving features (RC6 render standby,
                // and where the panel has them DPST + Dynamic Refresh Switching) so the iGPU stays
                // fully awake. Fires the SAME setters the Intel tab's switches use (per the user's
                // explicit choice), and every value they write is in ManagedValueNames, so the tab's
                // Reset fully heals it. Desktop-only: DPST/DRRS are laptop panel features, and a
                // desktop has no battery to preserve.
                if (!_powerPlan.HasBattery())
                {
                    bool rc6Off  = _intelGpu.ResolveFeature(ip, new[] { IntelGpuService.RC6 }).Value == 0;
                    bool dpstOff = _intelGpu.ResolveFeature(ip, new[] { IntelGpuService.DpstEnable }).Value == 0;
                    bool drrsOff = _intelGpu.ResolveFeature(ip, new[] { IntelGpuService.DrrsEnabled }).Value == 0;
                    extras.Add(new() { Label = "Intel GPU power saving off", IsDone = rc6Off && dpstOff && drrsOff });
                }
            }

            // DELL LAPTOPS with a BIOS thermal profile ONLY: recommend the "Ultra Performance"
            // thermal mode for the plugged-in (AC) profile. Only surfaces when the Dell BIOS
            // actually exposes the thermal attribute (DetectSupport == Supported) and lists an
            // UltraPerformance mode. Battery profile is left untouched. Recommended-only.
            if (_powerPlan.HasBattery() && _thermal.DetectSupport() == ThermalSupport.Supported)
            {
                bool hasUltra = _thermal.AvailableModes.Any(m => string.Equals(m, "UltraPerformance", StringComparison.OrdinalIgnoreCase));
                if (hasUltra)
                {
                    bool acIsUltra = string.Equals(_settings.ThermalModeAc, "UltraPerformance", StringComparison.OrdinalIgnoreCase);
                    extras.Add(new() { Label = "Dell Ultra Performance on AC", IsDone = acIsUltra });
                }
            }

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

                _extraRecs.Clear();
                _extraRecs.AddRange(extras);

                RebuildRecommendationsFromChecklist();
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

        RebuildRecommendationsFromChecklist();   // mode on hides the feed; off resurfaces it

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

            // 2. Privacy cleanup — telemetry services + tasks AND every "Recommended"
            //    background service in one pass. Replaces the old telemetry-only step.
            //    Auto-Pilot Mode (toggle ON) re-applies this whenever drift is detected;
            //    a one-shot "Apply settings" click runs it once and stops.
            var gamesInstalled = _gameBooster.GamesInstalled;
            await _serviceControl.DisablePrivacyAndRecommendedAsync(gamesInstalled);
            _log.Info("DashboardViewModel", "Privacy cleanup applied (telemetry + recommended services)");

            // 2b. No Telemetry Pro — the registry policy layer + error reporting + scheduled
            //     tasks, on top of the telemetry services the privacy cleanup already handles.
            await _serviceControl.SetNoTelemetryProAsync(true);
            _log.Info("DashboardViewModel", "No Telemetry Pro applied (telemetry policies + tasks + error reporting)");

            // 3. High Performance power plan — DESKTOPS ONLY. On a laptop, forcing High
            //    Performance drains the battery; the Balanced-on-battery step below manages
            //    laptop power instead. Persisting PerformanceModeEnabled lets VisualViewModel
            //    re-apply HP at every subsequent startup (desktop only).
            if (!_powerPlan.HasBattery())
            {
                await _powerPlan.SetHighPerformanceAsync();
                _settings.PerformanceModeEnabled = true;
                _log.Info("DashboardViewModel", "Power plan → High Performance (desktop)");
            }

            // 4. (Battery optimization was removed from Auto-Pilot. It's a laptop-specific power
            //     choice the user makes manually on the Performance tab, not something Auto-Pilot
            //     forces — so laptops get no power step here at all.)

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

            // 12. Foreground priority boost — keeps the active app responsive under load.
            if (!_stability.IsForegroundBoostEnabled())
            {
                await _stability.EnableForegroundBoostAsync();
                _settings.ForegroundBoostEnabled = true;
                _log.Info("DashboardViewModel", "Foreground priority boost enabled");
            }

            // 13. Launch Boost — newly launched apps get a temporary priority boost
            // so they open faster. Requires Task Sleep to be enabled.
            if (!_taskSleepVm.IsEnabled || !_taskSleepVm.LaunchBoostEnabled)
            {
                _taskSleepVm.EnableLaunchBoost();
                _log.Info("DashboardViewModel", "Launch Boost enabled");
            }

            // 14. Disable MPO — steadier frame timing where the GPU driver integrates
            // Multi-Plane Overlay poorly. Takes effect on the next restart.
            //
            // NOT ON NVIDIA. NVIDIA's VSync and Independent Flip are built on MPO; turning it off
            // there causes tearing that no in-game VSync toggle can fix. Auto-Pilot used to apply
            // this on every machine, which is exactly how it broke VSync. Auto-Pilot owns this
            // value, so on NVIDIA it now restores MPO rather than merely skipping it — otherwise
            // the machines it already broke would stay broken.
            if (_graphics.IsMpoAutoDisableUnsafe())
            {
                if (_graphics.IsMpoDisabled())
                {
                    _graphics.SetMpoDisabled(false);
                    _settings.GraphicsMpoDisabled = false;
                    _log.Info("DashboardViewModel",
                        "MPO restored (Auto-Pilot) — NVIDIA GPU present, disabling it breaks VSync and Independent Flip");
                }
            }
            else if (!_graphics.IsMpoDisabled())
            {
                _graphics.SetMpoDisabled(true);
                _log.Info("DashboardViewModel", "MPO disabled (Auto-Pilot)");
            }

            // 15. Extend GPU recovery timeout — gives the GPU 10s to recover from a
            // brief stall instead of a hard reset, cutting black-screen flashes.
            if (!_graphics.IsTdrDelayExtended())
            {
                _graphics.SetTdrDelayExtended(true);
                _log.Info("DashboardViewModel", "GPU recovery timeout extended (Auto-Pilot)");
            }

            // 16. Maximum system responsiveness — set MMCSS SystemResponsiveness to 0.
            // Persist the opt-in so GameBooster's VSync self-heal keeps the 0 instead of
            // reverting it to 20. Takes effect on the next restart.
            if (!_stability.IsMaxResponsivenessEnabled())
            {
                await _stability.EnableMaxResponsivenessAsync();
                _settings.MaxResponsivenessEnabled = true;
                _log.Info("DashboardViewModel", "Maximum system responsiveness enabled (Auto-Pilot)");
            }

            // 17. Stable timer resolution — DESKTOPS ONLY (a forced high-res timer keeps a laptop CPU
            //     out of deep idle, hurting battery/thermals). Restart to take effect.
            if (!_powerPlan.HasBattery() && !_graphics.IsTimerResolutionForced())
            {
                _graphics.SetTimerResolution(true);
                _log.Info("DashboardViewModel", "Stable timer resolution enabled (Auto-Pilot, desktop)");
            }

            // 18. Priority graphics scheduling — MMCSS graphics/DWM tasks to high. Restart to apply.
            if (!_graphics.IsGraphicsSchedulingBoosted())
            {
                _graphics.SetGraphicsSchedulingBoosted(true);
                _log.Info("DashboardViewModel", "Priority graphics scheduling enabled (Auto-Pilot)");
            }

            // 19. Priority audio scheduling — MMCSS Audio task to high. Restart to apply.
            if (!_audio.IsAudioSchedulingBoosted())
            {
                _audio.SetAudioSchedulingBoosted(true);
                _log.Info("DashboardViewModel", "Priority audio scheduling enabled (Auto-Pilot)");
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
            // Notify ViewModels that depend on post-run state (e.g. ServicesViewModel
            // refreshing the merged Privacy & Background Services toggle reflection).
            SettingsService.RaiseOptimizationsApplied();
        }
    }

    /// <summary>
    /// Renders the "still running" list for the Privacy & background services
    /// checklist detail. Shows up to 3 names so the line stays scannable; anything
    /// beyond gets a "+N more" suffix.
    /// </summary>
    private static string Truncate(List<string> names)
    {
        if (names.Count <= 3) return string.Join(", ", names);
        return $"{string.Join(", ", names.Take(3))} +{names.Count - 3} more";
    }

    // "Apply settings once" is always available — never gated on IsAutoPilotApplied.
    private bool CanRunAutoPilot() => !IsAutoPilotRunning;

    // The only gate property is IsAutoPilotRunning — notify when it changes.
    partial void OnIsAutoPilotRunningChanged(bool value) =>
        RunAutoPilotCommand.NotifyCanExecuteChanged();

    // ── Recommended feed (two-zone Auto Pilot) ────────────────────────────────

    /// <summary>The "why" + per-item apply action for each optimization, keyed by the SAME
    /// Label the checklist uses. Each Apply mirrors the matching step in RunAutoPilotAsync, so
    /// applying one recommendation does exactly what the full pass would do for that item.</summary>
    private Dictionary<string, (string Title, string Why, Func<Task> Apply)> BuildRecMeta() => new()
    {
        ["Page file"] = ("Set an optimized page file",
            "A fixed page file sized to your RAM stops Windows resizing it on the fly, which avoids stutters when memory fills up.",
            async () => { var (rec, _) = _memoryService.GetRecommendedPagefileWithRam(); await _memoryService.ConfigurePagefileAsync(rec, rec); }),
        ["Privacy & background services"] = ("Turn off data collection and bloat services",
            "Disables the telemetry and background services that quietly collect data and use resources, with nothing you would miss.",
            async () => await _serviceControl.DisablePrivacyAndRecommendedAsync(_gameBooster.GamesInstalled)),
        ["No Telemetry Pro"] = ("Turn off all Windows telemetry",
            "Goes beyond the service cleanup and switches off Windows' telemetry policies, error reporting, and scheduled data-collection tasks, so the OS stops gathering and sending usage data.",
            async () => await _serviceControl.SetNoTelemetryProAsync(true)),
        ["Power plan"] = ("Switch to the High Performance power plan",
            "The High Performance plan stops Windows down-clocking the CPU on light load, so your PC responds the instant you ask it to.",
            async () => { await _powerPlan.SetHighPerformanceAsync(); _settings.PerformanceModeEnabled = true; }),
        ["Game Boost"] = ("Turn on Game Boost",
            "Game Boost frees up CPU and quiets background apps while you play, for steadier frame rates.",
            () => { _settings.GameBoosterEnabled = true; _gameBooster.SetEnabled(true); return Task.CompletedTask; }),
        ["DNS"] = ("Use Cloudflare DNS (1.1.1.1)",
            "Cloudflare's 1.1.1.1 resolver is usually faster and more private than your default DNS, so pages and games connect quicker.",
            async () => { var cf = DnsService.Profiles.FirstOrDefault(p => p.Primary == "1.1.1.1"); if (cf != null) await _dnsService.ApplyProfileAsync(cf); }),
        ["Preview updates"] = ("Block Windows preview updates",
            "Keeps you on stable Windows releases instead of the buggy preview and insider builds.",
            async () => await _wuTweaks.BlockPreviewUpdatesAsync()),
        ["CPU core efficiency"] = ("Enable CPU core parking",
            "Lets Windows park idle CPU cores across all your power plans, so the processor draws less power and runs cooler and quieter when the machine isn't under load. Systema keeps it applied across restarts.",
            async () => await _corePark.EnableForcedCoreParking()),
        ["Launch on startup"] = ("Start Systema with Windows",
            "Lets Systema start with Windows so your optimizations stay applied and maintained from the moment you log in.",
            () => { _settings.StartWithWindows = true; return Task.CompletedTask; }),
        ["SMBv1 removed"] = ("Remove the insecure SMBv1 protocol",
            "SMBv1 is an old, insecure file-sharing protocol and a known security risk. Removing it closes that hole with no downside on a modern PC.",
            async () => { if (_optFeatures.IsSMBv1Present()) await _optFeatures.RemoveSMBv1Async(); }),
        ["NTFS last-access timestamps"] = ("Stop NTFS last-access writes",
            "Stops Windows writing a timestamp every time a file is read, which cuts needless disk writes and wear.",
            async () => { if (!_stability.IsNtfsLastAccessDisabled()) await _stability.DisableNtfsLastAccessAsync(); }),
        ["Foreground priority boost"] = ("Boost the app you're using",
            "Gives the active window a bigger share of CPU, so it stays responsive even when something heavy runs in the background.",
            async () => { if (!_stability.IsForegroundBoostEnabled()) { await _stability.EnableForegroundBoostAsync(); _settings.ForegroundBoostEnabled = true; } }),
        ["Launch Boost"] = ("Speed up app launches",
            "Gives apps a quick priority boost the moment they launch so they open faster, then hands control back to Windows.",
            () => { if (!_taskSleepVm.IsEnabled || !_taskSleepVm.LaunchBoostEnabled) _taskSleepVm.EnableLaunchBoost(); return Task.CompletedTask; }),
        // NVIDIA counterpart of "Disable MPO" — same registry value, opposite direction.
        ["Keep Multi-Plane Overlay on"] = ("Turn Multi-Plane Overlay back on",
            "Your NVIDIA card uses Multi-Plane Overlay to hand games a direct path to the screen. With it turned off, Windows composites the frames instead, your game's own VSync setting stops having any effect, and you get tearing. Turning it back on restores normal VSync. An older version of Systema turned this off on every PC, including NVIDIA ones, which was a mistake. Takes effect after a restart.",
            () => { if (_graphics.IsMpoDisabled()) { _graphics.SetMpoDisabled(false); _settings.GraphicsMpoDisabled = false; } return Task.CompletedTask; }),
        ["Disable MPO"] = ("Disable Multi-Plane Overlay",
            "Some GPU drivers handle Multi-Plane Overlay poorly, which causes flicker, stutter, and uneven frame pacing, so turning it off is Microsoft's own fix and usually steadies frames. Takes effect after a restart.",
            () => { if (!_graphics.IsMpoDisabled()) _graphics.SetMpoDisabled(true); return Task.CompletedTask; }),
        ["Extend GPU recovery timeout"] = ("Extend the GPU recovery timeout",
            "Gives the GPU a moment longer to recover from a hang before Windows resets the driver, which avoids black screens under heavy load.",
            () => { if (!_graphics.IsTdrDelayExtended()) _graphics.SetTdrDelayExtended(true); return Task.CompletedTask; }),
        ["Maximum system responsiveness"] = ("Maximize system responsiveness",
            "Hands the CPU time Windows reserves for background work over to your foreground and multimedia apps, for steadier frame pacing.",
            async () => { if (!_stability.IsMaxResponsivenessEnabled()) { await _stability.EnableMaxResponsivenessAsync(); _settings.MaxResponsivenessEnabled = true; } }),
        ["Stable timer resolution"] = ("Force a stable 0.5 ms system timer",
            "Pins Windows to a steady high-resolution timer so frame pacing and input timing stay consistent instead of drifting. Best on a desktop, and it takes effect after a restart.",
            () => { if (!_graphics.IsTimerResolutionForced()) _graphics.SetTimerResolution(true); return Task.CompletedTask; }),
        ["Priority graphics scheduling"] = ("Raise graphics scheduling priority",
            "Bumps the Windows multimedia scheduler priority for graphics and the desktop compositor, so rendering gets CPU time sooner for steadier frames. Takes effect after a restart.",
            () => { if (!_graphics.IsGraphicsSchedulingBoosted()) _graphics.SetGraphicsSchedulingBoosted(true); return Task.CompletedTask; }),
        ["Priority audio scheduling"] = ("Raise audio scheduling priority",
            "Bumps the Windows audio task's scheduler priority so sound gets CPU time promptly, cutting crackles and dropouts when the system is busy. Takes effect after a restart.",
            () => { if (!_audio.IsAudioSchedulingBoosted()) _audio.SetAudioSchedulingBoosted(true); return Task.CompletedTask; }),

        // Recommendation-only (not in Auto-Pilot) — see _extraRecs.
        ["Disable Suggestions & nags"] = ("Turn off Windows suggestions and nags",
            "Stops Windows 11's tips, app suggestions, lock-screen spotlight ads, and the setup and finish-setup nags, for a quieter, less cluttered desktop.",
            async () => await _win11.DisableConsumerContentAsync()),
        ["Disable web search in Start"] = ("Turn off web results in Start search",
            "Removes Bing web results from Start menu search so it only searches your PC, which makes Start search quicker and more private.",
            async () => await _win11.DisableWebSearchAsync()),
        ["Turn off Game Bar capture"] = ("Turn off Game Bar background capture",
            "Stops Windows' Game Bar and Game DVR from recording in the background, removing the constant CPU and disk overhead it adds while you game. Restart any open games to apply.",
            () => { if (!_graphics.IsGameDvrDisabled()) _graphics.SetGameDvrDisabled(true); return Task.CompletedTask; }),
        ["Dell Ultra Performance on AC"] = ("Set the Dell thermal profile to Ultra Performance (plugged in)",
            "Dell laptops hold back their fans and clocks by default to stay quiet and cool. Ultra Performance lets the machine run the fans harder and hold higher clocks while it's plugged in, for noticeably more sustained CPU and GPU performance. This changes ONLY the plugged-in profile, so your on-battery runtime and behavior are untouched. Cons: plugged in it runs warmer and the fans get louder under load. It applies as soon as you're plugged in, and you can change it any time on the Dell tab.",
            async () => { string ultra = _thermal.AvailableModes.FirstOrDefault(m => string.Equals(m, "UltraPerformance", StringComparison.OrdinalIgnoreCase)) ?? "UltraPerformance";
                          _settings.ThermalModeAc = ultra;                                   // persist the plugged-in preference
                          if (!_powerPlan.IsOnBattery()) await Task.Run(() => _thermal.SetMode(ultra)); }),   // apply now only if actually on AC
        ["Intel GPU power saving off"] = ("Turn off Intel graphics power saving",
            "Turns off the Intel integrated graphics power-saving features (RC6 render standby, plus DPST display power saving and Dynamic Refresh Switching where the panel has them) so the chip stays fully awake for the most consistent performance. This is meant for desktops, where there's no battery to preserve. Cons: it uses a little more power at idle and runs a touch warmer. Takes effect after a restart, and you can undo it any time with Reset on the Intel Graphics tab.",
            () => { var a = _intelGpu.DetectIntelAdapters();
                    if (a.Count > 0) { _intelGpu.SetRc6(a, on: false); _intelGpu.SetDpst(a, on: false); _intelGpu.SetDrrs(a, on: false); }
                    return Task.CompletedTask; }),
        ["Intel GPU max performance"] = ("Set the Intel graphics to maximum performance",
            "By default the Intel graphics chip favors power saving and lets its clocks drop, which can make the desktop and light games feel less smooth. Setting the Power Policy to Max Performance keeps the graphics running at full speed for a snappier, more consistent feel. Cons: on a laptop running on battery it uses a bit more power, so if battery life matters more to you than smoothness, leave it on the driver default. Takes effect after a restart.",
            () => { var a = _intelGpu.DetectIntelAdapters();
                    if (a.Count > 0) { var pp = _intelGpu.ResolveFeature(a[0].FullPath, new[] { IntelGpuService.PowerPolicy });
                                       _intelGpu.WriteValue(a, pp.Name ?? IntelGpuService.PowerPolicy, 2); }
                    return Task.CompletedTask; }),
        ["NVIDIA power mode: maximum performance"] = ("Set the NVIDIA power mode to maximum performance",
            "This is the Power management mode setting from the NVIDIA app, and it's separate from the PowerMizer one above. Prefer maximum performance keeps the graphics card at its full clock speeds instead of dropping them whenever it thinks it can, which removes the brief moment where it has to spin back up and makes frame timing steadier. Cons: it uses more power sitting idle and runs warmer, so it's only suggested on desktops where you have the cooling for it and aren't running off a battery. It applies straight away with no restart, and you can change it any time on the Nvidia Graphics tab.",
            () => { _nvapi.SetPowerMode(NvapiService.PStateMaxPerf); return Task.CompletedTask; }),
        ["GPU max performance"] = ("Set the NVIDIA GPU to maximum performance",
            "By default the NVIDIA GPU idles its clocks down to save power (PowerMizer). On a desktop you have the power and cooling headroom to skip that, so this holds the GPU at full clocks for the best and most consistent performance. Cons: it draws a little more power at idle, and it's not recommended on laptops (there it can cause thermal throttling), which is why this only shows on desktops. Takes effect after a restart.",
            () => { var a = _nvidiaGpu.DetectNvidiaAdapters(); if (a.Count > 0) { _nvidiaGpu.SetPowerSaving(a, on: false); _settings.NvidiaGpuPreferMaxPerformance = true; } return Task.CompletedTask; }),
        ["Cap FPS to monitor refresh"] = ("Cap FPS to your monitor's refresh rate",
            "On a laptop, any frame your GPU renders above your screen's refresh rate is thrown away before you ever see it, so it's wasted work. Capping frames at your refresh rate (with NVIDIA's own limiter, the same one the NVIDIA app uses) cuts GPU load, heat, fan noise, and battery drain, and often makes frame pacing feel steadier. Cons: it adds a very tiny bit of input lag versus running fully uncapped, and it won't help games that already run below your refresh rate. You can change or remove the cap any time on the Nvidia Graphics tab.",
            async () => { int t = NvapiService.GetRefreshRateFpsTarget(); if (t > 0) await Task.Run(() => _nvapi.SetMaxFrameRate(t)); }),
        ["GPU scheduling & windowed optimizations"] = ("Turn off GPU scheduling and windowed game optimizations",
            "Turns off Hardware-accelerated GPU Scheduling and Optimizations for windowed games. How it helps: both add an extra layer to how frames are scheduled and presented, so turning them off keeps the graphics path simpler with fewer moving parts to glitch, which is more stable on many setups. Possible issue: on some capable GPUs these features can actually lower latency and smooth frame delivery, so if your games felt better with them on, you can re-enable them in the Graphics tab. GPU scheduling needs a PC restart, and open games need restarting.",
            () => {
                if (_graphics.IsHagsEnabled()) { _graphics.SetHags(false); _settings.GraphicsHagsPref = 0; }
                if (_graphics.IsWindowedOptimizationsEnabled()) { _graphics.SetWindowedOptimizations(false); _settings.GraphicsWindowedOptPref = 0; }
                return Task.CompletedTask;
            }),
    };

    /// <summary>Rebuilds the status line + the visible recommendation feed from the current
    /// checklist (already computed by the background pass). UI thread only. When Auto Pilot Mode
    /// is on the feed is empty (the engine manages everything, so the "all set" state shows).</summary>
    private void RebuildRecommendationsFromChecklist()
    {
        int applied = AutoPilotChecklist.Count(i => i.IsDone);
        int total   = AutoPilotChecklist.Count;
        AutoPilotActive     = AutoPilotModeEnabled || applied > 0;
        AutoPilotStatusLine = AutoPilotModeEnabled
            ? $"On · {applied} optimization{(applied == 1 ? "" : "s")} active · re-checked automatically"
            : (total > 0 ? $"{applied} of {total} optimizations applied" : "Checking…");

        Recommendations.Clear();
        if (!AutoPilotModeEnabled)
        {
            // Auto-Pilot checklist items first, then the recommendation-only extras (Suggestions &
            // nags, Start web search). The view shows one at a time, so there is no cap — the whole
            // pending set is queued up and the user steps through it with the arrows.
            foreach (var item in AutoPilotChecklist.Concat(_extraRecs))
            {
                if (item.IsDone || _dismissed.Contains(item.Label)) continue;
                if (!_recMeta.TryGetValue(item.Label, out var meta)) continue;
                Recommendations.Add(new Recommendation { Label = item.Label, Title = meta.Title, Why = meta.Why });
            }
        }
        HasRecommendations = Recommendations.Count > 0;
        SyncCurrentRec();
    }

    /// <summary>Points the focus card at the current queue position, clamping the index after the
    /// list changes (apply/dismiss/re-check shrink it) so the card always lands on a real item.</summary>
    private void SyncCurrentRec()
    {
        int count = Recommendations.Count;
        if (count == 0)
        {
            _recIndex = 0;
            CurrentRecommendation = null;
            RecPositionText = "";
            CanGoPrevRec = false;
            CanGoNextRec = false;
            return;
        }
        _recIndex = Math.Clamp(_recIndex, 0, count - 1);
        CurrentRecommendation = Recommendations[_recIndex];
        RecPositionText = $"{_recIndex + 1} of {count}";
        CanGoPrevRec = _recIndex > 0;
        CanGoNextRec = _recIndex < count - 1;
    }

    /// <summary>Step back one suggestion (no change is applied).</summary>
    [RelayCommand]
    private void PrevRec()
    {
        if (_recIndex <= 0) return;
        _recIndex--;
        SyncCurrentRec();
    }

    /// <summary>Step forward one suggestion (no change is applied).</summary>
    [RelayCommand]
    private void NextRec()
    {
        if (_recIndex >= Recommendations.Count - 1) return;
        _recIndex++;
        SyncCurrentRec();
    }

    /// <summary>Applies a single recommendation, then re-checks so the next one surfaces.</summary>
    [RelayCommand]
    private async Task ApplyRecommendation(Recommendation? rec)
    {
        if (rec == null || !_recMeta.TryGetValue(rec.Label, out var meta)) return;
        Recommendations.Remove(rec);                       // snappy: drop it right away
        HasRecommendations = Recommendations.Count > 0;
        SyncCurrentRec();                                  // advance the card before the slow apply
        try
        {
            await meta.Apply();
            StatusMessage = $"Applied: {rec.Title}";
            _log.Info("DashboardViewModel", $"Recommendation applied: {rec.Label}");
        }
        catch (Exception ex) { _log.Warn("DashboardViewModel", $"ApplyRecommendation '{rec.Label}' failed: {ex.Message}"); }
        await CheckAutoPilotStatusAsync();                 // refresh state + surface the next item
    }

    /// <summary>Dismisses a recommendation (persisted) and surfaces the next one.</summary>
    [RelayCommand]
    private void DismissRecommendation(Recommendation? rec)
    {
        if (rec == null) return;
        _dismissed.Add(rec.Label);
        SaveDismissed();
        RebuildDismissed();
        RebuildRecommendationsFromChecklist();
    }

    /// <summary>Un-dismisses a recommendation so it can appear in the feed again.</summary>
    [RelayCommand]
    private void RestoreRecommendation(Recommendation? rec)
    {
        if (rec == null) return;
        _dismissed.Remove(rec.Label);
        SaveDismissed();
        RebuildDismissed();
        RebuildRecommendationsFromChecklist();
    }

    /// <summary>Rebuilds the dismissed list + its count from the persisted dismissed keys.</summary>
    private void RebuildDismissed()
    {
        Dismissed.Clear();
        foreach (var label in _dismissed)
            if (_recMeta.TryGetValue(label, out var meta))
                Dismissed.Add(new Recommendation { Label = label, Title = meta.Title, Why = meta.Why });
        DismissedCount = Dismissed.Count;
        HasDismissed   = Dismissed.Count > 0;
    }

    [RelayCommand]
    private void ToggleSeeWhatsOn() => SeeWhatsOnExpanded = !SeeWhatsOnExpanded;

    private void LoadDismissed()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(DismissedRegKey);
            if (k?.GetValue("Dismissed") is string s)
                foreach (var part in s.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    _dismissed.Add(part);
        }
        catch (Exception ex) { _log.Warn("DashboardViewModel", $"LoadDismissed failed: {ex.Message}"); }
    }

    private void SaveDismissed()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(DismissedRegKey, writable: true);
            k?.SetValue("Dismissed", string.Join("\n", _dismissed), RegistryValueKind.String);
        }
        catch (Exception ex) { _log.Warn("DashboardViewModel", $"SaveDismissed failed: {ex.Message}"); }
    }
}
