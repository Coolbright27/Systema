// ════════════════════════════════════════════════════════════════════════════
// MonitorSnapshot.cs  ·  Full Task Sleep state snapshot published each tick
// ════════════════════════════════════════════════════════════════════════════
//
// Immutable record published by TaskSleepService at the end of every monitor
// tick. Contains the list of currently throttled processes (ProcessSnapshot
// collection), recent activity events (MonitorEvent list), and system-wide CPU
// load. TaskSleepViewModel reads it on the UI timer to update the live display.
//
// RELATED FILES
//   TaskSleepService.cs     — creates and publishes MonitorSnapshot each tick
//   TaskSleepViewModel.cs   — consumes snapshot to update observable collections
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

/// <summary>
/// Immutable snapshot of Task Sleep state at the end of a monitor tick.
/// Published by <see cref="Services.TaskSleepService"/> and consumed by the UI timer.
/// </summary>
public record MonitorSnapshot(
    double                         SystemCpuPercent,
    int                            TotalThrottled,
    long                           FreeRamMb,
    bool                           RamPressure,
    IReadOnlyList<ProcessSnapshot> Processes,
    IReadOnlyList<MonitorEvent>    RecentEvents);
