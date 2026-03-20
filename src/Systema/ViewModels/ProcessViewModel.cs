// ════════════════════════════════════════════════════════════════════════════
// ProcessViewModel.cs  ·  Real-time process list with CPU%, memory, and priority
// ════════════════════════════════════════════════════════════════════════════
//
// Implements IAutoRefreshable so MainViewModel refreshes it every 1 s (focused)
// or 5 s (unfocused). Wraps ProcessService to show per-process CPU percent,
// working-set memory, and current priority class. Also exposes commands to
// change a process's priority and CPU affinity mask.
//
// RELATED FILES
//   ProcessService.cs         — P/Invoke CPU% via GetProcessTimes, affinity ops
//   Models/ProcessInfo.cs     — process row data shape
//   Views/ProcessView.xaml    — DataGrid bound to Processes collection
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Core;
using Systema.Models;
using Systema.Services;

namespace Systema.ViewModels;

public partial class ProcessViewModel : ObservableObject, IAutoRefreshable
{
    private readonly ProcessService _processService;
    private static readonly LoggerService _log = LoggerService.Instance;
    private int _isRefreshing; // 0 = idle, 1 = running (Interlocked guard)

    [ObservableProperty] private ObservableCollection<ProcessInfo> _processes = new();
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ProcessViewModel(ProcessService processService)
    {
        _processService = processService;
    }

    // IAutoRefreshable — called by the timer; skips if already refreshing
    public Task RefreshAsync() => DoRefreshAsync(silent: true);

    [RelayCommand]
    private Task RefreshCommandAsync() => DoRefreshAsync(silent: false);

    private async Task DoRefreshAsync(bool silent)
    {
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0) return;
        IsLoading = true;
        if (!silent) StatusMessage = "Loading processes...";
        try
        {
            var list = await _processService.GetBackgroundProcessesAsync();
            Processes.Clear();
            foreach (var p in list.Take(50))
                Processes.Add(p);
            StatusMessage = $"{Processes.Count} background processes.";
        }
        catch (Exception ex)
        {
            _log.Error("ProcessViewModel", "Failed to refresh processes", ex);
            StatusMessage = $"Error loading processes: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }
}
