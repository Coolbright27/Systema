// ════════════════════════════════════════════════════════════════════════════
// AppRuleViewModel.cs  ·  Observable wrapper for per-process TaskSleep rules
// ════════════════════════════════════════════════════════════════════════════
//
// Wraps a TaskSleepAppRule model as an ObservableObject so the per-app rules
// DataGrid in TaskSleepView can two-way bind to individual rule fields. Any
// property change notifies the parent TaskSleepViewModel to persist and push
// updated settings to the running TaskSleepService.
//
// RELATED FILES
//   Models/TaskSleepAppRule.cs — underlying data record (ProcessName, priorities, affinity)
//   TaskSleepViewModel.cs     — owns the collection of AppRuleViewModels
// ════════════════════════════════════════════════════════════════════════════

using CommunityToolkit.Mvvm.ComponentModel;
using Systema.Models;

namespace Systema.ViewModels;

/// <summary>
/// Observable wrapper around a <see cref="TaskSleepAppRule"/> used in the Per-App Rules UI.
/// Any property change immediately notifies the parent ViewModel to persist and push new settings.
/// </summary>
public partial class AppRuleViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public string ProcessName { get; }

    [ObservableProperty] private bool   _isBlacklisted;
    [ObservableProperty] private string _cpuPriority    = "Default";
    [ObservableProperty] private string _gpuPriority    = "Default";
    [ObservableProperty] private string _ioPriority     = "Default";
    [ObservableProperty] private string _affinity       = "Default";
    [ObservableProperty] private string _efficiencyMode = "Default";

    public AppRuleViewModel(TaskSleepAppRule rule, Action onChanged)
    {
        _onChanged      = onChanged;
        ProcessName     = rule.ProcessName;
        _isBlacklisted  = rule.IsBlacklisted;
        _cpuPriority    = rule.CpuPriority    ?? "Default";
        _gpuPriority    = rule.GpuPriority    ?? "Default";
        _ioPriority     = rule.IoPriority     ?? "Default";
        _affinity       = rule.Affinity       ?? "Default";
        _efficiencyMode = rule.EfficiencyMode.HasValue
            ? (rule.EfficiencyMode.Value ? "On" : "Off")
            : "Default";
    }

    partial void OnIsBlacklistedChanged(bool value)    => _onChanged();
    partial void OnCpuPriorityChanged(string value)    => _onChanged();
    partial void OnGpuPriorityChanged(string value)    => _onChanged();
    partial void OnIoPriorityChanged(string value)     => _onChanged();
    partial void OnAffinityChanged(string value)       => _onChanged();
    partial void OnEfficiencyModeChanged(string value) => _onChanged();

    public TaskSleepAppRule ToModel() => new()
    {
        ProcessName    = ProcessName,
        IsBlacklisted  = IsBlacklisted,
        CpuPriority    = CpuPriority    == "Default" ? null : CpuPriority,
        GpuPriority    = GpuPriority    == "Default" ? null : GpuPriority,
        IoPriority     = IoPriority     == "Default" ? null : IoPriority,
        Affinity       = Affinity       == "Default" ? null : Affinity,
        EfficiencyMode = EfficiencyMode == "Default" ? (bool?)null
                       : EfficiencyMode == "On",
    };
}
