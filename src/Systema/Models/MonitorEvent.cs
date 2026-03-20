// ════════════════════════════════════════════════════════════════════════════
// MonitorEvent.cs  ·  Single log entry in the Task Sleep activity feed
// ════════════════════════════════════════════════════════════════════════════
//
// Records one discrete action taken by TaskSleepService: the process name,
// the action string ("Throttled" | "Restored" | "Re-enforced" | "Error"),
// a detail message, and the UTC timestamp. Surfaced in the TaskSleep recent-
// activity list in the UI via MonitorSnapshot.RecentEvents.
//
// RELATED FILES
//   TaskSleepService.cs      — creates MonitorEvent entries during throttle/restore
//   Views/TaskSleepView.xaml — binds the recent-events feed to this type
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

/// <summary>
/// A single entry in the Task Sleep activity log.
/// Action is one of: "Throttled" | "Restored" | "Re-enforced" | "Error"
/// </summary>
public record MonitorEvent(
    DateTime Timestamp,
    string   ProcessName,
    int      Pid,
    string   Action,
    string   Detail = "");
