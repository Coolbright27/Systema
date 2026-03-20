// ════════════════════════════════════════════════════════════════════════════
// MemoryViewModel.cs  ·  RAM usage display and startup item management
// ════════════════════════════════════════════════════════════════════════════
//
// Displays physical RAM totals and usage (from MemoryService via P/Invoke) and
// lists startup items sourced from registry Run keys and Task Scheduler (via
// StartupService). Exposes enable/disable commands for each startup entry.
// Implements IAutoRefreshable for periodic RAM stat updates.
//
// RELATED FILES
//   MemoryService.cs          — GlobalMemoryStatusEx P/Invoke, page-file stats
//   StartupService.cs         — enumerates registry + Task Scheduler startup items
//   Models/StartupItem.cs     — startup entry data shape
//   Views/MemoryView.xaml     — binds RAM gauges and startup list
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Core;
using Systema.Models;
using Systema.Services;
using static Systema.Core.ThreadHelper;

namespace Systema.ViewModels;

public partial class MemoryViewModel : ObservableObject, IAutoRefreshable
{
    private readonly MemoryService _memoryService;
    private readonly StartupService _startupService;
    private static readonly LoggerService _log = LoggerService.Instance;
    private int _isRefreshing;
    private bool _hasLoadedOnce;

    [ObservableProperty] private long _totalRamMb;
    [ObservableProperty] private long _availableRamMb;
    [ObservableProperty] private int _pagefileInitialMb;
    [ObservableProperty] private int _pagefileMaxMb;
    [ObservableProperty] private string _recommendedPagefileText = string.Empty;
    [ObservableProperty] private ObservableCollection<StartupItem> _startupItems = new();
    [ObservableProperty] private string _currentPagefileText = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public long UsedRamMb => TotalRamMb - AvailableRamMb;
    public double RamUsagePercent => TotalRamMb > 0 ? (double)UsedRamMb / TotalRamMb * 100 : 0;

    public MemoryViewModel(MemoryService memoryService, StartupService startupService)
    {
        _memoryService  = memoryService;
        _startupService = startupService;

        // Set RAM-based defaults immediately
        int recommended = _memoryService.GetRecommendedPagefileMb();
        _pagefileInitialMb = recommended;
        _pagefileMaxMb     = recommended;
    }

    // IAutoRefreshable — first call does a full refresh (loads startup items); subsequent timer calls are partial
    public Task RefreshAsync()
    {
        if (!_hasLoadedOnce)
        {
            _hasLoadedOnce = true;
            return DoRefreshAsync(fullRefresh: true);
        }
        return DoRefreshAsync(fullRefresh: false);
    }

    [RelayCommand]
    private Task RefreshCommandAsync() => DoRefreshAsync(fullRefresh: true);

    private async Task DoRefreshAsync(bool fullRefresh)
    {
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0) return;
        IsLoading = true;
        try
        {
            // Use the fast P/Invoke path — both values in a single kernel call
            var (total, avail) = await Task.Run(() => _memoryService.GetRamStats());
            TotalRamMb     = total;
            AvailableRamMb = avail;
            OnPropertyChanged(nameof(UsedRamMb));
            OnPropertyChanged(nameof(RamUsagePercent));

            // Update recommended text based on detected RAM
            int rec = _memoryService.GetRecommendedPagefileMb();
            RecommendedPagefileText = $"Recommended for {TotalRamMb / 1024} GB RAM: {rec:N0} MB";

            if (fullRefresh)
            {
                // Registry read for configured sizes (fast) + WMI for current running size
                var (init, max, isSystemManaged) = await RunOnLargeStackAsync(() => _memoryService.GetPagefileSettings());
                var (allocMb, usedMb)            = await RunOnLargeStackAsync(() => _memoryService.GetCurrentPagefileUsageMb());

                if (!isSystemManaged && init > 0)
                {
                    // Custom sizes configured — show them and pre-fill the text boxes
                    PagefileInitialMb = init;
                    PagefileMaxMb     = max;
                    string usageNote = allocMb > 0 ? $"  ·  {usedMb:N0} MB in use now" : string.Empty;
                    CurrentPagefileText = $"Set to: {init:N0} MB initial / {max:N0} MB max{usageNote}";
                }
                else
                {
                    // System-managed — show recommended defaults in text boxes + actual running size
                    PagefileInitialMb = rec;
                    PagefileMaxMb     = rec;
                    string runningNote = allocMb > 0 ? $"currently {allocMb:N0} MB" : "size varies";
                    CurrentPagefileText = $"Windows managed  ·  {runningNote}";
                }

                // GetStartupItems() calls TaskScheduler COM APIs which can exhaust a small threadpool stack
                var items = await RunOnLargeStackAsync(() => _startupService.GetStartupItems());
                StartupItems.Clear();
                foreach (var item in items) StartupItems.Add(item);
            }

            StatusMessage = $"RAM: {TotalRamMb:N0} MB total, {AvailableRamMb:N0} MB free";
        }
        catch (Exception ex)
        {
            _log.Error("MemoryViewModel", "Refresh failed", ex);
            StatusMessage = $"Error loading data: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    [RelayCommand]
    private async Task ConfigurePagefileAsync()
    {
        if (PagefileInitialMb <= 0 || PagefileMaxMb <= 0)
        {
            StatusMessage = "Pagefile size must be greater than 0.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Configuring pagefile...";
        try
        {
            var result = await _memoryService.ConfigurePagefileAsync(PagefileInitialMb, PagefileMaxMb);
            StatusMessage = result.Message;
            if (result.Success)
            {
                CurrentPagefileText = $"Current: {PagefileInitialMb:N0} MB initial / {PagefileMaxMb:N0} MB max (restart required)";
            }
            else
            {
                // Log failures even when no exception is thrown (e.g. disk space check)
                _log.Error("MemoryViewModel", $"Pagefile configuration failed: {result.Message}");
            }
        }
        catch (Exception ex)
        {
            _log.Error("MemoryViewModel", "Pagefile configuration threw an unexpected exception", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RevertPagefileAsync()
    {
        IsLoading = true;
        StatusMessage = "Reverting to Windows-managed pagefile...";
        try
        {
            var result = await _memoryService.RevertToManagedPagefileAsync();
            StatusMessage = result.Message;
            if (result.Success)
            {
                // Reset UI to show "System Managed" state
                int rec = _memoryService.GetRecommendedPagefileMb();
                PagefileInitialMb = rec;
                PagefileMaxMb     = rec;
                CurrentPagefileText = "Current: System Managed (restart required)";
            }
            else
            {
                _log.Error("MemoryViewModel", $"Pagefile revert failed: {result.Message}");
            }
        }
        catch (Exception ex)
        {
            _log.Error("MemoryViewModel", "Pagefile revert threw an unexpected exception", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleStartupItemAsync(StartupItem item)
    {
        try
        {
            var result = await Task.Run(() => _startupService.SetStartupItemEnabled(item, !item.IsEnabled));
            StatusMessage = result.Message;
            if (result.Success)
            {
                item.IsEnabled = !item.IsEnabled;
                var idx = StartupItems.IndexOf(item);
                if (idx >= 0) { StartupItems.RemoveAt(idx); StartupItems.Insert(idx, item); }
            }
        }
        catch (Exception ex)
        {
            _log.Error("MemoryViewModel", "Toggle startup item failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
    }
}
