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
using Systema.Core;
using Systema.Models;

namespace Systema.Services;

public sealed partial class TaskSleepService
{
    // ════════════════════════════════════════════════════════════════════════════
    //  Launch Boost
    // ════════════════════════════════════════════════════════════════════════════
    //
    //   SEES      every new process twice over: the Win32_ProcessStartTrace event and a 300 ms
    //             toolhelp poll (whichever is first; the claim in ApplyLaunchBoost stops doubles).
    //   DECIDES   with Core/LaunchBoostRules.cs: names never boosted, then the ordered launch rules
    //             (opened from the shell or a launcher stub it started → boost; a boosting parent's
    //             child → rides that boost; everything else → left alone).
    //   BOOSTS    CPU High, I/O High and efficiency mode off (each per its setting); page priority
    //             back to Normal if a napped parent left it lower; GPU scheduling to Realtime only
    //             if "GPU priority" is on (off by default), with the auto-disable safety net below.
    //   HOLDS     for the set duration (default 20 s), re-asserting CPU/I-O/efficiency every poll so
    //             Windows can't quietly flip EcoQoS back on. A child riding its parent's boost ends
    //             with it, so the whole app gets one window.
    //   ENDS      by restoring the ORIGINAL priorities, so Windows takes scheduling back over.
    //
    // Fully self-contained: owns its own state and a thread-safe event-log path; reuses the same
    // priority P/Invokes the napping engine uses (no new native surface for Defender/SAC to flag).

    private System.Threading.Timer? _launchBoostTimer;
    private readonly object _launchBoostLock = new();
    private HashSet<int> _lbKnownPids = new();
    private readonly Dictionary<int, LaunchBoostEntry> _lbBoosted = new();

    // Event-driven launch detection. Win32_ProcessStartTrace fires the instant a
    // process is created (ETW-backed, near-zero overhead), so the boost lands from
    // the app's first moments — DLL loads and init — instead of on the next poll.
    // The 300 ms polling timer is kept as a fallback (belt-and-suspenders) for
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

    // LaunchBoostTick reentrancy guard. The 300 ms System.Threading.Timer fires
    // on a threadpool thread; if a single tick takes longer than 300 ms (heavy boost dict,
    // slow P/Invoke, etc.) the next tick will start while the first is still
    // running, contending on _launchBoostLock. Worst case: any P/Invoke that
    // genuinely hangs leaves the lock held, and every subsequent tick blocks
    // forever waiting on it — eventually starving the threadpool, which is
    // exactly the "process alive but UI-dead" pattern. 0/1 flip via
    // Interlocked guarantees at most one tick body runs at a time.
    private int _lbTickInFlight;

    private sealed class LaunchBoostEntry
    {
        public DateTime Expiry;       // UTC
        public uint     OriginalCpu;  // CPU priority class to restore
        public int?     OriginalGpu;  // GPU sched priority to restore (null = GPU not boosted)
        public uint?    OriginalMem;  // page priority to restore (null = it was already Normal)
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
    /// the 300 ms polling timer still covers everything.
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
                    $"Launch Boost: process-start watcher failed to arm — falling back to 300 ms polling ({ex.Message})");
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
        // Returning is correct — the next 300 ms tick fires automatically.
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

    // ── Who gets boosted: Core/LaunchBoostRules.cs ─────────────────────────────
    // The decision is two ordered rule lists there (names never boosted, then the launch rules);
    // this part only gathers the facts they need and turns the answer into an expiry time.

    /// <summary>The nap engine's name lists that <see cref="LaunchBoostNames"/> consults.</summary>
    private LaunchBoostNames.NameLists BoostNameLists => new(
        IsWindowsProcess:   n => SystemProcessNames.Contains(n),
        IsSecuritySoftware: n => SecurityCriticalProcessNames.Contains(n) || _detectedAvProcessNames.Contains(n));

    /// <summary>
    /// Asks <see cref="LaunchBoostRules"/> about a newly seen process. The parent facts are lazy,
    /// so a launch the early rules decide never pays for a process lookup or a full snapshot.
    /// </summary>
    private LaunchDecision DecideLaunch(int ppid, string name, out DateTime? parentExpiry)
    {
        DateTime? pExp = null;
        lock (_launchBoostLock)
            if (_lbBoosted.TryGetValue(ppid, out var parentEntry)) pExp = parentEntry.Expiry;
        parentExpiry = pExp;

        return LaunchBoostRules.Decide(new LaunchFacts(
            Boostable:            LaunchBoostNames.NeverBoostReason(name, BoostNameLists) == null,
            ParentPid:            ppid,
            ParentBoosted:        pExp.HasValue,
            ParentNapped:         new(() => _throttledPids.ContainsKey(ppid) || _napBuckets.IsNapped(ppid)),
            ParentName:           new(() => GetProcessNameSafe(ppid)),
            ParentAge:            new(() => GetProcessAgeSafe(ppid)),
            ParentStartedByShell: new(() => WasStartedByShell(ppid))));
    }

    /// <summary>
    /// Whether to boost a newly seen process and until when. A new boost runs for the set duration;
    /// a child riding its parent's boost ends with it, so the whole app gets ONE window (a child that
    /// spawns part way through doesn't start a fresh one, and none are boosted once it closes).
    /// </summary>
    private bool ShouldLaunchBoost(int ppid, string name, TaskSleepSettings s, DateTime now, out DateTime expiry)
    {
        var decision = DecideLaunch(ppid, name, out DateTime? parentExpiry);
        expiry = decision.Action switch
        {
            LaunchAction.RideParentBoost => parentExpiry!.Value,
            LaunchAction.Boost           => now.AddSeconds(Math.Clamp(s.LaunchBoostDurationSeconds, 3, 120)),
            _                            => default,
        };
        return decision.Action != LaunchAction.Skip;
    }

    /// <summary>True when the Windows shell (explorer) started this process.</summary>
    private static bool WasStartedByShell(int pid) =>
        BuildParentMap().TryGetValue(pid, out int parent) &&
        string.Equals(GetProcessNameSafe(parent), "explorer", StringComparison.OrdinalIgnoreCase);

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
                // Concurrency guard: the event watcher and the 300 ms timer can both
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

            // Page priority. A launching process is faulting in its exe and DLLs, which is exactly
            // when page priority decides whether those pages survive memory pressure.
            //
            // NORMAL (5) is the CEILING — Windows has no above-normal page priority — so this is
            // a restore to par, not a boost past it. That makes it a no-op for most launches, and
            // it earns its place in one specific case: a process spawned by a NAPPED parent
            // inherits that parent's page priority, so it starts at MEMORY_PRIORITY_LOWEST and
            // thrashes while loading. Systema created that situation, so Systema should undo it.
            uint? origMem = GetMemoryPriority(h);
            if (origMem is uint m && m < MEMORY_PRIORITY_NORMAL)
                TrySetMemoryPriority(h, MEMORY_PRIORITY_NORMAL);
            else
                origMem = null;   // nothing changed, so nothing to restore

            // GPU scheduling priority — opt-in only (default off). Capture the
            // original so it's restored exactly when the boost ends.
            //
            // SAFETY: if the WDDM driver starts returning non-zero NTSTATUS (the
            // pattern on older Intel iGPUs / unstable WDDM), keep calling it would
            // risk triggering a TDR (graphics device reset) which kills WPF
            // rendering and leaves the app alive but UI-frozen. We count failures
            // and stop touching GPU priority for the rest of the session after
            // GpuBoostFailureThreshold (50) error returns, or 3 if the call throws
            // (a success resets the count) — the boost still applies CPU/I-O
            // priority and efficiency-off, just no GPU. A log line tells us once.
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
            if (origGpu.HasValue || origMem.HasValue)
            {
                lock (_launchBoostLock)
                {
                    if (_lbBoosted.TryGetValue(pid, out var entry))
                    {
                        if (origGpu.HasValue) entry.OriginalGpu = origGpu;
                        if (origMem.HasValue) entry.OriginalMem = origMem;
                    }
                }
            }

            string what = "CPU/I-O High, efficiency off"
                        + (gpuApplied ? ", GPU High" : "")
                        + (origMem.HasValue ? ", page priority restored to Normal" : "");
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

            // Only touched when it was BELOW Normal, so put back exactly what was found rather
            // than assuming Normal. A process legitimately left low by its parent should go back
            // to low once its launch is over.
            if (e.OriginalMem is uint om) TrySetMemoryPriority(h, om);
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
