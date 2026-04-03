// ════════════════════════════════════════════════════════════════════════════
// ReEnforceCounter.cs  ·  Counts how many times re-enforce fired per PID
// ════════════════════════════════════════════════════════════════════════════
//
// Pure counting class — no P/Invoke, no threading. Tracks how many times the
// re-enforce step had to push a process back to its nap state within a rolling
// time window. Used by TaskSleepService to detect processes that persistently
// reject their nap and should be skipped permanently.
//
// RELATED FILES
//   TaskSleepService.cs  — sole consumer
//   Systema.Tests/       — unit tests

using System.Collections.Concurrent;

namespace Systema.Core;

internal sealed class ReEnforceCounter
{
    private readonly ConcurrentDictionary<int, (int Count, DateTime WindowStart)> _state = new();

    /// <summary>
    /// Records one re-enforce event for <paramref name="pid"/>.
    /// Returns <c>true</c> if the accumulated count within <paramref name="window"/>
    /// has reached <paramref name="threshold"/>.
    /// </summary>
    public bool Record(int pid, TimeSpan window, int threshold)
    {
        var now = DateTime.UtcNow;

        var updated = _state.AddOrUpdate(
            pid,
            _ => (1, now),
            (_, entry) => now - entry.WindowStart <= window
                ? (entry.Count + 1, entry.WindowStart)
                : (1, now));

        return updated.Count >= threshold;
    }

    /// <summary>Returns the current count for <paramref name="pid"/>, or 0 if not tracked.</summary>
    public int GetCount(int pid) =>
        _state.TryGetValue(pid, out var entry) ? entry.Count : 0;

    /// <summary>Removes all tracking state for <paramref name="pid"/>.</summary>
    public void Reset(int pid) => _state.TryRemove(pid, out _);
}
