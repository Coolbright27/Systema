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
    /// When true, child processes of a napped app are also napped automatically.
    /// Off by default.
    /// </summary>
    public bool NapChildrenEnabled { get; set; } = false;

    // ── CPU Thresholds ────────────────────────────────────────────────────────
    // NOTE: high-CPU "off-screen" napping was removed — nap decisions are visibility + time based.
    public int SystemCpuTriggerPercent { get; set; } = 12;   // adaptive-tick cadence gate (idle → slower ticks)
    public int ProcessCpuStopPercent   { get; set; } = 3;    // "classic" time-based restore: wake when CPU drops below this
    public int TimeOverQuotaMs         { get; set; } = 1500;  // aggressive-nap dwell before throttling a known waster
    public int MinAdjustmentDurationMs { get; set; } = 5000;  // keep throttled for at least this long
    public int MaxAdjustmentDurationMs { get; set; } = 30000; // force-restore after this long (fallback when PersistentNap=off)

    // ── I/O & Core Affinity ───────────────────────────────────────────────────
    // GPU priority deliberately NOT tunable — D3DKMTSetProcessSchedulingPriorityClass
    // disrupts the shared HAGS flip queue and breaks VSync system-wide (including the
    // foreground game). The feature was removed entirely in favour of never touching
    // GPU scheduling from this service.
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
    /// Closest Windows equivalent to macOS's compressed-memory behavior.
    ///
    /// When a napped process crosses into deep sleep (idle for the deep-sleep
    /// threshold, default ~10 min), trim its working set with
    /// <c>SetProcessWorkingSetSize(-1,-1)</c> + <c>EmptyWorkingSet</c>. Pages get
    /// pushed to the standby list where Windows 10+ compresses them in place at
    /// roughly 2-4× ratio. Also re-trims after every brief wake that ends while
    /// the process is still in deep sleep, so the compressed footprint stays
    /// stable across many wake cycles.
    ///
    /// On by default — this replaces the v0.7.9-era "hard RAM cap" and
    /// "re-trim after brief wake" toggles. The hard cap was removed because
    /// modern Windows handles working-set compression automatically; this
    /// gentler trim-on-deep-sleep approach achieves the same outcome with no
    /// risk of crashing apps that don't tolerate a hard cap.
    /// </summary>
    public bool CompressDeepSleep { get; set; } = true;

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

    /// <summary>
    /// When true, a minimized / tray app whose WHOLE process tree is using more than
    /// <see cref="BusyMinimizedCpuThresholdPercent"/> CPU is kept fully awake — it's likely still
    /// doing work the user backgrounded (an export, a build, a render). ON by default. The entire
    /// app tree is held awake across every nap path until it settles below the threshold.
    /// </summary>
    public bool SkipBusyMinimizedApps { get; set; } = true;

    /// <summary>CPU % above which a minimized app is treated as "busy" and skipped when
    /// <see cref="SkipBusyMinimizedApps"/> is on. Default: 30.</summary>
    public int BusyMinimizedCpuThresholdPercent { get; set; } = 30;

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
    /// CPU rate control — a real kernel-level cap. Default: 1%.
    /// </summary>
    public int NappedCpuCapPercent { get; set; } = 1;

    /// <summary>
    /// CPU cap (percent, 1–100) applied DURING brief wake windows instead of fully
    /// releasing the cap. Lets napped apps make real progress during their wake window
    /// without letting them spike. 5% ≈ 2.5× the nap cap — enough to process a message
    /// queue or fire a setInterval callback, but keeps concurrent-wake CPU bounded
    /// (3 wakes × 5% = 15%, under the system nap trigger's steady-state budget).
    /// Default: 5%.
    /// </summary>
    public int BriefWakeCpuCapPercent { get; set; } = 5;

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
    /// When true, all instances of the foreground app's process name are protected.
    /// E.g. if one Chrome window is focused, all chrome.exe instances are protected. On by default.
    /// </summary>
    public bool ProcessGroupAwarenessEnabled { get; set; } = true;

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

    // ── Launch Boost ──────────────────────────────────────────────────────────
    /// <summary>
    /// Master switch for Launch Boost. When on, a newly-launched app gets a
    /// temporary priority boost so it opens faster. OFF by default — opt-in.
    /// </summary>
    public bool LaunchBoostEnabled { get; set; } = false;

    /// <summary>How long (seconds) the boost lasts after an app launches. Default 20.</summary>
    public int LaunchBoostDurationSeconds { get; set; } = 20;

    /// <summary>Raise CPU priority to High during the boost window. Default on.</summary>
    public bool LaunchBoostCpu { get; set; } = true;

    /// <summary>Raise I/O priority to High during the boost window. Default on.</summary>
    public bool LaunchBoostIo { get; set; } = true;

    /// <summary>Turn off efficiency mode (EcoQoS) during the boost window. Default on.
    /// (RAM priority is intentionally left unchanged.)</summary>
    public bool LaunchBoostDisableEfficiency { get; set; } = true;

    /// <summary>
    /// Raise GPU scheduling priority to High during the boost window. DEFAULT OFF.
    /// Opt-in only: this touches the GPU scheduler (D3DKMTSetProcessSchedulingPriorityClass),
    /// which on some systems can affect VSync/frame pacing. Original GPU priority is
    /// captured and restored when the boost ends.
    /// </summary>
    public bool LaunchBoostGpu { get; set; } = false;
}
