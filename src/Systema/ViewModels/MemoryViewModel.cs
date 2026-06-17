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
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Core;
using Systema.Models;
using Systema.Services;
using static Systema.Core.ThreadHelper;

namespace Systema.ViewModels;

public partial class MemoryViewModel : ObservableObject, IAutoRefreshable, IDisposable
{
    private readonly MemoryService  _memoryService;
    private readonly StartupService _startupService;
    private readonly SettingsService _settings;
    private static readonly LoggerService _log = LoggerService.Instance;
    private int  _isRefreshing;
    private bool _hasLoadedOnce;

    [ObservableProperty] private long _totalRamMb;
    [ObservableProperty] private long _availableRamMb;
    /// <summary>Single static page-file size. Passed as both initial and maximum to the service.</summary>
    [ObservableProperty] private int _pagefileInitialMb;
    [ObservableProperty] private string _recommendedPagefileText = string.Empty;
    [ObservableProperty] private ObservableCollection<StartupItem> _startupItems = new();
    [ObservableProperty] private string _currentPagefileText = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Free RAM card
    [ObservableProperty] private bool   _isFreeing;
    [ObservableProperty] private string _freeRamStatus = string.Empty;

    public long UsedRamMb => TotalRamMb - AvailableRamMb;
    public double RamUsagePercent => TotalRamMb > 0 ? (double)UsedRamMb / TotalRamMb * 100 : 0;

    // ── Friendly GB-formatted stats for the redesigned tiles ──
    public string UsedRamGb => (UsedRamMb / 1024.0).ToString("0.0");
    public string FreeRamGb => (AvailableRamMb / 1024.0).ToString("0.0");

    // ── "Speed up your startup" recommendation — currently-enabled High-impact apps ──
    private System.Collections.Generic.IEnumerable<StartupItem> HeavyEnabled =>
        StartupItems.Where(i => i.IsEnabled && i.ImpactLabel == "High");
    public int    HighImpactCount          => HeavyEnabled.Count();
    public bool   HasStartupRecommendation => HighImpactCount > 0;
    public string HighImpactNames          => string.Join(", ", HeavyEnabled.Select(i => i.Name));
    public string StartupRecommendationText => HighImpactCount == 1
        ? "1 app is slowing down your startup"
        : $"{HighImpactCount} apps are slowing down your startup";

    private void RaiseStartupRecommendation()
    {
        OnPropertyChanged(nameof(HighImpactCount));
        OnPropertyChanged(nameof(HasStartupRecommendation));
        OnPropertyChanged(nameof(HighImpactNames));
        OnPropertyChanged(nameof(StartupRecommendationText));
    }

    private void RaiseRamStats()
    {
        OnPropertyChanged(nameof(UsedRamMb));
        OnPropertyChanged(nameof(RamUsagePercent));
        OnPropertyChanged(nameof(UsedRamGb));
        OnPropertyChanged(nameof(FreeRamGb));
    }

    /// <summary>
    /// True when Auto-Pilot Mode is active — XAML binds this to IsEnabled (inverted)
    /// so Auto-Pilot-managed controls are grayed out when the mode is on.
    /// </summary>
    public bool IsAutoPilotActive => _settings.AutoPilotModeEnabled;

    public MemoryViewModel(MemoryService memoryService, StartupService startupService,
                           SettingsService settings)
    {
        _memoryService  = memoryService;
        _startupService = startupService;
        _settings       = settings;

        // Set RAM-based default immediately so the TextBox is pre-filled on open.
        _pagefileInitialMb = _memoryService.GetRecommendedPagefileMb();

        SettingsService.AutoPilotModeChanged += OnAutoPilotModeChanged;
    }

    private void OnAutoPilotModeChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(
            () => OnPropertyChanged(nameof(IsAutoPilotActive)));

    public void Dispose() => SettingsService.AutoPilotModeChanged -= OnAutoPilotModeChanged;

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
            RaiseRamStats();

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
                    // Custom/static size configured — pre-fill with the existing value (use init).
                    PagefileInitialMb = init;
                    string usageNote = allocMb > 0 ? $"  ·  {usedMb:N0} MB in use now" : string.Empty;
                    CurrentPagefileText = $"Set to: {init:N0} MB static{usageNote}";
                }
                else
                {
                    // System-managed or no custom size — show recommended default as starting point.
                    PagefileInitialMb = rec;
                    string runningNote = allocMb > 0 ? $"currently {allocMb:N0} MB" : "size varies";
                    CurrentPagefileText = $"Windows managed  ·  {runningNote}";
                }

                // GetStartupItems() calls TaskScheduler COM APIs which can exhaust a small threadpool stack
                var items = await RunOnLargeStackAsync(() => _startupService.GetStartupItems());
                StartupItems.Clear();
                foreach (var item in items) StartupItems.Add(item);
                RaiseStartupRecommendation();
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
        if (PagefileInitialMb <= 0)
        {
            StatusMessage = "Page file size must be greater than 0.";
            return;
        }

        IsLoading = true;
        StatusMessage = $"Setting static page file to {PagefileInitialMb:N0} MB...";
        try
        {
            // Static = initial == maximum so the size never fluctuates.
            var result = await _memoryService.ConfigurePagefileAsync(PagefileInitialMb, PagefileInitialMb);
            StatusMessage = result.Message;
            if (result.Success)
                CurrentPagefileText = $"Set to: {PagefileInitialMb:N0} MB static (restart required)";
            else
                _log.Error("MemoryViewModel", $"Page file configuration failed: {result.Message}");
        }
        catch (Exception ex)
        {
            _log.Error("MemoryViewModel", "Page file configuration threw an unexpected exception", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task RevertPagefileAsync()
    {
        int rec = _memoryService.GetRecommendedPagefileMb();
        IsLoading = true;
        StatusMessage = $"Resetting page file to recommended size ({rec:N0} MB)...";
        try
        {
            // "Reset to default" sets back to the RAM-based recommended static size.
            // This avoids WMI complexity and is always reliable.
            var result = await _memoryService.ConfigurePagefileAsync(rec, rec);
            StatusMessage = result.Message;
            if (result.Success)
            {
                PagefileInitialMb   = rec;
                CurrentPagefileText = $"Set to: {rec:N0} MB static (restart required)";
            }
            else
                _log.Error("MemoryViewModel", $"Page file reset failed: {result.Message}");
        }
        catch (Exception ex)
        {
            _log.Error("MemoryViewModel", "Page file reset threw an unexpected exception", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ToggleStartupItemAsync(StartupItem item)
    {
        try
        {
            var result = await Task.Run(() => _startupService.SetStartupItemEnabled(item, !item.IsEnabled));
            StatusMessage = result.Message;
            if (result.Success) item.IsEnabled = !item.IsEnabled;
            // Always re-insert so the toggle switch re-binds to the TRUE IsEnabled — commits the
            // change on success, and reverts the switch visually if it failed (e.g. needs admin).
            var idx = StartupItems.IndexOf(item);
            if (idx >= 0) { StartupItems.RemoveAt(idx); StartupItems.Insert(idx, item); }
            RaiseStartupRecommendation();
        }
        catch (Exception ex)
        {
            _log.Error("MemoryViewModel", "Toggle startup item failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    /// <summary>One-tap "speed up startup": disables every currently-enabled High-impact item.</summary>
    [RelayCommand]
    private async Task DisableHeavyStartupAsync()
    {
        // Snapshot first — ToggleStartupItemAsync mutates the collection as it goes.
        foreach (var item in StartupItems.Where(i => i.IsEnabled && i.ImpactLabel == "High").ToList())
            await ToggleStartupItemAsync(item);
        RaiseStartupRecommendation();
    }

    [RelayCommand]
    private async Task FreeRamAsync()
    {
        if (IsFreeing) return;
        IsFreeing     = true;
        FreeRamStatus = "Flushing working sets and clearing standby RAM...";
        try
        {
            var (freed, msg) = await Task.Run(() => _memoryService.FreeRam());
            FreeRamStatus = msg;

            // Refresh the available RAM display immediately after
            var (total, avail) = await Task.Run(() => _memoryService.GetRamStats());
            TotalRamMb     = total;
            AvailableRamMb = avail;
            RaiseRamStats();

            // Clear status after 8 seconds so stale messages don't linger
            _ = Task.Delay(8000).ContinueWith(_ =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (FreeRamStatus == msg) FreeRamStatus = string.Empty;
                }));
        }
        catch (Exception ex)
        {
            _log.Error("MemoryViewModel", "FreeRam failed", ex);
            FreeRamStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsFreeing = false;
        }
    }
}
