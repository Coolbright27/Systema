// ════════════════════════════════════════════════════════════════════════════
// WatchdogService.cs  ·  Keeps Systema running via a Task Scheduler task
// ════════════════════════════════════════════════════════════════════════════
//
// When "Keep Systema Running" is enabled, this service registers a Task
// Scheduler task that:
//   • Fires immediately on user logon
//   • Repeats every 5 minutes indefinitely
//   • Checks via PowerShell whether Systema.exe is running; launches it if not
//
// The single-instance mutex in App.xaml.cs prevents duplicate instances from
// spawning if the task fires while Systema is already running.
// ════════════════════════════════════════════════════════════════════════════

using Microsoft.Win32.TaskScheduler;
using Systema.Core;

namespace Systema.Services;

public class WatchdogService
{
    private static readonly LoggerService _log = LoggerService.Instance;
    private const string TaskName = "Systema Watchdog";

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var ts = new TaskService();
                return ts.FindTask(TaskName, true) != null;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Creates (or replaces) the watchdog scheduled task.
    /// <paramref name="exePath"/> should be the full path to Systema.exe.
    /// </summary>
    public void Enable(string exePath)
    {
        try
        {
            using var ts = new TaskService();
            var td = ts.NewTask();

            td.RegistrationInfo.Description = "Restarts Systema if it ever stops running.";
            td.RegistrationInfo.Author      = "Systema";

            td.Principal.RunLevel = TaskRunLevel.Highest;   // run elevated

            td.Settings.Hidden                    = true;
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries    = false;
            td.Settings.ExecutionTimeLimit        = TimeSpan.Zero; // no time limit
            td.Settings.MultipleInstances         = TaskInstancesPolicy.IgnoreNew;

            // Logon trigger for the current user, repeating every 5 minutes
            var logon = new LogonTrigger
            {
                UserId = $"{Environment.MachineName}\\{Environment.UserName}",
            };
            logon.Repetition.Interval          = TimeSpan.FromMinutes(5);
            logon.Repetition.StopAtDurationEnd = false;
            td.Triggers.Add(logon);

            // PowerShell one-liner: only launch if not already running
            var psCmd = $"if (-not (Get-Process -Name 'Systema' -ErrorAction SilentlyContinue)) {{ Start-Process '{exePath}' }}";
            td.Actions.Add(new ExecAction(
                "powershell.exe",
                $"-WindowStyle Hidden -NonInteractive -Command \"{psCmd}\""));

            ts.RootFolder.RegisterTaskDefinition(
                TaskName, td,
                TaskCreation.CreateOrUpdate,
                null, null,
                TaskLogonType.InteractiveToken);

            _log.Info("Watchdog", $"Watchdog task enabled — checks every 5 min ({exePath})");
        }
        catch (Exception ex)
        {
            _log.Error("Watchdog", $"Failed to enable watchdog task: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>Removes the watchdog scheduled task.</summary>
    public void Disable()
    {
        try
        {
            using var ts = new TaskService();
            if (ts.FindTask(TaskName, true) != null)
            {
                ts.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
                _log.Info("Watchdog", "Watchdog task removed");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Watchdog", $"Failed to disable watchdog task: {ex.Message}", ex);
        }
    }
}
