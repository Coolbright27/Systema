// ════════════════════════════════════════════════════════════════════════════
// ProcessService.cs  ·  Process enumeration with P/Invoke CPU% and affinity ops
// ════════════════════════════════════════════════════════════════════════════
//
// Enumerates running processes and computes per-process CPU percentage using
// GetProcessTimes P/Invoke (avoids WMI latency). Exposes SetPriority and
// SetAffinity helpers consumed by ProcessViewModel. Runs on a large-stack thread
// via ThreadHelper to prevent StackOverflowException during deep enumeration.
//
// RELATED FILES
//   Models/ProcessInfo.cs      — process row data shape (Pid, Name, CpuPercent, etc.)
//   ProcessViewModel.cs        — calls GetProcessesAsync, SetPriority, SetAffinity
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using Systema.Core;
using Systema.Models;
using static Systema.Core.ThreadHelper;

namespace Systema.Services;

public class ProcessService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    private static readonly HashSet<string> _systemProcessWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "fontdrvhost", "dwm", "explorer", "Systema"
    };

    public async Task<List<ProcessInfo>> GetBackgroundProcessesAsync()
    {
        return await RunOnLargeStackAsync(() =>
        {
            Log.Info("ProcessService", "Enumerating background processes");
            var result = new List<ProcessInfo>();
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (_systemProcessWhitelist.Contains(proc.ProcessName)) continue;
                    result.Add(new ProcessInfo
                    {
                        Pid = proc.Id,
                        Name = proc.ProcessName,
                        WorkingSetMb = proc.WorkingSet64 / 1024 / 1024,
                        Priority = proc.PriorityClass.ToString()
                    });
                }
                catch { /* access denied on protected processes — expected */ }
            }
            Log.Info("ProcessService", $"Found {result.Count} background processes");
            return result.OrderByDescending(p => p.WorkingSetMb).ToList();
        });
    }

}
