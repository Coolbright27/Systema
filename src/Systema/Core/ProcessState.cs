// ════════════════════════════════════════════════════════════════════════════
// ProcessState.cs  ·  One per-PID state record (replaces ~28 parallel dictionaries)
// ════════════════════════════════════════════════════════════════════════════
//
// The nap engine used to keep roughly thirty separate pid-keyed collections
// (_throttledAt, _restoredAt, _cpuAtThrottle, _napSince, _idleSince, …). Every
// one had to be scrubbed in lockstep in CleanupDeadProcesses, and forgetting a
// single one stranded state — the source of the "stuck Idle / not all restored"
// bug class. This collapses them into ONE record per PID held in a single map:
//   • cleanup is one TryRemove — it can never miss a field,
//   • adding a feature is one field — no new cleanup wiring,
//   • pid-reuse is caught for free via CreationTime.
//
// Fields are NULLABLE where the old dictionary's KEY-PRESENCE carried meaning
// (e.g. "is this pid's throttle-time tracked?"). A null field == the old "key
// absent". Plain value types (int counters) keep their natural default.
//
// Not thread-safe per-record: like the old single-thread dictionaries, records
// are mutated only on the monitor thread. The MAP that holds them is a
// ConcurrentDictionary so the snapshot/diagnostic path can read it safely.
//
// RELATED FILES
//   TaskSleepService.cs  — sole owner; see _state / StateFor / DropState
//   Systema.Tests/       — ProcessStateTests

namespace Systema.Core;

internal sealed class ProcessState
{
    /// <summary>Process creation time (100-ns FILETIME) captured by the sampler. Lets the
    /// engine detect a recycled PID — if Windows reuses a number, the creation time differs
    /// and any state carried over from the dead process is dropped instead of mis-applied.</summary>
    public long CreationTime;

    // ── Nap timing (Batch 1) ──────────────────────────────────────────────────
    /// <summary>When this process was throttled (drives the classic time-based restore).</summary>
    public System.DateTime? ThrottledAt;
    /// <summary>When this process was last restored — a 5 s cooldown blocks immediate re-nap.</summary>
    public System.DateTime? RestoredAt;
    /// <summary>CPU% sampled at throttle time (diagnostics / hysteresis).</summary>
    public double? CpuAtThrottle;
    /// <summary>When this process first crossed the CPU trigger (sustained-load timer).</summary>
    public System.DateTime? OverThresholdSince;
    /// <summary>When this process first dropped below the idle threshold (idle-nap timer).</summary>
    public System.DateTime? IdleSince;
    /// <summary>Consecutive low-CPU ticks (smart-nap counter). 0 == absent.</summary>
    public int LowCpuTickCount;
    /// <summary>Last time this PID was in the foreground/protected set (background-nap timer).</summary>
    public System.DateTime? LastForegroundAt;
    /// <summary>When this process entered nap (deep-sleep timer / brief-wake fairness sort).</summary>
    public System.DateTime? NapSince;

    // ── Child tracking (Batch 2) ──────────────────────────────────────────────
    /// <summary>If this process was napped as a CHILD of a napped parent, the parent's PID
    /// (else null). Replaces both the old _napChildPids set ("is a nap-child" == this is
    /// non-null) and the _parentOfNapChild map (the parent PID itself).</summary>
    public int? NapChildParent;

    // ── Sampling / IO / window / caches (Batch 3) ─────────────────────────────
    /// <summary>Most-recent sampled CPU%.</summary>
    public double? LastCpuPercent;
    /// <summary>Last raw I/O byte counters + sample time (for rate computation).</summary>
    public (long ReadBytes, long WriteBytes, long OtherBytes, System.DateTime SampleTime)? IoSample;
    /// <summary>Last computed network / disk I/O rates (KB/s).</summary>
    public (double NetKBps, double DiskKBps)? IoRates;
    /// <summary>Last known top-level window title (notification-grace change detection).</summary>
    public string? LastWindowTitle;
    /// <summary>When the window title last changed (notification grace start).</summary>
    public System.DateTime? TitleChangedAt;
    /// <summary>Last known visible-window count (notification grace).</summary>
    public int? LastWindowCount;
    /// <summary>When this PID last had an Active audio session (audio stickiness).</summary>
    public System.DateTime? LastAudioActiveAt;
    /// <summary>Cached "is elevated / SYSTEM" result (null = not yet computed).</summary>
    public bool? ElevatedCache;
    /// <summary>Cached "is a service account (S-1-5-18/19/20)" result (null = not computed).</summary>
    public bool? ServiceAccountCache;
    /// <summary>Cached "is a Windows component under System32/SysWOW64" result (path is immutable).</summary>
    public bool? IsWindowsSystemBinary;

    // ── Brief-wake bookkeeping ────────────────────────────────────────────────
    /// <summary>While a napped process is mid-brief-wake it's pulled out of _throttledPids, so
    /// its true pre-nap priority is parked here for the window. Brief wakes change no priorities;
    /// re-nap re-seats this value and a full wake restores to it. Null when not mid-brief-wake.</summary>
    public uint? NappedOriginalCpu;
    /// <summary>True once this napped PID's working set was trimmed by the deep-sleep compressor.</summary>
    public bool DeepSleepTrimmed;
    /// <summary>Human-readable reason this PID was skipped this tick (monitor UI).</summary>
    public string? SkipReason;
    /// <summary>Access-denied backoff: consecutive failures + last failure time.</summary>
    public (int Count, System.DateTime LastFail)? AccessDenied;
}
