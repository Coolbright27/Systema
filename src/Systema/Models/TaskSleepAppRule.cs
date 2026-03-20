// ════════════════════════════════════════════════════════════════════════════
// TaskSleepAppRule.cs  ·  Per-app override rule for Task Sleep throttling
// ════════════════════════════════════════════════════════════════════════════
//
// Defines per-process overrides for TaskSleepService: custom CPU/GPU/IO
// priority levels, CPU affinity mask, and a blacklist flag (never throttle).
// ProcessName is stored without the .exe extension (e.g. "chrome", "discord").
// Wrapped by AppRuleViewModel for two-way binding in the per-app rules DataGrid.
//
// RELATED FILES
//   TaskSleepSettings.cs    — contains List<TaskSleepAppRule> AppRules
//   AppRuleViewModel.cs     — observable wrapper for UI binding
//   TaskSleepService.cs     — looks up matching rules during throttle decisions
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

public class TaskSleepAppRule
{
    /// <summary>Process name without .exe (e.g. "chrome", "discord").</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>When true, this process is never throttled regardless of other settings.</summary>
    public bool IsBlacklisted { get; set; } = false;

    // null = inherit the global setting for that dimension
    public string? CpuPriority    { get; set; }  // "Idle" | "Below Normal" | "Normal" | "Above Normal" | "High"
    public string? GpuPriority    { get; set; }  // "Idle" | "Normal"
    public string? IoPriority     { get; set; }  // "Very Low" | "Low" | "Normal"
    public string? Affinity       { get; set; }  // "E-cores" | "Any"
    public bool?   EfficiencyMode { get; set; }  // true | false
}
