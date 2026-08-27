// ════════════════════════════════════════════════════════════════════════════
// TaskSleepService.LaunchBoost.cs  ·  Temporary priority boost for new launches
// ════════════════════════════════════════════════════════════════════════════
//
// A newly launched app gets High CPU and I/O priority and efficiency mode off
// for a short window (default 20 s), then its ORIGINAL priorities are restored
// so Windows takes scheduling back over.
//
// Self-contained by design: it owns its own state and event-log path, and reuses
// the same priority P/Invokes the napping engine already uses rather than adding
// native surface for Defender or SAC to flag.
//
// The interaction that matters is with napping. A Launch-Boosted process must
// never be napped concurrently: the two would ping-pong every tick, and worse,
// the nap path captures the CURRENT priority as the value to restore later. If
// that captured value is the boosted High, the process is restored to High
// permanently. IsLaunchBoosted is checked in ShouldSkip for exactly this reason.
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Systema.Models;

namespace Systema.Services;

public sealed partial class TaskSleepService
{
    // ════════════════════════════════════════════════════════════════════════════
    //  Launch Boost
    // ════════════════════════════════════════════════════════════════════════════
    //
    // When enabled, a dedicated 1.5 s timer watches for newly-launched processes.
    // Each new user app gets a temporary boost — CPU High, I/O High, efficiency
    // mode off (RAM unchanged; GPU scheduling NEVER touched) — for a configurable
    // window (default 20 s), then its original priorities are restored so Windows
    // takes scheduling back over. Fully self-contained: owns its own state and a
    // thread-safe event-log path; reuses the same priority P/Invokes the napping
    // engine uses (no new native surface for Defender/SAC to flag).

    private System.Threading.Timer? _launchBoostTimer;
    private readonly object _launchBoostLock = new();
    private HashSet<int> _lbKnownPids = new();
    private readonly Dictionary<int, LaunchBoostEntry> _lbBoosted = new();

    // Event-driven launch detection. Win32_ProcessStartTrace fires the instant a
    // process is created (ETW-backed, near-zero overhead), so the boost lands from
    // the app's first moments — DLL loads and init — instead of up to 1.5s later.
    // The 1.5s polling timer above is kept as a fallback (belt-and-suspenders) for
    // anything the watcher misses or in case the watcher fails to start.
    private ManagementEventWatcher? _lbStartWatcher;

    // GPU boost auto-disable: counts non-zero returns from D3DKMT and, after
    // enough in a row, stops calling it for the rest of the session as a safety
    // net. The threshold is intentionally high (50) because helper subprocesses
    // inside multi-process apps (Spotify, Discord, ChatGPT, Claude, etc.)
    // routinely return non-zero for benign per-process reasons — they don't
    // have a D3D device — and that's fine. A truly broken WDDM driver still
    // trips the threshold eventually; a brief burst from a multi-process app
    // launch never will. A successful call resets the counter.
    private const int GpuBoostFailureThreshold = 50;
    private int  _gpuBoostFailureCount;
    private bool _gpuBoostDisabledForSession;

    /// <summary>
    /// Lowers a napped process's D3DKMT GPU scheduling priority to Idle so the GPU is
    /// handed to the foreground app. Win11+ only (see <see cref="GpuNapLoweringSupported"/>);
    /// the original is saved in <see cref="_originalGpuPriority"/> and restored on wake.
    /// Shares the GPU auto-disable safety net with Launch Boost — if the WDDM driver keeps
    /// erroring we stop touching GPU priority for the session (avoids TDR risk). A non-zero
    /// return is usually just a helper process with no D3D device, which is harmless.
    /// </summary>
    private void LowerNapGpuPriority(IntPtr handle, int pid)
    {
        if (!GpuNapLoweringSupported || _gpuBoostDisabledForSession) return;
        if (_originalGpuPriority.ContainsKey(pid)) return; // already lowered
        try
        {
            int getRc = D3DKMTGetProcessSchedulingPriorityClass(handle, out int orig);
            if (getRc != 0) return; // no D3D device / can't read — leave it alone
            if (orig == D3DKMT_GPU_PRIORITY_IDLE)
            {
                // Already at Idle — a helper with no live D3D device reads 0, or a previous
                // cycle left it here. Record NORMAL (not Idle) as the restore target so the next
                // full wake lifts it back to the default instead of pinning it at Idle forever.
                // That stranded-at-Idle state is precisely the "GPU priority won't restore on
                // wake" symptom. Nothing to set now — it is already Idle.
                _originalGpuPriority[pid] = D3DKMT_GPU_PRIORITY_NORMAL;
                return;
            }

            int setRc = D3DKMTSetProcessSchedulingPriorityClass(handle, D3DKMT_GPU_PRIORITY_IDLE);
            if (setRc == 0)
            {
                _originalGpuPriority[pid] = orig;
                _gpuBoostFailureCount = 0;
            }
            else
            {
                _gpuBoostFailureCount++;
                if (_gpuBoostFailureCount >= GpuBoostFailureThreshold)
                {
                    _gpuBoostDisabledForSession = true;
                    _log.Warn("TaskSleepService",
                        "GPU priority control auto-DISABLED for this session — D3DKMT kept returning errors.");
                }
            }
        }
        catch (Exception ex)
        {
            _gpuBoostFailureCount++;
            _log.Warn("TaskSleepService", $"LowerNapGpuPriority PID {pid} threw: {ex.Message}");
            if (_gpuBoostFailureCount >= GpuBoostFailureThreshold)
                _gpuBoostDisabledForSession = true;
        }
    }

    /// <summary>
    /// Restores the GPU scheduling priority we lowered at nap time. Safe no-op if the
    /// pid was never lowered. Called from every wake/restore path.
    /// </summary>
    private void RestoreNapGpuPriority(IntPtr handle, int pid)
    {
        if (!_originalGpuPriority.TryRemove(pid, out int orig)) return;
        // Never hand a woken app back at a BELOW-NORMAL GPU priority. The captured "original"
        // can legitimately be Idle (a helper that read 0 at nap time, or a re-nap that recaptured
        // a stale Idle), and restoring Idle there leaves the app stuck at the lowest GPU priority
        // after it wakes — the exact bug being fixed. Normal (2) is the default every foreground
        // app should run at, so clamp the restore target up to it.
        int target = orig < D3DKMT_GPU_PRIORITY_NORMAL ? D3DKMT_GPU_PRIORITY_NORMAL : orig;
        try
        {
            int rc = D3DKMTSetProcessSchedulingPriorityClass(handle, target);
            if (rc != 0)
                _log.Warn("TaskSleepService", $"RestoreNapGpuPriority PID {pid}: D3DKMTSet returned 0x{rc:X8} (target {target})");
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"RestoreNapGpuPriority PID {pid} failed: {ex.Message}"); }
    }

    // LaunchBoostTick reentrancy guard. The 1.5s System.Threading.Timer fires
    // on a threadpool thread; if a single tick takes >1.5s (heavy boost dict,
    // slow P/Invoke, etc.) the next tick will start while the first is still
    // running, contending on _launchBoostLock. Worst case: any P/Invoke that
    // genuinely hangs leaves the lock held, and every subsequent tick blocks
    // forever waiting on it — eventually starving the threadpool, which is
    // exactly the "process alive but UI-dead" pattern. 0/1 flip via
    // Interlocked guarantees at most one tick body runs at a time.
    private int _lbTickInFlight;

    // Admin / maintenance binaries that are NOT user-app launches. Boosting them
    // wastes the slot, fills the boost dictionary (so the 1.5 s re-assert tick
    // does more work and more P/Invokes), and on weak GPUs amplifies WDDM stress.
    // None of these are something the user is "opening" — they're Windows
    // servicing tools, COM helpers, UAC prompts, NGEN compilation, etc.
    private static readonly HashSet<string> LaunchBoostExclusionExtras =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Command-line / scripting / config utilities
        "cmd", "powershell", "pwsh", "wscript", "cscript", "reg", "regedit",
        "schtasks", "sc", "fsutil", "powercfg", "where", "whoami", "tasklist",
        "taskkill", "wmic", "net", "net1", "netsh", "ipconfig", "nslookup",
        "ping", "tracert", "route", "arp", "icacls",
        // Dev-shell subprocesses that spawn in bursts (a terminal / IDE / dev tool running
        // commands): git & Unix coreutils from Git-Bash/WSL. Transient, sub-second, and boosting
        // dozens of them is pure noise — this is the flood in the diagnostic report.
        "git", "git-lfs", "bash", "sh", "conhost", "openconsole",
        "findstr", "find", "cat", "head", "tail", "grep", "sed", "awk", "ls",
        "sort", "wc", "cut", "tr", "xargs", "more", "dirname", "basename", "env",
        "cygpath", "uname", "date", "sleep", "printf", "expr", "test", "true", "false",
        // UAC and user-mode security prompts
        "consent", "RuntimeBroker",
        // .NET Framework NGEN — fires in big bursts after every Windows Update;
        // boosting twenty of these at once is what overwhelms the boost dict and
        // hammers the GPU scheduler with priority calls.
        "mscorsvw", "ngen", "ngentask",
        // Windows Update / servicing
        "TiWorker", "TrustedInstaller", "Dism", "DismHost", "wimserv",
        "sdbinst", "wuaucltcore", "MoUsoCoreWorker", "MusNotification",
        "MusNotifyIcon", "musnotificationux",
        // Telemetry / compatibility scans (Microsoft's own background tasks)
        "CompatTelRunner", "DeviceCensus", "deviceenroller", "diskaudit",
        // Modern Windows shell hosts / picker hosts (system internal UI)
        "UIEOrchestrator", "UIEOrchestratorStub", "PickerHost",
        "DataExchangeHost", "ShellHost", "CrossDeviceResume",
        // Volume / Disk / Firmware system services
        "vds", "vdsldr", "VSSVC", "FirmwareTPM",
        // Generic Win32 helpers used by system tasks
        "rundll32", "regsvr32", "CompPkgSrv", "OpenWith",
        // Search indexing helpers
        "SearchFilterHost", "SearchProtocolHost", "WmiApSrv",
        // Crash / error reporters
        "crashreporter", "crashhelper", "WerFault", "WerFaultSecure",
        // CHX SmartScreen helper
        "CHXSmartScreen",
        // Dell / OEM inventory + driver-update agents seen on test machines
        "invcol", "DRVUpdate", "SalomanDock", "provtool",
        // Background "open hint" prompts
        "downloader", "updatesrv", "pingsender", "ByteCodeGenerator",
        // Our own installer (the previous build's setup) — never boost it
        "Systema_Setup",
    };

    private sealed class LaunchBoostEntry
    {
        public DateTime Expiry;       // UTC
        public uint     OriginalCpu;  // CPU priority class to restore
        public int?     OriginalGpu;  // GPU sched priority to restore (null = GPU not boosted)
        public string   Name = "";
    }

    /// <summary>
    /// Thread-safe: true while the process currently has an ACTIVE launch boost. Read by the
    /// nap engine (<see cref="ShouldSkip"/>) so a boosting process is never napped — which
    /// would otherwise ping-pong the priority and make the nap path capture the boosted High
    /// priority as the value to restore later.
    /// </summary>
    private bool IsLaunchBoosted(int pid)
    {
        lock (_launchBoostLock) return _lbBoosted.ContainsKey(pid);
    }

    /// <summary>Starts or stops the Launch Boost watcher to match the current settings.</summary>
    private void ApplyLaunchBoostState(TaskSleepSettings s)
    {
        if (s.LaunchBoostEnabled && _running) StartLaunchBoost();
        else                                  StopLaunchBoost();
    }

    private void StartLaunchBoost()
    {
        lock (_launchBoostLock)
        {
            if (_launchBoostTimer != null) return;                 // already armed
            _lbKnownPids = CurrentLaunchBoostPids();                 // baseline — only boost NEW launches
            // 300 ms poll: a launch is boosted within ~300 ms instead of waiting on the WMI
            // Win32_ProcessStartTrace event, which lags ~1 s behind the actual start because
            // of ETW buffer flushing. The per-poll cost is one toolhelp snapshot (~2 ms), so
            // even at 300 ms this is a tiny fraction of a core.
            _launchBoostTimer = new System.Threading.Timer(_ => LaunchBoostTick(), null, 300, 300);
        }
        StartLaunchBoostWatcher();
        _log.Info("TaskSleepService", "Launch Boost armed — new apps get a temporary priority boost on launch");
    }

    /// <summary>
    /// Arms the Win32_ProcessStartTrace event watcher so launches are boosted the
    /// instant the process is created. Best-effort: if WMI rejects the query (rare),
    /// the 1.5s polling timer still covers everything.
    /// </summary>
    private void StartLaunchBoostWatcher()
    {
        lock (_launchBoostLock)
        {
            if (_lbStartWatcher != null) return; // already watching
            try
            {
                // Win32_ProcessStartTrace is an extrinsic ETW event — selecting specific
                // columns throws WBEM_E_INVALID_PARAMETER on many systems, so use "*".
                // The event still carries ProcessID / ProcessName / ParentProcessID.
                var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
                _lbStartWatcher = new ManagementEventWatcher(query);
                _lbStartWatcher.EventArrived += OnProcessStarted;
                _lbStartWatcher.Start();
                _log.Info("TaskSleepService", "Launch Boost: instant process-start watcher armed");
            }
            catch (Exception ex)
            {
                _log.Warn("TaskSleepService",
                    $"Launch Boost: process-start watcher failed to arm — falling back to 1.5s polling ({ex.Message})");
                try { _lbStartWatcher?.Dispose(); } catch { }
                _lbStartWatcher = null;
            }
        }
    }

    private void StopLaunchBoost()
    {
        System.Threading.Timer? t;
        ManagementEventWatcher? w;
        List<KeyValuePair<int, LaunchBoostEntry>> toRestore;
        lock (_launchBoostLock)
        {
            t = _launchBoostTimer;
            _launchBoostTimer = null;
            w = _lbStartWatcher;
            _lbStartWatcher = null;
            toRestore = _lbBoosted.ToList();
            _lbBoosted.Clear();
        }
        // Tear down the event watcher first so no new boosts land mid-restore.
        if (w != null)
        {
            try { w.EventArrived -= OnProcessStarted; w.Stop(); w.Dispose(); }
            catch (Exception ex) { _log.Warn("TaskSleepService", $"Launch Boost watcher teardown failed: {ex.Message}"); }
        }
        if (t == null) return;
        t.Dispose();
        foreach (var kv in toRestore) RestoreLaunchBoost(kv.Key, kv.Value);
        _log.Info("TaskSleepService", "Launch Boost disarmed — in-flight boosts restored");
    }

    /// <summary>
    /// Fires the instant any process is created. Boosts it immediately if it's a
    /// normal user-app launch, OR if its parent is currently boosted (so child /
    /// helper processes that spawn a moment later — game exes launched by Steam/Epic,
    /// renderer subprocesses — ride the same boost window). Runs on a WMI callback
    /// thread; ApplyLaunchBoost is concurrency-safe via the _lbBoosted claim guard.
    /// </summary>
    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            TaskSleepSettings s;
            lock (_settingsLock) { s = _settings; }
            if (!s.LaunchBoostEnabled || !_running) return;

            var props = e.NewEvent.Properties;
            int pid  = Convert.ToInt32(props["ProcessID"].Value);
            int ppid = Convert.ToInt32(props["ParentProcessID"].Value);
            string raw = props["ProcessName"].Value?.ToString() ?? "";
            if (pid <= 0 || string.IsNullOrEmpty(raw)) return;

            // Win32_ProcessStartTrace reports "name.exe"; the exclusion sets use the
            // bare process name (matching Process.ProcessName), so strip the extension.
            string name = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? raw[..^4] : raw;

            // Boost ONLY a genuine user launch (shell-spawned) or a child riding its
            // parent's active session window — never background/scheduled spawns.
            if (!ShouldLaunchBoost(ppid, name, s, DateTime.UtcNow, out DateTime expiry))
                return;

            ApplyLaunchBoost(pid, name, s, expiry);
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"OnProcessStarted failed: {ex.Message}");
        }
    }

    private void LaunchBoostTick()
    {
        // Reentrancy guard: skip if the previous tick is still in progress.
        // Returning is correct — the next 1.5s timer fires automatically.
        if (System.Threading.Interlocked.CompareExchange(ref _lbTickInFlight, 1, 0) != 0)
            return;
        try
        {
            TaskSleepSettings s;
            lock (_settingsLock) { s = _settings; }
            if (!s.LaunchBoostEnabled) return;

            var now = DateTime.UtcNow;

            // 1. Restore boosts whose window has elapsed.
            List<KeyValuePair<int, LaunchBoostEntry>> expired;
            lock (_launchBoostLock)
                expired = _lbBoosted.Where(kv => now >= kv.Value.Expiry).ToList();
            foreach (var kv in expired)
            {
                RestoreLaunchBoost(kv.Key, kv.Value);
                lock (_launchBoostLock) _lbBoosted.Remove(kv.Key);
            }

            // 1b. Re-assert the boost on still-active processes EVERY tick. This is
            // the whole point of the efficiency toggle: Windows' EcoQoS scheduler
            // will silently flip efficiency mode back ON on a freshly-launched
            // process, even one in the foreground. Re-applying each tick forces it
            // back off (and keeps CPU/I-O High pinned) for the full boost window.
            List<int> active;
            lock (_launchBoostLock) active = _lbBoosted.Where(kv => now < kv.Value.Expiry).Select(kv => kv.Key).ToList();
            foreach (int pid in active) ReassertLaunchBoost(pid, s);

            // 2. Detect newly-launched processes and boost them. ONE cheap toolhelp snapshot
            //    carries pid + parent pid + name inline (no second lookup), and at the 300 ms
            //    poll this is what actually boosts most launches — well ahead of the laggy WMI
            //    event. Same gate as the watcher (shell/stub launch or inherited session), so
            //    it can't boost background/scheduled processes.
            var current = LaunchBoostScanSnapshot();             // (pid, ppid, name)
            var currentPids = new HashSet<int>(current.Count);
            foreach (var (pid, ppid, name) in current)
            {
                currentPids.Add(pid);
                if (_lbKnownPids.Contains(pid)) continue;        // was already running
                bool alreadyBoosted;
                lock (_launchBoostLock) alreadyBoosted = _lbBoosted.ContainsKey(pid);
                if (alreadyBoosted) continue;

                if (!ShouldLaunchBoost(ppid, name, s, now, out DateTime expiry)) continue;
                ApplyLaunchBoost(pid, name, s, expiry);
            }
            _lbKnownPids = currentPids;                          // forget exited PIDs; mark current as seen
        }
        catch (Exception ex)
        {
            _log.Warn("TaskSleepService", $"LaunchBoostTick failed: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _lbTickInFlight, 0);
        }
    }

    private HashSet<int> CurrentLaunchBoostPids()
    {
        var set = new HashSet<int>();
        foreach (var (pid, _, _) in LaunchBoostScanSnapshot()) set.Add(pid);
        return set;
    }

    /// <summary>
    /// Cheap single-snapshot list of (pid, parentPid, name) for every running process via one
    /// CreateToolhelp32Snapshot — far lighter than Process.GetProcesses(), and it carries the
    /// parent pid inline so the launch decision needs no second lookup. This is what lets the
    /// launch poll run at 300 ms without measurable overhead.
    /// </summary>
    private static List<(int pid, int ppid, string name)> LaunchBoostScanSnapshot()
    {
        var list = new List<(int, int, string)>();
        try
        {
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snap, ref e))
                    do
                    {
                        string raw = e.szExeFile ?? "";
                        string nm  = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw;
                        list.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, nm));
                    }
                    while (Process32Next(snap, ref e));
            }
            finally { CloseHandle(snap); }
        }
        catch { }
        return list;
    }

    /// <summary>True for ordinary user apps — excludes OS, security, AV, and Systema itself.</summary>
    private bool IsBoostableLaunch(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Equals("Systema", StringComparison.OrdinalIgnoreCase)) return false;
        if (SystemProcessNames.Contains(name))         return false;
        if (SecurityCriticalProcessNames.Contains(name)) return false;
        if (_detectedAvProcessNames.Contains(name))    return false;
        if (LaunchBoostExclusionExtras.Contains(name)) return false;
        // Defensive prefix check for things like "Systema_Setup_0.7.28" / "...tmp"
        // — we never want to boost our own installer or its temp helpers.
        if (name.StartsWith("Systema_Setup", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// Eligibility for boosting a CHILD process that a currently-boosted app spawned. Blocks the
    /// same sets as <see cref="IsBoostableLaunch"/> — including the transient CLI / shell / system
    /// "extras" (cmd, git, bash, findstr, reg, rundll32…). Those are sub-second helpers that provide
    /// nothing when boosted; letting them ride a parent's window is exactly what floods the log when
    /// a terminal or dev tool runs a burst of commands. A real app's meaningful children (its own
    /// helper exes, renderers, anti-cheat) aren't on the block list, so they still inherit the boost.
    /// </summary>
    private bool IsBoostableChild(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Equals("Systema", StringComparison.OrdinalIgnoreCase)) return false;
        if (SystemProcessNames.Contains(name))           return false;
        if (SecurityCriticalProcessNames.Contains(name)) return false;
        if (_detectedAvProcessNames.Contains(name))      return false;
        if (LaunchBoostExclusionExtras.Contains(name))   return false;   // transient CLI/shell/system junk — never boost, even as a child
        if (name.StartsWith("Systema_Setup", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// Decides whether a newly-seen process should be launch-boosted and, if so, the expiry
    /// to use. Two ways in — and ONLY these two:
    /// <list type="bullet">
    /// <item><b>INHERIT</b> — the parent is already boosted, so this child rides the SAME
    /// session window. The whole app boosts for ONE fixed duration; a child that spawns part
    /// way through does NOT start its own fresh window, and once the window closes new
    /// children are not re-boosted. This is what makes "boost the WHOLE thing for the set
    /// time" actually true.</item>
    /// <item><b>NEW SESSION</b> — only when the USER launched the app, detected as the parent
    /// being the Windows shell (explorer.exe): Start menu, taskbar, desktop, Run, or a
    /// double-clicked file. Gets a fresh now+duration window.</item>
    /// </list>
    /// Everything else is skipped. Background tasks, updaters and service helpers are spawned
    /// by services.exe / svchost / taskhostw / a napped launcher — never by the shell — so an
    /// Epic/Edge/Store background process that starts while the user isn't launching anything
    /// gets no boost (and therefore stays nap-eligible).
    /// </summary>
    private bool ShouldLaunchBoost(int ppid, string name, TaskSleepSettings s, DateTime now, out DateTime expiry)
    {
        expiry = default;


        // INHERIT: ride the parent's active session window (whole-tree, one shared duration).
        lock (_launchBoostLock)
        {
            if (_lbBoosted.TryGetValue(ppid, out var parentEntry))
            {
                if (!IsBoostableChild(name)) return false;
                expiry = parentEntry.Expiry;   // share the parent's window — no fresh 40 s
                return true;
            }
        }

        // NEW SESSION: only for a genuine user launch.
        if (!IsBoostableLaunch(name)) return false;
        if (!IsUserLaunch(ppid))      return false;
        expiry = now.AddSeconds(Math.Clamp(s.LaunchBoostDurationSeconds, 3, 120));
        return true;
    }

    /// <summary>Service / scheduler / updater host processes that spawn BACKGROUND work, never
    /// user-launched apps. A new process parented by one of these is a scheduled task, a
    /// service helper, or an auto-updater — exactly the things that should NOT be boosted.</summary>
    private static readonly HashSet<string> LaunchBoostBackgroundParents = new(StringComparer.OrdinalIgnoreCase)
    {
        "services", "svchost", "taskhostw", "wininit", "winlogon", "lsass", "WmiPrvSE",
        "dllhost", "RuntimeBroker", "sihost", "MoUsoCoreWorker", "usocoreworker", "UsoClient",
        "wuauclt", "TrustedInstaller", "TiWorker", "OfficeClickToRun", "backgroundTaskHost",
        "smartscreen", "SearchIndexer", "SearchProtocolHost", "SgrmBroker",
    };

    /// <summary>Interpreters / shells that RUN commands rather than launch apps. A child of one of
    /// these is a script or tool subprocess, not a user launch, so it must never originate a fresh
    /// boost session — otherwise a terminal, IDE, or dev tool cascades boosts across its whole
    /// subprocess tree. (Game launchers like Steam/Epic are deliberately NOT here.)</summary>
    private static readonly HashSet<string> LaunchBoostShellParents = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "bash", "sh", "wsl", "wslhost", "conhost", "openconsole",
        "node", "python", "python3", "py", "perl", "ruby", "git",
    };

    /// <summary>
    /// True when a brand-new top-level process looks like something the USER just launched,
    /// rather than a background spawn or a child of an already-running app. We can't read the
    /// window yet (it doesn't exist at process-start), so we judge by the parent:
    /// <list type="bullet">
    /// <item>REJECT if the parent is a napped/throttled app (dormant → background spawn) or a
    /// service/scheduler/updater host (<see cref="LaunchBoostBackgroundParents"/>).</item>
    /// <item>ACCEPT if the parent is the shell (explorer) — a direct Start/taskbar/desktop launch.</item>
    /// <item>ACCEPT if the parent is a transient launcher stub: a young process (&lt; 20 s old).
    /// Many apps (Firefox, etc.) launch via a short-lived stub that explorer spawns; the real
    /// process is the stub's child. Treating a young non-background parent as a launch origin
    /// catches that even if the stub's own boost was missed.</item>
    /// <item>REJECT otherwise — an established, long-running app spawning a child is NOT a fresh
    /// user launch (so a running browser's late renderers / a dev tool's git children don't get
    /// their own boost once the app's launch window has closed).</item>
    /// </list>
    /// </summary>
    private bool IsUserLaunch(int ppid)
    {
        if (ppid <= 0) return false;

        // Background spawn: parent is a dormant (napped) app.
        if (_throttledPids.ContainsKey(ppid) || _napBuckets.IsNapped(ppid)) return false;

        string? parent = GetProcessNameSafe(ppid);
        if (parent == null) return false;                                   // parent gone — can't verify; don't start a session
        if (LaunchBoostBackgroundParents.Contains(parent)) return false;    // service / scheduler / updater host
        if (parent.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return true;   // direct shell launch

        // A shell / interpreter parent (cmd, bash, git, node…) is running a command or script — not a
        // user launching an app — so it never originates a boost session. Without this, a terminal or
        // dev tool cascades a fresh boost onto every subprocess it spawns.
        if (LaunchBoostShellParents.Contains(parent)) return false;

        // Transient launcher stub (young, non-background, non-shell parent) → treat as a launch origin.
        TimeSpan? age = GetProcessAgeSafe(ppid);
        if (age.HasValue && age.Value < TimeSpan.FromSeconds(20)) return true;

        // Established running app spawning a child → not a fresh launch.
        return false;
    }

    private static string? GetProcessNameSafe(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; }
        catch { return null; }
    }

    private static TimeSpan? GetProcessAgeSafe(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return DateTime.Now - p.StartTime; }
        catch { return null; }
    }

    private void ApplyLaunchBoost(int pid, string name, TaskSleepSettings s, DateTime expiryUtc)
    {
        IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return;
        try
        {
            uint orig = GetPriorityClass(h);

            // CLAIM FIRST, THEN RAISE PRIORITY. We register this PID in _lbBoosted
            // *before* touching its priority. ShouldSkip()/IsLaunchBoosted() consult
            // _lbBoosted to keep the nap engine off boosted processes — if we raised
            // the priority to HIGH first and a nap tick fired in the window before the
            // registration, the nap would capture HIGH as the process's "original"
            // priority and restore it to HIGH forever (the bug seen with Firefox:
            // minimize-while-boosted → reopen → stuck High). Claiming first makes that
            // window impossible. OriginalGpu is filled in below once we read/set it.
            bool claimed;
            lock (_launchBoostLock)
            {
                // Concurrency guard: the event watcher and the 1.5s timer can both
                // reach the same fresh PID. Whoever records the entry first owns the
                // original-priority value — never overwrite it.
                if (_lbBoosted.ContainsKey(pid)) { claimed = false; }
                else
                {
                    _lbBoosted[pid] = new LaunchBoostEntry { Expiry = expiryUtc, OriginalCpu = orig, OriginalGpu = null, Name = name };
                    claimed = true;
                }
            }
            if (!claimed) return;   // someone else already booked this PID — don't touch priority

            if (s.LaunchBoostCpu)               SetPriorityClass(h, HIGH_PRIORITY_CLASS);

            if (s.LaunchBoostIo)                SetIoPriorityLevel(h, IO_PRIORITY_HIGH);
            if (s.LaunchBoostDisableEfficiency) SetEfficiencyMode(h, false);

            // GPU scheduling priority — opt-in only (default off). Capture the
            // original so it's restored exactly when the boost ends.
            //
            // SAFETY: if the WDDM driver starts returning non-zero NTSTATUS (the
            // pattern on older Intel iGPUs / unstable WDDM), keep calling it would
            // risk triggering a TDR (graphics device reset) which kills WPF
            // rendering and leaves the app alive but UI-frozen. We count failures
            // and silently stop touching GPU priority for the rest of the session
            // after 3 strikes — the boost still applies CPU/I-O priority and
            // efficiency-off, just no GPU. A log line tells us once.
            int?  origGpu    = null;
            bool  gpuApplied = false;
            if (s.LaunchBoostGpu && !_gpuBoostDisabledForSession)
            {
                try
                {
                    int getRc = D3DKMTGetProcessSchedulingPriorityClass(h, out int g);
                    if (getRc == 0) origGpu = g;
                    // Max GPU scheduling priority (Realtime, the highest class) for the boosted app.
                    // If the driver rejects Realtime, setRc is non-zero and the strike/auto-disable
                    // path below handles it gracefully (boost still applies CPU/I-O). Restored to the
                    // captured origGpu when the boost ends.
                    int setRc = D3DKMTSetProcessSchedulingPriorityClass(h, D3DKMT_GPU_PRIORITY_REALTIME);
                    if (setRc == 0)
                    {
                        gpuApplied = true;
                        // A successful set resets the strike counter — transient blips
                        // shouldn't permanently disable a working GPU boost.
                        _gpuBoostFailureCount = 0;
                    }
                    else
                    {
                        // Non-zero return. Most often this is just a helper subprocess
                        // (multi-process Electron apps) that has no D3D device, so the
                        // call doesn't apply — totally benign. Count toward the safety
                        // threshold so a TRULY broken driver still gets the brakes,
                        // but the threshold is high enough that normal app launches
                        // never disable GPU boost prematurely.
                        _gpuBoostFailureCount++;
                        if (_gpuBoostFailureCount >= GpuBoostFailureThreshold)
                        {
                            _gpuBoostDisabledForSession = true;
                            _log.Warn("TaskSleepService",
                                "GPU boost auto-DISABLED for this session — D3DKMT kept returning errors. " +
                                "Turn off 'GPU priority → Max' in Task Sleep settings if this keeps happening.");
                        }
                        origGpu = null;
                    }
                }
                catch (Exception ex)
                {
                    _gpuBoostFailureCount++;
                    _log.Warn("TaskSleepService", $"GPU boost for {name} threw ({_gpuBoostFailureCount}/3): {ex.Message}");
                    if (_gpuBoostFailureCount >= 3)
                    {
                        _gpuBoostDisabledForSession = true;
                        _log.Warn("TaskSleepService",
                            "GPU boost auto-DISABLED for this session after repeated exceptions from D3DKMT.");
                    }
                    origGpu = null;
                }
            }

            // We already claimed the entry above (before raising priority). Now that
            // we've read/changed the GPU scheduling class, record its original so the
            // boost restores GPU exactly when it ends.
            if (origGpu.HasValue)
            {
                lock (_launchBoostLock)
                {
                    if (_lbBoosted.TryGetValue(pid, out var entry))
                        entry.OriginalGpu = origGpu;
                }
            }

            string what = "CPU/I-O High, efficiency off" + (gpuApplied ? ", GPU High" : "");
            AddLaunchBoostEvent(name, pid, "Launch Boost", $"boosted for {s.LaunchBoostDurationSeconds}s — {what}");
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"ApplyLaunchBoost({name}) failed: {ex.Message}"); }
        finally { CloseHandle(h); }
    }

    /// <summary>Re-applies the boost rules to an already-boosted process. Called every
    /// tick so Windows can't quietly re-enable efficiency mode (EcoQoS) or decay the
    /// priority during the boost window. GPU is set once at launch (not re-asserted
    /// here) to avoid needless GPU-scheduler churn.</summary>
    private void ReassertLaunchBoost(int pid, TaskSleepSettings s)
    {
        IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return;
        try
        {
            if (s.LaunchBoostCpu)               SetPriorityClass(h, HIGH_PRIORITY_CLASS);
            if (s.LaunchBoostIo)                SetIoPriorityLevel(h, IO_PRIORITY_HIGH);
            if (s.LaunchBoostDisableEfficiency) SetEfficiencyMode(h, false);   // force EcoQoS back off
        }
        catch { /* process may have exited mid-tick — harmless */ }
        finally { CloseHandle(h); }
    }

    private void RestoreLaunchBoost(int pid, LaunchBoostEntry e)
    {
        IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return;   // process exited — nothing to restore
        try
        {
            // Hand scheduling back to Windows: restore the original CPU class (or
            // Normal if it was unknown) and Normal I/O. Efficiency mode was only
            // cleared (off) — that's the default for user apps, so we leave it.
            SetPriorityClass(h, e.OriginalCpu != 0 ? e.OriginalCpu : NORMAL_PRIORITY_CLASS);
            SetIoPriorityLevel(h, IO_PRIORITY_NORMAL);
            // Restore GPU scheduling priority if we changed it.
            if (e.OriginalGpu.HasValue)
            {
                try { D3DKMTSetProcessSchedulingPriorityClass(h, e.OriginalGpu.Value); }
                catch (Exception ex) { _log.Warn("TaskSleepService", $"GPU restore for {e.Name} failed: {ex.Message}"); }
            }
            AddLaunchBoostEvent(e.Name, pid, "Boost ended", "priority restored to default");
        }
        catch (Exception ex) { _log.Warn("TaskSleepService", $"RestoreLaunchBoost({e.Name}) failed: {ex.Message}"); }
        finally { CloseHandle(h); }
    }

    /// <summary>Thread-safe activity-log entry for Launch Boost (the timer runs off the monitor thread,
    /// so it must NOT touch the monitor-thread-owned batch dictionary used by AddEvent).</summary>
    private void AddLaunchBoostEvent(string name, int pid, string action, string detail)
    {
        _eventLog.Enqueue(new MonitorEvent(DateTime.Now, name, pid, action, detail));
        while (_eventLog.Count > MaxEvents) _eventLog.TryDequeue(out _);
        _log.Info("TaskSleepService", $"{action}: {name} (PID {pid}) — {detail}");
    }

}
