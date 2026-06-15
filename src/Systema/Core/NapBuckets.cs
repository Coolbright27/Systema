// ════════════════════════════════════════════════════════════════════════════
// NapBuckets.cs  ·  Single source of truth for which nap CATEGORY a PID is in
// ════════════════════════════════════════════════════════════════════════════
//
// Pure state class — no P/Invoke, no priority manipulation. Replaces the four
// parallel HashSet<int> collections (_minimizedNapPids / _trayNapPids /
// _backgroundNapPids / _idleNapPids) with ONE map: pid → NapReason.
//
// WHY THIS EXISTS
//   A process is only ever napped under a SINGLE category at a time. Tracking
//   that as four separate sets meant every wake/cleanup site had to remember to
//   scrub the right subset — and missing one stranded the process (the "tray
//   helper / Firefox tree member never restored" class of bug). One map makes
//   "what category is this pid?" and "forget this pid" each a single operation,
//   so a category can't be half-cleared.
//
//   Backed by a ConcurrentDictionary so reads from the snapshot/diagnostic path
//   are safe alongside the monitor thread's writes (the old plain HashSets were
//   already read from both, technically a latent race).
//
// RELATED FILES
//   TaskSleepService.cs  — sole consumer (ClearNapState / MarkNap / wake chain)
//   Systema.Tests/       — NapBucketsTests

using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Systema.Core;

/// <summary>The single category a napped process is currently sleeping under.</summary>
internal enum NapReason
{
    /// <summary>App was minimized to the taskbar.</summary>
    Minimized,
    /// <summary>App has no visible top-level window (tray-only / background helper).</summary>
    Tray,
    /// <summary>App went unfocused for the background-nap timeout.</summary>
    Background,
    /// <summary>App sat at ~0% CPU long enough to idle-nap.</summary>
    Idle,
}

/// <summary>
/// Maps each napped PID to the one <see cref="NapReason"/> it is sleeping under.
/// Replaces four parallel HashSets; see file header for rationale.
/// </summary>
internal sealed class NapBuckets
{
    private readonly ConcurrentDictionary<int, NapReason> _reason = new();

    /// <summary>Records (or re-categorizes) <paramref name="pid"/> as napped under <paramref name="reason"/>.</summary>
    public void Mark(int pid, NapReason reason) => _reason[pid] = reason;

    /// <summary>Forgets <paramref name="pid"/> entirely. Returns true if it was tracked.</summary>
    public bool Clear(int pid) => _reason.TryRemove(pid, out _);

    /// <summary>Forgets every PID (full reset on RestoreAll).</summary>
    public void ClearAll() => _reason.Clear();

    /// <summary>True if <paramref name="pid"/> is napped under exactly <paramref name="reason"/>.</summary>
    public bool Is(int pid, NapReason reason) =>
        _reason.TryGetValue(pid, out var r) && r == reason;

    /// <summary>True if <paramref name="pid"/> is napped under any category.</summary>
    public bool IsNapped(int pid) => _reason.ContainsKey(pid);

    /// <summary>Gets the category for <paramref name="pid"/>, or null if not napped.</summary>
    public NapReason? Get(int pid) => _reason.TryGetValue(pid, out var r) ? r : (NapReason?)null;

    /// <summary>Snapshot of every napped PID (safe to enumerate while writers run).</summary>
    public IEnumerable<int> Pids => _reason.Keys;

    /// <summary>Number of PIDs currently napped.</summary>
    public int Count => _reason.Count;
}
