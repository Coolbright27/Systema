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
using Systema.Services.TaskSleep;

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
public sealed partial class TaskSleepService : IDisposable
{
    private static readonly LoggerService _log = LoggerService.Instance;

    private Thread?           _monitorThread;
    private volatile bool     _running;
    private TaskSleepSettings _settings;
    private Dictionary<string, TaskSleepAppRule> _appRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly object   _settingsLock = new();

    // ── Dynamically detected AV process names (populated at Start() via SecurityCenter2) ──
    // These are always protected regardless of user settings — they supplement the static list.
    private volatile HashSet<string> _detectedAvProcessNames = new(StringComparer.OrdinalIgnoreCase);

    // ── Per-process state (monitor-thread only) ───────────────────────────────

    // pid -> original priority class before we lowered it
    private readonly ConcurrentDictionary<int, uint> _throttledPids = new();

    // ── Unified per-PID state (replaces ~28 parallel dictionaries; see ProcessState.cs) ──
    // ConcurrentDictionary so the snapshot/diagnostic path can read it while the monitor
    // thread mutates records. Records themselves are touched only on the monitor thread.
    private readonly ConcurrentDictionary<int, ProcessState> _state = new();
    /// <summary>Get (or create) the state record for a PID — use for WRITES.</summary>
    private ProcessState StateFor(int pid) => _state.GetOrAdd(pid, static _ => new ProcessState());
    /// <summary>Try to read an existing record without creating one — use for READS.</summary>
    private bool TryState(int pid, out ProcessState st) => _state.TryGetValue(pid, out st!);
    /// <summary>Forget a PID entirely — the single cleanup path (replaces ~28 Remove calls).</summary>
    private void DropState(int pid) => _state.TryRemove(pid, out _);

    // (ProcessState.ThrottledAt — when this process was throttled)

    // (ProcessState.OverThresholdSince — when this process first exceeded the CPU start threshold)

    // Single-call kernel sampler — owns its own pid → (CreateTime, TotalCpu, WallTicks)
    // baseline state. Replaces the v1.7.30 Parallel.ForEach + OpenProcess-per-PID path
    // (which cost ~200-900 ms per tick on a busy system).
    private readonly NtProcessSampler _ntSampler = new();

    // pid -> last computed CPU percentage (monitor-thread only)
    // _lastCpuPercent → ProcessState.LastCpuPercent

    // pid -> process display name (monitor-thread only)
    private readonly Dictionary<int, string> _processNames = new();

    // pid -> CreationTime in 100-ns ticks since 1601 (Windows FILETIME units).
    // Acts as the ProcessKey identity half: any dictionary keyed purely by PID can
    // be validated by comparing this value to the latest sampler output. If the PID
    // has been reused by a new process, the creation times differ and the stale
    // throttle / cap state is dropped.
    private readonly Dictionary<int, long> _pidCreationTimes = new();

    // Change signature of the last napped set written to the crash-recovery NapJournal.
    // Lets Tick skip rewriting the journal file when the napped set hasn't changed.
    private int _lastJournalSig = -1;

    // Systema's OWN process id. Cached once at load. The name-based exclusion ("Systema" in
    // the protected list) can miss if ProcessName ever resolves oddly, so every throttle / nap /
    // CPU-cap entry point also checks this PID directly — a pid comparison can never misfire, so
    // Systema can never throttle, nap, or cap itself regardless of name resolution.
    private static readonly int OwnPid = Environment.ProcessId;

    // pid -> original affinity mask (saved before we pin to E-cores)
    private readonly ConcurrentDictionary<int, UIntPtr> _originalAffinities = new();

    // Original D3DKMT GPU scheduling priority of a napped process, saved before we
    // lower it to Idle so it can be restored exactly on wake. See LowerNapGpuPriority.
    private readonly ConcurrentDictionary<int, int> _originalGpuPriority = new();

    // GPU priority lowering for napped apps is gated to Windows 11+. On older builds
    // touching D3DKMT process scheduling priority could disturb the DWM present queue
    // (the historical VSync/tearing problem); Win11 fixed that ordering, so we only
    // do it there. Lowering (vs. the old Launch-Boost raise) also gives the GPU to the
    // foreground app — the safe direction — and is fully restored on wake.
    private static readonly bool GpuNapLoweringSupported =
        Environment.OSVersion.Version.Build >= 22000;

    // ── Nap category state (single source of truth) ───────────────────────────
    // Which nap CATEGORY each napped PID is in (Minimized / Tray / Background /
    // Idle). Replaces four parallel HashSets — see NapBuckets.cs for rationale.
    private readonly NapBuckets _napBuckets = new();

    // ── Minimize Nap state (monitor-thread only) ──────────────────────────────

    // When the next brief idle wake is allowed for each minimize-napped PID
    private readonly Dictionary<int, DateTime> _nextBriefWakeAt  = new();
    // If in a brief wake, when to re-throttle (key absent = not in brief-wake)
    private readonly Dictionary<int, DateTime> _briefWakeEndAt   = new();
    // Cached set of PIDs with active audio sessions
    private HashSet<int> _cachedAudioPids    = new();
    private DateTime     _lastAudioCacheTime = DateTime.MinValue;
    private const double AudioCacheSeconds   = 2.0;  // refreshed every 2s — fast enough to catch audio before minimize-nap
    // Audio stickiness: remembers when each PID last had an Active audio session.
    // Protects against the chicken-and-egg problem where throttling a process causes
    // its audio to go Inactive, making us think it's safe to keep napping.
    // _lastAudioActiveAt → ProcessState.LastAudioActiveAt
    private const double AudioStickySeconds = 30.0; // protect for 30s after last detected audio

    // ── Tray Nap state (monitor-thread only) ──────────────────────────────────

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

    // ── Access denied backoff ─────────────────────────────────────────────────
    // pid → (consecutive denied count, last denial time). If count >= 3 and < 60 s
    // since last denial, ShouldSkip returns true so we stop hammering protected procs.
    // _accessDeniedPids → ProcessState.AccessDenied

    // ── CPU savings tracking ───────────────────────────────────────────────────
    // (ProcessState.CpuAtThrottle — CPU% at the moment it was throttled, for CpuFreedPercent)

    // ── Child process nap state ────────────────────────────────────────────────
    // (ProcessState.NapChildParent — parent PID a process was napped under; null = not a
    //  nap-child. Replaces both the _napChildPids set and the _parentOfNapChild map.)

    // ── Deep sleep escalation ─────────────────────────────────────────────────
    // pid → DateTime when the process first entered minimize-nap or tray-nap.
    // (ProcessState.NapSince — when the process entered nap; deep-sleep escalation +
    //  brief-wake fairness sort key)

    // ── Log batching: coalesce repeated (name, action) pairs within a tick ────
    // Prevents log spam when many child processes of the same app are napped at once.
    private readonly Dictionary<(string name, string action), int> _logBatchCounts = new();

    // ── Elevated/system process integrity level cache ─────────────────────────
    // pid → true if the process runs at High or System integrity (elevated/admin).
    // Cached per-PID because integrity level never changes during a process lifetime.
    // Cleaned up in CleanupDeadProcesses when the PID exits.
    // _elevatedPidCache → ProcessState.ElevatedCache

    // Cached per-PID: true when the process runs as NT AUTHORITY\SYSTEM, LOCAL SERVICE,
    // or NETWORK SERVICE (the well-known service accounts that must never be napped).
    // _serviceAccountCache → ProcessState.ServiceAccountCache

    // ── Auto-detected critical services (populated at startup via WMI scan) ────
    // Supplements StaticSystemProcessNames to catch critical services that appear
    // in OS updates or are otherwise not in the static list.
    private volatile HashSet<string> _detectedCriticalServices = new(StringComparer.OrdinalIgnoreCase);

    // ── CPU cap via Job Objects ───────────────────────────────────────────────
    // pid → Job Object handle. When a process is napped and CPU cap is enabled,
    // a job with a hard CPU rate limit is created and the process assigned to it.
    // Closing the job handle on restore releases the cap.
    // If job assignment fails (e.g. Chromium/browser sandbox procs already in a
    // non-nestable job), the cap is simply skipped for that process — priority,
    // EcoQoS, affinity, and I/O/Memory priority throttling still apply and are
    // sufficient. The v1.7.30 NtSuspendProcess duty-cycle fallback was removed
    // in v1.7.31 because it could hang windowed GPU/COM workloads.
    private readonly Dictionary<int, IntPtr> _cpuCapJobs = new();

    // ── Re-enforce tracking ───────────────────────────────────────────────────
    // Counts how many times the re-enforce step had to push each process back.
    // If it happens 3+ times in 60 s the process is restored and skipped permanently.
    private readonly ReEnforceCounter _reEnforceCounter = new();

    // Process names that have been permanently skipped for this session after
    // hitting the re-enforce threshold. Persisted to the user whitelist via event.
    private readonly HashSet<string> _napSuppressed = new(StringComparer.OrdinalIgnoreCase);

    // ── WASAPI exclusive mode ─────────────────────────────────────────────────
    // Set to UtcNow when exclusive audio mode is detected; cleared after 15 s idle.
    private DateTime _exclusiveModeDetectedAt = DateTime.MinValue;

    // ── E-core detection (lazy, cached) ──────────────────────────────────────
    private bool    _eCoresDetected;
    private bool    _hasECores;
    private UIntPtr _eCoreMask;

    // ── P-core detection (lazy, cached) — used to pin Systema ITSELF to the fast cores ──
    private bool    _pCoresDetected;
    private UIntPtr _pCoreMask;   // P-cores only; Zero on a homogeneous CPU (leave affinity alone)

    // ── System CPU state ──────────────────────────────────────────────────────
    private long     _prevSysIdle;
    private long     _prevSysTotal;
    private DateTime _prevSysSample;
    private double   _lastSystemCpuPercent;
    private bool     _systemTimesWarned;

    // ── Background nap: tracks when each process was last in the foreground ──
    // (ProcessState.LastForegroundAt — last time this PID was in the protected set)

    // ── Idle nap: tracks consecutive low-CPU duration ────────────────────────
    // (ProcessState.IdleSince — when process first dropped below idle threshold)

    // ── Skip reason tracking for monitor UI (#24) ────────────────────────────
    // pid → human-readable reason why this process wasn't napped this tick
    // _skipReasons → ProcessState.SkipReason

    // ── Monitoring ────────────────────────────────────────────────────────────
    private readonly ConcurrentQueue<MonitorEvent> _eventLog = new();
    private const    int MaxEvents = 200;
    private volatile MonitorSnapshot? _latestSnapshot;

    public MonitorSnapshot? GetLatestSnapshot() => _latestSnapshot;

    public event Action<string>? StatusChanged;

    /// <summary>
    /// Fired when a process is permanently whitelisted due to repeated priority fight-back.
    /// Argument is the process name. Subscribe in the ViewModel to persist it to the user whitelist.
    /// </summary>
    public event Action<string>? ProcessAutoWhitelisted;

    // ── Manual wake requests (UI → monitor thread, thread-safe) ──────────────
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _wakeRequests = new();

    /// <summary>
    /// Signals the monitor thread to immediately restore and stop napping the named process.
    /// Takes effect on the very next tick (~1 second). Thread-safe.
    /// </summary>
    public void WakeProcess(string processName)
        => _wakeRequests.Enqueue(processName.ToLowerInvariant());

    // ── Security / AV processes — ALWAYS protected, regardless of any user setting ──
    // These are checked unconditionally in ShouldSkip AND in the re-enforce step.
    // Add process names here when an AV vendor's processes are found to be touched.
    // WMI SecurityCenter2 detection also supplements this list at runtime.
    private static readonly HashSet<string> SecurityCriticalProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Windows Defender / Security ───────────────────────────────────────
        "MsMpEng",                // Defender antivirus engine — real-time scanning
        "NisSrv",                 // Network Inspection Service — network threat detection
        "MpCmdRun",               // Defender command-line scanner
        "MpDefenderCoreService",  // Defender core service (Windows 11+)
        "SecurityHealthService",  // Windows Security health monitoring
        "SecurityHealthSystray",  // Windows Security tray icon
        "SgrmBroker",             // System Guard Runtime Monitor — firmware/boot integrity
        "SecHealthUI",            // Windows Security app UI
        // ── Bitdefender ──────────────────────────────────────────────────────
        "bdagent",        // Bitdefender main agent
        "bdservicehost",  // Bitdefender service host
        "bdntwrk",        // Bitdefender network filter
        "bdredline",      // Bitdefender real-time scanning
        "vsserv",         // Bitdefender virus shield service
        "vsservppl",      // Bitdefender protected process light
        "bdwtxag",        // Bitdefender web traffic agent
        "bdupdater",      // Bitdefender updater
        "bdmcon",         // Bitdefender management console
        "BDVpnService",   // Bitdefender VPN service
        "BDVpnHelper",    // Bitdefender VPN helper
        "bdqdiag",        // Bitdefender diagnostics
        "bdagentopt",     // Bitdefender optimizer agent
        "ProductAgentService", // Bitdefender product agent
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
        // ── Windows SmartScreen — file/URL reputation checking ──────────
        "smartscreen",  // SmartScreen filter — throttling breaks file verification
    };

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
        // Auth / logon / shutdown — must never be starved or the PC appears frozen / hangs
        "LockApp", "LogonUI", "wininit", "shutdown",
        // UWP infrastructure — WinStore.App runs inside ApplicationFrameHost;
        // throttling these causes Store downloads to stall and UWP apps to hang
        "WinStore.App", "Microsoft.WindowsStore",
        // Windows Update orchestration — throttling breaks Windows Update COM registration
        // and coordination between update services, causing 0x80004002 COM interface errors
        "wuauclt",                // Windows Update Auto Update client
        "musNotification",        // MU notification UI (tray)
        "musNotificationUx",      // MU notification (newer UX)
        "WaaSMedicAgent",         // Windows as a Service health diagnostics
        "WaaSMedicSvc",           // WaaS Medic Service — monitors update health
        "UsoSvc",                 // Update Orchestrator Service — main Windows Update coordinator
        "UsoClient",              // UOS client (moved from AggressiveNapTargets to protected)
        "WuauserV1",              // WSUS update handler variant
        "svchost",                // (already protected in core OS list above)
        // Diagnostics / perf tools — throttling Task Manager while troubleshooting is confusing
        "Taskmgr", "PerfHost",
        // ── Anti-cheat services — throttling these causes game kicks or bans ────
        "vgc",              // Valorant Vanguard anti-cheat
        "vgtray",           // Vanguard tray
        "EasyAntiCheat",    // Epic / most popular EAC titles
        "EasyAntiCheat_EOS",// EAC with Epic Online Services
        "BEService",        // BattlEye service (PUBG, Rainbow Six, etc.)
        "BEService_x64",    // BattlEye 64-bit variant
        "ngs2",             // nProtect GameGuard service
        "GameGuard",        // nProtect GameGuard
        "xhunter1",         // XIGNCODE3 anti-cheat
        "PnkBstrA",         // PunkBuster agent
        "PnkBstrB",         // PunkBuster background service
        "EQU8",             // EQU8 anti-cheat
        "mhyprot2",         // MiHoYo anti-cheat (Genshin / Honkai)
        // ── GPU driver processes — throttling causes frame drops / display glitches ──
        "NVDisplay.Container",  // NVIDIA display driver container
        "nvcontainer",          // NVIDIA component container (multiple instances)
        "nvWmi64",              // NVIDIA WMI provider (driver telemetry bridge)
        "nvsphelper64",         // NVIDIA ShadowPlay helper
        "nvsphelper32",         // NVIDIA ShadowPlay helper (32-bit)
        "NvBackend",            // NVIDIA GeForce Experience backend
        "NvContainerLocalSystem", // NVIDIA system-level container
        "igfxEM",               // Intel HD Graphics event monitor
        "igfxHK",               // Intel HD Graphics hotkey service
        "igfxTray",             // Intel HD Graphics tray icon
        "GfxUI",                // Intel Graphics UI
        "atieclxx",             // AMD External Events Client — GPU event handler
        "atiesrxx",             // AMD External Events Server
        "RadeonSoftware",       // AMD Radeon Software overlay
        "RadeonsoftwareSlimService", // AMD Radeon slim service
        "AMDRSServ",                // AMD Radeon Software service helper
        "AMDRSSrcExt",              // AMD Radeon Software source extension
        "amdfendr",                 // AMD Crash Defender (anti-cheat companion)
        "NvTelemetryContainer",     // NVIDIA telemetry container (driver component)
        "NvNodeLauncher",           // NVIDIA node.js launcher (GFE component)
        "GameBarPresenceWriter",    // Xbox Game Bar presence writer (overlay infra)
        "GameBarFTServer",          // Xbox Game Bar frame target server
        "XboxGameBarWidgets",       // Xbox Game Bar widgets host
        "WinRing0_1_2_0",           // WinRing0 driver helper (HWiNFO, RTSS, etc.)
        "RTSS",                     // RivaTuner Statistics Server (frame limiter/OSD)
        "RTSSHooksLoader64",        // RTSS hooks loader
        "EncoderServer64",          // NVIDIA NVENC encoder server (ShadowPlay)
        // ── Audio driver processes — throttling causes crackle / latency spikes ──
        "RtkAudUService64",     // Realtek HD Audio UAD service (64-bit)
        "RtkAudUService32",     // Realtek HD Audio UAD service (32-bit)
        "RtkNGUI64",            // Realtek audio control panel
        "RAVCpl64",             // Realtek audio manager
        "RAVBg64",              // Realtek audio background helper
        "WavesSvc64",           // Waves Audio MaxxAudio service (common on Dell/HP)
        "WavesSvc",             // Waves Audio service (32-bit)
        "WavesAPO64Service",    // Waves APO audio processing service
        "audiodg",              // Windows audio device graph (already listed, guard)
        // ── Intel platform services — driver/firmware services that must stay responsive ──
        "IntelCpHDCPSvc",           // Intel Content Protection HDCP service
        "IntelCpHeciSvc",           // Intel ME Host Embedded Controller Interface
        "IntelAudioService",        // Intel Audio service
        "igfxCUIService",           // Intel Graphics Command Center service
        "OneApp.IGCC.WinService",   // Intel Graphics Command Center (new)
        "IGCC",                     // Intel Graphics Command Center UI
        "esif_uf",                  // Intel Dynamic Tuning (DPTF) framework
        "LMS",                      // Intel Local Manageability Service
        "jhi_service",              // Intel DAL (Dynamic Application Loader) host
        // ── Thunderbolt / connectivity ──
        "ThunderboltService",       // Intel Thunderbolt controller service
        "TbtP2pShortcutService",    // Thunderbolt peer-to-peer
        "wlanext",                  // WLAN extensibility framework (WiFi driver)
        // ── Hyper-V / virtualisation — throttling breaks WSL2, Docker, etc. ──
        "vmms",                     // Hyper-V VM Management Service
        "vmcompute",                // Hyper-V Host Compute Service
        "vmmemCmZygote",            // Hyper-V memory manager helper
        "vmwp",                     // Hyper-V VM Worker Process (one per VM — runs the actual VM)
        "vmconnect",                // Hyper-V VM Connect (remote desktop to VM)
        "vmware-vmx",               // VMware Workstation VM process
        "VBoxHeadless",             // VirtualBox headless VM process
        "VBoxSVC",                  // VirtualBox service
        // ── Dell / OEM hardware services ──
        "DellFFDPWmiService",       // Dell Foundation Device Platform
        "RstMwService",             // Intel Rapid Storage Technology
        // ── Audio infrastructure ──
        "WavesSysSvc64",            // Waves MaxxAudio system service
        "MidiSrv",                  // Windows MIDI service
        // ── Edge / browser infrastructure (elevation service) ──
        "elevation_service",        // Chromium elevation service (msedge/chrome)
        // ── Credential / auth services ──
        "CredentialEnrollmentManager", // Windows credential enrollment
        "NgcIso",                   // Windows Hello NGC isolation
        // ── System settings / UWP infrastructure ──
        "SystemSettingsBroker",     // Settings app broker
        "backgroundTaskHost",       // UWP background task host — throttling breaks notifications
        "UserOOBEBroker",           // Out-of-box experience broker
        // ── Gaming services — throttling breaks Xbox/Game Pass installs ──
        "gamingservicesnet",        // Xbox Gaming Services network
        "GameInputRedistService",   // Game Input redistributable
        "xgamehelper",              // Xbox game helper
        // ── Other system services ──
        "ProcessGovernor",          // System process governor
        "srvstub",                  // Windows service stub
        "aesm_service",             // Intel SGX Application Enclave service
        "logi_lamparray_service",   // Logitech lighting service
        "WMIRegistrationService",   // WMI registration
        // ── Input / pointing devices (mouse, touchpad, keyboard) ─────────────────
        //   A process that installs a GLOBAL low-level input hook (WH_MOUSE_LL /
        //   WH_KEYBOARD_LL) sits in the path of EVERY mouse and keyboard event. If it
        //   gets napped (Idle priority + EcoQoS + ~1% CPU cap), Windows waits on the
        //   starved hook for each event, so the WHOLE system's cursor and typing lag
        //   badly. These run in the user session at medium integrity, so the
        //   system-integrity / service-account guards don't catch them — exclude by name.
        "PowerToys",                  // PowerToys runner — owns global mouse/keyboard hooks
        "PowerToys.PowerLauncher",    // PowerToys Run
        "PowerToys.FancyZones",       // window snapping (mouse-drag hook)
        "PowerToys.KeyboardManagerEngine", // remaps every keystroke
        "PowerToys.MouseWithoutBorders",
        "PowerToys.PowerDisplay",
        "PowerToys.Peek.UI",
        "PowerToys.ColorPickerUI",
        "PowerToys.Awake",
        "SynTPEnh", "SynTPHelper",    // Synaptics touchpad
        "ETDCtrl", "ETDCtrlHelper", "ETDService", // ELAN touchpad
        "Apoint", "ApMsgFwd", "ApntEx",           // Alps pointing device
        "lghub", "lghub_agent",                   // Logitech G HUB (mice / keyboards)
        "LogiOptions", "LogiOptionsMgr", "logioptionsplus_agent", "LCore",
        "iCUE", "CorsairService",     // Corsair input devices
        "SteelSeriesGG",              // SteelSeries input
        // ── Display / GPU user-session helpers — throttling stutters cursor/frames ──
        "igfxEMN",                    // Intel Graphics event monitor (was being napped)
        "igfxext", "igfxsrvc", "igfxpers", "igfxCUIServiceN",
        "DSATray",                    // Intel Driver & Support Assistant tray
        "dptf_helper",                // Intel DPTF helper (platform power, incl. display)
        "esrv", "esrv_svc",           // Intel Energy Server (power / thermal telemetry)
        // ── Audio hardware service (owns the active audio device) ──
        "TISmartAmpService",          // Texas Instruments SmartAmp speaker amplifier
        // ── Windows shell / cross-device UI infrastructure ──
        "ShellHost",                  // Windows Shell host
        "CrossDeviceResume",          // Windows cross-device handoff
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
        "DiagTrack", "wsqmcons", "compattelrunner",
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
        // Nvidia / AMD background workers (NvBackend, NvContainerLocalSystem already in SystemProcessNames)
        "RzSynapse",  // Razer Synapse background
        // Microsoft Store — removed: UWP app hosted inside ApplicationFrameHost,
        // foreground PID is AFH not WinStore.App, so it gets falsely napped.
        // MS Store downloads also need unthrottled CPU to work properly.
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

    // ── Processes that must never be minimize/tray-napped ──────────────────────
    // These are ALWAYS treated as having active audio regardless of Core Audio.
    // Only truly always-active apps go here (media players, screen recorders).
    private static readonly HashSet<string> AlwaysActiveProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Screen recorders / streaming — actively capturing (always protected)
        "obs64", "obs32", "obs", "StreamlabsOBS", "Streamlabs OBS",
        "nvsphelper64", "nvsphelper32", // NVIDIA ShadowPlay helpers
    };

    // ── Audio-capable apps: browsers and apps that use child processes for audio ─
    // These apps play audio through a child/renderer process, not the main process.
    // When any process in their app family has an active audio session, ALL family
    // members are protected. When idle (no audio), they're eligible for napping.
    private static readonly HashSet<string> AudioCapableAppNames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers — audio plays in content/renderer process, not main process
        "firefox", "chrome", "msedge", "brave", "opera", "vivaldi",
        "Waterfox", "LibreWolf", "Tor Browser", "chromium",
        // Electron apps (audio in renderer process)
        "Slack", "Discord", "Teams", "ms-teams",
        // Media players — only protected when Core Audio reports active session
        "Spotify", "SpotifyWebHelper", "vlc", "wmplayer", "groove",
        "foobar2000", "AIMP", "MusicBee", "winamp", "mpc-hc", "mpc-hc64",
        "mpc-be", "mpc-be64", "PotPlayerMini", "PotPlayerMini64", "mpv", "mpv.net",
        "iTunes", "AppleMusic",
    };

    // ── Communication apps: only protected when they have an active audio session ─
    // These are NOT in AlwaysActiveProcessNames — they ARE nappable when idle.
    // When Core Audio reports an active audio session for any of these, they get
    // the same protection as AlwaysActive. When idle (no call/stream), they nap.
    private static readonly HashSet<string> CommsProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "Teams", "ms-teams", "Zoom", "ZoomIt", "Discord", "Slack",
        "skype", "skypehost", "skypebridge",
        "WebexHost", "WebexApp", "Cisco_Spark", "RingCentral",
        "EpicGamesLauncher", // only protect when actively playing audio
    };

    // ── Priority class constants ───────────────────────────────────────────────
    private const uint IDLE_PRIORITY_CLASS   = 0x00000040;
    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    private const uint HIGH_PRIORITY_CLASS   = 0x00000080;   // Launch Boost — CPU High
    private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    private const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    private const uint REALTIME_PRIORITY_CLASS     = 0x00000100;
    private const int  IO_PRIORITY_LOW              = 1;
    private const int  IO_PRIORITY_HIGH             = 3;       // Launch Boost — I/O High

    // ── Process access rights ──────────────────────────────────────────────────
    private const uint PROCESS_TERMINATE                 = 0x0001;
    private const uint PROCESS_SET_INFORMATION           = 0x0200;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_SET_QUOTA                = 0x0100;

    // ── Efficiency Mode (EcoQoS) constants ────────────────────────────────────
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    // Tells Windows to IGNORE this process's timer-resolution request while it's napped.
    // EXECUTION_SPEED above controls how FAST a napped process runs; this controls how often
    // it WAKES THE CPU. Apps like browsers, Discord and Spotify call timeBeginPeriod(1), which
    // drags the CPU out of idle ~1000x/second even at 0% CPU — one of the biggest "warm and
    // draining while doing nothing" causes on a laptop, and invisible to every CPU-based signal
    // the nap engine acts on. Reducing wakeups lets the package reach deep C-states, which saves
    // more power than merely slowing a busy core down.
    private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    // Both levers are applied together on nap and cleared together on wake.
    private const uint NapPowerThrottlingMask =
        PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;

    // ── Constructor / Settings ─────────────────────────────────────────────────

    public TaskSleepService(TaskSleepSettings settings)
    {
        _settings = settings;
        // Pin Systema to Above-Normal (and P-cores only on hybrid CPUs) from launch, even if Task
        // Sleep starts disabled (Start() and the per-tick re-assert only run while the monitor runs).
        EnforceOwnPriorityAboveNormal();
        EnforceOwnPCoreAffinity();

        // If a previous Systema instance died unclean (crash / force-quit / power loss) while it had
        // processes napped, those processes are still throttled with no live record of them. Recover
        // them now — runs unconditionally (even if Task Sleep is currently disabled) so orphaned
        // throttles never linger across a restart.
        RecoverOrphanedNaps();
    }

    // Set by UpdateSettings when the max-concurrent-wakes cap is reduced.
    // Checked at the start of each Tick() so the monitor thread enforces the new cap safely.
    private volatile bool _enforceWakeCapOnNextTick;

    // Set by UpdateSettings when the CPU cap setting changes (enabled/disabled/percent).
    // Processed by the monitor thread during Tick() to avoid cross-thread access to
    // _cpuCapJobs (non-concurrent collection).
    private volatile bool _cpuCapSettingsChanged;
    private volatile int  _pendingCpuCapPercent;
    private volatile bool _pendingCpuCapEnabled;

    // Set of PIDs that have already had their working set trimmed for the
    // current deep-sleep cycle. Used by the "compress in deep sleep" feature so
    // we don't trim a process on every monitor tick — just once when it first
    // crosses the deep-sleep threshold, and again after each brief wake (the
    // brief-wake-start code removes the PID from this set, so the next deep-
    // sleep scan re-trims it). PIDs are cleared on full restore.
    // _deepSleepTrimmedPids → ProcessState.DeepSleepTrimmed

    public void UpdateSettings(TaskSleepSettings settings)
    {
        // ── Validate settings before applying ────────────────────────────────
        // MaxConcurrentBriefWakes must be at least 1 (0 would block all wakes permanently)
        settings.MaxConcurrentBriefWakes = Math.Clamp(settings.MaxConcurrentBriefWakes, 1, 10);
        // SystemCpuTriggerPercent can reach 0 via preset halving — clamp to sane minimum
        settings.SystemCpuTriggerPercent = Math.Max(settings.SystemCpuTriggerPercent, 2);
        // NappedCpuCapPercent must be 1–100
        settings.NappedCpuCapPercent = Math.Clamp(settings.NappedCpuCapPercent, 1, 100);

        bool oldCapEnabled;
        int  oldCapPercent;
        int  oldMaxWakes;
        lock (_settingsLock)
        {
            oldCapEnabled    = _settings.NappedCpuCapEnabled;
            oldCapPercent    = _settings.NappedCpuCapPercent;
            oldMaxWakes      = _settings.MaxConcurrentBriefWakes;
            _settings        = settings;
            _appRules        = settings.AppRules.ToDictionary(r => r.ProcessName, StringComparer.OrdinalIgnoreCase);
        }

        // If the cap was reduced, signal the monitor thread to enforce it on the very next tick.
        if (settings.MaxConcurrentBriefWakes < oldMaxWakes)
            _enforceWakeCapOnNextTick = true;

        // Arm / disarm Launch Boost to match the new settings.
        ApplyLaunchBoostState(settings);

        // ── Live-update CPU cap on already-throttled processes ────────────────
        // Signal the monitor thread to apply the change on the next Tick.
        // We must NOT touch _cpuCapJobs here because it is a non-concurrent
        // collection owned by the monitor thread.
        if (settings.NappedCpuCapPercent != oldCapPercent || settings.NappedCpuCapEnabled != oldCapEnabled)
        {
            _pendingCpuCapPercent = settings.NappedCpuCapPercent;
            _pendingCpuCapEnabled = settings.NappedCpuCapEnabled;
            _cpuCapSettingsChanged = true;
        }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_running) return;
        _running = true;

        // Pin Systema's own process priority to Above-Normal so the monitor + spawn/Launch-Boost
        // watchers get scheduled sooner and react faster to newly spawned / napping apps. On a
        // hybrid P/E CPU, also pin Systema to the P-cores only. Both are re-asserted every tick
        // (see Tick) so nothing can hold the priority down or move it off the P-cores.
        if (EnforceOwnPriorityAboveNormal())
            _log.Info("TaskSleepService", "Systema process priority pinned to Above-Normal");
        EnforceOwnPCoreAffinity();

        // Detect registered 3rd-party antivirus products so their processes are protected.
        // Run on background threads — WMI can be slow and we don't want to block startup.
        // These initialize critical protection lists.
        _ = System.Threading.Tasks.Task.Run(DetectRegisteredAntiviruses);
        _ = System.Threading.Tasks.Task.Run(ScanAndProtectCriticalServices);

        _monitorThread = new Thread(MonitorLoop, 8 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "TaskSleep-Monitor",
            Priority = ThreadPriority.BelowNormal
        };
        _monitorThread.Start();

        // Arm Launch Boost if it was enabled in the persisted settings.
        TaskSleepSettings startupSettings;
        lock (_settingsLock) { startupSettings = _settings; }
        ApplyLaunchBoostState(startupSettings);

        _log.Info("TaskSleepService", "Started");
        Notify("Task Sleep active — monitoring background processes.");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        StopLaunchBoost();
        _monitorThread?.Join(3000); // wait for monitor thread to exit cleanly
        _monitorThread = null;
        RestoreAll();
        _lastSystemCpuPercent = 0; // reset AdaptiveTick state so next Start() samples fresh
        _log.Info("TaskSleepService", "Stopped — all processes restored");
        Notify("Task Sleep is off.");
    }

    public void Dispose()
    {
        Stop();
        _ntSampler.Dispose();
    }

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
            // Duty-cycle PIDs stay suspended during Tick() for tight cap enforcement.
            // Priority lowering + EcoQoS are the main mechanism (App Nap style);
            // duty-cycle suspend/resume is the hard-cap enforcement for processes
            // that couldn't get a kernel Job Object cap.
            try   { Tick(); }
            catch (Exception ex) { _log.Error("TaskSleepService", "Tick failed", ex); }

            if (_running)
            {
                // Adaptive tick: when the system is idle and nothing is throttled there
                // is nothing to do — sleep longer to reduce the monitor's own overhead.
                // Event-driven fast paths (foreground / minimize / audio) drive restore
                // latency, so this tick is now primarily a reconciliation cadence.
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

    /// <summary>
    /// Pins Systema's OWN process priority class to Above-Normal. Returns true if it had to set it
    /// (i.e. it was not already Above-Normal). Re-asserting this every tick means nothing — Task
    /// Manager, another tool, or an errant code path — can hold Systema below Above-Normal for more
    /// than one tick. Setting one's OWN process priority is a benign, non-elevated call that touches
    /// no other process, so it never trips SAC/Defender.
    /// </summary>
    private bool EnforceOwnPriorityAboveNormal()
    {
        try
        {
            IntPtr self = GetCurrentProcess();   // pseudo-handle, no close needed
            if (GetPriorityClass(self) == ABOVE_NORMAL_PRIORITY_CLASS) return false;
            SetPriorityClass(self, ABOVE_NORMAL_PRIORITY_CLASS);
            return true;
        }
        catch { return false; }   // best-effort; priority is non-critical
    }

    /// <summary>
    /// On a hybrid P/E-core CPU, pins Systema's OWN process to the P-cores only, so its monitor and
    /// spawn watchers always run on the fast cores and never get parked on an E-core. No-op on a
    /// homogeneous CPU — affinity is left at all cores (Zero mask). Re-asserted each tick (check-then-
    /// set) so nothing moves Systema off the P-cores. Setting one's OWN affinity is benign and never
    /// trips SAC/Defender. Returns true if it had to (re)apply the mask.
    /// </summary>
    private bool EnforceOwnPCoreAffinity()
    {
        UIntPtr pMask = GetOrDetectPCoreMask();
        if (pMask == UIntPtr.Zero) return false;   // not hybrid — leave affinity at all cores
        try
        {
            IntPtr self = GetCurrentProcess();
            if (GetProcessAffinityMask(self, out UIntPtr current, out _) && current == pMask) return false;
            SetProcessAffinityMask(self, pMask);
            return true;
        }
        catch { return false; }   // best-effort; affinity is non-critical
    }

    private UIntPtr GetOrDetectPCoreMask()
    {
        if (!_pCoresDetected)
        {
            _pCoresDetected = true;
            long raw = BuildPCoreMask();
            _pCoreMask = (UIntPtr)(ulong)raw;
            _log.Info("TaskSleepService", raw != 0
                ? $"P-cores detected — Systema pinned to P-core mask 0x{raw:X}"
                : "No P/E core split — Systema affinity left at all cores.");
        }
        return _pCoreMask;
    }

    private void Tick()
    {
        TaskSleepSettings s;
        Dictionary<string, TaskSleepAppRule> rules;
        lock (_settingsLock) { s = _settings; rules = _appRules; }

        // ── SELF-PROTECTION: Systema must NEVER throttle itself ──
        // Primary prevention is the OwnPid guard in ShouldSkip / TryThrottle / ApplyCpuCap /
        // UpdateCpuCap, so this should never fire. It stays as a reactive backstop: if Systema's
        // own PID ever ends up throttled or capped, undo it immediately.
        if (_throttledPids.ContainsKey(OwnPid) || _cpuCapJobs.ContainsKey(OwnPid))
        {
            _log.Warn("TaskSleepService",
                $"⚠️ SELF-PROTECTION: Systema itself (PID {OwnPid}) was throttled/capped — restoring immediately. This should never happen; please report the circumstances.");
            TryRestoreProcess(OwnPid);
            RemoveCpuCap(OwnPid);
        }

        // Keep Systema pinned at Above-Normal (and on the P-cores, hybrid CPUs) — re-assert if
        // anything dropped the priority or moved it off the P-cores.
        EnforceOwnPriorityAboveNormal();
        EnforceOwnPCoreAffinity();

        // 4d. If the max-concurrent-wakes cap was reduced, expire excess active brief wakes now.
        // This runs on the monitor thread so dictionary access is safe (single-threaded tick).
        if (_enforceWakeCapOnNextTick)
        {
            _enforceWakeCapOnNextTick = false;
            var now = DateTime.UtcNow;
            int activeWakes = _briefWakeEndAt.Values.Count(e => now < e)
                            + _trayBriefWakeEndAt.Values.Count(e => now < e);
            int excess = activeWakes - s.MaxConcurrentBriefWakes;
            if (excess > 0)
            {
                // Expire the latest-ending brief wakes first (give priority to those that started earlier).
                // Set their end time to now so the re-throttle section picks them up this tick
                // instead of removing the entry (which would leave them permanently awake).
                var expireNow = DateTime.UtcNow;
                foreach (var pid in _briefWakeEndAt.OrderByDescending(kv => kv.Value)
                                                    .Take(excess)
                                                    .Select(kv => kv.Key)
                                                    .ToList())
                    _briefWakeEndAt[pid] = expireNow;
            }
        }

        // 4e. Apply pending CPU cap changes from UpdateSettings (deferred to monitor thread
        //      for thread safety — _cpuCapJobs is non-concurrent).
        if (_cpuCapSettingsChanged)
        {
            _cpuCapSettingsChanged = false;
            bool capEnabled = _pendingCpuCapEnabled;
            int  capPercent = _pendingCpuCapPercent;

            if (!capEnabled)
            {
                // Cap was disabled — remove all active caps
                foreach (int pid in _cpuCapJobs.Keys.ToList())
                    RemoveCpuCap(pid);
                _log.Info("TaskSleepService", "CPU cap disabled — removed caps from all napped processes");
            }
            else
            {
                // Cap enabled or percent changed — update existing + apply to uncapped
                foreach (var kvp in _cpuCapJobs.ToList())
                {
                    if (kvp.Value != IntPtr.Zero)
                        UpdateCpuCap(kvp.Key, capPercent);
                }
                // Apply to any throttled processes that don't have a cap yet
                foreach (int pid in _throttledPids.Keys.ToList())
                {
                    if (!_cpuCapJobs.ContainsKey(pid))
                        ApplyCpuCap(pid, capPercent);
                }
                _log.Info("TaskSleepService", $"CPU cap live-updated to {capPercent}% on all napped processes");
            }
        }

        // 4f. Napped-memory compression sweep.
        //      For every napped PID that we have NOT yet trimmed this nap cycle,
        //      trim its working set so Windows can compress the pages on the
        //      standby list. This applies to regular nap AND deep sleep — a napped
        //      app starts getting compressed the moment it naps, not only after it
        //      crosses the deep-sleep threshold. The DeepSleepTrimmed guard avoids
        //      re-trimming a process every tick; a brief wake clears the flag so
        //      the next sweep after the wake re-trims (the "re-trim after every
        //      brief wake" half of the feature — see FullyRestore / brief-wake
        //      re-nap, which reset the flag).
        if (s.CompressDeepSleep && _throttledPids.Count > 0)
        {
            int trimmed = 0;
            foreach (int pid in _throttledPids.Keys.ToList())
            {
                if (TryState(pid, out var dsSt) && dsSt.DeepSleepTrimmed) continue;  // already done this cycle

                IntPtr h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) continue;
                try { TrimProcessWorkingSet(h); }
                finally { CloseHandle(h); }

                StateFor(pid).DeepSleepTrimmed = true;
                trimmed++;
            }
            if (trimmed > 0)
                _log.Info("TaskSleepService",
                    $"Nap memory compression: trimmed {trimmed} process(es) (working set → standby/compressed)");
        }

        // 0. Clear skip reasons and log batch from last tick
        FlushLogBatch();
        foreach (var skst in _state.Values) skst.SkipReason = null;   // fresh skip reasons each tick

        // 1. Sample total system CPU
        double sysCpu = SampleSystemCpu();

        // 2. Get all processes + foreground protection set
        uint foregroundPid = GetForegroundPid();
        var  protectedPids = BuildProtectedSet(foregroundPid, s.ActOnForegroundChildren);

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"Process.GetProcesses() failed: {ex.Message}");
            return; // skip this tick entirely — no process list available
        }
        var livePids = new HashSet<int>(all.Select(p => p.Id));

        // 4b. Collect window / audio state for minimize-, tray-, and hidden-nap. Computed HERE — BEFORE
        //     the visible-on-monitor protection below — because a fully-covered ("hidden") window still
        //     reports IsWindowVisible=true, so GetVisibleOnAnyMonitorPids() returns it. If it were
        //     protected as "visible on a monitor" first, the very apps Nap-hidden targets would keep
        //     themselves (and their whole name family) permanently awake and never nap.
        HashSet<int> minimizedPids = s.MinimizeNapEnabled
            ? GetMinimizedProcessIds() : new HashSet<int>();
        // Always query audio PIDs — all nap types (minimize, tray, background, idle)
        // need audio detection to protect apps playing audio or using the microphone.
        HashSet<int> audioPids = GetOrRefreshAudioPids();
        // Tray-nap: get PIDs with NO visible non-minimized top-level windows
        HashSet<int> trayPids = s.TrayNapEnabled
            ? GetTrayProcessIds(minimizedPids) : new HashSet<int>();
        // Nap hidden apps: windows that are open but FULLY covered by other windows. Treated exactly
        // like minimized apps — merged into minimizedPids so they inherit every rule (busy-awake skip,
        // recently-restored cooldown, audio protection, grace period, brief wakes, deep sleep) and wake
        // automatically the moment they're uncovered (they drop out of the recomputed set next tick).
        // Computed AFTER trayPids so tray detection is unaffected; a hidden app has a visible window so
        // it's never a tray candidate anyway.
        HashSet<int> hiddenPids = s.HiddenNapEnabled
            ? GetHiddenProcessIds(minimizedPids, trayPids) : new HashSet<int>();
        if (hiddenPids.Count > 0) minimizedPids.UnionWith(hiddenPids);

        // 2b. Multi-monitor awareness — protect all PIDs visible on any monitor.
        //     If the user can see the app, it must never be napped (minimize, tray, or CPU).
        //     Also protect all same-name processes (Electron renderers share the parent name).
        HashSet<int> visibleOnMonitorPids = s.MultiMonitorAwarenessEnabled
            ? GetVisibleOnAnyMonitorPids() : new HashSet<int>();
        // A fully-covered window still reports IsWindowVisible=true, so it shows up here. Drop the hidden
        // pids or the protection below (and the background-nap "in front" refresh at 2d) would keep the
        // apps Nap-hidden is meant to sleep permanently awake — this is why covered apps never napped.
        if (hiddenPids.Count > 0) visibleOnMonitorPids.ExceptWith(hiddenPids);
        if (visibleOnMonitorPids.Count > 0)
        {
            // Collect process names of visible PIDs, then protect ALL PIDs that belong to
            // the same app family. This covers:
            //   • Electron/Chromium renderers (Discord, Claude, ChatGPT — same exe name)
            //   • Helper processes with different names (steamwebhelper, EpicWebHelper, etc.)
            var visibleNames     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visibleBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in all)
            {
                try
                {
                    if (visibleOnMonitorPids.Contains(p.Id))
                    {
                        visibleNames.Add(p.ProcessName);
                        string bn = GetAppFamilyBaseName(p.ProcessName);
                        if (bn.Length >= 4) visibleBaseNames.Add(bn);
                    }
                }
                catch { }
            }
            foreach (var p in all)
            {
                try
                {
                    // Exact name match OR app family match (prefix-based)
                    if (visibleNames.Contains(p.ProcessName))
                    {
                        protectedPids.Add(p.Id);
                    }
                    else
                    {
                        foreach (string bn in visibleBaseNames)
                        {
                            if (IsAppFamilyMatch(p.ProcessName, bn))
                            {
                                protectedPids.Add(p.Id);
                                break;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        // 2c. Beta: Process group awareness — protect all instances of foreground app's name
        if (s.ProcessGroupAwarenessEnabled)
        {
            foreach (int pid in GetProcessGroupPids(foregroundPid, all))
                protectedPids.Add(pid);
        }

        // 2c-bis. Subtree awareness — extend protection to the ENTIRE descendant tree of the
        //   app the user is actively using (foreground or visible on a monitor). The name-based
        //   family logic above catches same-name renderers and known helper-name suffixes, but
        //   misses generically-named leaf helpers an app spawns (node, cmd, a console host, a
        //   bundled updater). Without this, those leaves strand at the nap cap after the app is
        //   re-focused — they have no window to focus and no family-name match to trigger a
        //   restore, the exact "not everything restored when I reopen the app" symptom.
        //   Anchoring ONLY on real-app roots — shell/system hosts (explorer, svchost, dllhost,
        //   RuntimeBroker, …) are excluded as roots — keeps clicking the desktop or a system
        //   surface from protecting the entire machine and disabling nap. Re-evaluated every
        //   tick, so minimizing the app drops it from the anchor set and the tree naps again.
        if (s.ProcessGroupAwarenessEnabled)
        {
            var nameByPid = new Dictionary<int, string>();
            foreach (var p in all)
            {
                try { nameByPid[p.Id] = p.ProcessName; } catch { }
            }
            bool IsRealAppAnchor(int pid) =>
                nameByPid.TryGetValue(pid, out string? n) &&
                !SystemProcessNames.Contains(n) &&
                !SecurityCriticalProcessNames.Contains(n);

            var subtreeRoots = new HashSet<int>();
            if (foregroundPid != 0 && IsRealAppAnchor((int)foregroundPid))
                subtreeRoots.Add((int)foregroundPid);
            foreach (int vpid in visibleOnMonitorPids)
                if (IsRealAppAnchor(vpid)) subtreeRoots.Add(vpid);

            if (subtreeRoots.Count > 0)
            {
                var subtree = new HashSet<int>(subtreeRoots);
                ExpandWithDescendants(subtreeRoots, subtree);
                foreach (int dpid in subtree)
                    protectedPids.Add(dpid);
            }
        }

        // 2d. Update last-foreground timestamps for background nap tracking
        if (s.BackgroundNapEnabled)
        {
            var now2 = DateTime.UtcNow;
            foreach (int pid in protectedPids)
                StateFor(pid).LastForegroundAt = now2;
            // Visible-on-monitor = user can see it → don't start unfocused countdown
            foreach (int pid in visibleOnMonitorPids)
                StateFor(pid).LastForegroundAt = now2;
            // Also mark audio-producing processes as "in use"
            var audio = GetOrRefreshAudioPids();
            foreach (int pid in audio)
                StateFor(pid).LastForegroundAt = now2;
        }

        // A hidden app must nap as a UNIT. GetHiddenProcessIds only flags the process that OWNS the
        // covered window; a multi-process app (Electron/Chromium) runs its renderer/GPU/utility work in
        // separate, WINDOWLESS child processes. Those children would otherwise be tray/idle/background-
        // napped on their own short grace the moment the app lost visible-family protection (they stop
        // being "visible on a monitor" once the parent is hidden) — napping half the app seconds after
        // it's covered instead of honouring the hidden delay. Pull the whole process tree into hiddenPids
        // so every member rides the same hidden grace and naps together. Any descendant that still has a
        // visible window of its own is left out — it stays protected and isn't dragged down.
        if (s.HiddenNapEnabled && hiddenPids.Count > 0)
        {
            var hiddenFamily = new HashSet<int>(hiddenPids);
            ExpandWithDescendants(hiddenPids, hiddenFamily);
            hiddenFamily.ExceptWith(visibleOnMonitorPids);   // never nap a still-visible child
            hiddenPids.UnionWith(hiddenFamily);
            minimizedPids.UnionWith(hiddenFamily);
        }

        // Build parent→child map for child process napping (minimize/tray/hidden nap)
        Dictionary<int, int> parentMap = (s.MinimizeNapEnabled || s.TrayNapEnabled || s.HiddenNapEnabled)
            ? BuildParentMap() : new Dictionary<int, int>();

        // 3. Collect per-process CPU samples (QUERY_LIMITED access only)
        var cpuMap = SampleAllProcessCpu(all);

        // 4. Clean up state for processes that no longer exist
        CleanupDeadProcesses(livePids);

        // (Window / audio state — minimizedPids, audioPids, trayPids, hiddenPids — is now collected
        //  earlier, above the visible-on-monitor protection, so a fully-covered app isn't protected as
        //  "visible" before hidden-nap can act on it.)

        // 4b-2. "Keep busy apps awake": if a minimized / tray app's WHOLE TREE is over the busy CPU
        // threshold it's likely still doing work the user backgrounded — keep its ENTIRE tree awake
        // across every nap path (and wake it if it's already napped). OFF by default. Whole-tree is
        // essential: a multi-process app keeps the window owner near-idle while a content child does
        // the work, and without this its idle children would still get background/idle/tray-napped
        // while the busy child ran on uncapped — the "half-napped, not capping" bug.
        HashSet<int> busyAwakePids = new();
        if (s.SkipBusyMinimizedApps)
        {
            foreach (int ownerPid in minimizedPids.Concat(trayPids))
            {
                var tree = AppTreePids(ownerPid, parentMap);
                double treeCpu = 0;
                foreach (int p in tree) if (cpuMap.TryGetValue(p, out double c)) treeCpu += c;
                if (treeCpu <= s.BusyMinimizedCpuThresholdPercent) continue;
                foreach (int p in tree) busyAwakePids.Add(p);
            }
            // Wake any member that's currently napped — the app is busy and must be fully awake.
            foreach (int bp in busyAwakePids)
                if (_throttledPids.ContainsKey(bp) || _napBuckets.IsNapped(bp))
                    WakeFully(bp);
        }

        // 4c. Process manual wake requests from the UI (e.g. "Stop Napping" button)
        if (!_wakeRequests.IsEmpty)
        {
            var wakeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_wakeRequests.TryDequeue(out string? wn))
                if (wn != null) wakeNames.Add(wn);

            // Union: throttled pids AND pids currently in a brief wake. BeginBriefWake
            // removes from _throttledPids, so brief-wake PIDs live only in the nap-type
            // dicts — without this union, "Stop Napping" silently no-ops on them.
            var allNappedPids = new HashSet<int>(_throttledPids.Keys);
            allNappedPids.UnionWith(_napBuckets.Pids);

            foreach (int pid in allNappedPids)
            {
                if (_processNames.TryGetValue(pid, out string? pname) && wakeNames.Contains(pname))
                {
                    // Manual "Stop Napping" — full wake including the 5 s cooldown, so the
                    // very next tick can't immediately re-nap the process the user just woke.
                    WakeFully(pid);
                }
            }
        }

        // Brief-wake fairness: collect candidates during step 5, process sorted after the loop
        // Each entry: (pid, isTray, scheduledWakeTime) — sorted by earliest scheduled = longest waiting
        var briefWakeCandidates = new List<(int Pid, bool IsTray, DateTime ScheduledAt)>();

        // 5. Evaluate currently napped processes — restore if conditions are met
        foreach (int pid in _throttledPids.Keys.ToList())
        {
            bool   shouldRestore = false;
            string restoreReason = "";

            if (!livePids.Contains(pid))
            {
                // Process exited — always clean up
                shouldRestore = true; restoreReason = "process exited";
            }
            else if (protectedPids.Contains(pid) &&
                     (pid == (int)foregroundPid || visibleOnMonitorPids.Contains(pid)))
            {
                // User brought the app DIRECTLY to foreground — wake it permanently.
                shouldRestore = true; restoreReason = "opened by user";
            }
            else if (protectedPids.Contains(pid) &&
                     !_napBuckets.Is(pid, NapReason.Minimized) && !_napBuckets.Is(pid, NapReason.Tray))
            {
                // This napped pid is part of the foreground app's process tree (background-,
                // idle-, or CPU-napped helper). Previously only bg/idle helpers were restored
                // here, which left CPU-napped windowless helpers (Firefox/Chromium content
                // processes, Steam webhelpers, Electron/Discord renderers) stuck at the napped
                // cap while the main app was foreground — making the whole app feel slow.
                //
                // Restore the helper so it can render/process alongside its parent. Minimize/
                // tray-napped family members are NOT restored here — the user deliberately hid
                // those, and their own branches below handle un-minimize / visibility / audio.
                shouldRestore = true; restoreReason = "app family focused";
            }
            else if (_napBuckets.Is(pid, NapReason.Minimized))
            {
                // ── Minimize-napped process: separate restore logic ───────────────
                bool nowMinimized = minimizedPids.Contains(pid);
                string pn = _processNames.TryGetValue(pid, out var pn_) ? pn_ : "";
                bool hasAudio = IsAudioProtected(pid, pn, audioPids);

                if (!nowMinimized || hasAudio)
                {
                    // App was un-minimized or started audio — restore permanently
                    shouldRestore = true;
                    restoreReason = hasAudio ? "audio detected" : "app un-minimized";
                }
                else
                {
                    // Still minimized & silent — collect as brief-wake candidate (fairness: sorted later).
                    // No system-CPU gate: apps like Steam commonly run 5+ processes each capped
                    // at NappedCpuCapPercent (default 3%), so the napped contribution alone is
                    // already 15%+ of total CPU. Any CPU-based gate would permanently block
                    // wakes for multi-process apps. Instead we rely on:
                    //   • MaxConcurrentBriefWakes (default 3) — hard cap on parallel wakes
                    //   • BriefWakeCpuCapPercent (default 7%) — each wake only adds ~4% delta
                    //   • 10-second wake window — self-limiting even if the gate misjudges
                    //   • Game Mode suppression — covers the "system genuinely busy" case
                    bool wakeNeeded = !_nextBriefWakeAt.TryGetValue(pid, out DateTime nextWake) ||
                                      DateTime.UtcNow >= nextWake;
                    bool gameBlocks = s.IsGameModeActive && s.SuppressBriefWakesDuringGameMode;
                    if (!gameBlocks && wakeNeeded)
                    {
                        // Use nap-start time for fairness sort (earliest nap = waited longest).
                        // Falls back to current time for new candidates so they queue behind older ones.
                        DateTime scheduledAt = TryState(pid, out var nsSt) && nsSt.NapSince is { } ns ? ns : DateTime.UtcNow;
                        briefWakeCandidates.Add((pid, false, scheduledAt));
                    }
                    // else: continue napping — no action
                }
                // Do NOT fall through to PersistentNap / time-based restore for minimize-napped procs
            }
            else if (_napBuckets.Is(pid, NapReason.Tray))
            {
                // ── Tray-napped process: restore if it got a visible window or started audio ─
                bool stillTray = trayPids.Contains(pid);
                string pn2 = _processNames.TryGetValue(pid, out var pn2_) ? pn2_ : "";
                bool hasAudio = IsAudioProtected(pid, pn2, audioPids);

                // A windowless helper (Electron renderer, GPU/utility/console host) that got
                // tray-napped never regains a window of its own, so the two checks above would
                // keep it cycling in perpetual brief-wake even while its app is open — the
                // "tray processes stay napped when I reopen the app" bug. If a foreground/visible
                // member of its app family (or its in-use parent's subtree) is protected this
                // tick, the whole app is in use, so wake this helper alongside it. protectedPids
                // only contains this pid when the family/subtree-awareness logic matched an
                // in-use sibling, so a genuinely tray-only background app is unaffected.
                bool familyInUse = protectedPids.Contains(pid);

                if (!stillTray || hasAudio || familyInUse)
                {
                    // App opened a window, started audio, or an in-use sibling pulled it awake.
                    shouldRestore = true;
                    restoreReason = hasAudio   ? "audio detected"
                                  : !stillTray ? "window appeared"
                                  :              "app family focused";
                }
                else
                {
                    // Still tray-only — collect as brief-wake candidate (fairness: sorted later).
                    // No system-CPU gate — see rationale in the minimize-nap branch above.
                    bool wakeNeeded = !_trayNextBriefWakeAt.TryGetValue(pid, out DateTime nextTrayWake) ||
                                      DateTime.UtcNow >= nextTrayWake;
                    bool gameBlocks = s.IsGameModeActive && s.SuppressBriefWakesDuringGameMode;
                    if (!gameBlocks && wakeNeeded)
                    {
                        // Use nap-start time for fairness sort (earliest nap = waited longest).
                        DateTime scheduledAt = TryState(pid, out var nsSt) && nsSt.NapSince is { } ns ? ns : DateTime.UtcNow;
                        briefWakeCandidates.Add((pid, true, scheduledAt));
                    }
                }
                // Do NOT fall through to PersistentNap / time-based restore for tray-napped procs
            }
            else if (_napBuckets.Is(pid, NapReason.Background) || _napBuckets.Is(pid, NapReason.Idle))
            {
                // Background/idle napped — restore when the user focuses it
                // (protectedPids check at top already handles foreground restore).
                // Also restore if the process started producing audio.
                string bgPn = _processNames.TryGetValue(pid, out var bgPn_) ? bgPn_ : "";
                if (IsAudioProtected(pid, bgPn, audioPids))
                {
                    shouldRestore = true;
                    restoreReason = "audio detected";
                }
                // else: keep napping — user hasn't focused it
            }
            else if (s.PersistentNapEnabled)
            {
                // Nap until used: keep napping until the user focuses the app.
                // The foreground check above is the only restore trigger.
            }
            else if (TryState(pid, out var taSt) && taSt.ThrottledAt is { } ta)
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
                bool isNapChild = TryState(pid, out var ncSt0) && ncSt0.NapChildParent.HasValue;
                WakeFully(pid);
                if (!isNapChild)
                    AddEvent(name ?? $"PID {pid}", pid, "Woke up", restoreReason);

                // ── Bulk-restore siblings: when the user opens an app, restore ALL
                //    throttled processes with the same name / app family so the entire
                //    app wakes up instantly (renderers, GPU process, utilities, etc.)
                if (restoreReason == "opened by user" && name != null &&
                    s.ProcessGroupAwarenessEnabled)
                {
                    string baseName = GetAppFamilyBaseName(name);
                    int siblingCount = 0;
                    foreach (int sibPid in _throttledPids.Keys.ToList())
                    {
                        if (sibPid == pid) continue; // already restored above
                        if (!_processNames.TryGetValue(sibPid, out string? sibName)) continue;
                        if (!string.Equals(sibName, name, StringComparison.OrdinalIgnoreCase) &&
                            !IsAppFamilyMatch(sibName, baseName))
                            continue;

                        WakeFully(sibPid);
                        siblingCount++;
                    }
                    if (siblingCount > 0)
                        AddEvent(name, pid, "Woke up", $"opened by user (+{siblingCount} siblings)");
                }
            }
        }

        // 5a. Process brief-wake candidates with fairness (longest-waiting first)
        if (briefWakeCandidates.Count > 0)
        {
            briefWakeCandidates.Sort((a, b) => a.ScheduledAt.CompareTo(b.ScheduledAt)); // earliest = longest waiting
            int activeWakes = _briefWakeEndAt.Values.Count(e => DateTime.UtcNow < e)
                            + _trayBriefWakeEndAt.Values.Count(e => DateTime.UtcNow < e);
            foreach (var (wPid, isTray, _) in briefWakeCandidates)
            {
                if (!_throttledPids.ContainsKey(wPid)) continue; // PID was restored mid-tick
                if (activeWakes >= Math.Max(1, s.MaxConcurrentBriefWakes)) break;
                _processNames.TryGetValue(wPid, out string? wName);

                if (isTray)
                {
                    BeginBriefWake(wPid, s);
                    _trayBriefWakeEndAt[wPid] = DateTime.UtcNow.AddMilliseconds(s.TrayBriefWakeDurationMs);

                    double nappedForMs = TryState(wPid, out var trayNsSt) && trayNsSt.NapSince is { } trayNapSince
                        ? (DateTime.UtcNow - trayNapSince).TotalMilliseconds : 0;
                    bool trayDeepSleep = s.TrayDeepSleepEnabled &&
                        nappedForMs >= s.TrayDeepSleepThresholdMs;
                    int trayWakeIntervalMs = trayDeepSleep
                        ? s.TrayDeepSleepWakeIntervalMs
                        : s.TrayBriefWakeIntervalMs;
                    _trayNextBriefWakeAt[wPid] = DateTime.UtcNow.AddMilliseconds(trayWakeIntervalMs);

                    string wakeLabel = trayDeepSleep ? "Tray Deep Wake" : "Tray Wake";
                    AddEvent(wName ?? $"PID {wPid}", wPid, wakeLabel, $"CPU {sysCpu:F0}%");
                }
                else
                {
                    BeginBriefWake(wPid, s);
                    _briefWakeEndAt[wPid] = DateTime.UtcNow.AddMilliseconds(s.MinimizedBriefWakeDurationMs);

                    double minimizedForMs = TryState(wPid, out var minNsSt) && minNsSt.NapSince is { } napSince
                        ? (DateTime.UtcNow - napSince).TotalMilliseconds : 0;
                    int wakeIntervalMs = minimizedForMs >= s.MinimizeDeepSleepThresholdMs
                        ? s.MinimizeDeepSleepWakeIntervalMs
                        : s.MinimizedBriefWakeIntervalMs;
                    _nextBriefWakeAt[wPid] = DateTime.UtcNow.AddMilliseconds(wakeIntervalMs);

                    string wakeLabel = minimizedForMs >= s.MinimizeDeepSleepThresholdMs
                        ? "Deep Wake" : "Brief Wake";
                    AddEvent(wName ?? $"PID {wPid}", wPid, wakeLabel, $"CPU {sysCpu:F0}%");
                }
                activeWakes++;
            }
        }

        // 5a-bis. BULLETPROOF FULL-APP WAKE — reopening/using an app must wake EVERY process
        //   it owns, not just the visible window. The name-family/subtree protection above can
        //   miss members when the foreground/visible process is a *child* (e.g. the Firefox
        //   window is owned by a renderer, so walking DOWN from it misses the 82-thread parent
        //   and the sibling renderers). So here we build the app's WHOLE process cluster
        //   directly: for the foreground process and every window visible on a monitor, walk UP
        //   to the topmost real-app ancestor, DOWN to all its descendants, AND include every
        //   same-NAME process (Chromium/Firefox/Electron renderers share the parent's exe name).
        //   Any cluster member still napped — in ANY bucket or mid-brief-wake, including
        //   minimize/tray — is fully woken. Runs regardless of the ProcessGroup/MultiMonitor
        //   toggles so it can't be silently disabled.
        var wakeSeeds = new HashSet<int>();
        if (foregroundPid > 4) wakeSeeds.Add((int)foregroundPid);
        // A fully-covered ("hidden") window still counts as visible-on-a-monitor, so exclude those or
        // this sweep would wake them right back every tick. They rejoin the seeds the moment they're
        // uncovered (they drop out of hiddenPids), which is exactly what wakes them.
        foreach (int vp in GetVisibleOnAnyMonitorPids())
            if (!hiddenPids.Contains(vp)) wakeSeeds.Add(vp);
        if (wakeSeeds.Count > 0)
        {
            var childToParent = BuildParentMap();                 // child → parent (full snapshot)
            var childrenOf = new Dictionary<int, List<int>>();    // parent → children
            foreach (var kv in childToParent)
            {
                if (!childrenOf.TryGetValue(kv.Value, out var lst)) childrenOf[kv.Value] = lst = new List<int>();
                lst.Add(kv.Key);
            }
            var nameOf = new Dictionary<int, string>();
            foreach (var p in all) { try { nameOf[p.Id] = p.ProcessName; } catch { } }
            bool IsRealApp(int q) => nameOf.TryGetValue(q, out string? n)
                && !SystemProcessNames.Contains(n) && !SecurityCriticalProcessNames.Contains(n);

            var cluster = new HashSet<int>();
            foreach (int seed in wakeSeeds)
            {
                if (!IsRealApp(seed)) continue;
                // Walk UP to the topmost real-app ancestor (the app's root process).
                int root = seed, cur = seed;
                for (int d = 0; d < 24; d++)
                {
                    if (!childToParent.TryGetValue(cur, out int par) || par <= 4 || par == cur) break;
                    if (!livePids.Contains(par) || !IsRealApp(par)) break;
                    root = par; cur = par;
                }
                // Collect the whole tree under root (BFS down).
                var stack = new Stack<int>(); stack.Push(root);
                while (stack.Count > 0)
                {
                    int x = stack.Pop();
                    if (!cluster.Add(x)) continue;
                    if (childrenOf.TryGetValue(x, out var kids)) foreach (int k in kids) stack.Push(k);
                }
                // Include all same-name processes as the seed (catches renderers the snapshot missed).
                if (nameOf.TryGetValue(seed, out string? sname))
                    foreach (var kv in nameOf)
                        if (string.Equals(kv.Value, sname, StringComparison.OrdinalIgnoreCase)) cluster.Add(kv.Key);
            }

            foreach (int cpid in cluster)
            {
                if (!livePids.Contains(cpid)) continue;
                protectedPids.Add(cpid);   // in use → protect from re-nap this tick regardless

                bool napped = _throttledPids.ContainsKey(cpid)
                           || _napBuckets.IsNapped(cpid)
                           || _briefWakeEndAt.ContainsKey(cpid) || _trayBriefWakeEndAt.ContainsKey(cpid);
                if (!napped) continue;

                _processNames.TryGetValue(cpid, out string? cname);
                bool wasNapChild = TryState(cpid, out var ncSt1) && ncSt1.NapChildParent.HasValue;

                WakeFully(cpid);

                if (!wasNapChild)
                    AddEvent(cname ?? $"PID {cpid}", cpid, "Woke up", "app in use");
            }
        }

        // 5b. Clear grace entries for processes that are no longer minimized / tray-only
        //     (e.g. user un-minimized the window before the grace period elapsed)
        foreach (int gPid in _minimizeGraceSince.Keys.ToList())
            if (!minimizedPids.Contains(gPid)) _minimizeGraceSince.Remove(gPid);
        foreach (int gPid in _trayGraceSince.Keys.ToList())
            if (!trayPids.Contains(gPid)) _trayGraceSince.Remove(gPid);

        // 6. Consider throttling new processes
        long freeRamMb  = GetAvailableRamMb();
        bool ramPressure = freeRamMb < 4096; // < 4 GB free = memory is genuinely constrained (reported in the snapshot)

        foreach (var proc in all)
        {
            try
            {
                // ── SKIP SYSTEMA ITSELF — It must never be throttled under any circumstances ──
                if (proc.ProcessName.Equals("Systema", StringComparison.OrdinalIgnoreCase))
                    continue;

                // ── Busy app kept awake — member of a minimized/tray app whose whole tree is over
                //    the busy-CPU threshold. Skip EVERY nap path for it (it was already woken above
                //    if it had been napped). Clear grace so it re-arms cleanly once it settles.
                if (busyAwakePids.Contains(proc.Id))
                {
                    StateFor(proc.Id).SkipReason = "Busy app — kept awake";
                    _minimizeGraceSince.Remove(proc.Id);
                    _trayGraceSince.Remove(proc.Id);
                    continue;
                }

                // ── Brief-wake handling: minimize-napped proc currently in a brief wake ──
                // During BeginBriefWake the pid is removed from _throttledPids, so the main
                // wake loop at #5 can't see it. We have to evaluate user-focus / audio /
                // un-minimize here and fully restore if any of them triggered.
                if ((s.MinimizeNapEnabled || s.HiddenNapEnabled) &&
                    !_throttledPids.ContainsKey(proc.Id) &&
                    _napBuckets.Is(proc.Id, NapReason.Minimized))
                {
                    bool stillMinimized = minimizedPids.Contains(proc.Id);
                    bool reNapAudio     = IsAudioProtected(proc.Id, proc.ProcessName, audioPids);
                    bool userFocused    = protectedPids.Contains(proc.Id);
                    bool wakeWindowOver = _briefWakeEndAt.TryGetValue(proc.Id, out DateTime wakeEnd) &&
                                          DateTime.UtcNow >= wakeEnd;

                    // User opened the window / focused the app / started audio → immediate
                    // full restore (don't wait for the 10 s brief wake window to elapse —
                    // otherwise the app sits at the loosened cap until it expires, which
                    // feels broken to the user).
                    if (!stillMinimized || userFocused || reNapAudio)
                    {
                        FullyRestoreFromBriefWake(proc.Id);
                        _processNames.TryGetValue(proc.Id, out string? wn);
                        string reason = reNapAudio   ? "audio detected"
                                       : userFocused ? "opened by user"
                                                     : "window shown again";   // un-minimized or no longer covered
                        AddEvent(wn ?? proc.ProcessName, proc.Id, "Woke up", reason);
                    }
                    else if (wakeWindowOver)
                    {
                        // Window expired with no user interaction — re-nap. The app never left its
                        // napped throttle profile (the brief wake only widened the cap), so just
                        // re-seat the original-priority record and tighten the cap back down —
                        // no priority re-capture, no priority change.
                        _briefWakeEndAt.Remove(proc.Id);
                        _throttledPids[proc.Id] = StateFor(proc.Id).NappedOriginalCpu ?? NORMAL_PRIORITY_CLASS;
                        StateFor(proc.Id).NappedOriginalCpu = null;
                        StateFor(proc.Id).ThrottledAt = DateTime.UtcNow;
                        if (s.NappedCpuCapEnabled && s.NappedCpuCapPercent > 0)
                            UpdateCpuCap(proc.Id, Math.Clamp(s.NappedCpuCapPercent, 1, 100));
                        _processNames.TryGetValue(proc.Id, out string? rn);
                        AddEvent(rn ?? proc.ProcessName, proc.Id, "Re-napping", "brief wake ended");

                        // Re-tighten any children that loosened with this parent.
                        if (s.NappedCpuCapEnabled)
                            SetNapChildCaps(proc.Id, s.NappedCpuCapPercent);

                        // Nap memory compression: the brief wake faulted pages back in; push them
                        // straight back to standby so Windows can re-compress. Applies to regular
                        // nap and deep sleep alike.
                        if (s.CompressDeepSleep)
                        {
                            TrimWorkingSetByPid(proc.Id, rn ?? proc.ProcessName);
                            StateFor(proc.Id).DeepSleepTrimmed = true;
                        }
                    }
                    // Whether we just restored, re-throttled, or are still in the wake window,
                    // skip the rest of the throttle logic for this process.
                    continue;
                }

                // ── Brief-wake handling: tray-napped proc currently in a brief wake ──
                if (s.TrayNapEnabled &&
                    !_throttledPids.ContainsKey(proc.Id) &&
                    _napBuckets.Is(proc.Id, NapReason.Tray))
                {
                    bool stillTray      = trayPids.Contains(proc.Id);
                    bool trayReNapAudio = IsAudioProtected(proc.Id, proc.ProcessName, audioPids);
                    bool userFocused    = protectedPids.Contains(proc.Id);
                    bool wakeWindowOver = _trayBriefWakeEndAt.TryGetValue(proc.Id, out DateTime trayWakeEnd) &&
                                          DateTime.UtcNow >= trayWakeEnd;

                    if (!stillTray || userFocused || trayReNapAudio)
                    {
                        FullyRestoreFromBriefWake(proc.Id);
                        _processNames.TryGetValue(proc.Id, out string? wn);
                        string reason = trayReNapAudio ? "audio detected"
                                       : userFocused   ? "opened by user"
                                                       : "window appeared";
                        AddEvent(wn ?? proc.ProcessName, proc.Id, "Woke up", reason);
                    }
                    else if (wakeWindowOver)
                    {
                        // Re-nap (cap-only) — same model as the minimize branch above: re-seat the
                        // parked original priority and tighten the cap; change no priorities.
                        _trayBriefWakeEndAt.Remove(proc.Id);
                        _throttledPids[proc.Id] = StateFor(proc.Id).NappedOriginalCpu ?? NORMAL_PRIORITY_CLASS;
                        StateFor(proc.Id).NappedOriginalCpu = null;
                        StateFor(proc.Id).ThrottledAt = DateTime.UtcNow;
                        if (s.NappedCpuCapEnabled && s.NappedCpuCapPercent > 0)
                            UpdateCpuCap(proc.Id, Math.Clamp(s.NappedCpuCapPercent, 1, 100));
                        _processNames.TryGetValue(proc.Id, out string? tn);
                        AddEvent(tn ?? proc.ProcessName, proc.Id, "Tray Re-nap", "brief wake ended");

                        // Re-tighten any children that loosened with this parent.
                        if (s.NappedCpuCapEnabled)
                            SetNapChildCaps(proc.Id, s.NappedCpuCapPercent);

                        // Nap memory compression: see the matching minimize-nap branch above.
                        if (s.CompressDeepSleep)
                        {
                            TrimWorkingSetByPid(proc.Id, tn ?? proc.ProcessName);
                            StateFor(proc.Id).DeepSleepTrimmed = true;
                        }
                    }
                    continue;
                }

                if (_throttledPids.ContainsKey(proc.Id)) continue;
                if (ShouldSkip(proc, protectedPids, s, rules, audioPids))
                {
                    // Clear any pending grace timers so the UI doesn't show "Pending"
                    // for processes that are being skipped (e.g. audio-active Firefox).
                    _minimizeGraceSince.Remove(proc.Id);
                    _trayGraceSince.Remove(proc.Id);
                    if (TryState(proc.Id, out var skSt)) { skSt.LowCpuTickCount = 0; skSt.IdleSince = null; skSt.OverThresholdSince = null; }
                    continue;
                }
                // Cooldown: don't re-throttle a process that was just restored
                if (TryState(proc.Id, out var cdSt) && cdSt.RestoredAt is { } rt &&
                    (DateTime.UtcNow - rt).TotalMilliseconds < 5_000) continue;

                // ── Minimize-nap: throttle minimized apps after grace period ──
                // (Busy minimized/tray apps are already handled up front via busyAwakePids — their
                //  whole tree is kept awake, so they never reach here.)
                if ((s.MinimizeNapEnabled || s.HiddenNapEnabled) && minimizedPids.Contains(proc.Id))
                {
                    bool hasAudio = IsAudioProtected(proc.Id, proc.ProcessName, audioPids);

                    if (!hasAudio)
                    {
                        // Record when this process first went minimized / hidden (grace period start)
                        if (!_minimizeGraceSince.ContainsKey(proc.Id))
                            _minimizeGraceSince[proc.Id] = DateTime.UtcNow;

                        // Hidden (fully-covered) apps get their own, longer, user-adjustable grace so a
                        // window you're just flipping in front of isn't napped the instant it's covered.
                        // Minimized/tray apps keep the short MinimizeTrayGraceMs.
                        bool pendingHidden = hiddenPids.Contains(proc.Id);
                        StateFor(proc.Id).IsPendingHidden = pendingHidden;
                        double graceMs = pendingHidden ? s.HiddenNapGraceMs : MinimizeTrayGraceMs;

                        bool graceElapsed =
                            (DateTime.UtcNow - _minimizeGraceSince[proc.Id]).TotalMilliseconds
                            >= graceMs;

                        if (graceElapsed)
                        {
                            _minimizeGraceSince.Remove(proc.Id);
                            if (TryThrottle(proc, s, rules, forceMaxThrottle: true))
                            {
                                double mnCpu = TryState(proc.Id, out var lcMn) && lcMn.LastCpuPercent is { } vMn ? vMn : 0;
                                StateFor(proc.Id).CpuAtThrottle =mnCpu;
                                StateFor(proc.Id).ThrottledAt     = DateTime.UtcNow;
                                MarkNap(proc.Id, NapReason.Minimized);
                                StateFor(proc.Id).NapSince ??= DateTime.UtcNow; // deep-sleep timer
                                _nextBriefWakeAt[proc.Id] =
                                    DateTime.UtcNow.AddMilliseconds(s.MinimizedBriefWakeIntervalMs);
                                _briefWakeEndAt.Remove(proc.Id);
                                bool isHidden = hiddenPids.Contains(proc.Id);
                                AddEvent(proc.ProcessName, proc.Id,
                                    isHidden ? "Hidden Nap"                 : "Minimize Nap",
                                    isHidden ? "hidden behind other windows" : "app minimized");
                                // Nap the whole tree as a unit so the entire app goes down together
                                // (renderers/helpers), not just the window owner. Restored together
                                // by the full-app wake sweep when the window comes back.
                                NapChildProcesses(proc.Id, all, parentMap, protectedPids, audioPids, s, rules);
                            }
                        }
                        continue;
                    }
                    // Has audio → clear grace, fall through (audio-active apps are never napped).
                    StateFor(proc.Id).SkipReason ="Audio active";
                    _minimizeGraceSince.Remove(proc.Id);
                }

                // ── Tray-nap: throttle tray-only processes after grace period ──
                if (s.TrayNapEnabled && trayPids.Contains(proc.Id) &&
                    !_napBuckets.Is(proc.Id, NapReason.Tray))
                {
                    bool hasAudio = IsAudioProtected(proc.Id, proc.ProcessName, audioPids);
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
                            if (TryThrottle(proc, s, rules, forceMaxThrottle: true))
                            {
                                double tnCpu = TryState(proc.Id, out var lcTn) && lcTn.LastCpuPercent is { } vTn ? vTn : 0;
                                StateFor(proc.Id).CpuAtThrottle =tnCpu;
                                StateFor(proc.Id).ThrottledAt         = DateTime.UtcNow;
                                MarkNap(proc.Id, NapReason.Tray);
                                _trayNextBriefWakeAt[proc.Id] =
                                    DateTime.UtcNow.AddMilliseconds(s.TrayBriefWakeIntervalMs);
                                _trayBriefWakeEndAt.Remove(proc.Id);
                                StateFor(proc.Id).NapSince ??= DateTime.UtcNow; // deep-sleep timer
                                string trayDetail = "no visible window";
                                AddEvent(proc.ProcessName, proc.Id, "Tray Nap", trayDetail);
                                // Nap the whole tree as a unit (see minimize-nap above).
                                NapChildProcesses(proc.Id, all, parentMap, protectedPids, audioPids, s, rules);
                            }
                        }
                        continue; // don't also CPU-throttle
                    }
                    // Has audio → clear grace, fall through; eligible for CPU throttle if high
                    StateFor(proc.Id).SkipReason ="Audio active";
                    _trayGraceSince.Remove(proc.Id);
                }

                // ── Background nap: nap processes unfocused for BackgroundNapAfterMs ──
                // This is the broadest nap — catches everything the user isn't using.
                // Skip session 0 (system services) — they never have foreground windows,
                // so they'd always hit the unfocused timer. Napping them risks breaking things.
                if (s.BackgroundNapEnabled &&
                    proc.SessionId != 0 &&                 // skip system services
                    !minimizedPids.Contains(proc.Id) &&   // minimize-nap already handles these
                    !trayPids.Contains(proc.Id))           // tray-nap already handles these
                {
                    var fgSt = StateFor(proc.Id);
                    if (fgSt.LastForegroundAt is not { } lastFg)
                    {
                        // First time seeing this process — assume it just started
                        fgSt.LastForegroundAt = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - lastFg).TotalMilliseconds >= s.BackgroundNapAfterMs)
                    {
                        bool hasAudio = IsAudioProtected(proc.Id, proc.ProcessName, audioPids);
                        if (!hasAudio)
                        {
                            if (TryThrottle(proc, s, rules))
                            {
                                StateFor(proc.Id).ThrottledAt = DateTime.UtcNow;
                                double bgCpu = TryState(proc.Id, out var lcBg) && lcBg.LastCpuPercent is { } vBg ? vBg : 0;
                                StateFor(proc.Id).CpuAtThrottle =bgCpu;
                                MarkNap(proc.Id, NapReason.Background);
                                int mins = (int)((DateTime.UtcNow - lastFg).TotalMinutes);
                                AddEvent(proc.ProcessName, proc.Id, "Background Nap",
                                    $"unfocused {mins}m — CPU {bgCpu:F1}%");
                            }
                            continue;
                        }
                    }
                }

                // ── Idle nap: nap processes with near-zero CPU for IdleNapAfterMs ──
                // Catches truly idle background processes regardless of system CPU.
                // Skip session 0 processes (system services) — they're already at 0% CPU,
                // napping them saves nothing and risks breaking drivers/system services.
                if (s.IdleNapEnabled && proc.SessionId != 0)
                {
                    cpuMap.TryGetValue(proc.Id, out double idleCpu);
                    if (idleCpu < s.IdleNapCpuThreshold)
                    {
                        var idleSt = StateFor(proc.Id);
                        idleSt.IdleSince ??= DateTime.UtcNow;

                        if ((DateTime.UtcNow - idleSt.IdleSince.Value).TotalMilliseconds >= s.IdleNapAfterMs)
                        {
                            bool hasAudio = IsAudioProtected(proc.Id, proc.ProcessName, audioPids);
                            if (!hasAudio)
                            {
                                idleSt.IdleSince = null;
                                if (TryThrottle(proc, s, rules))
                                {
                                    StateFor(proc.Id).ThrottledAt = DateTime.UtcNow;
                                    StateFor(proc.Id).CpuAtThrottle =idleCpu;
                                    MarkNap(proc.Id, NapReason.Idle);
                                    AddEvent(proc.ProcessName, proc.Id, "Idle Nap",
                                        $"CPU {idleCpu:F2}% for 2+ min");
                                }
                                continue;
                            }
                        }
                    }
                    else if (TryState(proc.Id, out var idleSt2))
                    {
                        idleSt2.IdleSince = null;
                    }
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
                        if (TryState(proc.Id, out var csst)) csst.OverThresholdSince = null; // reset so the grace restarts on next idle
                        continue;
                    }

                    var agSt = StateFor(proc.Id);
                    if (agSt.OverThresholdSince == null)
                    {
                        agSt.OverThresholdSince = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - agSt.OverThresholdSince.Value).TotalMilliseconds >= s.TimeOverQuotaMs)
                    {
                        if (TryThrottle(proc, s, rules))
                        {
                            agSt.ThrottledAt = DateTime.UtcNow;
                            agSt.OverThresholdSince = null;
                            double agCpu = agSt.LastCpuPercent ?? 0;
                            agSt.CpuAtThrottle = agCpu;
                            AddEvent(proc.ProcessName, proc.Id, "Napping",
                                $"background waster — CPU {agCpu:F1}%");
                        }
                    }
                    continue;
                }

                // High-CPU "off-screen" napping was removed deliberately: nap decisions are now
                // visibility + time based (minimize / tray / background / idle + their children).
                // A process that reaches here isn't minimized, in the tray, a known waster, or idle,
                // so it's left alone regardless of its CPU usage.
            }
            catch (InvalidOperationException)
            {
                // "No process is associated with this object." — the process exited between
                // Process.GetProcesses() snapshot and our access of a lazy property (e.g.
                // proc.SessionId). Benign — nothing to clean up, just move on.
            }
            catch (Exception ex) { _log.Warn("TaskSleepService", $"Tick: could not process PID {proc.Id}: {ex.Message}"); }
            finally { try { proc.Dispose(); } catch { } }
        }

        // 6b. Orphan-cap sweep — safety net for any Job Object cap that became
        //     disconnected from the process's nap state (e.g. a race where a pid was
        //     removed from every nap dict but _cpuCapJobs never got released). Without
        //     this, a stuck 3% cap would persist until process exit — the user's
        //     "app goes super slow" symptom. Only release caps where the pid is
        //     DEFINITIVELY not supposed to be throttled anymore: no throttle, no nap,
        //     no brief-wake tracking, no grace, and no pending restoration.
        if (_cpuCapJobs.Count > 0)
        {
            foreach (int capPid in _cpuCapJobs.Keys.ToList())
            {
                if (_throttledPids.ContainsKey(capPid)) continue;       // actively napped
                if (_napBuckets.IsNapped(capPid))       continue;       // napped under any category (minimize/tray/bg/idle)
                if (_briefWakeEndAt.ContainsKey(capPid)) continue;      // wake window still open
                if (_trayBriefWakeEndAt.ContainsKey(capPid)) continue;  // tray wake window still open
                // Orphan — release it.
                _processNames.TryGetValue(capPid, out string? orphanName);
                RemoveCpuCap(capPid);
                _originalAffinities.TryRemove(capPid, out _);
                _log.Info("TaskSleepService",
                    $"Orphan CPU cap released for {orphanName ?? $"PID {capPid}"} (PID {capPid}) — safety-net sweep");
            }
        }

        // 6c. Orphan nap-child sweep — if a nap-child's parent is no longer napped
        //      (parent exited, was restored by a path that skipped RestoreNapChildren,
        //      or PID was reused), the child would otherwise sit capped forever with
        //      no wake trigger (PersistentNap bypasses time-based restore). Detect
        //      and release these.
        var napChildren = _state
            .Where(kv => kv.Value.NapChildParent is not null)
            .Select(kv => (childPid: kv.Key, parentPid: kv.Value.NapChildParent!.Value))
            .ToList();
        if (napChildren.Count > 0)
        {
            foreach (var (childPid, parentPid) in napChildren)
            {
                // Parent still in any napped / brief-wake state → child stays napped.
                if (_throttledPids.ContainsKey(parentPid))      continue;
                if (_napBuckets.IsNapped(parentPid))            continue;   // napped under any category
                if (_briefWakeEndAt.ContainsKey(parentPid))     continue;
                if (_trayBriefWakeEndAt.ContainsKey(parentPid)) continue;

                // Parent is fully un-napped (or gone) — wake the child too.
                _processNames.TryGetValue(childPid, out string? orphanChildName);
                if (TryState(childPid, out var ncSt)) { ncSt.NapChildParent = null; ncSt.NapSince = null; }
                TryRestoreProcess(childPid);            // also releases cap if still held
                _log.Info("TaskSleepService",
                    $"Orphan nap-child released: {orphanChildName ?? $"PID {childPid}"} (parent PID {parentPid} no longer napped)");
            }
        }

        // 6d. New-child sweep — a napped app that spawns a child AFTER it was napped
        //     (Steam download worker, a new browser/Electron renderer, an updater
        //     child) would otherwise run completely uncapped. Re-scan each currently
        //     NAPPED parent for new children and bring them under the same nap throttle
        //     + cap. NapChildProcesses skips children already throttled, so this only
        //     catches the new ones. Parents mid-brief-wake aren't in _throttledPids, so
        //     they're correctly skipped (their children are handled via SetNapChildCaps).
        //     Runs unconditionally so a hidden app's tree stays fully napped as it spawns
        //     new renderers — the "nap all of it" half of the atomic nap/restore.
        // Built once — ResolveAppRootPid needs to look processes up per ascent step.
        var byIdForRoot = new Dictionary<int, Process>();
        foreach (var p in all) { try { byIdForRoot[p.Id] = p; } catch { } }

        foreach (int parentPid in _throttledPids.Keys.ToList())
        {
            if (!_napBuckets.Is(parentPid, NapReason.Minimized) && !_napBuckets.Is(parentPid, NapReason.Tray)) continue;

            // Re-root at the app's TOP process rather than at whichever process owns the window,
            // so a parent and its sibling helpers go down with it (see ResolveAppRootPid).
            int appRoot = ResolveAppRootPid(parentPid, parentMap, byIdForRoot,
                                            visibleOnMonitorPids, (int)foregroundPid);
            NapChildProcesses(appRoot, all, parentMap, protectedPids, audioPids, s, rules,
                              includeRoot: appRoot != parentPid, ownerPid: parentPid);
        }

        // 7. Re-enforce: re-apply throttle if a process raised its own priority back
        if (s.EnforceSettings)
        {
            foreach (int pid in _throttledPids.Keys.ToList())
            {
                _processNames.TryGetValue(pid, out string? nm);

                // If a security/AV or elevated process somehow ended up in the throttle list
                // (e.g. it was running when settings changed, or the guard was just enabled),
                // restore it and evict it immediately instead of fighting it in a priority loop.
                if (nm != null && IsSecurityCritical(nm))
                {
                    TryRestoreProcess(pid);
                    _log.Warn("TaskSleepService",
                        $"Re-enforce: security process {nm} (PID {pid}) found in throttle list — restored and evicted");
                    continue;
                }
                if (s.ElevatedProcessGuardEnabled && IsElevatedOrSystemProcess(pid))
                {
                    TryRestoreProcess(pid);
                    ClearNapState(pid);
                    AddEvent(nm ?? $"PID {pid}", pid, "Restored", "elevated process — auto-protected");
                    continue;
                }

                IntPtr h = OpenProcess(
                    PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    uint current = GetPriorityClass(h);
                    if (current != 0 && current != IDLE_PRIORITY_CLASS)
                    {
                        // Only a raise to an ELEVATED class (Above-Normal / High / Real-time)
                        // counts as an app "aggressively fighting" its nap. A drift back to
                        // Normal / Below-Normal is routine OS / Chromium behaviour — multi-
                        // process Electron apps (ChatGPT, Discord, etc.) do it constantly —
                        // so it gets re-napped silently and NEVER auto-whitelisted. That was
                        // the bug: counting any non-Idle priority whitelisted half the apps.
                        bool elevated = current == ABOVE_NORMAL_PRIORITY_CLASS
                                     || current == HIGH_PRIORITY_CLASS
                                     || current == REALTIME_PRIORITY_CLASS;

                        // Whitelist only after SUSTAINED elevated fighting (6 raises / 90 s):
                        // by then the app genuinely needs high priority (real-time audio, a
                        // game, etc.) and we should stop fighting it.
                        bool thresholdHit = elevated &&
                            _reEnforceCounter.Record(pid, TimeSpan.FromSeconds(90), 6);

                        if (thresholdHit)
                        {
                            TryRestoreProcess(pid);
                            ClearNapState(pid);
                            _reEnforceCounter.Reset(pid);
                            if (nm != null)
                            {
                                _napSuppressed.Add(nm);
                                ProcessAutoWhitelisted?.Invoke(nm);
                            }
                            AddEvent(nm ?? $"PID {pid}", pid, "Auto-whitelisted", "kept forcing high priority — removed from nap");
                            _log.Info("TaskSleepService", $"Auto-whitelisted {nm ?? $"PID {pid}"} after repeated elevated-priority re-enforce");
                        }
                        else
                        {
                            // Re-assert the nap. Routine non-elevated drift doesn't accumulate
                            // toward the whitelist and isn't logged (avoids per-tick spam).
                            SetPriorityClass(h, IDLE_PRIORITY_CLASS);
                            if (elevated)
                                AddEvent(nm ?? $"PID {pid}", pid, "Re-enforced", "process raised its own priority");
                            else
                                _reEnforceCounter.Reset(pid);
                        }
                    }
                    else
                    {
                        // Process stayed at idle — reset its counter
                        _reEnforceCounter.Reset(pid);
                    }

                    // ── Reinforcement: re-assert the kernel CPU cap every tick ─────
                    // A sandboxed/multi-process app could otherwise drift; re-asserting
                    // the rate cap each enforce tick makes it much harder to escape.
                    // Only for pids STILL napped after the priority re-enforce above
                    // (skips ones we just restored / auto-whitelisted). These are never
                    // mid-brief-wake (BeginBriefWake removes them from _throttledPids),
                    // so the nap-level clamp is always the correct target.
                    if (_throttledPids.ContainsKey(pid) &&
                        s.NappedCpuCapEnabled && s.NappedCpuCapPercent > 0 &&
                        _cpuCapJobs.TryGetValue(pid, out IntPtr hjob) && hjob != IntPtr.Zero)
                    {
                        UpdateCpuCap(pid, Math.Clamp(s.NappedCpuCapPercent, 1, 100));
                    }

                    // ── Reinforcement: re-assert E-core affinity ───────────────────
                    // Priority and the kernel CPU cap were both re-asserted every tick, but
                    // affinity never was. Plenty of apps call SetProcessAffinityMask on
                    // themselves — when they spin up worker pools, on config reload, or as a
                    // multi-process app launching a new child — and doing so silently undoes
                    // the E-core confinement while the process still counts as fully napped.
                    // That is the "sometimes not everything ends up on the E-cores" case: it
                    // WAS moved, then drifted back, and nothing put it there again.
                    //
                    // Only for pids we actually moved (_originalAffinities holds the value we
                    // captured), so this can never confine a process Systema never touched.
                    if (_throttledPids.ContainsKey(pid) && _originalAffinities.ContainsKey(pid))
                    {
                        UIntPtr wantMask = GetOrDetectECoreMask(s.DetectECores);
                        if (wantMask != UIntPtr.Zero &&
                            GetProcessAffinityMask(h, out UIntPtr nowMask, out _) &&
                            nowMask != wantMask)
                        {
                            SetProcessAffinityMask(h, wantMask);
                            AddEvent(nm ?? $"PID {pid}", pid, "Re-enforced", "affinity drifted off the E-cores");
                        }
                    }
                }
                catch (Exception ex) { _log.Warn("TaskSleepService", $"Re-enforce failed for PID {pid}: {ex.Message}"); }
                finally { CloseHandle(h); }
            }
        }

        // 7b. Diagnostic: surface why a high-CPU app isn't being capped.
        DiagnoseHighCpuNotCapped(cpuMap, protectedPids, s);

        // 8. Build and publish monitoring snapshot
        BuildAndPublishSnapshot(sysCpu, protectedPids, s, freeRamMb, ramPressure);

        // Use the count captured by the snapshot, not a fresh _throttledPids.Count.
        // Re-reading the dictionary here lets a process exit between the snapshot
        // build and this line slip into the gap, which is why the bottom status
        // line and the top "X napping" pill kept showing off-by-one counts.
        int count = _latestSnapshot?.TotalThrottled ?? _throttledPids.Count;
        Notify($"Task Sleep active — {count} {(count == 1 ? "process napping" : "processes napping")}.  System CPU: {sysCpu:F0}%");

        // Persist the current napped set for crash recovery (only rewrites the file when it changed).
        PersistNapJournalIfChanged();
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
    /// Samples every process in one NtQuerySystemInformation kernel call and returns
    /// pid → CPU% since the previous sample. Typical cost is &lt; 5 ms even on a box
    /// with 400+ processes — the old OpenProcess-per-PID path took 200-900 ms.
    ///
    /// Side effects (monitor-thread only):
    /// • <see cref="_lastCpuPercent"/>, <see cref="_processNames"/>,
    ///   <see cref="_pidCreationTimes"/> are updated for every sampled PID.
    /// • <see cref="_accessDeniedPids"/> is cleared for any PID that appeared in the
    ///   kernel output — the sampler never fails per-process because it doesn't
    ///   open any handles. Access-denied backoff is still driven by throttle /
    ///   restore call sites that actually do OpenProcess.
    /// </summary>
    private Dictionary<int, double> SampleAllProcessCpu(Process[] all)
    {
        var cpuMap = new Dictionary<int, double>(all.Length);
        var samples = _ntSampler.Sample();

        foreach (var s in samples)
        {
            cpuMap[s.Pid]              = s.CpuPercent;
            StateFor(s.Pid).LastCpuPercent     = s.CpuPercent;
            _pidCreationTimes[s.Pid]   = s.CreationTime100ns;

            if (s.ImageName is string raw && raw.Length > 0)
            {
                // Strip path and .exe suffix to match Process.ProcessName semantics
                int slash = raw.LastIndexOfAny(new[] { '\\', '/' });
                string name = slash >= 0 ? raw.Substring(slash + 1) : raw;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - 4);
                _processNames[s.Pid] = name;
            }

            // Fresh sample successful — reset any lingering access-denied backoff
            if (TryState(s.Pid, out var adRm)) adRm.AccessDenied = null;
        }

        return cpuMap;
    }

    /// <summary>
    /// Returns true when <paramref name="pid"/> still refers to the same process
    /// that was seen at the last sample. If the PID has been reused (different
    /// CreationTime) or is unknown, returns false — callers should treat the PID
    /// as stale and drop any associated throttle / cap state.
    /// </summary>
    private bool ProcessIdentityMatches(int pid, long expectedCreationTime)
    {
        return _pidCreationTimes.TryGetValue(pid, out long actual)
            && actual == expectedCreationTime;
    }

    private void CleanupDeadProcesses(HashSet<int> livePids)
    {
        // Let the kernel sampler drop its own baselines first
        _ntSampler.Prune(livePids);

        // Single pass: compute dead PIDs once, then remove from all dictionaries.
        // Use _pidCreationTimes as the authoritative domain — it tracks every PID
        // the sampler has ever seen, so nothing is missed.
        var dead = _pidCreationTimes.Keys.Where(pid => !livePids.Contains(pid)).ToList();
        foreach (int pid in dead)
        {
            DropState(pid);       // single cleanup for ALL per-PID ProcessState fields
            _pidCreationTimes.Remove(pid);
            _originalAffinities.TryRemove(pid, out _);
            _originalGpuPriority.TryRemove(pid, out _);
            _processNames.Remove(pid);
            ClearNapState(pid);   // _napBuckets + brief-wake timer dicts (not yet in ProcessState)
            _minimizeGraceSince.Remove(pid);
            _trayGraceSince.Remove(pid);
            _reEnforceCounter.Reset(pid);
            RemoveCpuCap(pid);
        }
    }

    // ── Throttle / Restore ─────────────────────────────────────────────────────

    private bool TryThrottle(Process proc, TaskSleepSettings s,
        Dictionary<string, TaskSleepAppRule> rules, bool forceMaxThrottle = false)
    {
        // ── FINAL SAFETY GATE — double-check critical processes before throttling ──
        // Even if a process made it through ShouldSkip, we have a second opportunity
        // to reject it here to prevent system corruption. This catches edge cases where
        // a process wasn't in our static lists but is actually critical (e.g., a new
        // Windows Update service in an OS update).
        if (proc.Id == OwnPid) return false;   // Systema must never throttle itself (PID guard, name-independent)
        if (IsSystemProcess(proc) || IsElevatedOrSystemProcess(proc.Id))
        {
            _log.Warn("TaskSleepService",
                $"SAFETY: Blocked throttle of critical process '{proc.ProcessName}' (PID {proc.Id}) in TryThrottle gate — this should never happen. Check ShouldSkip logic.");
            return false;
        }

        IntPtr handle = OpenProcess(
            PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
            false, proc.Id);

        if (handle == IntPtr.Zero) return false;

        try
        {
            uint original = GetPriorityClass(handle);
            if (original == 0) return false;

            // ── Determine effective settings (per-app overrides global) ────────
            // forceMaxThrottle = true for minimize-nap and tray-nap (full throttle always)
            // forceMaxThrottle = false for CPU-triggered throttle (follows user settings)
            // GPU priority IS now lowered for napped apps (Idle) via LowerNapGpuPriority,
            // but only on Windows 11+ where the present-queue ordering that used to cause
            // VSync tearing was fixed. It's lower-only (never raise here) and reversible.
            bool   lowerCpu    = forceMaxThrottle || s.LowerCpuPriority;
            bool   lowerIo     = forceMaxThrottle || s.LowerIoPriority;
            bool   lowerMem    = forceMaxThrottle || s.LowerMemoryPriority;
            // Both branches came out the same: the non-force branch omitted DetectECores, but
            // GetOrDetectECoreMask gates on it anyway, so the ternary only looked like it meant
            // something. One expression, one meaning.
            bool   moveToECores = s.MoveToECores && s.DetectECores;
            bool   effMode     = forceMaxThrottle || s.EnableEfficiencyMode;
            // Soft nap: use lighter throttle classes when user requests it (not for force-max)
            uint   cpuClass    = (!forceMaxThrottle && s.SoftNapEnabled) ? BELOW_NORMAL_PRIORITY_CLASS : IDLE_PRIORITY_CLASS;
            int    ioLevel     = (!forceMaxThrottle && s.SoftNapEnabled) ? IO_PRIORITY_LOW : IO_PRIORITY_VERY_LOW;

            if (!forceMaxThrottle && rules.TryGetValue(proc.ProcessName, out var rule))
            {
                if (rule.CpuPriority != null)
                    { lowerCpu = true; cpuClass = ParseCpuPriorityClass(rule.CpuPriority); }
                if (rule.IoPriority  != null)
                    { lowerIo  = rule.IoPriority  != "Normal"; ioLevel  = ParseIoPriority(rule.IoPriority); }
                if (rule.Affinity    != null)
                    moveToECores = rule.Affinity == "E-cores";
                if (rule.EfficiencyMode.HasValue)
                    effMode = rule.EfficiencyMode.Value;
            }

            // ── Apply ─────────────────────────────────────────────────────────
            bool changed = false;
            // The value we RECORD as this process's "original" (to restore on wake) must never
            // be sub-normal. If the process was already at Idle / Below-Normal when we napped it
            // — Chromium/Electron lowers its OWN backgrounded renderers to Idle, or it was
            // already napped — recording that value means restore sets Idle→Idle (a no-op) and
            // then drops it from tracking: the process is stranded at the nap floor, untracked,
            // and the UI shows the app as "not napped" while parts never come back. A napped
            // USER app always restores to at least Normal; if the app wants it lower (e.g. a
            // background renderer) it re-lowers itself. (Raw `original` is still used for the
            // change-detection comparisons below — only the STORED value is normalized.)
            uint storedOriginal = (original == 0
                                || original == IDLE_PRIORITY_CLASS
                                || original == BELOW_NORMAL_PRIORITY_CLASS)
                                ? NORMAL_PRIORITY_CLASS : original;

            if (lowerCpu && original != cpuClass)
            {
                if (SetPriorityClass(handle, cpuClass))
                {
                    _throttledPids.TryAdd(proc.Id, storedOriginal);
                    changed = true;
                }
            }

            if (effMode)
            {
                SetEfficiencyMode(handle, true);
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
                // E-core steering only — affinity is NOT used to confine on CPUs
                // without E-cores (the kernel CPU rate cap is the hard limiter there).
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

            if (lowerMem)
            {
                SetMemoryPriority(handle, MEMORY_PRIORITY_LOWEST);
                _throttledPids.TryAdd(proc.Id, storedOriginal);
                changed = true;
            }

            if (changed)
            {
                // Immediately reclaim the process's physical RAM pages so the OS can
                // give them to the foreground app without waiting for the pager.
                if (s.TrimWorkingSet) TrimProcessWorkingSet(handle);

                // Apply (or tighten) the kernel CPU cap via Job Object.
                // If a cap is already attached (e.g. we're re-napping after a brief wake,
                // where BeginBriefWake kept the job alive at BriefWakeCpuCapPercent),
                // UpdateCpuCap tightens it back to NappedCpuCapPercent without tearing
                // down the job object. Fresh naps go through ApplyCpuCap.
                if (s.NappedCpuCapEnabled && s.NappedCpuCapPercent > 0)
                {
                    int tightCap = Math.Clamp(s.NappedCpuCapPercent, 1, 100);
                    if (_cpuCapJobs.ContainsKey(proc.Id))
                        UpdateCpuCap(proc.Id, tightCap);
                    else
                        ApplyCpuCap(proc.Id, tightCap);
                }

                // Drop the napped app's GPU scheduling priority to Idle so the GPU goes
                // to the foreground app (Win11+ only, reversible). Restored on wake.
                LowerNapGpuPriority(handle, proc.Id);
            }

            return changed;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// Single cleanup path for a process leaving nap (woken, exited, or brief-wake released).
    /// Removes the pid from every nap CATEGORY bucket + brief-wake timer + the deep-sleep timer
    /// in one place. Removing from a bucket the pid isn't in is a harmless no-op, so this is safe
    /// to call regardless of which category the pid was napped under — which is the whole point:
    /// it eliminates the "each wake branch must remember the exact subset of buckets to scrub"
    /// fragility that caused stranded processes (tray helpers, Firefox tree members).
    ///
    /// Deliberately does NOT touch: _throttledPids (original priority — restored by
    /// TryRestoreProcess), _restoredAt (cooldown, set AFTER wake), grace timers (their own
    /// reset lifecycle), or _parentOfNapChild (read by the caller to decide event logging,
    /// and managed by RestoreNapChildren).
    /// </summary>
    private void ClearNapState(int pid)
    {
        _napBuckets.Clear(pid);
        _nextBriefWakeAt.Remove(pid);
        _briefWakeEndAt.Remove(pid);
        _trayNextBriefWakeAt.Remove(pid);
        _trayBriefWakeEndAt.Remove(pid);
        if (TryState(pid, out var st)) st.NapSince = null;
    }

    /// <summary>Records a process as napped under a single category. The reason-specific
    /// brief-wake / deep-sleep timers are set by the caller at the nap site.</summary>
    private void MarkNap(int pid, NapReason reason) => _napBuckets.Mark(pid, reason);

    /// <summary>
    /// The one full-wake sequence shared by every "this process should be awake now" path:
    /// the direct wake in the main loop, the sibling bulk-restore, and the whole-app cluster
    /// sweep. Restores the process (or releases its brief-wake cap if it was mid-brief-wake),
    /// clears EVERY piece of nap tracking, and wakes any children it had napped. Callers
    /// decide whether/how to log an event afterward (the messages differ per path).
    ///
    /// Having a single body here means there is exactly one place that has to get "fully
    /// wake a process" right — the previous three hand-copied versions were a standing
    /// invitation for them to drift apart.
    /// </summary>
    private void WakeFully(int pid)
    {
        if (_throttledPids.ContainsKey(pid)) TryRestoreProcess(pid);   // release priority/EcoQoS/GPU/cap
        else                                 FullyRestoreFromBriefWake(pid);
        var wst = StateFor(pid);
        wst.RestoredAt = DateTime.UtcNow;       // 5 s cooldown vs immediate re-nap
        wst.LowCpuTickCount = 0;                // reset smart-nap counter
        ClearNapState(pid);                     // all nap buckets + brief-wake + deep-sleep timers
        _minimizeGraceSince.Remove(pid);
        _trayGraceSince.Remove(pid);
        wst.NapChildParent = null;
        RestoreNapChildren(pid);                // and restore anything napped under it
    }

    private void TryRestoreProcess(int pid)
    {
        if (!_throttledPids.TryRemove(pid, out uint original)) return;

        // Process is no longer napped — drop it from deep-sleep tracking so
        // a subsequent re-nap starts the deep-sleep timer (and trim) fresh.
        if (TryState(pid, out var dsRst)) dsRst.DeepSleepTrimmed = false;

        IntPtr handle = OpenProcess(
            PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
            false, pid);

        if (handle == IntPtr.Zero)
        {
            _originalAffinities.TryRemove(pid, out _);
            _originalGpuPriority.TryRemove(pid, out _);
            RemoveCpuCap(pid);
            return;
        }

        try
        {
            if (original != 0) SetPriorityClass(handle, original);
            SetEfficiencyMode(handle, false);
            SetIoPriorityLevel(handle, IO_PRIORITY_NORMAL);
            SetMemoryPriority(handle, MEMORY_PRIORITY_NORMAL);
            RestoreNapGpuPriority(handle, pid);   // restore GPU scheduling priority

            if (_originalAffinities.TryRemove(pid, out UIntPtr origAffinity))
            {
                try { SetProcessAffinityMask(handle, origAffinity); }
                catch (Exception ex) { _log.Warn("TaskSleepService", $"Restore affinity failed for PID {pid}: {ex.Message}"); }
            }

            // Release CPU cap job object
            RemoveCpuCap(pid);
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"Restore PID {pid} failed: {ex.Message}");
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// Transitions a napped process into the brief-wake state: lifts priority / affinity /
    /// IO / memory throttles but KEEPS the Job Object cap alive, loosening it to
    /// <c>BriefWakeCpuCapPercent</c>. This prevents the CPU spike that would occur if the
    /// cap were fully removed at wake time — a common complaint where napped apps would
    /// briefly peg a core before being re-throttled. The kernel cap stays in force
    /// continuously through the nap → wake → re-nap cycle; only its rate changes.
    /// </summary>
    private void BeginBriefWake(int pid, TaskSleepSettings s)
    {
        if (!_throttledPids.TryRemove(pid, out uint original)) return;

        // Park the TRUE pre-nap priority for the window. The pid leaves _throttledPids (that's
        // how the brief-wake state is detected), so without this the original would be lost and
        // the re-nap path would re-capture the napped Idle priority as the "original".
        StateFor(pid).NappedOriginalCpu = original;

        // A brief wake ONLY widens the kernel CPU cap for the window. It deliberately changes
        // NO priorities — not CPU, I/O, memory, or GPU — and doesn't toggle EcoQoS or affinity.
        // The app keeps its entire napped throttle profile; only its CPU budget loosens. This is
        // the whole point of this change: the old behaviour bounced priority Idle↔Normal every
        // brief-wake cycle, and that churn caused inconsistency and scheduling hitches.
        if (s.NappedCpuCapEnabled && s.BriefWakeCpuCapPercent > 0)
            UpdateCpuCap(pid, Math.Clamp(s.BriefWakeCpuCapPercent, 1, 100));

        // Brief wake faults pages back into the working set — clear the deep-sleep trim marker
        // so the next compress sweep after the wake re-trims.
        if (TryState(pid, out var dsBw)) dsBw.DeepSleepTrimmed = false;

        // Children napped alongside this parent get the same loosened cap for the window.
        if (s.NappedCpuCapEnabled && s.BriefWakeCpuCapPercent > 0)
            SetNapChildCaps(pid, s.BriefWakeCpuCapPercent);
    }

    /// <summary>
    /// Fully restores a process that is currently mid-brief-wake (the user opened / focused it
    /// or it started audio). Brief wakes no longer lift any throttle except the CPU cap, so a
    /// real wake here is a FULL restore: re-seat the parked original priority into
    /// <see cref="_throttledPids"/> and reuse the standard restore path, which puts CPU / I-O /
    /// memory / GPU / affinity back and releases the kernel cap.
    /// </summary>
    private void FullyRestoreFromBriefWake(int pid)
    {
        // Re-seat the parked original-priority record so TryRestoreProcess restores to the true
        // pre-nap value (default Normal if somehow missing), then run the standard full restore.
        _throttledPids[pid] = StateFor(pid).NappedOriginalCpu ?? NORMAL_PRIORITY_CLASS;
        StateFor(pid).NappedOriginalCpu = null;
        TryRestoreProcess(pid);                     // priority / I-O / memory / GPU / affinity + RemoveCpuCap
        ClearNapState(pid);                         // all nap buckets + brief-wake + deep-sleep timers
        StateFor(pid).RestoredAt = DateTime.UtcNow;         // 5 s cooldown against immediate re-nap
        // Restore any child processes that were napped when this parent was napped —
        // otherwise Steam's helpers / Electron renderers stay stuck at 3% while the
        // parent is running freely, which produces the exact "super slow app" symptom
        // the main-loop fix addresses. Harmless if parent had no napped children.
        RestoreNapChildren(pid);
    }

    /// <summary>
    /// Creates a Windows Job Object with a hard CPU rate cap and assigns the process to it.
    /// Uses kernel-level enforcement — the OS itself caps the process's CPU time slices.
    ///
    /// On Windows 11 most processes already live in an implicit job. When our
    /// <c>AssignProcessToJobObject</c> is refused (Chromium sandbox, UWP containers,
    /// non-nestable jobs, or access denied), we mark the PID as "cap skipped" via the
    /// <see cref="IntPtr.Zero"/> sentinel and let the existing priority / EcoQoS /
    /// E-core affinity / I-O / memory-priority throttles do the work. No suspend-based
    /// fallback is used — it would fight the kernel scheduler and risked hanging
    /// windowed GPU/COM workloads (WDDM flip queue stalls, Intel/NVIDIA tools freezes).
    /// </summary>
    private void ApplyCpuCap(int pid, int capPercent)
    {
        if (pid == OwnPid) return;                 // never cap Systema's own CPU (final, name-independent gate)
        if (_cpuCapJobs.ContainsKey(pid)) return; // already capped or sentinel-marked
        try
        {
            // AssignProcessToJobObject requires PROCESS_SET_QUOTA AND PROCESS_TERMINATE
            // on the target handle. Missing PROCESS_TERMINATE was silently failing Assign
            // for every process, leaving the kernel cap unattached — apps like Steam
            // downloading a game could blow past the cap freely.
            IntPtr hProcess = OpenProcess(
                PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA | PROCESS_TERMINATE,
                false, pid);
            if (hProcess == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                _log.Info("TaskSleepService", $"ApplyCpuCap: OpenProcess denied for PID {pid} (err {err}) — cap skipped, other throttles active");
                _cpuCapJobs[pid] = IntPtr.Zero;
                return;
            }
            try
            {
                IntPtr hJob = CreateJobObjectW(IntPtr.Zero, null);
                if (hJob == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    _log.Warn("TaskSleepService", $"ApplyCpuCap: CreateJobObject failed for PID {pid} (err {err}) — cap skipped");
                    _cpuCapJobs[pid] = IntPtr.Zero;
                    return;
                }

                var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                {
                    ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                    CpuRate      = (uint)(capPercent * 100), // hundredths of a percent
                };

                if (!SetInformationJobObject(hJob, JobObjectCpuRateControlInformation,
                        ref info, Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
                {
                    int err = Marshal.GetLastWin32Error();
                    _log.Warn("TaskSleepService", $"ApplyCpuCap: SetInformationJobObject failed for PID {pid} (err {err}) — cap skipped");
                    CloseHandle(hJob);
                    _cpuCapJobs[pid] = IntPtr.Zero;
                    return;
                }

                if (AssignProcessToJobObject(hJob, hProcess))
                {
                    _cpuCapJobs[pid] = hJob;
                }
                else
                {
                    // Win11 permits nested jobs for most processes, but Chromium sandbox,
                    // some UWP containers, and a few AV products still refuse. Log the
                    // Win32 error so we can tell "permission issue" apart from "sandbox
                    // refused". The priority / EcoQoS / E-core / memory throttles still apply.
                    int err = Marshal.GetLastWin32Error();
                    _log.Warn("TaskSleepService", $"ApplyCpuCap: AssignProcessToJobObject failed for PID {pid} (err {err}) — hard cap unavailable, using soft throttles only");
                    CloseHandle(hJob);
                    _cpuCapJobs[pid] = IntPtr.Zero;
                }
            }
            finally { CloseHandle(hProcess); }
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"ApplyCpuCap PID {pid} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the CPU cap on an already-capped process (e.g. tighten for deep sleep).
    /// If the process has no job cap, does nothing.
    /// </summary>
    private void UpdateCpuCap(int pid, int capPercent)
    {
        if (pid == OwnPid) return;                 // never cap Systema's own CPU
        if (!_cpuCapJobs.TryGetValue(pid, out IntPtr hJob) || hJob == IntPtr.Zero) return;
        try
        {
            var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                CpuRate      = (uint)(capPercent * 100),
            };
            if (!SetInformationJobObject(hJob, JobObjectCpuRateControlInformation,
                    ref info, Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
            {
                _log.Warn("TaskSleepService", $"UpdateCpuCap: failed for PID {pid} (error {Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"UpdateCpuCap PID {pid} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Releases the CPU cap on a napped process.
    /// <para>
    /// CRITICAL Windows subtlety: closing a Job Object handle does NOT release the CPU
    /// cap on processes assigned to the job. Per MSDN, "the job object remains in the
    /// system until the last process assigned to the job has terminated" — with all its
    /// limits still in force. A process cannot be removed from a job once assigned.
    /// </para>
    /// <para>
    /// So we MUST first raise the job's CPU rate to 100% (effectively lifting the cap)
    /// BEFORE closing our handle. Otherwise the process stays stuck at the napped cap
    /// (3% by default) until it exits — which is exactly the "Steam goes super slow
    /// after wake, doesn't go high CPU" symptom the user kept reporting.
    /// </para>
    /// </summary>
    private void RemoveCpuCap(int pid)
    {
        if (_cpuCapJobs.Remove(pid, out IntPtr hJob) && hJob != IntPtr.Zero)
        {
            try
            {
                // Raise cap to 100.00% (10000 hundredths) BEFORE closing the handle.
                // This is the ONLY way to release the limit without killing the process.
                var lift = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                {
                    ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                    CpuRate      = 10000, // 100% of aggregate CPU — effectively no cap
                };
                if (!SetInformationJobObject(hJob, JobObjectCpuRateControlInformation,
                        ref lift, Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
                {
                    _log.Warn("TaskSleepService",
                        $"RemoveCpuCap: failed to lift cap for PID {pid} (err {Marshal.GetLastWin32Error()}) — closing handle anyway");
                }
            }
            catch (Exception ex) { _log.Warn("TaskSleepService", $"RemoveCpuCap: lift-before-close failed for PID {pid}: {ex.Message}"); }

            try { CloseHandle(hJob); }
            catch (Exception ex) { _log.Warn("TaskSleepService", $"RemoveCpuCap: CloseHandle failed for PID {pid}: {ex.Message}"); }
        }
    }

    private void RestoreAll()
    {
        // Restore EVERY napped process — including any currently mid-brief-wake. BeginBriefWake
        // removes a pid from _throttledPids (parking its true priority in ProcessState), so
        // iterating _throttledPids alone would strand a process that happens to be in a brief
        // wake at this instant at full nap throttle (Idle priority / lowest RAM / Idle GPU) after
        // Systema stops, disables, or pauses for an update. WakeFully handles both states:
        // throttled → TryRestoreProcess, brief-wake → FullyRestoreFromBriefWake (re-seat + restore).
        var allNapped = new HashSet<int>(_throttledPids.Keys);
        allNapped.UnionWith(_napBuckets.Pids);
        foreach (int pid in allNapped)
        {
            WakeFully(pid);
        }
        // Release all CPU cap job handles (skip sentinel zeros).
        // CRITICAL: must lift the cap to 100% FIRST, otherwise the processes in each
        // job stay capped at 3% until they exit (per MSDN — closing a job handle does
        // not remove limits from its processes). Without this, exiting Systema would
        // leave every previously napped app throttled indefinitely.
        var liftInfo = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
            CpuRate      = 10000, // 100%
        };
        int liftSize = Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>();
        foreach (var kvp in _cpuCapJobs)
        {
            if (kvp.Value == IntPtr.Zero) continue;
            try { SetInformationJobObject(kvp.Value, JobObjectCpuRateControlInformation, ref liftInfo, liftSize); } catch { }
            try { CloseHandle(kvp.Value); } catch { }
        }
        _cpuCapJobs.Clear();
        _originalAffinities.Clear();
        _originalGpuPriority.Clear();
        _napBuckets.ClearAll();
        _nextBriefWakeAt.Clear();
        _briefWakeEndAt.Clear();
        _trayNextBriefWakeAt.Clear();
        _trayBriefWakeEndAt.Clear();
        _minimizeGraceSince.Clear();
        _trayGraceSince.Clear();
        _state.Clear();   // drops every per-PID record (all migrated ProcessState fields) in one shot

        // Everything is restored — drop the crash-recovery journal so the next launch doesn't try to
        // "recover" PIDs we already un-throttled (which by then may belong to different processes).
        NapJournal.Clear();
        _lastJournalSig = -1;
    }

    // ── Crash-recovery journal ──────────────────────────────────────────────────

    /// <summary>
    /// Snapshots the currently-napped PIDs to the on-disk <see cref="NapJournal"/> whenever that set
    /// changes, so an unclean death (crash / force-quit / power loss) leaves a record the next launch
    /// can recover from. Cheap: a change-signature check skips the file write on ticks where nothing
    /// napped or woke. Called once per Tick.
    /// </summary>
    private void PersistNapJournalIfChanged()
    {
        // _throttledPids is the authoritative "we threw a throttle at this PID" set. Brief-wake PIDs
        // are temporarily out of it (parked in ProcessState.NappedOriginalCpu) but are still throttled,
        // so include them too — otherwise a crash mid-brief-wake would strand them.
        var pids = new List<int>(_throttledPids.Keys);
        foreach (var kv in _state)
            if (kv.Value.NappedOriginalCpu.HasValue && !_throttledPids.ContainsKey(kv.Key))
                pids.Add(kv.Key);

        int sig = pids.Count;
        foreach (int p in pids) sig = unchecked(sig * 31 + p);
        if (sig == _lastJournalSig) return;     // napped set unchanged — nothing to rewrite
        _lastJournalSig = sig;

        if (pids.Count == 0) { NapJournal.Clear(); return; }

        var entries = new List<NapJournal.Entry>(pids.Count);
        foreach (int pid in pids)
        {
            long ct  = _pidCreationTimes.TryGetValue(pid, out long c) ? c : 0;
            string n = _processNames.TryGetValue(pid, out var nm) ? nm : "";
            // The TRUE pre-nap priority lives in _throttledPids (throttled now) or, mid-brief-wake,
            // in ProcessState.NappedOriginalCpu — record it so recovery restores the real priority.
            uint origPrio = _throttledPids.TryGetValue(pid, out uint op) ? op
                          : (TryState(pid, out var pst) ? pst.NappedOriginalCpu ?? 0u : 0u);
            entries.Add(new NapJournal.Entry(pid, ct, n, origPrio));
        }
        NapJournal.Save(entries);
    }

    /// <summary>
    /// Runs ONCE at construction, before the monitor thread. If a previous Systema instance died
    /// without a graceful shutdown it left processes throttled with no live record; the NapJournal is
    /// that record. For each journaled process that is still alive AND still the SAME process
    /// (creation-time match — so a reused PID is never touched) we undo every throttle we can still
    /// reach via a process handle: priority, EcoQoS, I/O priority, memory priority, GPU scheduling
    /// priority, and CPU affinity. (The Job-Object CPU cap can't be lifted from a new process — its
    /// job is unreachable once the creator died — so that one is handled by PREVENTION on exit, not
    /// here; a cap orphaned by a hard kill clears when the app itself is closed.) The journal is then
    /// cleared, and the normal monitor re-naps whatever is still backgrounded.
    /// </summary>
    private void RecoverOrphanedNaps()
    {
        IReadOnlyList<NapJournal.Entry> entries;
        try { entries = NapJournal.Load(); }
        catch { return; }
        if (entries.Count == 0) { NapJournal.Clear(); return; }

        int recovered = 0;
        foreach (var e in entries)
        {
            try
            {
                // Identity gate: PID must still exist AND be the same process (creation time).
                long liveCt = GetProcessCreationTime(e.Pid);
                if (liveCt == 0) continue;                                   // exited (throttle died with it)
                if (e.CreationTime != 0 && liveCt != e.CreationTime) continue; // PID reused — different process

                IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, e.Pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    // Restore the REAL pre-nap priority recorded in the journal (0 = unknown → Normal).
                    SetPriorityClass(h, e.OriginalPriority != 0 ? e.OriginalPriority : NORMAL_PRIORITY_CLASS);
                    SetEfficiencyMode(h, false);
                    SetIoPriorityLevel(h, IO_PRIORITY_NORMAL);
                    SetMemoryPriority(h, MEMORY_PRIORITY_NORMAL);
                    if (GpuNapLoweringSupported)
                        try { D3DKMTSetProcessSchedulingPriorityClass(h, D3DKMT_GPU_PRIORITY_NORMAL); } catch { }
                    // Back to all cores: restore affinity to the full system mask.
                    if (GetProcessAffinityMask(h, out _, out UIntPtr sysMask) && sysMask != UIntPtr.Zero)
                        SetProcessAffinityMask(h, sysMask);
                    recovered++;
                }
                finally { CloseHandle(h); }
            }
            catch { /* one bad entry must not abort recovery of the rest */ }
        }

        NapJournal.Clear();
        _lastJournalSig = -1;
        if (recovered > 0)
            _log.Info("TaskSleepService",
                $"Nap recovery: restored {recovered} process(es) left throttled by a previous unclean shutdown.");
    }

    /// <summary>Process creation time as a 100-ns FILETIME (same units as the sampler / journal),
    /// or 0 if the PID no longer exists or can't be queried.</summary>
    private static long GetProcessCreationTime(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            return GetProcessTimes(h, out FILETIME ct, out _, out _, out _) ? FtToLong(ct) : 0;
        }
        finally { CloseHandle(h); }
    }


    /// <summary>
    /// Scans critical Windows services (Windows Update, diagnostics, licensing, etc.)
    /// and auto-protects them by adding to <see cref="_detectedCriticalServices"/>.
    /// This is a DEFENSIVE measure: if a new critical service appears in an OS update,
    /// we catch it and protect it automatically instead of discovering it by breaking
    /// that service (like we did with UsoSvc before v1.7.20).
    /// </summary>
    private void ScanAndProtectCriticalServices()
    {
        try
        {
            var criticalServicePatterns = new[]
            {
                // Windows Update family — all Uso* and WaaS* services
                "Uso", "Wuau", "WaaS", "Medic", "mus",
                // Core OS / licensing
                "Winlogon", "Lsass", "Services", "SmartScreen",
                // Component servicing
                "TrustedInstaller", "Wudf",
                // COM / RPC infrastructure — these are essential for all COM calls
                "RpcSs", "DcomLaunch",
            };

            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName FROM Win32_Service WHERE State = 'Running'");

            var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManagementObject service in searcher.Get())
            {
                var svcName = service["Name"] as string ?? "";
                var displayName = service["DisplayName"] as string ?? "";
                if (string.IsNullOrEmpty(svcName)) continue;

                // Check if this service name starts with any critical pattern
                bool isCritical = criticalServicePatterns.Any(pattern =>
                    svcName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase));

                if (isCritical && !SystemProcessNames.Contains(svcName))
                {
                    detected.Add(svcName);
                    _log.Info("TaskSleepService",
                        $"Auto-detected critical service for protection: '{svcName}' ({displayName})");
                }
            }

            _detectedCriticalServices = detected;
            if (detected.Count > 0)
                _log.Info("TaskSleepService", $"Startup: auto-detected and protected {detected.Count} critical services");
        }
        catch (Exception ex)
        {
            // Non-fatal — if service scan fails, we still have the static lists
            _log.Warn("TaskSleepService", $"ScanAndProtectCriticalServices failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Queries Windows Security Center (ROOT\SecurityCenter2\AntiVirusProduct) to discover
    /// which 3rd-party antivirus products are registered. Extracts the executable name from
    /// each product's signed path and adds it to <see cref="_detectedAvProcessNames"/> so
    /// those processes are always protected even if they are not in the static list.
    /// </summary>
    private void DetectRegisteredAntiviruses()
    {
        try
        {
            // Build a local set first, then swap atomically to avoid race with monitor thread.
            var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var searcher = new ManagementObjectSearcher(
                @"root\SecurityCenter2",
                "SELECT displayName, pathToSignedProductExe FROM AntiVirusProduct");

            foreach (ManagementObject av in searcher.Get())
            {
                var displayName = av["displayName"] as string ?? "unknown";
                var exePath     = av["pathToSignedProductExe"] as string ?? "";

                if (!string.IsNullOrEmpty(exePath))
                {
                    var exeName = System.IO.Path.GetFileNameWithoutExtension(exePath);
                    if (!string.IsNullOrEmpty(exeName) &&
                        !SecurityCriticalProcessNames.Contains(exeName))
                    {
                        detected.Add(exeName);
                        _log.Info("TaskSleepService",
                            $"SecurityCenter2: registered AV '{displayName}' → protecting '{exeName}'");
                    }
                    else
                    {
                        _log.Info("TaskSleepService",
                            $"SecurityCenter2: registered AV '{displayName}' (already in static list)");
                    }
                }
                else
                {
                    _log.Info("TaskSleepService",
                        $"SecurityCenter2: registered AV '{displayName}' (no exe path available)");
                }
            }

            // Atomic swap — monitor thread reads the reference via volatile field
            _detectedAvProcessNames = detected;
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService",
                $"SecurityCenter2 AV detection failed — falling back to static list only: {ex.Message}");
        }
    }

    // ── Foreground process tree ────────────────────────────────────────────────

    /// <summary>
    /// Builds a map of child PID → parent PID using a single Toolhelp32 snapshot.
    /// Used to nap child processes when their parent is minimize/tray-napped.
    /// </summary>
    private static Dictionary<int, int> BuildParentMap()
    {
        var map = new Dictionary<int, int>();
        try
        {
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snap, ref e))
                    do { map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; }
                    while (Process32Next(snap, ref e));
            }
            finally { CloseHandle(snap); }
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"BuildParentMap failed: {ex.Message}"); }
        return map;
    }

    /// <summary>
    /// Every PID in an app's process tree — the given root + all descendants (BFS over the
    /// child→parent map). Used to keep / nap / wake an app as a unit.
    /// </summary>
    private static List<int> AppTreePids(int rootPid, Dictionary<int, int> parentMap)
    {
        // Invert child → parent into parent → children for a downward BFS.
        var childrenOf = new Dictionary<int, List<int>>();
        foreach (var kv in parentMap)
        {
            if (!childrenOf.TryGetValue(kv.Value, out var lst)) childrenOf[kv.Value] = lst = new List<int>();
            lst.Add(kv.Key);
        }

        var result = new List<int>();
        var seen   = new HashSet<int> { rootPid };
        var queue  = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            result.Add(cur);
            if (childrenOf.TryGetValue(cur, out var kids))
                foreach (int k in kids) if (seen.Add(k)) queue.Enqueue(k);
        }
        return result;
    }

    /// <summary>
    /// Total CPU% across an app's whole process tree (root + every descendant). Multi-process apps
    /// keep the window-owner process near-idle while a content/GPU child does the work, so the
    /// owner's own CPU is NOT a reliable "is the app busy" signal — the busy-app check needs the
    /// tree total.
    /// </summary>
    private static double AppTreeCpu(int rootPid, Dictionary<int, double> cpuMap, Dictionary<int, int> parentMap)
    {
        double total = 0;
        foreach (int pid in AppTreePids(rootPid, parentMap))
            if (cpuMap.TryGetValue(pid, out double c)) total += c;
        return total;
    }

    /// <summary>
    /// Naps the ENTIRE descendant tree of a newly-napped parent (minimize/tray nap), so the
    /// whole app goes down as a unit instead of trickling process-by-process — the
    /// "it didn't nap all of Firefox" symptom. Walks the full tree (not just direct children)
    /// because Chromium/Firefox/Electron nest renderers a few levels deep. Members are skipped
    /// if foreground-protected, playing audio, whitelisted, or already napped. Restored
    /// together via <see cref="RestoreNapChildren"/> and the full-app wake sweep.
    /// </summary>
    /// <summary>
    /// Walks UP from the window-owning process to the top-most process that still belongs to the
    /// same app, and returns it (or the pid itself when it is already the root).
    ///
    /// Why: the nap tree used to be rooted at whichever process owns the window, and every walk
    /// went DOWNWARD from there. That is correct only when the window owner is also the app's top
    /// process. Steam is the common counter-example — steam.exe spawns steamwebhelper.exe and the
    /// helper owns the window — so napping walked down from the helper and left steam.exe and its
    /// sibling helpers running at full speed. That is the "app sleeps but not all of it" bug.
    ///
    /// Ascent is deliberately paranoid. It stops at anything the user can see, anything the OS
    /// owns, or anything we could not throttle anyway, so it can never climb into the shell:
    /// explorer.exe is in the system whitelist and most user apps are launched by it.
    /// </summary>
    private int ResolveAppRootPid(int pid, Dictionary<int, int> parentMap,
        Dictionary<int, Process> byId, HashSet<int> visiblePids, int foregroundPid)
    {
        int cur = pid;
        for (int depth = 0; depth < 4; depth++)          // bounded: never chase a long chain
        {
            if (!parentMap.TryGetValue(cur, out int parent)) break;
            if (parent <= 4 || parent == OwnPid || parent == cur) break;
            if (!byId.TryGetValue(parent, out var pproc)) break;      // parent already exited
            // Never ascend into something on screen or in front of the user.
            if (parent == foregroundPid || visiblePids.Contains(parent)) break;
            // Never ascend into the OS: shell, Windows binaries, elevated, or service accounts.
            if (IsSystemProcess(pproc) || IsWindowsSystemBinary(parent) ||
                IsElevatedOrSystemProcess(parent) || IsServiceAccount(parent)) break;
            cur = parent;
        }
        return cur;
    }

    /// <param name="parentPid">Where to WALK the tree from (the app root).</param>
    /// <param name="ownerPid">
    /// Who OWNS the resulting naps — defaults to parentPid. These differ once the tree is
    /// re-rooted upward: discovery starts at the app root, but every napped member is still
    /// tagged to the window-owning process, because that is the pid carrying the Minimized/Tray
    /// reason and therefore the one whose restore (and the orphan sweep in 6c) releases them.
    /// Tagging them to the root instead would strand the root and its siblings napped forever,
    /// since nothing ever un-naps the root directly.
    /// </param>
    private void NapChildProcesses(int parentPid, Process[] all, Dictionary<int, int> parentMap,
        HashSet<int> protectedPids, HashSet<int> audioPids, TaskSleepSettings s,
        Dictionary<string, TaskSleepAppRule> rules, bool includeRoot = false, int? ownerPid = null)
    {
        int owner = ownerPid ?? parentPid;
        // Build parent → children once, then BFS the whole subtree under parentPid.
        var childrenOf = new Dictionary<int, List<int>>();
        foreach (var kv in parentMap)
        {
            if (!childrenOf.TryGetValue(kv.Value, out var lst)) childrenOf[kv.Value] = lst = new List<int>();
            lst.Add(kv.Key);
        }
        var descendants = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(parentPid);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (childrenOf.TryGetValue(cur, out var kids))
                foreach (int k in kids)
                    if (descendants.Add(k)) queue.Enqueue(k);
        }
        // When the tree was re-rooted upward, the root itself is part of the app and must nap too.
        // It still passes through ShouldSkip/TryThrottle below, so every existing guard applies.
        if (includeRoot) descendants.Add(parentPid);
        if (descendants.Count == 0) return;

        var byId = new Dictionary<int, Process>();
        foreach (var p in all) { try { byId[p.Id] = p; } catch { } }

        foreach (int childId in descendants)
        {
            try
            {
                if (childId == owner) continue;                      // the owner is napped already
                if (_throttledPids.ContainsKey(childId)) continue;
                if (!byId.TryGetValue(childId, out var child)) continue;
                if (ShouldSkip(child, protectedPids, s, rules, audioPids)) continue;

                if (TryThrottle(child, s, rules, forceMaxThrottle: true))
                {
                    var childSt = StateFor(child.Id);
                    childSt.NapChildParent = owner;
                    childSt.NapSince ??= DateTime.UtcNow; // deep-sleep timer
                    double childCpu = childSt.LastCpuPercent ?? 0;
                    childSt.CpuAtThrottle = childCpu;
                    childSt.ThrottledAt = DateTime.UtcNow;
                    AddEvent(child.ProcessName, child.Id, "Child Nap", "app hidden — napping tree");
                }
            }
            catch (Exception ex) { _log.Warn("TaskSleepService", $"NapChildProcesses: failed for child PID {childId}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Restores all child processes that were napped as a result of the given parent being napped.
    /// Called when the parent is restored (user opened the app, audio detected, etc.).
    /// </summary>
    private void RestoreNapChildren(int parentPid)
    {
        var children = _state
            .Where(kv => kv.Value.NapChildParent == parentPid)
            .Select(kv => kv.Key)
            .ToList();
        foreach (int childPid in children)
        {
            if (TryState(childPid, out var st)) st.NapChildParent = null;
            TryRestoreProcess(childPid);
        }
    }

    /// <summary>
    /// Propagates a CPU-cap level to every nap-child of the given parent. Used so a
    /// parent's brief-wake (loosen → BriefWakeCpuCapPercent) and re-nap (tighten →
    /// NappedCpuCapPercent) transitions carry through to the children that were
    /// napped alongside it — otherwise a child doing the actual work (Steam download
    /// helper, browser/Electron renderer) stays pinned at the nap cap while the
    /// parent briefly wakes, so nothing progresses.
    /// <para>
    /// Only the kernel hard cap is modulated here. A child whose hard cap is a
    /// sentinel (Job Object refused) keeps its soft throttle — EcoQoS + idle priority
    /// — on continuously, so it stays bounded through the whole cycle regardless.
    /// UpdateCpuCap is a no-op for sentinel children.
    /// </para>
    /// </summary>
    private void SetNapChildCaps(int parentPid, int capPercent)
    {
        if (capPercent <= 0) return;
        int clamped = Math.Clamp(capPercent, 1, 100);
        var children = _state
            .Where(kv => kv.Value.NapChildParent == parentPid)
            .Select(kv => kv.Key)
            .ToList();
        foreach (int childPid in children)
            UpdateCpuCap(childPid, clamped);
    }

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
        // Track two sets: PIDs that have any iconic window, and PIDs that have any
        // visible non-iconic window. A PID is only "minimized" if it has at least one
        // iconic window AND zero visible non-iconic windows. This prevents Electron apps
        // (Discord, ChatGPT, Claude) from being misdetected: they create multiple windows
        // and only some may be iconic while the main content window is visible on screen.
        var hasIconic  = new HashSet<int>();
        var hasVisible = new HashSet<int>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid <= 4) return true;
                int iPid = (int)pid;

                if (IsIconic(hWnd))
                {
                    hasIconic.Add(iPid);
                }
                else if (IsWindowVisible(hWnd))
                {
                    uint exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                    if ((exStyle & WS_EX_TOOLWINDOW) == 0)
                        hasVisible.Add(iPid);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"GetMinimizedProcessIds: EnumWindows failed: {ex.Message}"); }

        // Only report PIDs that have iconic windows but NO visible non-iconic windows
        hasIconic.ExceptWith(hasVisible);
        return hasIconic;
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
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"GetTrayProcessIds: EnumWindows failed: {ex.Message}");
            return new HashSet<int>();
        }

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

    // ── Beta: Multi-monitor awareness ──────────────────────────────────────

    /// <summary>
    /// Returns PIDs of all processes that have a visible, non-iconic window with a
    /// non-zero size actually positioned on a monitor. Filters out Electron-style
    /// "ghost" windows that are WS_VISIBLE but zero-sized or positioned offscreen
    /// (common when apps like ChatGPT, Slack minimize to tray).
    /// </summary>
    private static HashSet<int> GetVisibleOnAnyMonitorPids()
    {
        var pids = new HashSet<int>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (IsWindowVisible(hWnd) && !IsIconic(hWnd))
                {
                    uint exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                    if ((exStyle & WS_EX_TOOLWINDOW) == 0)
                    {
                        // Verify the window has a meaningful size and is on a real monitor.
                        // Many Electron apps keep a WS_VISIBLE window at (0,0,0,0) or
                        // offscreen when "closed" to tray — these shouldn't count.
                        if (GetWindowRect(hWnd, out RECT rc))
                        {
                            int w = rc.Right - rc.Left;
                            int h = rc.Bottom - rc.Top;
                            if (w > 1 && h > 1 && MonitorFromWindow(hWnd, MONITOR_DEFAULTTONULL) != IntPtr.Zero)
                            {
                                GetWindowThreadProcessId(hWnd, out uint pid);
                                if (pid > 4) pids.Add((int)pid);
                            }
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"GetVisibleOnAnyMonitorPids: EnumWindows failed: {ex.Message}"); }
        return pids;
    }

    // ── Nap hidden apps: occlusion detection ───────────────────────────────────
    // Windows has no native "is this window covered?" API (macOS does), so we compute it. EnumWindows
    // returns top-level windows in Z-order, TOPMOST first. Walking that order we accumulate the union
    // of every OPAQUE visible window seen so far — which are exactly the windows ABOVE the current one
    // — and a window is "fully covered" when subtracting that accumulated region leaves nothing. A PID
    // is "hidden" only if it has at least one normal visible window AND every one of them is fully
    // covered, so an app with any uncovered window (its front window) is never treated as hidden.
    //
    // SAFE BY DESIGN — the cardinal sin is napping something you can still see, so we bias to
    // false-negatives:
    //   • A window counts as an OCCLUDER only when confidently opaque. Layered/alpha-blended windows
    //     and windows whose opacity we can't read are NOT occluders (we under-count coverage).
    //   • Cloaked windows (other virtual desktop / suspended UWP) are skipped entirely.
    //   • Any doubt → the window stays awake.
    private HashSet<int> GetHiddenProcessIds(HashSet<int> minimizedPids, HashSet<int> trayPids)
    {
        var hasUncovered = new HashSet<int>();   // pid has ≥1 normal window still (partly) visible → NOT hidden
        var hasCovered   = new HashSet<int>();   // pid has ≥1 normal window fully covered → hidden candidate

        IntPtr accumulated = CreateRectRgn(0, 0, 0, 0);   // union of opaque windows ABOVE the current one
        if (accumulated == IntPtr.Zero) return hasCovered;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
                    if (IsWindowCloaked(hWnd)) return true;                       // other desktop / suspended — not on screen
                    uint exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                    if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;          // tool windows don't participate
                    if (!GetWindowRect(hWnd, out RECT raw)) return true;
                    RECT rc = GetVisibleBounds(hWnd, raw);                       // visible frame, not the invisible-border rect
                    int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
                    if (w <= 1 || h <= 1) return true;                           // zero-size ghost window
                    if (MonitorFromWindow(hWnd, MONITOR_DEFAULTTONULL) == IntPtr.Zero) return true; // fully off-screen

                    GetWindowThreadProcessId(hWnd, out uint upid);
                    int pid = (int)upid;
                    bool normalAppWindow = pid > 4 && !minimizedPids.Contains(pid) && !trayPids.Contains(pid);

                    IntPtr cand = CreateRectRgn(rc.Left, rc.Top, rc.Right, rc.Bottom);
                    if (cand != IntPtr.Zero)
                    {
                        try
                        {
                            if (normalAppWindow)
                            {
                                IntPtr diff = CreateRectRgn(0, 0, 0, 0);
                                if (diff != IntPtr.Zero)
                                {
                                    try
                                    {
                                        // Fully covered ⇔ (this window − everything opaque above it) is empty.
                                        bool fullyCovered = CombineRgn(diff, cand, accumulated, RGN_DIFF) == NULLREGION;
                                        if (fullyCovered) hasCovered.Add(pid);
                                        else              hasUncovered.Add(pid);
                                    }
                                    finally { DeleteObject(diff); }
                                }
                            }
                            // This window occludes the ones below it only if it's confidently opaque.
                            if (IsOpaqueWindow(hWnd, exStyle))
                                CombineRgn(accumulated, accumulated, cand, RGN_OR);
                        }
                        finally { DeleteObject(cand); }
                    }
                }
                catch { /* skip one window; never abort the whole scan */ }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"GetHiddenProcessIds failed: {ex.Message}"); }
        finally { DeleteObject(accumulated); }

        // Hidden = has a fully-covered window AND no still-visible window.
        hasCovered.ExceptWith(hasUncovered);

        // Diagnostic: log only when the covered set changes, so a live test shows exactly
        // what the detector sees without spamming the log every tick.
        if (!hasCovered.SetEquals(_lastHiddenLog))
        {
            _lastHiddenLog = new HashSet<int>(hasCovered);
            if (hasCovered.Count == 0)
                _log.Info("TaskSleepService", "Nap hidden apps: nothing is fully covered right now");
            else
            {
                var names = new List<string>();
                foreach (int p in hasCovered)
                {
                    try { names.Add(Process.GetProcessById(p).ProcessName + "(" + p + ")"); }
                    catch { names.Add(p.ToString()); }
                }
                _log.Info("TaskSleepService", "Nap hidden apps: fully covered = " + string.Join(", ", names));
            }
        }
        return hasCovered;
    }

    private HashSet<int> _lastHiddenLog = new();

    /// <summary>Confidently opaque? Non-layered windows are. A layered window is only trusted as an
    /// occluder when it uses a constant alpha of 255 with no colour-key holes; per-pixel alpha or an
    /// unreadable state is treated as see-through (conservative — we'd rather under-count coverage).</summary>
    private static bool IsOpaqueWindow(IntPtr hWnd, uint exStyle)
    {
        if ((exStyle & WS_EX_LAYERED) == 0) return true;
        if (GetLayeredWindowAttributes(hWnd, out _, out byte alpha, out uint flags))
            return (flags & LWA_ALPHA) != 0 && alpha == 255 && (flags & LWA_COLORKEY) == 0;
        return false;   // UpdateLayeredWindow / per-pixel alpha — can't verify → not an occluder
    }

    /// <summary>True when DWM has cloaked the window (another virtual desktop, or a suspended UWP) —
    /// it isn't really on screen, so it neither occludes nor counts as a covered app window.</summary>
    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        try { if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0) return cloaked != 0; }
        catch { }
        return false;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int  CombineRgn(IntPtr dst, IntPtr src1, IntPtr src2, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint crKey, out byte bAlpha, out uint dwFlags);
    [DllImport("dwmapi.dll")] private static extern int  DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeRect(IntPtr hWnd, int attr, out RECT value, int size);
    private const int  RGN_OR = 2, RGN_DIFF = 4, NULLREGION = 1;
    private const uint WS_EX_LAYERED = 0x00080000, LWA_COLORKEY = 0x1, LWA_ALPHA = 0x2;
    private const int  DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>The window's real on-screen rectangle. GetWindowRect includes ~7px of invisible
    /// resize border on every side, which can leave a phantom uncovered sliver even when a window
    /// is visually 100% hidden. DWMWA_EXTENDED_FRAME_BOUNDS is the actual painted frame — using it
    /// for both the covered window and its occluders makes "completely covered" mean visibly covered.</summary>
    private static RECT GetVisibleBounds(IntPtr hWnd, RECT fallback)
    {
        try { if (DwmGetWindowAttributeRect(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT b, Marshal.SizeOf<RECT>()) == 0) return b; }
        catch { }
        return fallback;
    }

    /// <summary>
    /// Given a set of root PIDs, walks the process tree via CreateToolhelp32Snapshot
    /// and adds all descendant PIDs to the <paramref name="target"/> set.
    /// </summary>
    private void ExpandWithDescendants(HashSet<int> rootPids, HashSet<int> target)
    {
        try
        {
            var entries = new List<(int Pid, int ParentPid)>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return;

            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snap, ref e))
                    do { entries.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID)); }
                    while (Process32Next(snap, ref e));
            }
            finally { CloseHandle(snap); }

            // BFS: walk until no new children found
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (pid, parentPid) in entries)
                    if (target.Contains(parentPid) && target.Add(pid))
                        changed = true;
            }
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"ExpandWithDescendants failed: {ex.Message}"); }
    }

    // ── Beta: Process group awareness ────────────────────────────────────────

    /// <summary>
    /// Given the foreground PID, finds its process name and returns PIDs of ALL processes
    /// with the same name. E.g. if chrome.exe is focused, protects all chrome.exe instances.
    /// </summary>
    private static HashSet<int> GetProcessGroupPids(uint foregroundPid, Process[] all)
    {
        var result = new HashSet<int>();
        if (foregroundPid == 0) return result;

        string? fgName = null;
        foreach (var p in all)
        {
            try
            {
                if (p.Id == (int)foregroundPid)
                {
                    fgName = p.ProcessName;
                    break;
                }
            }
            catch (Exception ex) { _log.Warn("TaskSleepService", $"GetProcessGroupPids: could not read foreground process: {ex.Message}"); }
        }
        if (fgName == null) return result;

        // Protect:
        //  1. All PIDs with the exact same name (e.g. all chrome.exe)
        //  2. All PIDs whose name belongs to the same "app family" — shares a common
        //     prefix with the foreground process. This catches helper/child processes
        //     that use a different exe name but belong to the same app:
        //       steam → steamwebhelper, steamservice
        //       Discord → DiscordSystemHelper
        //       Epic → EpicWebHelper, EpicOnlineServicesUserHelper
        //       ChatGPT → (single name, still matched)
        //       firefox → (single name, still matched)
        //     The base name is the foreground process name stripped of common suffixes.
        string baseName = GetAppFamilyBaseName(fgName);

        foreach (var p in all)
        {
            try
            {
                if (string.Equals(p.ProcessName, fgName, StringComparison.OrdinalIgnoreCase) ||
                    IsAppFamilyMatch(p.ProcessName, baseName))
                    result.Add(p.Id);
            }
            catch (Exception ex) { _log.Warn("TaskSleepService", $"GetProcessGroupPids: could not read process {p.Id}: {ex.Message}"); }
        }
        return result;
    }

    // ── App Family matching ──────────────────────────────────────────────────
    // Many apps launch helper processes with different exe names:
    //   steam.exe → steamwebhelper.exe, steamservice.exe
    //   Discord.exe → DiscordSystemHelper.exe
    //   EpicGamesLauncher.exe → EpicWebHelper.exe, EpicOnlineServicesUserHelper.exe
    //   claude.exe → (children have same name, already handled)
    //
    // Strategy: extract the "base" app name (shortest meaningful prefix) from the
    // foreground process name, then match other processes whose name starts with it.
    // A minimum base length of 4 prevents overly broad matches (e.g. "ms" matching
    // hundreds of Microsoft processes).

    /// <summary>
    /// Known app family mappings: foreground process name → base prefix used to match
    /// helper processes. Covers apps where the helper name isn't a simple prefix of the
    /// main process (e.g. EpicGamesLauncher → Epic).
    /// </summary>
    private static readonly Dictionary<string, string> AppFamilyOverrides =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["EpicGamesLauncher"] = "Epic",
        ["EpicWebHelper"]     = "Epic",
        ["ChatGPT"]           = "ChatGPT",
        ["firefox"]           = "firefox",
        ["chrome"]            = "chrome",
        ["msedge"]            = "msedge",
        ["opera"]             = "opera",
        ["brave"]             = "brave",
    };

    /// <summary>
    /// Extracts the app family base name from a process name. If the name is in
    /// AppFamilyOverrides, uses that. Otherwise strips common suffixes (WebHelper,
    /// SystemHelper, Service, Helper, Crashpad, CrashHelper, etc.) to get the root.
    /// Returns the process name itself if no suffix is found (minimum 4 chars).
    /// </summary>
    private static string GetAppFamilyBaseName(string processName)
    {
        if (AppFamilyOverrides.TryGetValue(processName, out string? over))
            return over;

        // Strip known suffixes to find the root app name
        string[] suffixes = [
            "webhelper", "systemhelper", "helper", "service",
            "crashpad", "crashhelper", "renderer", "gpu",
            "broker", "utility", "agent", "updater", "watcher"
        ];

        string lower = processName.ToLowerInvariant();
        foreach (string suffix in suffixes)
        {
            if (lower.EndsWith(suffix) && lower.Length > suffix.Length)
            {
                string candidate = processName[..^suffix.Length];
                // Only accept if the base is at least 4 characters (avoid "ms", "hp", etc.)
                if (candidate.Length >= 4)
                    return candidate;
            }
        }

        // No suffix matched — use the full name as the base (at least 4 chars)
        return processName.Length >= 4 ? processName : "";
    }

    /// <summary>
    /// Returns true if a process name belongs to the same app family as the given base.
    /// A match occurs when the process name starts with the base name (case-insensitive)
    /// and the base is at least 4 characters long.
    /// </summary>
    private static bool IsAppFamilyMatch(string processName, string baseName)
    {
        if (baseName.Length < 4) return false;
        return processName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase);
    }


    /// <summary>
    /// Returns true if a process should be protected from minimize/tray nap due to audio.
    /// AlwaysActive processes (media players, recorders) are unconditionally protected.
    /// Comms apps (Discord, Teams, Slack, etc.) are only protected when they have an
    /// active audio session (i.e. in a call), not when just sitting idle.
    /// </summary>
    private bool IsAudioProtected(int pid, string processName, HashSet<int> audioPids)
    {
        // Always-active apps: unconditionally protected (media players, OBS, etc.)
        if (AlwaysActiveProcessNames.Contains(processName)) return true;
        // Core Audio says this PID has active audio — protect regardless of name
        if (audioPids.Contains(pid)) return true;
        // App family audio matching: if ANY PID in the same app family has audio,
        // protect this PID too. Browsers (Firefox, Chrome, Edge) use child processes
        // for audio — the main process might not have the audio session directly.
        if (AudioCapableAppNames.Contains(processName) || CommsProcessNames.Contains(processName))
        {
            string baseName = GetAppFamilyBaseName(processName);
            if (baseName.Length >= 3)
            {
                foreach (int aPid in audioPids)
                {
                    if (_processNames.TryGetValue(aPid, out string? audioName) &&
                        audioName != null && IsAppFamilyMatch(audioName, baseName))
                        return true;
                }
            }
        }
        // WASAPI exclusive mode: some app has exclusive audio access but we can't
        // identify which PID. Protect known audio-capable apps to be safe.
        if (_exclusiveModeDetectedAt != DateTime.MinValue &&
            (DateTime.UtcNow - _exclusiveModeDetectedAt).TotalSeconds <= 15 &&
            (CommsProcessNames.Contains(processName) || AudioCapableAppNames.Contains(processName)))
            return true;
        // Comms apps are NOT protected here when they have no active audio.
        // This lets idle Discord/Teams/Slack get napped like any other app.
        return false;
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
    /// Queries Windows Core Audio to find all PIDs with an Active (state=1) audio session
    /// on any render (playback) OR capture (microphone) endpoint. Inactive sessions (state=0)
    /// are NOT counted — many apps (browsers, Electron) register audio on startup without
    /// ever playing. Audio stickiness (30s memory) handles the chicken-and-egg case where
    /// throttling suppresses a previously-active audio stream.
    /// </summary>
    private HashSet<int> SampleActiveAudioPids()
    {
        var pids = new HashSet<int>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();
            bool anyExclusive = false;

            try
            {
            // Query BOTH playback (eRender=0) and capture (eCapture=1) endpoints.
            // Playback: apps playing music, video, game audio, call audio output
            // Capture:  apps using microphone — Discord calls, Zoom, Teams, OBS, etc.
            foreach (int dataFlow in new int[] { 0 /* eRender */, 1 /* eCapture */ })
            {
                if (enumerator.EnumAudioEndpoints(dataFlow, 1 /* DEVICE_STATE_ACTIVE */,
                        out IMMDeviceCollection devices) != 0) continue;

                try
                {
                devices.GetCount(out uint deviceCount);
                for (uint d = 0; d < deviceCount; d++)
                {
                    if (devices.Item(d, out IMMDevice device) != 0) continue;

                    try
                    {
                    Guid iid = IID_IAudioSessionManager2;
                    if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object mgr2Obj) != 0)
                        continue;

                    try
                    {
                    var mgr2 = (IAudioSessionManager2)mgr2Obj;
                    if (mgr2.GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum) != 0)
                        continue;

                    try
                    {
                    sessionEnum.GetCount(out int sessionCount);
                    for (int si = 0; si < sessionCount; si++)
                    {
                        object? sessionObj = null;
                        try
                        {
                            if (sessionEnum.GetSession(si, out sessionObj) != 0) continue;
                            var sc2 = (IAudioSessionControl2)sessionObj;
                            if (sc2.GetState(out int state) == 0 && sc2.GetProcessId(out uint pid) == 0 && pid > 4)
                            {
                                int iPid = (int)pid;
                                if (state == 1 /* AudioSessionStateActive */)
                                {
                                    pids.Add(iPid);
                                    StateFor(iPid).LastAudioActiveAt = DateTime.UtcNow; // update stickiness
                                }
                                // Note: state == 0 (Inactive) is NOT counted. Many apps register
                                // audio sessions on startup but never play audio (browsers, Electron).
                                // The 30-second audio stickiness handles the real chicken-and-egg
                                // case where a previously-active stream was suppressed by throttling.
                            }
                        }
                        catch (Exception ex) { _log.Warn("TaskSleepService", $"SampleActiveAudioPids: session query failed (flow={dataFlow}, index {si}): {ex.Message}"); }
                        finally
                        {
                            if (sessionObj != null)
                                try { Marshal.ReleaseComObject(sessionObj); } catch { }
                        }
                    }

                    // If a render device is active but has no sessions at all, a process likely
                    // has exclusive WASAPI access (bypasses IAudioSessionManager2 entirely).
                    if (dataFlow == 0 && sessionCount == 0) anyExclusive = true;

                    } finally { try { Marshal.ReleaseComObject(sessionEnum); } catch { } }
                    } finally { try { Marshal.ReleaseComObject(mgr2Obj); } catch { } }
                    } finally { try { Marshal.ReleaseComObject(device); } catch { } }
                }
                } finally { try { Marshal.ReleaseComObject(devices); } catch { } }
            }
            } finally { try { Marshal.ReleaseComObject(enumerator); } catch { } }

            if (anyExclusive)
                _exclusiveModeDetectedAt = DateTime.UtcNow;
            else if ((DateTime.UtcNow - _exclusiveModeDetectedAt).TotalSeconds > 15)
                _exclusiveModeDetectedAt = DateTime.MinValue; // reset after 15 s idle
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"SampleActiveAudioPids: COM enumeration failed: {ex.Message}"); }

        // Audio stickiness: include PIDs that recently had active audio (within 30 s).
        // This prevents the chicken-and-egg problem where throttling suppresses audio,
        // then we don't detect audio, so we keep throttling.
        var now = DateTime.UtcNow;
        foreach (var kv in _state.Where(s => s.Value.LastAudioActiveAt is not null).ToList())
        {
            if ((now - kv.Value.LastAudioActiveAt!.Value).TotalSeconds <= AudioStickySeconds)
                pids.Add(kv.Key);
            else
                kv.Value.LastAudioActiveAt = null; // expired — clean up
        }

        return pids;
    }

    // ── Efficiency Mode (EcoQoS) ───────────────────────────────────────────────

    private static void SetEfficiencyMode(IntPtr handle, bool enable)
    {
        try
        {
            // Per MSDN ProcessPowerThrottling:
            //   enable  → ControlMask=EXECUTION_SPEED, StateMask=EXECUTION_SPEED (force EcoQoS on)
            //   restore → ControlMask=0,               StateMask=0               (reset to system default — let Windows manage)
            // Previously we used ControlMask=EXECUTION_SPEED / StateMask=0 on restore,
            // which *explicitly disables* OS-controlled EcoQoS for the process until it
            // exits — bypassing Windows' own adaptive throttling. Resetting to default
            // lets Windows reclaim control once we're done throttling the process.
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version     = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = enable ? NapPowerThrottlingMask : 0,
                StateMask   = enable ? NapPowerThrottlingMask : 0
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
        catch (Exception ex) { _log.Warn("TaskSleepService", $"SetEfficiencyMode failed (EcoQoS may not be supported): {ex.Message}"); }
    }

    // ── GPU Priority ───────────────────────────────────────────────────────────
    // Intentionally NOT implemented. D3DKMTSetProcessSchedulingPriorityClass disrupts
    // the shared HAGS flip queue and breaks VSync system-wide. The previous
    // SetGpuPriority helper and its P/Invoke were removed entirely — TaskSleepService
    // never touches GPU scheduling.

    // ── I/O Priority ───────────────────────────────────────────────────────────

    private static void SetIoPriorityLevel(IntPtr handle, int level)
    {
        try { NtSetInformationProcess(handle, PROCESS_IO_PRIORITY_CLASS, ref level, sizeof(int)); }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"SetIoPriorityLevel failed: {ex.Message}"); }
    }

    // ── Memory Priority ────────────────────────────────────────────────────────

    private static void SetMemoryPriority(IntPtr handle, uint priority)
    {
        try
        {
            // MEMORY_PRIORITY_LOWEST (0) is documented as valid but rejected on some Windows
            // builds (only 1..5 accepted). If 0 is refused, fall back to VERY_LOW (1) — the
            // lowest the OS will actually take.
            if (!TrySetMemoryPriority(handle, priority) && priority == MEMORY_PRIORITY_LOWEST)
                TrySetMemoryPriority(handle, MEMORY_PRIORITY_VERY_LOW);
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"SetMemoryPriority failed: {ex.Message}"); }
    }

    /// <summary>
    /// Reads a process's current page priority, or null if it cannot be read.
    ///
    /// Needed so Launch Boost can put back what it found. The value matters most for a process
    /// spawned by a NAPPED parent: children inherit the parent's page priority, so an app launched
    /// from a napped one starts at MEMORY_PRIORITY_LOWEST and thrashes while it is faulting in its
    /// own binaries.
    /// </summary>
    private static uint? GetMemoryPriority(IntPtr handle)
    {
        int size = Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetProcessInformation(handle, PROCESS_INFORMATION_CLASS.ProcessMemoryPriority, ptr, (uint)size))
                return null;
            return Marshal.PtrToStructure<MEMORY_PRIORITY_INFORMATION>(ptr).MemoryPriority;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static bool TrySetMemoryPriority(IntPtr handle, uint priority)
    {
        var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
        int size = Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            return SetProcessInformation(handle,
                PROCESS_INFORMATION_CLASS.ProcessMemoryPriority, ptr, (uint)size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
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
        catch (Exception ex) { _log.Warn("TaskSleepService", $"TrimProcessWorkingSet failed: {ex.Message}"); }
    }

    /// <summary>
    /// BETA — see <see cref="TaskSleepSettings.ReTrimAfterBriefWake"/>.
    /// Opens a short-lived handle to <paramref name="pid"/> and calls
    /// <see cref="TrimProcessWorkingSet"/>. Used by the compress-in-deep-sleep
    /// path: once when a process first crosses the deep-sleep threshold, and
    /// again after each brief wake that ends while the process is still in
    /// deep sleep. Failures are logged at Debug level and never thrown.
    /// </summary>
    private static void TrimWorkingSetByPid(int pid, string processName)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return;       // process gone or insufficient rights — silent
            TrimProcessWorkingSet(h);
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"TrimWorkingSetByPid({processName}/{pid}) failed: {ex.Message}");
        }
        finally
        {
            if (h != IntPtr.Zero) CloseHandle(h);
        }
    }

    /// <summary>
    /// True when <paramref name="pid"/> has been napped for at least the
    /// deep-sleep threshold appropriate for its nap type (tray vs minimize).
    /// Returns false if the PID isn't currently napped or its nap timer is
    /// missing. Single source of truth for "is this process in deep sleep"
    /// — used by both the diagnostic display and the compress-in-deep-sleep
    /// trim trigger so the two never disagree.
    /// </summary>
    private bool IsInDeepSleep(int pid, TaskSleepSettings s, DateTime now)
    {
        if (!(TryState(pid, out var nsSt) && nsSt.NapSince is { } napStart)) return false;
        double nappedMs = (now - napStart).TotalMilliseconds;
        bool isTray = _napBuckets.Is(pid, NapReason.Tray);
        return isTray
            ? (s.TrayDeepSleepEnabled && nappedMs >= s.TrayDeepSleepThresholdMs)
            : (nappedMs >= s.MinimizeDeepSleepThresholdMs);
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

    private static int ParseIoPriority(string? s) => s switch
    {
        "Very Low" => 0,
        "Low"      => 1,
        "Normal"   => 2,
        _          => 0,
    };

    // ── E-core Detection & Affinity ────────────────────────────────────────────

    private DateTime _lastHighCpuDiag;

    /// <summary>
    /// Logs (at most once / 20 s) the single highest-CPU non-Systema process and its
    /// full nap/cap state, so "CPU won't drop" reports are debuggable straight from
    /// the log: it shows whether the hog is napped, whether a hard cap is attached
    /// (vs. a sentinel), whether it's foreground-protected or mid-brief-wake, and the
    /// exact skip reason. Pure diagnostics — changes no behaviour.
    /// </summary>
    private void DiagnoseHighCpuNotCapped(Dictionary<int, double> cpuMap, HashSet<int> protectedPids, TaskSleepSettings s)
    {
        try
        {
            if ((DateTime.UtcNow - _lastHighCpuDiag).TotalSeconds < 20) return;

            int topPid = -1; double topCpu = 0;
            foreach (var kv in cpuMap)
            {
                if (kv.Value <= topCpu) continue;
                _processNames.TryGetValue(kv.Key, out string? nm);
                if (nm == null || nm.Equals("Systema", StringComparison.OrdinalIgnoreCase)) continue;
                topPid = kv.Key; topCpu = kv.Value;
            }
            if (topPid < 0 || topCpu < 20) return;   // nothing notably busy — stay quiet
            _lastHighCpuDiag = DateTime.UtcNow;

            _processNames.TryGetValue(topPid, out string? topName);
            bool napped    = _throttledPids.ContainsKey(topPid);
            bool hasCap    = _cpuCapJobs.TryGetValue(topPid, out IntPtr cj) && cj != IntPtr.Zero;
            bool sentinel  = _cpuCapJobs.TryGetValue(topPid, out IntPtr cj2) && cj2 == IntPtr.Zero;
            bool prot      = protectedPids.Contains(topPid);
            bool briefWake = _briefWakeEndAt.ContainsKey(topPid) || _trayBriefWakeEndAt.ContainsKey(topPid);
            string? skip = TryState(topPid, out var skDg) ? skDg.SkipReason : null;
            _log.Info("TaskSleepService",
                $"CPU-CAP DIAG: top hog {topName ?? "?"} (PID {topPid}) at {topCpu:F0}% of total — " +
                $"napped={napped} hardCap={hasCap} capSentinel={sentinel} foregroundProtected={prot} " +
                $"briefWake={briefWake} skipReason={skip ?? "(none)"} napCap={s.NappedCpuCapPercent}% capEnabled={s.NappedCpuCapEnabled}");
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"HighCpu diag failed: {ex.Message}"); }
    }

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

    /// <summary>
    /// Reads (EfficiencyClass, affinityMask) for every physical core via
    /// GetLogicalProcessorInformationEx(RelationProcessorCore). Empty list on a single-core CPU or
    /// failure. WMI's NumberOfEfficiencyClasses is unreliable on older Windows 10 builds and throws
    /// "Invalid query", so we read the processor topology directly.
    /// </summary>
    private List<(byte effClass, ulong mask)> ReadPhysicalCores()
    {
        var cores = new List<(byte effClass, ulong mask)>();
        try
        {
            if (Environment.ProcessorCount <= 1) return cores;

            const int RelationProcessorCore = 0;

            uint bufSize = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref bufSize);

            IntPtr buf = Marshal.AllocHGlobal((int)bufSize);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref bufSize))
                    return cores;

                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX layout (x64):
                //   +0  DWORD Relationship
                //   +4  DWORD Size
                //   +8  PROCESSOR_RELATIONSHIP:
                //         +0  BYTE  Flags
                //         +1  BYTE  EfficiencyClass
                //         +2  BYTE  Reserved[20]
                //         +22 WORD  GroupCount
                //         +24 GROUP_AFFINITY[GroupCount]: +0 ULONG_PTR Mask (8 bytes x64)
                int offset = 0;
                while (offset < (int)bufSize)
                {
                    int  rel  = Marshal.ReadInt32(buf, offset);
                    uint size = (uint)Marshal.ReadInt32(buf, offset + 4);

                    if (rel == RelationProcessorCore)
                    {
                        byte  effClass     = Marshal.ReadByte(buf, offset + 9);
                        ulong affinityMask = (ulong)Marshal.ReadInt64(buf, offset + 32); // first GROUP_AFFINITY.Mask
                        cores.Add((effClass, affinityMask));
                    }

                    offset += (int)size;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"ReadPhysicalCores failed: {ex.Message}");
        }
        return cores;
    }

    // E-cores = the least powerful cores (lowest EfficiencyClass). 0 on a homogeneous CPU.
    private long BuildECoreMask() => BuildCoreMaskForClass(useMax: false);

    // P-cores = the most powerful cores (highest EfficiencyClass). 0 on a homogeneous CPU.
    private long BuildPCoreMask() => BuildCoreMaskForClass(useMax: true);

    private long BuildCoreMaskForClass(bool useMax)
    {
        var cores = ReadPhysicalCores();
        if (cores.Count == 0) return 0;

        byte minClass = cores.Min(c => c.effClass);
        byte maxClass = cores.Max(c => c.effClass);
        if (minClass == maxClass) return 0; // homogeneous CPU — no P/E split

        byte target = useMax ? maxClass : minClass;
        long mask = 0;
        foreach (var (effClass, coreMask) in cores)
            if (effClass == target)
                mask |= (long)coreMask;
        return mask;
    }

    // ── Monitoring helpers ─────────────────────────────────────────────────────

    private void AddEvent(string name, int pid, string action, string detail = "")
    {
        _eventLog.Enqueue(new MonitorEvent(DateTime.Now, name, pid, action, detail));
        while (_eventLog.Count > MaxEvents) _eventLog.TryDequeue(out _);

        // Mirror significant events to the global activity log so they show in the log viewer.
        // Skip noisy per-tick "Brief Wake" heartbeats to keep the log readable.
        if (string.IsNullOrEmpty(action) || action == "Brief Wake" || action == "Tray Wake")
            return;

        // Batch duplicate (name, action) pairs within a tick to prevent log spam
        // when many child processes of the same app are napped/restored at once.
        var key = (name, action);
        if (_logBatchCounts.TryGetValue(key, out int count))
        {
            _logBatchCounts[key] = count + 1;
            return; // suppress — summary emitted at tick end
        }

        _logBatchCounts[key] = 1;
        _log.Info("TaskSleepService", $"{action}: {name} (PID {pid}){(string.IsNullOrEmpty(detail) ? "" : $" — {detail}")}");
    }

    /// <summary>Emit summary lines for any batched (name, action) groups with count > 1.</summary>
    private void FlushLogBatch()
    {
        foreach (var kv in _logBatchCounts)
        {
            if (kv.Value > 1)
                _log.Info("TaskSleepService", $"{kv.Key.action}: {kv.Key.name} (+{kv.Value - 1} more)");
        }
        _logBatchCounts.Clear();
    }

    private void BuildAndPublishSnapshot(double sysCpu, HashSet<int> protectedPids, TaskSleepSettings s, long freeRamMb, bool ramPressure)
    {
        try
        {
            var now = DateTime.UtcNow;
            var throttledKeys = _throttledPids.Keys.ToHashSet();

            // Always include all throttled processes.
            // Also include top CPU consumers, but skip system/AV processes — they can't
            // be napped anyway and just clutter the UI with svchost/audiodg/etc.
            var pids = new HashSet<int>(throttledKeys);
            int added = 0;
            foreach (var kv in _state.Where(s => s.Value.LastCpuPercent is not null)
                                     .OrderByDescending(s => s.Value.LastCpuPercent!.Value))
            {
                if (added >= 15) break;
                _processNames.TryGetValue(kv.Key, out string? pn);
                if (pn != null && (SystemProcessNames.Contains(pn) ||
                                   SecurityCriticalProcessNames.Contains(pn) ||
                                   _detectedAvProcessNames.Contains(pn)))
                    continue; // skip system/AV — not user-visible in process list
                pids.Add(kv.Key);
                added++;
            }

            // Also include processes that are in the 30-second grace period (about to be napped)
            foreach (int gPid in _minimizeGraceSince.Keys) pids.Add(gPid);
            foreach (int gPid in _trayGraceSince.Keys)     pids.Add(gPid);

            // Always include protected (foreground / visible) PIDs so they show "Active"
            foreach (int pPid in protectedPids)
            {
                if (!(TryState(pPid, out var lcP) && lcP.LastCpuPercent.HasValue)) continue;
                _processNames.TryGetValue(pPid, out string? ppn);
                if (ppn != null && (SystemProcessNames.Contains(ppn) ||
                                    SecurityCriticalProcessNames.Contains(ppn) ||
                                    _detectedAvProcessNames.Contains(ppn)))
                    continue;
                pids.Add(pPid);
            }

            var snapshots = new List<ProcessSnapshot>(pids.Count);
            foreach (int pid in pids)
            {
                bool isThrottled = throttledKeys.Contains(pid);
                bool isProtected = protectedPids.Contains(pid);
                bool isPending   = !isThrottled && (_minimizeGraceSince.ContainsKey(pid) || _trayGraceSince.ContainsKey(pid));
                double cpu = TryState(pid, out var lcSn) && lcSn.LastCpuPercent is { } vSn ? vSn : 0;
                _processNames.TryGetValue(pid, out string? name);
                bool onECores = _originalAffinities.ContainsKey(pid);
                DateTime ta = TryState(pid, out var taSt) && taSt.ThrottledAt is { } taVal ? taVal : default;

                // Compute grace countdown label (seconds remaining before nap)
                string pendingLabel = "";
                if (isPending)
                {
                    _minimizeGraceSince.TryGetValue(pid, out DateTime mgs);
                    _trayGraceSince.TryGetValue(pid, out DateTime tgs);
                    DateTime earliest = mgs == default ? tgs : (tgs == default ? mgs : (mgs < tgs ? mgs : tgs));
                    double elapsedMs  = (now - earliest).TotalMilliseconds;
                    // Hidden apps run the longer, user-set grace; minimized/tray use the short const.
                    double graceMs    = (TryState(pid, out var pgSt) && pgSt.IsPendingHidden)
                                        ? s.HiddenNapGraceMs : MinimizeTrayGraceMs;
                    double remSec     = Math.Max(0, (graceMs - elapsedMs) / 1000.0);
                    pendingLabel = $"~{(int)Math.Ceiling(remSec)}s";
                }

                bool isDeepSleep = false;
                if (isThrottled && TryState(pid, out var snapNsSt) && snapNsSt.NapSince is { } napSince2)
                {
                    double nappedMs = (now - napSince2).TotalMilliseconds;
                    bool isTray = _napBuckets.Is(pid, NapReason.Tray);
                    isDeepSleep = isTray
                        ? (s.TrayDeepSleepEnabled && nappedMs >= s.TrayDeepSleepThresholdMs)
                        : (nappedMs >= s.MinimizeDeepSleepThresholdMs);
                }

                string statusLabel = isThrottled ? (isDeepSleep ? "Deep Sleep" : "Napping")
                                   : isProtected ? "Active"
                                   : isPending   ? "Pending"
                                   : "";

                // Determine skip reason for non-throttled processes
                string skipReason = "";
                if (!isThrottled && !isPending && TryState(pid, out var skSt2))
                    skipReason = skSt2.SkipReason ?? "";

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
                    SkipReason   = skipReason ?? "",
                });
            }

            // Collapse identically-named processes (e.g. firefox.exe child processes) into
            // one row so the list doesn't show 15 "firefox" entries.  CPU is summed;
            // IsThrottled/IsProtected/IsPendingNap are true if ANY member matches.
            var grouped = snapshots
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    // Prefer the throttled representative so status/duration are meaningful
                    var rep = g.FirstOrDefault(p => p.IsThrottled)
                           ?? g.FirstOrDefault(p => p.IsPendingNap)
                           ?? g.First();
                    bool grpProtected  = g.Any(p => p.IsProtected);
                    bool grpThrottled  = g.Any(p => p.IsThrottled);
                    bool grpPending    = g.Any(p => p.IsPendingNap);
                    // Protected wins: if any instance is visible/foreground, show Active
                    string grpStatus = grpProtected ? "Active"
                                     : grpThrottled ? rep.StatusLabel
                                     : grpPending   ? "Pending"
                                     : "";
                    return new ProcessSnapshot
                    {
                        Pid          = rep.Pid,
                        Name         = rep.Name,
                        CpuPercent   = g.Sum(p => p.CpuPercent),
                        IsThrottled  = grpThrottled && !grpProtected,
                        IsProtected  = grpProtected,
                        IsPendingNap = grpPending && !grpProtected,
                        StatusLabel  = grpStatus,
                        CoreLabel    = rep.CoreLabel,
                        ThrottledFor = grpProtected ? "" : rep.ThrottledFor,
                        SkipReason   = rep.SkipReason,
                    };
                })
                .ToList();

            // Sort: throttled first, then by CPU descending
            grouped.Sort((a, b) =>
            {
                int tc = b.IsThrottled.CompareTo(a.IsThrottled);
                return tc != 0 ? tc : b.CpuPercent.CompareTo(a.CpuPercent);
            });
            snapshots = grouped;

            var events = _eventLog.ToArray();
            var recentEvents = (IReadOnlyList<MonitorEvent>)
                (events.Length > 50 ? events[^50..] : events);

            // Sum up CPU% that throttled processes were using before being napped
            double cpuFreed = 0;
            foreach (int tpid in throttledKeys)
                if (TryState(tpid, out var cst) && cst.CpuAtThrottle is { } savedCpu)
                    cpuFreed += savedCpu;
            cpuFreed = Math.Min(cpuFreed, 100.0); // cap at 100%

            _latestSnapshot = new MonitorSnapshot(
                sysCpu, throttledKeys.Count,
                freeRamMb, ramPressure, cpuFreed,
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
        ((long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    private void Notify(string msg) => StatusChanged?.Invoke(msg);

}
