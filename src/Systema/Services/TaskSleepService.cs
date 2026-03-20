// ════════════════════════════════════════════════════════════════════════════
// TaskSleepService.cs  ·  Background monitor that throttles high-CPU background processes
// ════════════════════════════════════════════════════════════════════════════
//
// Runs a dedicated background thread (Tick loop) that samples all processes,
// identifies candidates exceeding the CPU threshold or matching MinimizeNap rules,
// and throttles them via CPU priority, EcoQoS, GPU/IO priority, E-core affinity,
// and memory priority. Restores all settings when the process drops below threshold
// or the user exits. Publishes a MonitorSnapshot each tick for the live UI feed.
//
// QUICK EDIT GUIDE
//   Add throttle method    → TryThrottle() in the throttle section
//   Add restore logic      → step 5 in Tick()
//   Add new setting field  → TaskSleepSettings.cs then TaskSleepViewModel.cs
//
// RELATED FILES
//   Models/TaskSleepSettings.cs   — all config with default values
//   Models/TaskSleepAppRule.cs    — per-app override rules
//   Models/MonitorEvent.cs        — individual activity log entry
//   Models/ProcessSnapshot.cs     — per-process row in the live monitor list
//   Models/MonitorSnapshot.cs     — full tick snapshot published to the VM
//   TaskSleepViewModel.cs         — owns this service, displays monitor feed
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Systema.Core;
using Systema.Models;

namespace Systema.Services;

/// <summary>
/// Monitors all running processes and throttles background tasks by lowering their
/// CPU priority and enabling Windows Efficiency Mode (EcoQoS).
/// Throttling is threshold-driven: a process must exceed the per-process CPU threshold
/// while the system is also above the system-wide trigger, and must stay over-threshold
/// for at least TimeOverQuotaMs before any action is taken.
/// Foreground processes and their children are always protected.
/// System / security processes are never touched.
/// </summary>
public sealed class TaskSleepService : IDisposable
{
    private static readonly LoggerService _log = LoggerService.Instance;

    private Thread?           _monitorThread;
    private volatile bool     _running;
    private TaskSleepSettings _settings;
    private Dictionary<string, TaskSleepAppRule> _appRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly object   _settingsLock = new();

    // ── Per-process state (monitor-thread only) ───────────────────────────────

    // pid -> original priority class before we lowered it
    private readonly ConcurrentDictionary<int, uint> _throttledPids = new();

    // pid -> UTC timestamp when this process was throttled
    private readonly Dictionary<int, DateTime> _throttledAt = new();

    // pid -> UTC timestamp when this process was last restored (cooldown guard)
    private readonly Dictionary<int, DateTime> _restoredAt = new();

    // pid -> UTC timestamp when this process first exceeded the CPU start threshold
    private readonly Dictionary<int, DateTime> _overThresholdSince = new();

    // pid -> (total CPU time in 100-ns ticks, sample timestamp) for CPU% calculation
    private readonly Dictionary<int, (long TotalTime, DateTime SampleTime)> _cpuSamples = new();

    // pid -> last computed CPU percentage (monitor-thread only)
    private readonly Dictionary<int, double> _lastCpuPercent = new();

    // pid -> process display name (monitor-thread only)
    private readonly Dictionary<int, string> _processNames = new();

    // pid -> original affinity mask (saved before we pin to E-cores)
    private readonly ConcurrentDictionary<int, UIntPtr> _originalAffinities = new();

    // ── Minimize Nap state (monitor-thread only) ──────────────────────────────

    // PIDs currently throttled because the app was minimized (not CPU-triggered)
    private readonly HashSet<int>              _minimizedNapPids = new();
    // When the next brief idle wake is allowed for each minimize-napped PID
    private readonly Dictionary<int, DateTime> _nextBriefWakeAt  = new();
    // If in a brief wake, when to re-throttle (key absent = not in brief-wake)
    private readonly Dictionary<int, DateTime> _briefWakeEndAt   = new();
    // Cached set of PIDs with active audio sessions
    private HashSet<int> _cachedAudioPids    = new();
    private DateTime     _lastAudioCacheTime = DateTime.MinValue;
    private const double AudioCacheSeconds   = 5.0;

    // ── Tray Nap state (monitor-thread only) ──────────────────────────────────

    // PIDs throttled because the process lives only in the system tray (no visible windows)
    private readonly HashSet<int>              _trayNapPids          = new();
    // When the next rare brief wake is allowed for each tray-napped PID
    private readonly Dictionary<int, DateTime> _trayNextBriefWakeAt  = new();
    // If in a brief wake, when to re-throttle the tray-napped process
    private readonly Dictionary<int, DateTime> _trayBriefWakeEndAt   = new();

    // ── Grace period (30 s) before minimize/tray nap kicks in ─────────────────
    // Prevents snap-napping something the user just briefly minimized or something
    // that starts tray-only while it's still initialising.
    private const    int                       MinimizeTrayGraceMs   = 30_000; // 30 seconds
    private readonly Dictionary<int, DateTime> _minimizeGraceSince   = new();
    private readonly Dictionary<int, DateTime> _trayGraceSince       = new();

    // ── E-core detection (lazy, cached) ──────────────────────────────────────
    private bool    _eCoresDetected;
    private bool    _hasECores;
    private UIntPtr _eCoreMask;

    // ── System CPU state ──────────────────────────────────────────────────────
    private long     _prevSysIdle;
    private long     _prevSysTotal;
    private DateTime _prevSysSample;
    private double   _lastSystemCpuPercent;
    private bool     _systemTimesWarned;

    // ── Monitoring ────────────────────────────────────────────────────────────
    private readonly ConcurrentQueue<MonitorEvent> _eventLog = new();
    private const    int MaxEvents = 200;
    private volatile MonitorSnapshot? _latestSnapshot;

    public MonitorSnapshot? GetLatestSnapshot() => _latestSnapshot;

    public event Action<string>? StatusChanged;

    // ── Manual wake requests (UI → monitor thread, thread-safe) ──────────────
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _wakeRequests = new();

    /// <summary>
    /// Signals the monitor thread to immediately restore and stop napping the named process.
    /// Takes effect on the very next tick (~1 second). Thread-safe.
    /// </summary>
    public void WakeProcess(string processName)
        => _wakeRequests.Enqueue(processName.ToLowerInvariant());

    // ── System / security processes we will never touch ───────────────────────
    private static readonly HashSet<string> SystemProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Core OS
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon",
        "lsass", "lsaiso", "services", "svchost", "ntoskrnl", "dwm", "conhost",
        "fontdrvhost", "sihost", "taskhostw", "ctfmon", "RuntimeBroker",
        "WmiPrvSE", "SearchIndexer", "spoolsv", "WUDFHost",
        "audiodg", "LsaIso", "WerFault", "WerFaultSecure",
        // Installers / component servicing — throttling mid-install can corrupt packages
        "TrustedInstaller", "msiexec", "wermgr", "setup", "SetupHost",
        // ── Windows Defender ─────────────────────────────────────────────────
        "MsMpEng",                // Defender antivirus engine — real-time scanning
        "NisSrv",                 // Network Inspection Service — network threat detection
        "MpCmdRun",               // Defender command-line scanner
        "MpDefenderCoreService",  // Defender core service (Windows 11+)
        "SecurityHealthService",  // Windows Security health monitoring
        "SecurityHealthSystray",  // Windows Security tray icon
        "SgrmBroker",             // System Guard Runtime Monitor — firmware/boot integrity
        "SecHealthUI",            // Windows Security app UI
        // ── Bitdefender ──────────────────────────────────────────────────────
        "bdagent", "bdservicehost", "bdntwrk", "bdredline", "vsserv",
        "vsservppl", "bdwtxag", "bdupdater",
        // ── ESET ─────────────────────────────────────────────────────────────
        "ekrn",    // ESET kernel service — real-time protection engine
        "egui",    // ESET GUI
        "esets_daemon", "esetservice",
        // ── Kaspersky ────────────────────────────────────────────────────────
        "avp",     // Kaspersky AV protection main process
        "kavtray", // Kaspersky tray icon
        "avpui",   // Kaspersky UI
        // ── Norton / Symantec ────────────────────────────────────────────────
        "ccsvchst", "nsservice", "NortonSecurity", "Norton360",
        "NortonLifeLock", "symantec", "sndsrvc",
        // ── McAfee / Trellix ─────────────────────────────────────────────────
        "mcshield",  // McAfee on-access scanner
        "mfemms",    // McAfee core service
        "mfevtps",   // McAfee validation trust protection
        "mcuicnt",   // McAfee UI
        // ── Malwarebytes ─────────────────────────────────────────────────────
        "MBAMService",  // Malwarebytes real-time protection service
        "mbam",         // Malwarebytes scanner
        "MBAMAgent",    // Malwarebytes agent
        // ── Webroot ──────────────────────────────────────────────────────────
        "WRSA",          // Webroot SecureAnywhere agent
        "WRCoreService", // Webroot core
        // ── Avast / AVG ──────────────────────────────────────────────────────
        "avastui", "avastsvc", "afwserv",  // Avast
        "avgui", "avgsvc",                  // AVG
        // ── CrowdStrike Falcon ────────────────────────────────────────────────
        "CSFalconService", "CSFalconContainer", "falconHostService",
        // ── SentinelOne ──────────────────────────────────────────────────────
        "SentinelAgent", "SentinelStaticEngine", "SentinelOne",
        // ── Cylance / BlackBerry ──────────────────────────────────────────────
        "CylanceSvc", "CylanceUI", "CylancePROTECT",
        // ── Trend Micro ──────────────────────────────────────────────────────
        "uiWatchDog", "coreServiceShell",
        // ── Sophos ───────────────────────────────────────────────────────────
        "SophosAgent", "SophosNtpService", "SAVMainUI",
        // ── Windows Shell — throttling any of these breaks Start, taskbar, or Explorer ──
        "explorer",                   // shell, file manager, taskbar host
        "StartMenuExperienceHost",    // Start menu (Windows 11)
        "ShellExperienceHost",        // taskbar, Action Center, notification area
        "SearchHost",                 // Windows Search UI / search bar
        "SearchApp",                  // Search (older Windows builds)
        "TextInputHost",              // touch keyboard, emoji panel, handwriting
        "ApplicationFrameHost",       // UWP app container / hosting frame
        "SystemSettings",             // Settings app
        "Widgets",                    // Windows 11 Widgets panel
        "WidgetService",              // Widgets background service
        "msedgewebview2",             // WebView2 runtime — powers Start menu & Widgets
        // Shell helpers — COM Surrogate runs shell extensions & thumbnail generators;
        // throttling it causes shell operations (folder opens, right-clicks) to hang
        "dllhost",
        // Auth / logon screens — must never be starved or the PC appears frozen
        "LockApp", "LogonUI",
        // Diagnostics / perf tools — throttling Task Manager while troubleshooting is confusing
        "Taskmgr", "PerfHost",
        // This app itself
        "Systema"
    };

    /// <summary>
    /// Well-known background wasters that should be aggressively throttled whenever
    /// they are not in the foreground — even when system CPU is below the trigger.
    /// These processes provide no real-time value to the user and are notorious for
    /// burning CPU/memory in the background.
    /// </summary>
    private static readonly HashSet<string> AggressiveNapTargets =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Telemetry / data collection
        "DiagTrack", "WerFaultSecure", "wsqmcons", "compattelrunner",
        // Windows Update background workers (not the core update service)
        "wuauclt", "UsoClient",
        // Cloud sync agents (throttle when not actively syncing visible files)
        "OneDrive", "Dropbox", "GoogleDriveFS", "iCloudDrive",
        "iCloud", "iCloudServices", "BoxSync", "pCloud",
        // Game / app launchers (background idle state)
        "EpicGamesLauncher", "GalaxyClient", "Battle.net",
        "FocusedServer",  // GOG Galaxy background worker
        "AmazonGamesUI",
        // Cortana / Copilot background workers
        "Cortana", "Microsoft.Cortana",
        // Edge background workers when no Edge windows open
        "MicrosoftEdgeUpdate",
        // Adobe background services
        "AdobeUpdateService", "AGSService", "AdobeIPCBroker",
        "AdobeCollabSync", "CoreSync", "Creative Cloud Helper",
        // Nvidia / AMD background workers
        "NvBackend", "NvContainerLocalSystem",
        "RzSynapse",  // Razer Synapse background
        // Microsoft Store / WinRT background workers
        "WinStore.App", "Microsoft.WindowsStore",
    };

    /// <summary>
    /// Subset of AggressiveNapTargets that are cloud sync agents.
    /// These are given a CPU-activity guard: if they're currently above 2% CPU
    /// (i.e. actively syncing files), they are skipped this tick so the sync
    /// can complete without being throttled mid-transfer.
    /// </summary>
    private static readonly HashSet<string> CloudSyncAgents =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "OneDrive", "Dropbox", "GoogleDriveFS", "iCloudDrive",
        "iCloud", "iCloudServices", "BoxSync", "pCloud",
    };

    private const double CloudSyncActiveCpuThreshold = 2.0; // % — above this = actively syncing

    // ── Processes that must never be minimize-napped ──────────────────────────
    // Anything in this list is treated as always having active audio/media output
    // regardless of what Core Audio reports (fast name-based guard).
    private static readonly HashSet<string> AlwaysActiveProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Music / media players
        "Spotify", "SpotifyWebHelper", "vlc", "wmplayer", "groove",
        "foobar2000", "AIMP", "MusicBee", "winamp", "mpc-hc", "mpc-hc64",
        "mpc-be", "mpc-be64", "PotPlayerMini", "PotPlayerMini64", "mpv", "mpv.net",
        "iTunes", "AppleMusic",
        // Communication / calls
        "Teams", "ms-teams", "Zoom", "ZoomIt", "Discord", "Slack",
        "skype", "skypehost", "skypebridge",
        "WebexHost", "WebexApp", "Cisco_Spark", "RingCentral",
        // Screen recorders / streaming
        "obs64", "obs32", "obs", "StreamlabsOBS", "Streamlabs OBS",
        "nvsphelper64", "nvsphelper32", // NVIDIA ShadowPlay helpers
        // Game launchers that manage audio in background
        "EpicGamesLauncher",
    };

    // ── Priority class constants ───────────────────────────────────────────────
    private const uint IDLE_PRIORITY_CLASS   = 0x00000040;
    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;

    // ── Process access rights ──────────────────────────────────────────────────
    private const uint PROCESS_SET_INFORMATION           = 0x0200;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ── Efficiency Mode (EcoQoS) constants ────────────────────────────────────
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    // ── Constructor / Settings ─────────────────────────────────────────────────

    public TaskSleepService(TaskSleepSettings settings)
    {
        _settings = settings;
    }

    public void UpdateSettings(TaskSleepSettings settings)
    {
        lock (_settingsLock)
        {
            _settings  = settings;
            _appRules  = settings.AppRules.ToDictionary(r => r.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_running) return;
        _running = true;

        _monitorThread = new Thread(MonitorLoop, 8 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "TaskSleep-Monitor",
            Priority = ThreadPriority.BelowNormal
        };
        _monitorThread.Start();

        _log.Info("TaskSleepService", "Started");
        Notify("Task Sleep active — monitoring background processes.");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        RestoreAll();
        _log.Info("TaskSleepService", "Stopped — all processes restored");
        Notify("Task Sleep is off.");
    }

    public void Dispose() => Stop();

    // ── Monitor loop ───────────────────────────────────────────────────────────

    private void MonitorLoop()
    {
        // ── Startup grace: wait until system has been running for at least 45 s ──
        // This lets Windows finish loading drivers, services, and shell components
        // before we start throttling anything. Without this, normal startup processes
        // may be incorrectly napped mid-boot.
        const long GraceMs = 45_000;
        long uptimeMs = Environment.TickCount64;
        if (uptimeMs < GraceMs)
        {
            long waitMs = GraceMs - uptimeMs;
            _log.Info("TaskSleepService", $"Startup grace: waiting {waitMs / 1000.0:F1}s for system to finish booting.");
            Notify($"Task Sleep waiting {waitMs / 1000:F0}s for boot to complete…");
            int slept = 0;
            while (_running && slept < waitMs)
            {
                Thread.Sleep(500);
                slept += 500;
            }
            if (!_running) return;
        }

        while (_running)
        {
            try   { Tick(); }
            catch (Exception ex) { _log.Error("TaskSleepService", "Tick failed", ex); }

            if (_running)
            {
                // Adaptive tick: when the system is idle and nothing is throttled there
                // is nothing to do — sleep longer to reduce the monitor's own overhead.
                TaskSleepSettings s;
                lock (_settingsLock) { s = _settings; }
                int sleepMs = (s.AdaptiveTick &&
                               _lastSystemCpuPercent < s.SystemCpuTriggerPercent &&
                               _throttledPids.IsEmpty)
                    ? 2500 : 1000;
                Thread.Sleep(sleepMs);
            }
        }
    }

    private void Tick()
    {
        TaskSleepSettings s;
        Dictionary<string, TaskSleepAppRule> rules;
        lock (_settingsLock) { s = _settings; rules = _appRules; }

        // 1. Sample total system CPU
        double sysCpu = SampleSystemCpu();

        // 2. Get all processes + foreground protection set
        uint foregroundPid = GetForegroundPid();
        var  protectedPids = BuildProtectedSet(foregroundPid, s.ActOnForegroundChildren);

        Process[] all     = Process.GetProcesses();
        var       livePids = new HashSet<int>(all.Select(p => p.Id));

        // 3. Collect per-process CPU samples (QUERY_LIMITED access only)
        var cpuMap = SampleAllProcessCpu(all);

        // 4. Clean up state for processes that no longer exist
        CleanupDeadProcesses(livePids);

        // 4b. Collect window / audio state for minimize-nap and tray-nap
        HashSet<int> minimizedPids = s.MinimizeNapEnabled
            ? GetMinimizedProcessIds() : new HashSet<int>();
        HashSet<int> audioPids = (s.MinimizeNapEnabled || s.TrayNapEnabled)
            ? GetOrRefreshAudioPids() : new HashSet<int>();
        // Tray-nap: get PIDs with NO visible non-minimized top-level windows
        HashSet<int> trayPids = s.TrayNapEnabled
            ? GetTrayProcessIds(minimizedPids) : new HashSet<int>();

        // 4c. Process manual wake requests from the UI (e.g. "Stop Napping" button)
        if (!_wakeRequests.IsEmpty)
        {
            var wakeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_wakeRequests.TryDequeue(out string? wn))
                if (wn != null) wakeNames.Add(wn);

            foreach (int pid in _throttledPids.Keys.ToList())
            {
                if (_processNames.TryGetValue(pid, out string? pname) && wakeNames.Contains(pname))
                {
                    TryRestoreProcess(pid);
                    _minimizedNapPids.Remove(pid);
                    _trayNapPids.Remove(pid);
                    _nextBriefWakeAt.Remove(pid);
                    _briefWakeEndAt.Remove(pid);
                    _trayNextBriefWakeAt.Remove(pid);
                    _trayBriefWakeEndAt.Remove(pid);
                }
            }
        }

        // 5. Evaluate currently napped processes — restore if conditions are met
        foreach (int pid in _throttledPids.Keys.ToList())
        {
            bool   shouldRestore = false;
            string restoreReason = "";

            if (!livePids.Contains(pid))
            {
                // Process exited — always clean up
                shouldRestore = true; restoreReason = "process exited";
                _minimizedNapPids.Remove(pid); _nextBriefWakeAt.Remove(pid); _briefWakeEndAt.Remove(pid);
            }
            else if (protectedPids.Contains(pid))
            {
                // User brought the app to foreground — wake it permanently
                shouldRestore = true; restoreReason = "opened by user";
                _minimizedNapPids.Remove(pid); _nextBriefWakeAt.Remove(pid); _briefWakeEndAt.Remove(pid);
            }
            else if (_minimizedNapPids.Contains(pid))
            {
                // ── Minimize-napped process: separate restore logic ───────────────
                bool nowMinimized = minimizedPids.Contains(pid);
                bool hasAudio     = audioPids.Contains(pid) ||
                    AlwaysActiveProcessNames.Contains(
                        _processNames.TryGetValue(pid, out var pn) ? pn : "");

                if (!nowMinimized || hasAudio)
                {
                    // App was un-minimized or started audio — restore permanently
                    shouldRestore = true;
                    restoreReason = hasAudio ? "audio detected" : "app un-minimized";
                    _minimizedNapPids.Remove(pid); _nextBriefWakeAt.Remove(pid); _briefWakeEndAt.Remove(pid);
                }
                else
                {
                    // Still minimized & silent — allow brief idle wakes (suppressed during game mode)
                    bool cpuIdle    = sysCpu < s.SystemCpuTriggerPercent / 2.0;
                    bool wakeNeeded = !_nextBriefWakeAt.TryGetValue(pid, out DateTime nextWake) ||
                                      DateTime.UtcNow >= nextWake;
                    if (!s.IsGameModeActive && cpuIdle && wakeNeeded)
                    {
                        _processNames.TryGetValue(pid, out string? nm2);
                        TryRestoreProcess(pid);
                        _throttledAt.Remove(pid);
                        _briefWakeEndAt[pid]  = DateTime.UtcNow.AddMilliseconds(s.MinimizedBriefWakeDurationMs);
                        _nextBriefWakeAt[pid] = DateTime.UtcNow.AddMilliseconds(s.MinimizedBriefWakeIntervalMs);
                        AddEvent(nm2 ?? $"PID {pid}", pid, "Brief Wake", $"CPU {sysCpu:F0}%");
                    }
                    // else: continue napping — no action
                }
                // Do NOT fall through to PersistentNap / time-based restore for minimize-napped procs
            }
            else if (_trayNapPids.Contains(pid))
            {
                // ── Tray-napped process: restore if it got a visible window or started audio ─
                bool stillTray = trayPids.Contains(pid);
                bool hasAudio  = audioPids.Contains(pid) ||
                    AlwaysActiveProcessNames.Contains(
                        _processNames.TryGetValue(pid, out var pn2) ? pn2 : "");

                if (!stillTray || hasAudio)
                {
                    // App opened a window or started audio — restore permanently
                    shouldRestore = true;
                    restoreReason = hasAudio ? "audio detected" : "window appeared";
                    _trayNapPids.Remove(pid); _trayNextBriefWakeAt.Remove(pid); _trayBriefWakeEndAt.Remove(pid);
                }
                else
                {
                    // Still tray-only — allow rare brief idle wakes (suppressed during game mode)
                    bool cpuIdle    = sysCpu < s.SystemCpuTriggerPercent / 2.0;
                    bool wakeNeeded = !_trayNextBriefWakeAt.TryGetValue(pid, out DateTime nextTrayWake) ||
                                      DateTime.UtcNow >= nextTrayWake;
                    if (!s.IsGameModeActive && cpuIdle && wakeNeeded)
                    {
                        _processNames.TryGetValue(pid, out string? nm3);
                        TryRestoreProcess(pid);
                        _throttledAt.Remove(pid);
                        _trayBriefWakeEndAt[pid]  = DateTime.UtcNow.AddMilliseconds(s.TrayBriefWakeDurationMs);
                        _trayNextBriefWakeAt[pid] = DateTime.UtcNow.AddMilliseconds(s.TrayBriefWakeIntervalMs);
                        AddEvent(nm3 ?? $"PID {pid}", pid, "Tray Wake", $"CPU {sysCpu:F0}%");
                    }
                }
                // Do NOT fall through to PersistentNap / time-based restore for tray-napped procs
            }
            else if (s.PersistentNapEnabled)
            {
                // Nap until used: keep napping until the user focuses the app.
                // The foreground check above is the only restore trigger.
            }
            else if (_throttledAt.TryGetValue(pid, out DateTime ta))
            {
                // Classic time-based restore (used when PersistentNap is off)
                double elapsed = (DateTime.UtcNow - ta).TotalMilliseconds;

                if (elapsed >= s.MaxAdjustmentDurationMs)
                {
                    shouldRestore = true; restoreReason = "max duration reached";
                }
                else if (elapsed >= s.MinAdjustmentDurationMs)
                {
                    if (cpuMap.TryGetValue(pid, out double procCpu) &&
                        procCpu < s.ProcessCpuStopPercent)
                    {
                        shouldRestore = true; restoreReason = $"CPU dropped to {procCpu:F1}%";
                    }
                }
            }

            if (shouldRestore)
            {
                _processNames.TryGetValue(pid, out string? name);
                TryRestoreProcess(pid);
                _throttledAt.Remove(pid);
                _restoredAt[pid] = DateTime.UtcNow; // cooldown: block re-throttle for 5 s
                AddEvent(name ?? $"PID {pid}", pid, "Woke up", restoreReason);
            }
        }

        // 5b. Clear grace entries for processes that are no longer minimized / tray-only
        //     (e.g. user un-minimized the window before the grace period elapsed)
        foreach (int gPid in _minimizeGraceSince.Keys.ToList())
            if (!minimizedPids.Contains(gPid)) _minimizeGraceSince.Remove(gPid);
        foreach (int gPid in _trayGraceSince.Keys.ToList())
            if (!trayPids.Contains(gPid)) _trayGraceSince.Remove(gPid);

        // 6. Consider throttling new processes
        bool systemOverThreshold = sysCpu >= s.SystemCpuTriggerPercent;
        long freeRamMb  = GetAvailableRamMb();
        bool ramPressure = freeRamMb < 4096; // < 4 GB free = memory is genuinely constrained
        // Throttle when CPU is high OR RAM is low — skip entirely when both are comfortable
        bool shouldConsiderThrottling = systemOverThreshold || ramPressure;

        foreach (var proc in all)
        {
            try
            {
                // ── Brief-wake re-throttle: minimize-napped proc whose idle-wake window expired ──
                if (s.MinimizeNapEnabled &&
                    !_throttledPids.ContainsKey(proc.Id) &&
                    _minimizedNapPids.Contains(proc.Id))
                {
                    if (_briefWakeEndAt.TryGetValue(proc.Id, out DateTime wakeEnd) &&
                        DateTime.UtcNow >= wakeEnd)
                    {
                        _briefWakeEndAt.Remove(proc.Id);
                        if (!protectedPids.Contains(proc.Id))
                        {
                            if (TryThrottle(proc, s, rules))
                            {
                                _throttledAt[proc.Id] = DateTime.UtcNow;
                                _processNames.TryGetValue(proc.Id, out string? rn);
                                AddEvent(rn ?? proc.ProcessName, proc.Id,
                                    "Re-napping", "brief wake ended");
                            }
                        }
                        else
                        {
                            // User focused the app during brief wake — free it permanently
                            _minimizedNapPids.Remove(proc.Id);
                            _nextBriefWakeAt.Remove(proc.Id);
                        }
                    }
                    // Whether we just re-throttled or are still in the wake window, skip
                    // the CPU throttle path for this process — minimize-nap owns it.
                    continue;
                }

                // ── Brief-wake re-throttle: tray-napped proc whose wake window expired ──
                if (s.TrayNapEnabled &&
                    !_throttledPids.ContainsKey(proc.Id) &&
                    _trayNapPids.Contains(proc.Id))
                {
                    if (_trayBriefWakeEndAt.TryGetValue(proc.Id, out DateTime trayWakeEnd) &&
                        DateTime.UtcNow >= trayWakeEnd)
                    {
                        _trayBriefWakeEndAt.Remove(proc.Id);
                        if (!protectedPids.Contains(proc.Id) && trayPids.Contains(proc.Id))
                        {
                            if (TryThrottle(proc, s, rules))
                            {
                                _throttledAt[proc.Id] = DateTime.UtcNow;
                                _processNames.TryGetValue(proc.Id, out string? tn);
                                AddEvent(tn ?? proc.ProcessName, proc.Id,
                                    "Tray Re-nap", "brief wake ended");
                            }
                        }
                        else
                        {
                            // App opened a window during wake — free it permanently
                            _trayNapPids.Remove(proc.Id);
                            _trayNextBriefWakeAt.Remove(proc.Id);
                        }
                    }
                    continue;
                }

                if (_throttledPids.ContainsKey(proc.Id)) continue;
                if (ShouldSkip(proc, protectedPids, s, rules)) continue;
                // Cooldown: don't re-throttle a process that was just restored
                if (_restoredAt.TryGetValue(proc.Id, out DateTime rt) &&
                    (DateTime.UtcNow - rt).TotalMilliseconds < 5_000) continue;

                // ── Minimize-nap: throttle minimized apps after 30 s grace period ──
                if (s.MinimizeNapEnabled && minimizedPids.Contains(proc.Id))
                {
                    bool hasAudio = audioPids.Contains(proc.Id) ||
                        AlwaysActiveProcessNames.Contains(proc.ProcessName);
                    if (!hasAudio)
                    {
                        // Record when this process first went minimized (grace period start)
                        if (!_minimizeGraceSince.ContainsKey(proc.Id))
                            _minimizeGraceSince[proc.Id] = DateTime.UtcNow;

                        bool graceElapsed =
                            (DateTime.UtcNow - _minimizeGraceSince[proc.Id]).TotalMilliseconds
                            >= MinimizeTrayGraceMs;

                        if (graceElapsed)
                        {
                            _minimizeGraceSince.Remove(proc.Id);
                            if (TryThrottle(proc, s, rules))
                            {
                                _throttledAt[proc.Id]     = DateTime.UtcNow;
                                _minimizedNapPids.Add(proc.Id);
                                _nextBriefWakeAt[proc.Id] =
                                    DateTime.UtcNow.AddMilliseconds(s.MinimizedBriefWakeIntervalMs);
                                _briefWakeEndAt.Remove(proc.Id);
                                AddEvent(proc.ProcessName, proc.Id, "Minimize Nap", "app minimized");
                            }
                        }
                        continue; // don't also apply CPU throttle logic
                    }
                    // Has audio → clear grace, fall through; may still get CPU-throttled if high
                    _minimizeGraceSince.Remove(proc.Id);
                }

                // ── Tray-nap: throttle tray-only processes after 30 s grace period ──
                if (s.TrayNapEnabled && trayPids.Contains(proc.Id) &&
                    !_trayNapPids.Contains(proc.Id))
                {
                    bool hasAudio = audioPids.Contains(proc.Id) ||
                        AlwaysActiveProcessNames.Contains(proc.ProcessName);
                    if (!hasAudio)
                    {
                        // Record when this process first became tray-only (grace period start)
                        if (!_trayGraceSince.ContainsKey(proc.Id))
                            _trayGraceSince[proc.Id] = DateTime.UtcNow;

                        bool graceElapsed =
                            (DateTime.UtcNow - _trayGraceSince[proc.Id]).TotalMilliseconds
                            >= MinimizeTrayGraceMs;

                        if (graceElapsed)
                        {
                            _trayGraceSince.Remove(proc.Id);
                            if (TryThrottle(proc, s, rules))
                            {
                                _throttledAt[proc.Id]         = DateTime.UtcNow;
                                _trayNapPids.Add(proc.Id);
                                _trayNextBriefWakeAt[proc.Id] =
                                    DateTime.UtcNow.AddMilliseconds(s.TrayBriefWakeIntervalMs);
                                _trayBriefWakeEndAt.Remove(proc.Id);
                                AddEvent(proc.ProcessName, proc.Id, "Tray Nap", "no visible window");
                            }
                        }
                        continue; // don't also CPU-throttle
                    }
                    // Has audio → clear grace, fall through; eligible for CPU throttle if high
                    _trayGraceSince.Remove(proc.Id);
                }

                // ── Aggressive nap: known background wasters — throttle even when CPU is low ──
                // These processes are notorious for wasting resources and have no foreground value.
                if (AggressiveNapTargets.Contains(proc.ProcessName) &&
                    !trayPids.Contains(proc.Id) &&   // tray-nap handles tray instances
                    !minimizedPids.Contains(proc.Id)) // minimize-nap handles minimized instances
                {
                    // Cloud sync guard: if the agent is actively syncing (CPU above threshold),
                    // skip this tick so the transfer completes without being throttled mid-sync.
                    if (CloudSyncAgents.Contains(proc.ProcessName) &&
                        cpuMap.TryGetValue(proc.Id, out double syncCpu) &&
                        syncCpu >= CloudSyncActiveCpuThreshold)
                    {
                        _overThresholdSince.Remove(proc.Id); // reset so the grace restarts on next idle
                        continue;
                    }

                    if (!_overThresholdSince.TryGetValue(proc.Id, out DateTime agSince))
                    {
                        _overThresholdSince[proc.Id] = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - agSince).TotalMilliseconds >= s.TimeOverQuotaMs)
                    {
                        if (TryThrottle(proc, s, rules))
                        {
                            _throttledAt[proc.Id] = DateTime.UtcNow;
                            _overThresholdSince.Remove(proc.Id);
                            _lastCpuPercent.TryGetValue(proc.Id, out double agCpu);
                            AddEvent(proc.ProcessName, proc.Id, "Napping",
                                $"background waster — CPU {agCpu:F1}%");
                        }
                    }
                    continue;
                }

                // ── CPU / RAM throttle logic (original) ────────────────────────────
                if (shouldConsiderThrottling &&
                    cpuMap.TryGetValue(proc.Id, out double procCpu) &&
                    procCpu >= s.ProcessCpuStartPercent)
                {
                    if (!_overThresholdSince.TryGetValue(proc.Id, out DateTime since))
                    {
                        _overThresholdSince[proc.Id] = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - since).TotalMilliseconds >= s.TimeOverQuotaMs)
                    {
                        if (TryThrottle(proc, s, rules))
                        {
                            _throttledAt[proc.Id] = DateTime.UtcNow;
                            _overThresholdSince.Remove(proc.Id);
                            _lastCpuPercent.TryGetValue(proc.Id, out double cpu);
                            AddEvent(proc.ProcessName, proc.Id, "Napping", $"CPU {cpu:F1}%");
                        }
                    }
                }
                else
                {
                    _overThresholdSince.Remove(proc.Id);
                }
            }
            catch { /* inaccessible process — skip */ }
            finally { try { proc.Dispose(); } catch { } }
        }

        // 7. Re-enforce: re-apply throttle if a process raised its own priority back
        if (s.EnforceSettings)
        {
            foreach (int pid in _throttledPids.Keys.ToList())
            {
                IntPtr h = OpenProcess(
                    PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    uint current = GetPriorityClass(h);
                    if (current != 0 && current != IDLE_PRIORITY_CLASS)
                    {
                        SetPriorityClass(h, IDLE_PRIORITY_CLASS);
                        _processNames.TryGetValue(pid, out string? nm);
                        AddEvent(nm ?? $"PID {pid}", pid, "Re-enforced", "process raised its own priority");
                        _log.Info("TaskSleepService", $"Re-enforced priority: PID {pid}");
                    }
                }
                catch (Exception ex) { _log.Warn("TaskSleepService", $"Re-enforce failed for PID {pid}: {ex.Message}"); }
                finally { CloseHandle(h); }
            }
        }

        // 8. Build and publish monitoring snapshot
        BuildAndPublishSnapshot(sysCpu, protectedPids, s, freeRamMb, ramPressure);

        int count = _throttledPids.Count;
        Notify($"Task Sleep active — {count} {(count == 1 ? "process napping" : "processes napping")}.  System CPU: {sysCpu:F0}%");
    }

    // ── CPU Sampling ───────────────────────────────────────────────────────────

    /// <summary>Samples total system CPU usage using GetSystemTimes.</summary>
    private double SampleSystemCpu()
    {
        try
        {
            GetSystemTimes(out FILETIME ftIdle, out FILETIME ftKernel, out FILETIME ftUser);
            long idle  = FtToLong(ftIdle);
            long total = FtToLong(ftKernel) + FtToLong(ftUser);

            if (_prevSysSample == default)
            {
                _prevSysIdle   = idle;
                _prevSysTotal  = total;
                _prevSysSample = DateTime.UtcNow;
                return 0;
            }

            long idleDelta  = idle  - _prevSysIdle;
            long totalDelta = total - _prevSysTotal;

            _prevSysIdle   = idle;
            _prevSysTotal  = total;
            _prevSysSample = DateTime.UtcNow;

            if (totalDelta <= 0) return _lastSystemCpuPercent;
            _lastSystemCpuPercent = Math.Max(0, Math.Min(100,
                (1.0 - (double)idleDelta / totalDelta) * 100.0));
        }
        catch (Exception ex)
        {
            if (!_systemTimesWarned)
            {
                _systemTimesWarned = true;
                _log.Warn("TaskSleepService", $"GetSystemTimes failed — system CPU tracking disabled: {ex.Message}");
            }
        }

        return _lastSystemCpuPercent;
    }

    /// <summary>
    /// Opens each process with QUERY_LIMITED access, reads kernel+user times,
    /// and returns a map of pid → CPU percentage since the last sample.
    /// Work is spread across up to 4 threads to keep the tick fast on busy systems.
    /// Processes with no previous sample are skipped this tick (first-time baseline).
    /// </summary>
    private Dictionary<int, double> SampleAllProcessCpu(Process[] all)
    {
        var now   = DateTime.UtcNow;
        int cores = Environment.ProcessorCount;

        // Collect results from all threads into concurrent dicts, then merge back.
        // Reading _cpuSamples in parallel is safe: reads happen here, writes happen
        // below (single-threaded) after the parallel section completes.
        var parallelResult  = new System.Collections.Concurrent.ConcurrentDictionary<int, double>();
        var parallelSamples = new System.Collections.Concurrent.ConcurrentDictionary<int, (long TotalTime, DateTime SampleTime)>();
        var parallelNames   = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();

        Parallel.ForEach(all,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Math.Min(cores / 2, 16)) },
            proc =>
            {
                try
                {
                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, proc.Id);
                    if (h == IntPtr.Zero) return;

                    try
                    {
                        if (!GetProcessTimes(h, out _, out _, out FILETIME ftKernel, out FILETIME ftUser))
                            return;

                        long totalTime = FtToLong(ftKernel) + FtToLong(ftUser);

                        if (_cpuSamples.TryGetValue(proc.Id, out var prev))
                        {
                            double elapsed = (now - prev.SampleTime).TotalSeconds;
                            if (elapsed >= 0.5)
                            {
                                long   delta      = totalTime - prev.TotalTime;
                                double cpu        = delta / (elapsed * cores * 10_000_000.0) * 100.0;
                                double clampedCpu = Math.Max(0, Math.Min(100.0 * cores, cpu));
                                parallelResult[proc.Id] = clampedCpu;
                            }
                        }

                        parallelSamples[proc.Id] = (totalTime, now);
                        parallelNames[proc.Id]   = proc.ProcessName;
                    }
                    finally { CloseHandle(h); }
                }
                catch { /* access denied or process exited — skip */ }
            });

        // Merge back into non-concurrent monitor-thread dicts
        foreach (var kv in parallelResult)  _lastCpuPercent[kv.Key] = kv.Value;
        foreach (var kv in parallelSamples) _cpuSamples[kv.Key]     = kv.Value;
        foreach (var kv in parallelNames)   _processNames[kv.Key]   = kv.Value;

        return new Dictionary<int, double>(parallelResult);
    }

    private void CleanupDeadProcesses(HashSet<int> livePids)
    {
        // Single pass: compute dead PIDs once, then remove from all dictionaries.
        var dead = _cpuSamples.Keys.Where(pid => !livePids.Contains(pid)).ToList();
        foreach (int pid in dead)
        {
            _cpuSamples.Remove(pid);
            _overThresholdSince.Remove(pid);
            _throttledAt.Remove(pid);
            _restoredAt.Remove(pid);
            _originalAffinities.TryRemove(pid, out _);
            _lastCpuPercent.Remove(pid);
            _processNames.Remove(pid);
            _minimizedNapPids.Remove(pid);
            _nextBriefWakeAt.Remove(pid);
            _briefWakeEndAt.Remove(pid);
            _trayNapPids.Remove(pid);
            _trayNextBriefWakeAt.Remove(pid);
            _trayBriefWakeEndAt.Remove(pid);
            _minimizeGraceSince.Remove(pid);
            _trayGraceSince.Remove(pid);
        }
    }

    // ── Throttle / Restore ─────────────────────────────────────────────────────

    private bool TryThrottle(Process proc, TaskSleepSettings s,
        Dictionary<string, TaskSleepAppRule> rules)
    {
        IntPtr handle = OpenProcess(
            PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
            false, proc.Id);

        if (handle == IntPtr.Zero) return false;

        try
        {
            uint original = GetPriorityClass(handle);
            if (original == 0) return false;

            // ── Determine effective settings (per-app overrides global) ────────
            bool   lowerCpu    = s.LowerCpuPriority;
            bool   lowerGpu    = s.LowerGpuPriority;
            bool   lowerIo     = s.LowerIoPriority;
            bool   moveToECores = s.MoveToECores;
            bool   effMode     = s.EnableEfficiencyMode;
            uint   cpuClass    = IDLE_PRIORITY_CLASS;
            var    gpuClass    = KMTSCHEDULINGPRIORITYCLASS.Idle;
            int    ioLevel     = IO_PRIORITY_VERY_LOW;

            if (rules.TryGetValue(proc.ProcessName, out var rule))
            {
                if (rule.CpuPriority != null)
                    { lowerCpu = true; cpuClass = ParseCpuPriorityClass(rule.CpuPriority); }
                if (rule.GpuPriority != null)
                    { lowerGpu = rule.GpuPriority != "Normal"; gpuClass = ParseGpuPriorityClass(rule.GpuPriority); }
                if (rule.IoPriority  != null)
                    { lowerIo  = rule.IoPriority  != "Normal"; ioLevel  = ParseIoPriority(rule.IoPriority); }
                if (rule.Affinity    != null)
                    moveToECores = rule.Affinity == "E-cores";
                if (rule.EfficiencyMode.HasValue)
                    effMode = rule.EfficiencyMode.Value;
            }

            // ── Apply ─────────────────────────────────────────────────────────
            bool changed = false;
            uint storedOriginal = original != 0 ? original : NORMAL_PRIORITY_CLASS;

            if (lowerCpu && original != cpuClass)
            {
                if (SetPriorityClass(handle, cpuClass))
                {
                    _throttledPids.TryAdd(proc.Id, original);
                    changed = true;
                }
            }

            if (effMode)
            {
                SetEfficiencyMode(handle, true);
                _throttledPids.TryAdd(proc.Id, storedOriginal);
                changed = true;
            }

            if (lowerGpu)
            {
                SetGpuPriority(handle, gpuClass);
                _throttledPids.TryAdd(proc.Id, storedOriginal);
                changed = true;
            }

            if (lowerIo)
            {
                SetIoPriorityLevel(handle, ioLevel);
                _throttledPids.TryAdd(proc.Id, storedOriginal);
                changed = true;
            }

            if (moveToECores)
            {
                UIntPtr eCoreMask = GetOrDetectECoreMask(s.DetectECores);
                if (eCoreMask != UIntPtr.Zero &&
                    GetProcessAffinityMask(handle, out UIntPtr origAffinity, out _))
                {
                    _originalAffinities.TryAdd(proc.Id, origAffinity);
                    SetProcessAffinityMask(handle, eCoreMask);
                    _throttledPids.TryAdd(proc.Id, storedOriginal);
                    changed = true;
                }
            }

            if (s.LowerMemoryPriority)
            {
                SetMemoryPriority(handle, MEMORY_PRIORITY_VERY_LOW);
                _throttledPids.TryAdd(proc.Id, storedOriginal);
                changed = true;
            }

            if (changed)
            {
                // Immediately reclaim the process's physical RAM pages so the OS can
                // give them to the foreground app without waiting for the pager.
                if (s.TrimWorkingSet) TrimProcessWorkingSet(handle);
                _log.Info("TaskSleepService", $"Throttled: {proc.ProcessName} (PID {proc.Id})");
            }

            return changed;
        }
        finally { CloseHandle(handle); }
    }

    private void TryRestoreProcess(int pid)
    {
        if (!_throttledPids.TryRemove(pid, out uint original)) return;

        IntPtr handle = OpenProcess(
            PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
            false, pid);

        if (handle == IntPtr.Zero)
        {
            _originalAffinities.TryRemove(pid, out _);
            return;
        }

        try
        {
            if (original != 0) SetPriorityClass(handle, original);
            SetEfficiencyMode(handle, false);
            SetGpuPriority(handle, KMTSCHEDULINGPRIORITYCLASS.Normal);
            SetIoPriorityLevel(handle, IO_PRIORITY_NORMAL);
            SetMemoryPriority(handle, MEMORY_PRIORITY_NORMAL);

            if (_originalAffinities.TryRemove(pid, out UIntPtr origAffinity))
                SetProcessAffinityMask(handle, origAffinity);

            _log.Info("TaskSleepService", $"Restored: PID {pid}");
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"Restore PID {pid} failed: {ex.Message}");
        }
        finally { CloseHandle(handle); }
    }

    private void RestoreAll()
    {
        foreach (int pid in _throttledPids.Keys.ToList())
        {
            TryRestoreProcess(pid);
            _throttledAt.Remove(pid);
        }
        _overThresholdSince.Clear();
        _originalAffinities.Clear();
        _minimizedNapPids.Clear();
        _nextBriefWakeAt.Clear();
        _briefWakeEndAt.Clear();
        _trayNapPids.Clear();
        _trayNextBriefWakeAt.Clear();
        _trayBriefWakeEndAt.Clear();
        _minimizeGraceSince.Clear();
        _trayGraceSince.Clear();
    }

    // ── Process filtering ──────────────────────────────────────────────────────

    private static bool ShouldSkip(
        Process proc, HashSet<int> protectedPids, TaskSleepSettings s,
        Dictionary<string, TaskSleepAppRule> rules)
    {
        if (proc.Id <= 4) return true;
        if (s.ExcludeSystemServices && IsSystemProcess(proc)) return true;
        if (s.IgnoreForeground && protectedPids.Contains(proc.Id)) return true;
        if (rules.TryGetValue(proc.ProcessName, out var rule) && rule.IsBlacklisted) return true;
        return false;
    }

    private static bool IsSystemProcess(Process proc)
    {
        // Name-based exclusion only. We no longer use SessionId == 0 as a blanket guard,
        // because that would protect every background service process — including Windows
        // Update workers, cloud-sync agents, telemetry runners, etc. — which are exactly
        // what we want to throttle. SystemProcessNames now explicitly enumerates everything
        // that must never be touched; everything else (including non-critical session 0
        // processes) is eligible for throttling.
        return SystemProcessNames.Contains(proc.ProcessName);
    }

    // ── Foreground process tree ────────────────────────────────────────────────

    private static uint GetForegroundPid()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    private static HashSet<int> BuildProtectedSet(uint foregroundPid, bool actOnChildren)
    {
        var set = new HashSet<int>();
        if (foregroundPid == 0) return set;
        set.Add((int)foregroundPid);

        if (actOnChildren) return set;

        try
        {
            var entries = new List<(int Pid, int ParentPid)>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return set;

            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snap, ref e))
                    do { entries.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID)); }
                    while (Process32Next(snap, ref e));
            }
            finally { CloseHandle(snap); }

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (pid, parentPid) in entries)
                    if (set.Contains(parentPid) && set.Add(pid))
                        changed = true;
            }
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"GetForegroundDescendants failed: {ex.Message}"); }

        return set;
    }

    // ── Minimize Nap helpers ───────────────────────────────────────────────────

    /// <summary>Returns PIDs of all currently minimized (iconic) top-level windows.</summary>
    private static HashSet<int> GetMinimizedProcessIds()
    {
        var pids = new HashSet<int>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (IsIconic(hWnd))
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid > 4) pids.Add((int)pid);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { /* best-effort */ }
        return pids;
    }

    // ── Tray Nap helpers ──────────────────────────────────────────────────────

    // GWL_STYLE index
    private const int GWL_STYLE  = -16;
    private const int GWL_EXSTYLE = -20;
    // Window style bits
    private const uint WS_VISIBLE      = 0x10000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    /// <summary>
    /// Returns PIDs whose processes have no normal visible top-level windows
    /// (i.e., they live exclusively in the system tray or background).
    /// A process is "tray-only" if every one of its top-level windows is either:
    ///   • not WS_VISIBLE, OR
    ///   • a tool-window (WS_EX_TOOLWINDOW — never shows in taskbar/switcher), OR
    ///   • iconic (minimized — handled separately by MinimizeNap).
    /// Processes with zero top-level windows are also considered tray-only.
    /// The minimizedPids set is passed in to avoid double-napping those processes here.
    /// </summary>
    private static HashSet<int> GetTrayProcessIds(HashSet<int> minimizedPids)
    {
        // Build map: pid → has any "normal" visible window
        var hasVisibleWindow = new Dictionary<int, bool>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid <= 4) return true;
                int iPid = (int)pid;

                uint style   = GetWindowLong(hWnd, GWL_STYLE);
                uint exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);

                bool visible    = (style & WS_VISIBLE) != 0;
                bool toolWindow = (exStyle & WS_EX_TOOLWINDOW) != 0;
                bool iconic     = IsIconic(hWnd);

                // A "normal" visible window: visible, not iconic, not a tool window
                bool normalVisible = visible && !iconic && !toolWindow;

                if (normalVisible)
                    hasVisibleWindow[iPid] = true;
                else if (!hasVisibleWindow.ContainsKey(iPid))
                    hasVisibleWindow[iPid] = false;

                return true;
            }, IntPtr.Zero);
        }
        catch { return new HashSet<int>(); }

        // A PID is tray-only if it has NO normal visible windows
        // and is not already handled by minimize-nap (minimized)
        var tray = new HashSet<int>();
        foreach (var kv in hasVisibleWindow)
        {
            if (!kv.Value && !minimizedPids.Contains(kv.Key))
                tray.Add(kv.Key);
        }
        return tray;
    }

    /// <summary>Returns PIDs with an active audio session; result is cached for 5 s.</summary>
    private HashSet<int> GetOrRefreshAudioPids()
    {
        if ((DateTime.UtcNow - _lastAudioCacheTime).TotalSeconds < AudioCacheSeconds)
            return _cachedAudioPids;

        _cachedAudioPids    = SampleActiveAudioPids();
        _lastAudioCacheTime = DateTime.UtcNow;
        return _cachedAudioPids;
    }

    /// <summary>
    /// Queries Windows Core Audio to find all PIDs with an Active audio session on any
    /// render (playback) endpoint.  Returns an empty set if COM fails for any reason.
    /// </summary>
    private static HashSet<int> SampleActiveAudioPids()
    {
        var pids = new HashSet<int>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();

            // eRender = 0 (playback); only playback sessions count for "playing audio"
            if (enumerator.EnumAudioEndpoints(0, 1 /* DEVICE_STATE_ACTIVE */,
                    out IMMDeviceCollection devices) != 0) return pids;

            devices.GetCount(out uint deviceCount);
            for (uint d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out IMMDevice device) != 0) continue;

                Guid iid = IID_IAudioSessionManager2;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object mgr2Obj) != 0)
                    continue;

                var mgr2 = (IAudioSessionManager2)mgr2Obj;
                if (mgr2.GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum) != 0)
                    continue;

                sessionEnum.GetCount(out int sessionCount);
                for (int si = 0; si < sessionCount; si++)
                {
                    object? sessionObj = null;
                    try
                    {
                        if (sessionEnum.GetSession(si, out sessionObj) != 0) continue;
                        var sc2 = (IAudioSessionControl2)sessionObj;
                        if (sc2.GetState(out int state) == 0 && state == 1 /* Active */)
                        {
                            if (sc2.GetProcessId(out uint pid) == 0 && pid > 4)
                                pids.Add((int)pid);
                        }
                    }
                    catch { /* session QI failed, skip */ }
                    finally
                    {
                        if (sessionObj != null)
                            try { Marshal.ReleaseComObject(sessionObj); } catch { }
                    }
                }

                try { Marshal.ReleaseComObject(mgr2Obj); } catch { }
            }
        }
        catch { /* COM unavailable — return empty set */ }
        return pids;
    }

    // ── Efficiency Mode (EcoQoS) ───────────────────────────────────────────────

    private static void SetEfficiencyMode(IntPtr handle, bool enable)
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version     = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask   = enable ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
            };

            int size = Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(state, ptr, false);
                SetProcessInformation(handle,
                    PROCESS_INFORMATION_CLASS.ProcessPowerThrottling, ptr, (uint)size);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { }
    }

    // ── GPU Priority ───────────────────────────────────────────────────────────

    private static void SetGpuPriority(IntPtr handle, KMTSCHEDULINGPRIORITYCLASS cls)
    {
        try { D3DKMTSetProcessSchedulingPriorityClass(handle, cls); }
        catch { /* GPU scheduling not available or process has no GPU context */ }
    }

    // ── I/O Priority ───────────────────────────────────────────────────────────

    private static void SetIoPriorityLevel(IntPtr handle, int level)
    {
        try { NtSetInformationProcess(handle, PROCESS_IO_PRIORITY_CLASS, ref level, sizeof(int)); }
        catch { }
    }

    // ── Memory Priority ────────────────────────────────────────────────────────

    private static void SetMemoryPriority(IntPtr handle, uint priority)
    {
        try
        {
            var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
            int size = Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetProcessInformation(handle,
                    PROCESS_INFORMATION_CLASS.ProcessMemoryPriority, ptr, (uint)size);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { }
    }

    /// <summary>
    /// Aggressively reclaims the process's physical RAM: first removes the working-set
    /// floor (SetProcessWorkingSetSize -1/-1 lets the OS trim to zero), then flushes
    /// remaining pages to the standby list (EmptyWorkingSet). Combined, this returns
    /// substantially more RAM to the OS than either call alone.
    /// </summary>
    private static void TrimProcessWorkingSet(IntPtr handle)
    {
        try
        {
            // Remove soft min/max limits so the OS can trim to zero pages
            SetProcessWorkingSetSize(handle, (IntPtr)(-1), (IntPtr)(-1));
            // Immediately flush remaining pages to the standby list
            EmptyWorkingSet(handle);
        }
        catch { /* not critical — working set trim is best-effort */ }
    }

    // ── Priority parsing helpers ───────────────────────────────────────────────

    private static uint ParseCpuPriorityClass(string? s) => s switch
    {
        "Idle"         => 0x00000040,
        "Below Normal" => 0x00004000,
        "Normal"       => 0x00000020,
        "Above Normal" => 0x00008000,
        "High"         => 0x00000080,
        _              => 0x00000040,
    };

    private static KMTSCHEDULINGPRIORITYCLASS ParseGpuPriorityClass(string? s) => s switch
    {
        "Normal" => KMTSCHEDULINGPRIORITYCLASS.Normal,
        _        => KMTSCHEDULINGPRIORITYCLASS.Idle,
    };

    private static int ParseIoPriority(string? s) => s switch
    {
        "Very Low" => 0,
        "Low"      => 1,
        "Normal"   => 2,
        _          => 0,
    };

    // ── E-core Detection & Affinity ────────────────────────────────────────────

    private UIntPtr GetOrDetectECoreMask(bool detect)
    {
        if (!detect) return UIntPtr.Zero;

        if (!_eCoresDetected)
        {
            _eCoresDetected = true;
            long rawMask = BuildECoreMask();
            _hasECores = rawMask != 0;
            _eCoreMask = (UIntPtr)(ulong)rawMask;

            if (_hasECores)
                _log.Info("TaskSleepService", $"E-cores detected, affinity mask: 0x{rawMask:X}");
            else
                _log.Info("TaskSleepService", "No E-cores detected on this CPU.");
        }

        return _hasECores ? _eCoreMask : UIntPtr.Zero;
    }

    private long BuildECoreMask()
    {
        try
        {
            int totalLogical = Environment.ProcessorCount;
            if (totalLogical <= 1) return 0;

            int pCoreLogicalCount = 0;
            int eCoreLogicalCount = 0;

            using var cpuSearcher = new ManagementObjectSearcher(
                "SELECT NumberOfCores, NumberOfLogicalProcessors, NumberOfEfficiencyClasses " +
                "FROM Win32_Processor");

            foreach (ManagementObject cpu in cpuSearcher.Get())
            {
                uint effClasses = Convert.ToUInt32(cpu["NumberOfEfficiencyClasses"] ?? 1u);
                uint logicals   = Convert.ToUInt32(cpu["NumberOfLogicalProcessors"] ?? (uint)totalLogical);
                uint cores      = Convert.ToUInt32(cpu["NumberOfCores"] ?? logicals);

                if (effClasses < 2)
                {
                    pCoreLogicalCount += (int)logicals;
                    continue;
                }

                bool hyperthreading = logicals > cores;
                int  pLogicals      = hyperthreading ? (int)(cores * 2) : (int)cores;
                int  eLogicals      = (int)logicals - pLogicals;

                pCoreLogicalCount += pLogicals;
                eCoreLogicalCount += eLogicals;
            }

            if (eCoreLogicalCount <= 0) return 0;

            long mask = 0;
            for (int i = pCoreLogicalCount; i < pCoreLogicalCount + eCoreLogicalCount; i++)
            {
                if (i >= 64) break;
                mask |= (1L << i);
            }
            return mask;
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"BuildECoreMask failed: {ex.Message}");
            return 0;
        }
    }

    // ── Monitoring helpers ─────────────────────────────────────────────────────

    private void AddEvent(string name, int pid, string action, string detail = "")
    {
        _eventLog.Enqueue(new MonitorEvent(DateTime.Now, name, pid, action, detail));
        while (_eventLog.Count > MaxEvents) _eventLog.TryDequeue(out _);

        // Mirror significant events to the global activity log so they show in the log viewer.
        // Skip noisy per-tick "Brief Wake" heartbeats to keep the log readable.
        if (!string.IsNullOrEmpty(action) && action != "Brief Wake" && action != "Tray Wake")
            _log.Info("TaskSleepService", $"{action}: {name} (PID {pid}){(string.IsNullOrEmpty(detail) ? "" : $" — {detail}")}");
    }

    private void BuildAndPublishSnapshot(double sysCpu, HashSet<int> protectedPids, TaskSleepSettings s, long freeRamMb, bool ramPressure)
    {
        try
        {
            var now = DateTime.UtcNow;
            var throttledKeys = _throttledPids.Keys.ToHashSet();

            // Always include all throttled processes.
            // If "all processes" would be desired, include top 15 CPU consumers too.
            var pids = new HashSet<int>(throttledKeys);
            foreach (var kv in _lastCpuPercent.OrderByDescending(kv => kv.Value).Take(15))
                pids.Add(kv.Key);

            // Also include processes that are in the 30-second grace period (about to be napped)
            foreach (int gPid in _minimizeGraceSince.Keys) pids.Add(gPid);
            foreach (int gPid in _trayGraceSince.Keys)     pids.Add(gPid);

            var snapshots = new List<ProcessSnapshot>(pids.Count);
            foreach (int pid in pids)
            {
                bool isThrottled = throttledKeys.Contains(pid);
                bool isProtected = protectedPids.Contains(pid);
                bool isPending   = !isThrottled && (_minimizeGraceSince.ContainsKey(pid) || _trayGraceSince.ContainsKey(pid));
                _lastCpuPercent.TryGetValue(pid, out double cpu);
                _processNames.TryGetValue(pid, out string? name);
                bool onECores = _originalAffinities.ContainsKey(pid);
                _throttledAt.TryGetValue(pid, out DateTime ta);

                // Compute grace countdown label (seconds remaining before nap)
                string pendingLabel = "";
                if (isPending)
                {
                    _minimizeGraceSince.TryGetValue(pid, out DateTime mgs);
                    _trayGraceSince.TryGetValue(pid, out DateTime tgs);
                    DateTime earliest = mgs == default ? tgs : (tgs == default ? mgs : (mgs < tgs ? mgs : tgs));
                    double elapsedMs  = (now - earliest).TotalMilliseconds;
                    double remSec     = Math.Max(0, (MinimizeTrayGraceMs - elapsedMs) / 1000.0);
                    pendingLabel = $"~{(int)Math.Ceiling(remSec)}s";
                }

                string statusLabel = isThrottled ? "Napping"
                                   : isProtected ? "Active"
                                   : isPending   ? "Pending"
                                   : "";

                snapshots.Add(new ProcessSnapshot
                {
                    Pid          = pid,
                    Name         = name ?? $"PID {pid}",
                    CpuPercent   = cpu,
                    IsThrottled  = isThrottled,
                    IsProtected  = isProtected,
                    IsPendingNap = isPending,
                    StatusLabel  = statusLabel,
                    CoreLabel    = onECores ? "E-cores" : "All Cores",
                    ThrottledFor = isThrottled && ta != default
                        ? FormatDuration((now - ta).TotalSeconds)
                        : isPending ? pendingLabel : "",
                });
            }

            // Sort: throttled first, then by CPU descending
            snapshots.Sort((a, b) =>
            {
                int tc = b.IsThrottled.CompareTo(a.IsThrottled);
                return tc != 0 ? tc : b.CpuPercent.CompareTo(a.CpuPercent);
            });

            var events = _eventLog.ToArray();
            var recentEvents = (IReadOnlyList<MonitorEvent>)
                (events.Length > 50 ? events[^50..] : events);

            _latestSnapshot = new MonitorSnapshot(
                sysCpu, throttledKeys.Count,
                freeRamMb, ramPressure,
                snapshots.AsReadOnly(), recentEvents);
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"BuildAndPublishSnapshot failed: {ex.Message}");
        }
    }

    private static string FormatDuration(double totalSeconds)
    {
        if (totalSeconds < 60) return $"{(int)totalSeconds}s";
        int m = (int)(totalSeconds / 60);
        int s = (int)(totalSeconds % 60);
        return $"{m}m {s}s";
    }

    private static long FtToLong(FILETIME ft) =>
        ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    private void Notify(string msg) => StatusChanged?.Invoke(msg);

    // ── RAM pressure helper ────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>Returns available (free) physical RAM in MB, or long.MaxValue on error.</summary>
    private static long GetAvailableRamMb()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref m) ? (long)(m.ullAvailPhys / 1024 / 1024) : long.MaxValue;
        }
        catch { return long.MaxValue; }
    }

    // ── P/Invoke declarations ──────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr hProcess,
        out FILETIME lpCreationTime, out FILETIME lpExitTime,
        out FILETIME lpKernelTime,   out FILETIME lpUserTime);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(
        uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(
        IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(
        IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        PROCESS_INFORMATION_CLASS processInformationClass,
        IntPtr processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(
        IntPtr hProcess,
        out UIntPtr lpProcessAffinityMask,
        out UIntPtr lpSystemAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessAffinityMask(
        IntPtr hProcess, UIntPtr dwProcessAffinityMask);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(
        IntPtr hProcess, KMTSCHEDULINGPRIORITYCLASS priorityClass);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(
        IntPtr hProcess, int processInformationClass,
        ref int processInformation, int processInformationLength);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess,
        IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    // I/O priority constants (NtSetInformationProcess, class 33)
    private const int PROCESS_IO_PRIORITY_CLASS = 33;
    private const int IO_PRIORITY_VERY_LOW       = 0;
    private const int IO_PRIORITY_NORMAL         = 2;

    // Memory priority constants (SetProcessInformation, class ProcessMemoryPriority)
    private const uint MEMORY_PRIORITY_VERY_LOW = 1;
    private const uint MEMORY_PRIORITY_NORMAL   = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public int  dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct PROCESSENTRY32
    {
        public uint    dwSize;
        public uint    cntUsage;
        public uint    th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint    th32ModuleID;
        public uint    cntThreads;
        public uint    th32ParentProcessID;
        public int     pcPriClassBase;
        public uint    dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string  szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private enum PROCESS_INFORMATION_CLASS
    {
        ProcessMemoryPriority       = 0,
        ProcessMemoryExhaustionInfo = 1,
        ProcessAppMemoryInfo        = 2,
        ProcessInJobMemoryInfo      = 3,
        ProcessPowerThrottling      = 4,
    }

    private enum KMTSCHEDULINGPRIORITYCLASS
    {
        Idle        = 0,
        BelowNormal = 1,
        Normal      = 2,
        AboveNormal = 3,
        High        = 4,
        Realtime    = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    // ── Windows Core Audio COM interfaces (minimal vtable-accurate declarations) ─

    // CLSID_MMDeviceEnumerator
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ClassInterface(ClassInterfaceType.None)]
    private class MMDeviceEnumeratorCoClass { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask,
            out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role,
            out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId,
            out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx,
            IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, IntPtr ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr AudioSessionGuid, uint StreamFlags,
            [MarshalAs(UnmanagedType.IUnknown)] out object SessionControl);
        [PreserveSig] int SimpleAudioVolume(IntPtr AudioSessionGuid, uint StreamFlags,
            [MarshalAs(UnmanagedType.IUnknown)] out object AudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionList);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionCount,
            [MarshalAs(UnmanagedType.IUnknown)] out object Session);
    }

    // Flat vtable layout: IAudioSessionControl slots (1–9) then IAudioSessionControl2 (10–14)
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value,
            IntPtr EventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value,
            IntPtr EventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid Override, IntPtr EventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int GetSessionIdentifier(
            [MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier(
            [MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    private static readonly Guid IID_IAudioSessionManager2 =
        new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private const uint CLSCTX_ALL = 0x17;
}
