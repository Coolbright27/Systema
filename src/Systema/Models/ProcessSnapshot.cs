// ════════════════════════════════════════════════════════════════════════════
// ProcessSnapshot.cs  ·  Single-process row in the TaskSleep live monitor list
// ════════════════════════════════════════════════════════════════════════════
//
// Immutable record capturing one process's state at a monitor tick: name, PID,
// CPU percent, current throttle state, and which throttle actions are active.
// Created on the monitor thread and read on the UI thread via MonitorSnapshot.
//
// RELATED FILES
//   TaskSleepService.cs     — creates ProcessSnapshot instances each tick
//   TaskSleepViewModel.cs   — displays them in the live monitor DataGrid
//   Views/TaskSleepView.xaml — binds to ProcessSnapshot properties
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

/// <summary>
/// Immutable snapshot of a single process captured during a Task Sleep monitor tick.
/// Created on the monitor thread; read-only on the UI thread.
/// </summary>
public class ProcessSnapshot
{
    public int    Pid          { get; init; }
    public string Name         { get; init; } = "";
    public double CpuPercent   { get; init; }
    public bool   IsThrottled  { get; init; }
    public bool   IsProtected  { get; init; }   // foreground / child of foreground
    public bool   IsPendingNap { get; init; }   // in 30s grace period — nap imminent
    public string StatusLabel  { get; init; } = ""; // "Napping" | "Active" | "Pending" | ""
    public string CoreLabel    { get; init; } = ""; // "E-cores" | "All Cores"
    public string ThrottledFor { get; init; } = ""; // "5s", "2m 3s", "" when not throttled
}
