// ════════════════════════════════════════════════════════════════════════════
// ProcessInfo.cs  ·  Process snapshot row for the Process tab list
// ════════════════════════════════════════════════════════════════════════════
//
// Carries one process's display data: Pid, Name, CPU percent (computed via
// GetProcessTimes P/Invoke delta), working-set memory in MB, and current
// priority class. Populated by ProcessService and bound to the ProcessView
// DataGrid via ProcessViewModel.
//
// RELATED FILES
//   ProcessService.cs      — creates ProcessInfo instances during enumeration
//   ProcessViewModel.cs    — holds the ObservableCollection<ProcessInfo>
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

public class ProcessInfo
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public double CpuPercent { get; set; }
    public long WorkingSetMb { get; set; }
    public string Priority { get; set; } = string.Empty;
    public bool IsThrottled { get; set; }
}
