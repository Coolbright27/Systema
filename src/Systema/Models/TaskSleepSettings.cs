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

    /// <summary>
    /// When true, background processes that exceed the CPU threshold are throttled.
    /// Off by default — minimize-nap and tray-nap handle most cases without this.
    /// </summary>
    public bool CpuTriggeredNapEnabled { get; set; } = true;

    /// <summary>
    /// When true, child processes of a napped app are also napped automatically.
    /// Off by default.
    /// </summary>
    public bool NapChildrenEnabled { get; set; } = false;

    // ── CPU Thresholds ────────────────────────────────────────────────────────
    public int SystemCpuTriggerPercent { get; set; } = 12;   // activate only when total CPU > this
    public int ProcessCpuStartPercent  { get; set; } = 7;    // throttle process when it exceeds this
    public int ProcessCpuStopPercent   { get; set; } = 3;    // unthrottle when it drops below this
    public int TimeOverQuotaMs         { get; set; } = 1500;  // must be over threshold for this long before throttling
    public int MinAdjustmentDurationMs { get; set; } = 5000;  // keep throttled for at least this long
    public int MaxAdjustmentDurationMs { get; set; } = 30000; // force-restore after this long (fallback when PersistentNap=off)

    // ── GPU, I/O & Core Affinity ──────────────────────────────────────────────
    public bool LowerGpuPriority { get; set; } = false; // default OFF — D3DKMT Idle tier disrupts the shared HAGS flip queue, breaking VSync for all processes including foreground games
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
    /// How often (ms) a minimized-napped app is allowed a brief wake. Default: 60 s.
    /// Not gated on system CPU — MaxConcurrentBriefWakes, BriefWakeCpuCapPercent, and
    /// the 10-second wake window together bound the cost.
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

    /// <summary>
    /// When true, tray-napped processes escalate to deep sleep after TrayDeepSleepThresholdMs,
    /// using an even longer wake interval (TrayDeepSleepWakeIntervalMs). Default: true.
    /// </summary>
    public bool TrayDeepSleepEnabled { get; set; } = true;

    /// <summary>
    /// How long (ms) a tray-napped process must be napped before escalating to deep sleep.
    /// Default: 600 000 ms (10 minutes).
    /// </summary>
    public int TrayDeepSleepThresholdMs { get; set; } = 600_000;

    /// <summary>
    /// Wake interval (ms) used once a tray-napped app enters deep sleep mode.
    /// Default: 600 000 ms (10 minutes) — even rarer than the normal tray wake.
    /// </summary>
    public int TrayDeepSleepWakeIntervalMs { get; set; } = 600_000;

    // ── CPU Cap ──────────────────────────────────────────────────────────────
    /// <summary>
    /// When true, napped processes are placed in a Windows Job Object with a hard CPU
    /// rate limit. This enforces a real CPU ceiling instead of just lowering priority.
    /// Default: true.
    /// </summary>
    public bool NappedCpuCapEnabled { get; set; } = true;

    /// <summary>
    /// Maximum CPU usage (percent, 1–100) for napped processes. Uses Job Object
    /// CPU rate control — a real kernel-level cap. Default: 3%.
    /// </summary>
    public int NappedCpuCapPercent { get; set; } = 3;

    /// <summary>
    /// CPU cap (percent, 1–100) applied DURING brief wake windows instead of fully
    /// releasing the cap. Lets napped apps make real progress during their wake window
    /// without letting them spike. 7% ≈ 2.3× the nap cap — enough to process a message
    /// queue or fire a setInterval callback, but keeps concurrent-wake CPU bounded
    /// (3 wakes × 7% = 21%, under the 12% system nap trigger's steady-state budget).
    /// Default: 7%.
    /// </summary>
    public int BriefWakeCpuCapPercent { get; set; } = 7;

    /// <summary>
    /// When true, scheduled brief wakes (both minimize-nap and tray-nap) are suspended
    /// while Game Mode is active. Napped apps stay napped for the entire game session,
    /// reserving 100% of the CPU for the game. Default: true — this is the intended
    /// behavior for most users. Turn off only if you actively need background apps
    /// (e.g. Discord bots, streaming scripts) to keep getting periodic runtime while gaming.
    /// </summary>
    public bool SuppressBriefWakesDuringGameMode { get; set; } = true;

    // ── Beta Features ────────────────────────────────────────────────────────

    /// <summary>
    /// When true, processes running at High or System integrity level (elevated/admin
    /// processes, OS infrastructure like Hyper-V vmwp.exe, Docker, WSL2, etc.) are
    /// automatically protected from napping. Prevents throttling critical system
    /// processes that aren't in the foreground's process tree. On by default.
    /// </summary>
    public bool ElevatedProcessGuardEnabled { get; set; } = true;

    /// <summary>
    /// When true, apps visible on ANY monitor are protected from napping, not just
    /// the foreground window. Uses EnumWindows to find all non-iconic visible windows
    /// across all displays. On by default.
    /// </summary>
    public bool MultiMonitorAwarenessEnabled { get; set; } = true;

    /// <summary>
    /// When true, processes with significant network I/O are protected from napping
    /// (e.g. downloads, uploads, streaming). Off by default.
    /// </summary>
    public bool NetworkActivityGuardEnabled { get; set; } = false;

    /// <summary>
    /// Network I/O threshold in KB/s. Processes exceeding this are protected from napping.
    /// Default: 50 KB/s.
    /// </summary>
    public int NetworkActivityThresholdKBps { get; set; } = 50;

    /// <summary>
    /// When true, all instances of the foreground app's process name are protected.
    /// E.g. if one Chrome window is focused, all chrome.exe instances are protected. On by default.
    /// </summary>
    public bool ProcessGroupAwarenessEnabled { get; set; } = true;

    /// <summary>
    /// When true, processes with significant disk I/O are protected from napping
    /// (e.g. saving, compiling, file operations). Off by default.
    /// </summary>
    public bool DiskIoGuardEnabled { get; set; } = false;

    /// <summary>
    /// Disk I/O threshold in KB/s. Processes exceeding this are protected from napping.
    /// Default: 100 KB/s.
    /// </summary>
    public int DiskIoThresholdKBps { get; set; } = 100;

    /// <summary>
    /// When true, processes that have been consistently idle (below 1% CPU for 5+ ticks)
    /// are escalated to aggressive nap even if they don't match the AggressiveNapTargets list.
    /// Off by default.
    /// </summary>
    public bool SmartAggressiveNapEnabled { get; set; } = false;

    /// <summary>
    /// CPU threshold for smart aggressive nap detection. Processes below this % for
    /// SmartAggressiveTickCount consecutive ticks are candidates. Default: 1%.
    /// </summary>
    public int SmartAggressiveCpuThresholdPercent { get; set; } = 1;

    /// <summary>
    /// How many consecutive ticks a process must be below SmartAggressiveCpuThresholdPercent
    /// before it's napped aggressively. Default: 5 ticks.
    /// </summary>
    public int SmartAggressiveTickCount { get; set; } = 5;

    /// <summary>
    /// When true, a process whose window title changes (indicating a notification or update)
    /// gets a grace period before being napped. Off by default.
    /// </summary>
    public bool NotificationGracePeriodEnabled { get; set; } = false;

    /// <summary>
    /// How long (ms) to wait after a window title change before allowing nap. Default: 15 s.
    /// </summary>
    public int NotificationGracePeriodMs { get; set; } = 15_000;

    // ── Background Nap (unfocused timer) ────────────────────────────────────
    /// <summary>
    /// When true, any non-foreground process is napped after being unfocused for
    /// BackgroundNapAfterMs, regardless of CPU usage. The most effective nap mode —
    /// catches everything the user isn't actively using.
    /// </summary>
    public bool BackgroundNapEnabled { get; set; } = true;

    /// <summary>
    /// How long (ms) a process must be unfocused before background nap kicks in.
    /// Default: 180 000 ms (3 minutes).
    /// </summary>
    public int BackgroundNapAfterMs { get; set; } = 180_000;

    // ── Idle Nap (low CPU auto-nap) ──────────────────────────────────────────
    /// <summary>
    /// When true, any non-foreground process using less than IdleNapCpuThreshold %
    /// for IdleNapAfterMs is napped regardless of system CPU level. Catches truly
    /// idle background processes that waste resources by just existing.
    /// </summary>
    public bool IdleNapEnabled { get; set; } = true;

    /// <summary>
    /// CPU % threshold below which a process is considered idle. Default: 0.5%.
    /// </summary>
    public double IdleNapCpuThreshold { get; set; } = 0.5;

    /// <summary>
    /// How long (ms) a process must stay below IdleNapCpuThreshold before being
    /// idle-napped. Default: 120 000 ms (2 minutes).
    /// </summary>
    public int IdleNapAfterMs { get; set; } = 120_000;

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
