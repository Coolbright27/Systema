// ════════════════════════════════════════════════════════════════════════════
// TaskSleepSettings.cs  ·  All configuration for TaskSleepService
// ════════════════════════════════════════════════════════════════════════════
//
// Plain data class holding every tuneable parameter for the Task Sleep monitor.
// Default values defined here are the live defaults shown in the UI. Serialised
// to/from registry by TaskSleepViewModel via BuildSettings/LoadSettings/SaveSettings.
//
// QUICK EDIT GUIDE
//   Adding a new setting → add property here + [ObservableProperty] in
//                          TaskSleepViewModel.cs + wire into BuildSettings /
//                          LoadSettings / SaveSettings there
//
// RELATED FILES
//   TaskSleepService.cs     — consumes this settings object each tick
//   TaskSleepViewModel.cs   — creates and persists instances of this class
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

public class TaskSleepSettings
{
    // ── Automatic Controls ────────────────────────────────────────────────────
    public bool IsEnabled               { get; set; } = true;
    public bool LowerCpuPriority        { get; set; } = true;
    public bool IgnoreForeground        { get; set; } = true;
    public bool ActOnForegroundChildren { get; set; } = false;
    public bool ExcludeSystemServices   { get; set; } = true;
    public bool EnableEfficiencyMode    { get; set; } = true;

    // ── CPU Thresholds ────────────────────────────────────────────────────────
    public int SystemCpuTriggerPercent { get; set; } = 12;   // activate only when total CPU > this
    public int ProcessCpuStartPercent  { get; set; } = 7;    // throttle process when it exceeds this
    public int ProcessCpuStopPercent   { get; set; } = 3;    // unthrottle when it drops below this
    public int TimeOverQuotaMs         { get; set; } = 1500;  // must be over threshold for this long before throttling
    public int MinAdjustmentDurationMs { get; set; } = 5000;  // keep throttled for at least this long
    public int MaxAdjustmentDurationMs { get; set; } = 30000; // force-restore after this long (fallback when PersistentNap=off)

    // ── GPU, I/O & Core Affinity ──────────────────────────────────────────────
    public bool LowerGpuPriority { get; set; } = true;
    public bool LowerIoPriority  { get; set; } = true;
    public bool DetectECores     { get; set; } = true;
    public bool MoveToECores     { get; set; } = true;

    // ── Per-App Rules ─────────────────────────────────────────────────────────
    public List<TaskSleepAppRule> AppRules { get; set; } = new();

    // ── Persistent Nap (App Nap style) ────────────────────────────────────────
    /// <summary>
    /// When true, napped processes stay napped until the user opens them (foreground).
    /// Time-based restore is skipped entirely — the app sleeps until used.
    /// </summary>
    public bool PersistentNapEnabled { get; set; } = true;

    // ── Advanced ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Lower memory priority on throttled processes so the OS ages out their pages first,
    /// reclaiming physical RAM for the foreground application.
    /// </summary>
    public bool LowerMemoryPriority { get; set; } = true;

    /// <summary>
    /// Immediately trim the working set of a newly throttled process, actively returning
    /// its physical RAM pages to the OS. Most impactful on memory-heavy background apps.
    /// </summary>
    public bool TrimWorkingSet { get; set; } = true;

    /// <summary>
    /// Slow the monitor tick to 2 500 ms when the system is idle and nothing is throttled,
    /// reducing the monitor's own scheduling and CPU overhead.
    /// </summary>
    public bool AdaptiveTick { get; set; } = true;

    // ── Minimize Nap ─────────────────────────────────────────────────────────
    /// <summary>
    /// When true, apps are automatically throttled when minimized, unless they are
    /// actively playing audio, on a call, or screen-recording.
    /// </summary>
    public bool MinimizeNapEnabled { get; set; } = true;

    /// <summary>
    /// How often (ms) a minimized-napped app is allowed a brief wake when the system
    /// CPU is low enough (below SystemCpuTriggerPercent / 2). Default: 60 s.
    /// </summary>
    public int MinimizedBriefWakeIntervalMs { get; set; } = 60_000;

    /// <summary>
    /// How long (ms) the brief wake window lasts before the process is re-throttled.
    /// Default: 10 s.
    /// </summary>
    public int MinimizedBriefWakeDurationMs { get; set; } = 10_000;

    /// <summary>
    /// How long (ms) a minimized app must stay napped before switching to deep sleep
    /// (longer wake interval). Default: 600 000 ms (10 minutes).
    /// </summary>
    public int MinimizeDeepSleepThresholdMs { get; set; } = 600_000;

    /// <summary>
    /// Wake interval (ms) used once a minimized app enters deep sleep mode
    /// (has been napped longer than MinimizeDeepSleepThresholdMs).
    /// Default: 300 000 ms (5 minutes).
    /// </summary>
    public int MinimizeDeepSleepWakeIntervalMs { get; set; } = 300_000;

    // ── Tray Nap ──────────────────────────────────────────────────────────────
    /// <summary>
    /// When true, processes with no visible windows (living only in the system tray)
    /// are automatically throttled, with very rare brief wakes (default every 5 minutes).
    /// </summary>
    public bool TrayNapEnabled { get; set; } = true;

    /// <summary>
    /// How often (ms) a tray-napped process is allowed a brief wake when system CPU is low.
    /// Default: 300 000 ms (5 minutes) — much rarer than minimize-nap wakes.
    /// </summary>
    public int TrayBriefWakeIntervalMs { get; set; } = 300_000;

    /// <summary>
    /// How long (ms) the brief wake window lasts for tray-napped processes. Default: 10 s.
    /// </summary>
    public int TrayBriefWakeDurationMs { get; set; } = 10_000;

    // ── Monitoring ────────────────────────────────────────────────────────────
    /// <summary>
    /// Re-apply throttle settings every tick, even if a process raised its own priority back.
    /// Defaults to true so throttled apps cannot escape their nap by self-elevating priority.
    /// </summary>
    public bool EnforceSettings { get; set; } = true;

    // ── Soft Nap Mode ─────────────────────────────────────────────────────────
    /// <summary>
    /// When true, CPU throttle is reduced to Below Normal (instead of Idle) and
    /// I/O priority to Low (instead of Very Low). Keeps napped apps more responsive
    /// at the cost of slightly less CPU headroom for the foreground. Off by default.
    /// Does not affect minimize-nap or tray-nap — those always use full throttle.
    /// </summary>
    public bool SoftNapEnabled { get; set; } = false;

    // ── Brief Wake Concurrency ────────────────────────────────────────────────
    /// <summary>
    /// Maximum number of napped processes allowed to be in a brief-wake window
    /// simultaneously. Caps CPU spikes from many processes waking at once.
    /// Valid range: 1–10. Default: 3.
    /// </summary>
    public int MaxConcurrentBriefWakes { get; set; } = 3;

    // ── Game mode integration ─────────────────────────────────────────────────
    /// <summary>
    /// When true (set by GameBoosterService via TaskSleepViewModel.SetGameMode),
    /// suppresses brief idle wakes for minimized and tray-napped processes so the
    /// CPU stays fully available to the game.
    /// </summary>
    public bool IsGameModeActive { get; set; } = false;
}
