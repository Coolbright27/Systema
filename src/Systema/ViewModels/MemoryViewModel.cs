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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
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
    // ── Page file: a dropdown of fixed sizes plus "Windows decides" ──
    /// <summary>The choices shown, largest first; Windows decides is last.</summary>
    public ObservableCollection<PagefileOption> PagefileOptions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyPagefile))]
    [NotifyCanExecuteChangedFor(nameof(ConfigurePagefileCommand))]
    private PagefileOption? _selectedPagefileOption;

    // What is configured right now: 0 = Windows decides, above 0 = fixed size in MB, -1 = not read yet.
    private int _appliedPagefileMb = -1;

    /// <summary>Apply only lights up when the choice differs from what's already set.</summary>
    public bool CanApplyPagefile => SelectedPagefileOption != null && SelectedPagefileOption.Mb != _appliedPagefileMb;

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
    public string UsedRamGb  => (UsedRamMb / 1024.0).ToString("0.0");
    public string FreeRamGb  => (AvailableRamMb / 1024.0).ToString("0.0");
    public string TotalRamGb => (TotalRamMb / 1024.0).ToString("0");

    // ── Memory breakdown (In use / Cached / Free) for the hero bar ──
    private long _inUseMb, _cachedMb, _freeSegMb;
    private long _compressedMb = -1;
    /// <summary>Star-proportioned column widths so the three-segment bar fills its track.</summary>
    public GridLength InUseStar  => new(System.Math.Max(1, _inUseMb),  GridUnitType.Star);
    public GridLength CachedStar => new(System.Math.Max(0, _cachedMb), GridUnitType.Star);
    public GridLength FreeStar   => new(System.Math.Max(1, _freeSegMb), GridUnitType.Star);
    public string InUseGb  => (_inUseMb  / 1024.0).ToString("0.0");
    public string CachedGb => (_cachedMb / 1024.0).ToString("0.0");
    public string FreeSegGb => (_freeSegMb / 1024.0).ToString("0.0");
    public bool   HasCached     => _cachedMb > 0;
    public bool   HasCompressed => _compressedMb >= 0;
    public string CompressedGb  => (_compressedMb / 1024.0).ToString("0.0");

    // ── Live memory-usage trend line (sampled each refresh tick while the tab is open) ──
    private const int    SparkSamples = 48;
    private const double SparkW = 168, SparkH = 46;
    private readonly Queue<double> _usageHistory = new();
    [ObservableProperty] private PointCollection _sparklinePoints = new();
    [ObservableProperty] private PointCollection _sparklineArea   = new();
    /// <summary>True once we have enough samples to draw a line (hides the sparkline on first paint).</summary>
    public bool HasSparkline => _usageHistory.Count >= 2;

    private int _tick;

    private void RaiseBreakdown()
    {
        OnPropertyChanged(nameof(InUseStar));   OnPropertyChanged(nameof(CachedStar));  OnPropertyChanged(nameof(FreeStar));
        OnPropertyChanged(nameof(InUseGb));     OnPropertyChanged(nameof(CachedGb));    OnPropertyChanged(nameof(FreeSegGb));
        OnPropertyChanged(nameof(HasCached));
    }

    /// <summary>Pulls the In use / Cached / Free split and derives Total/Available from it, then
    /// updates the trend line. Single source of the tab's live numbers, used by both the timer
    /// refresh and the manual "Free up memory" action.</summary>
    private async Task RefreshBreakdownAsync()
    {
        var (inUse, cached, freeSeg) = await Task.Run(() => _memoryService.GetMemoryBreakdown());
        _inUseMb = inUse; _cachedMb = cached; _freeSegMb = freeSeg;
        TotalRamMb     = inUse + cached + freeSeg;
        AvailableRamMb = cached + freeSeg;
        RaiseRamStats();
        RaiseBreakdown();
        SampleSparkline();
    }

    private void SampleSparkline()
    {
        // Plot memory IN USE over time (Total − Available). Blue line to match the "In use"
        // segment of the bar; a usage trend reads more naturally than a free-memory trend.
        _usageHistory.Enqueue(UsedRamMb / 1024.0);
        while (_usageHistory.Count > SparkSamples) _usageHistory.Dequeue();
        OnPropertyChanged(nameof(HasSparkline));
        if (_usageHistory.Count < 2) return;

        double[] vals = _usageHistory.ToArray();
        double min = vals.Min(), max = vals.Max(), range = max - min;
        if (range < 0.15) { double mid = (min + max) / 2; min = mid - 0.5; max = mid + 0.5; range = max - min; }
        double pad = range * 0.15; min -= pad; range += pad * 2;

        var line = new PointCollection();
        for (int i = 0; i < vals.Length; i++)
        {
            double x = (double)i / (vals.Length - 1) * SparkW;
            double y = SparkH - (vals[i] - min) / range * SparkH;
            line.Add(new System.Windows.Point(x, y));
        }
        var area = new PointCollection(line);
        area.Add(new System.Windows.Point(SparkW, SparkH));
        area.Add(new System.Windows.Point(0, SparkH));
        line.Freeze(); area.Freeze();
        SparklinePoints = line;
        SparklineArea   = area;
    }

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
        OnPropertyChanged(nameof(TotalRamGb));
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

        // Fill the page file dropdown straight away (recommended size selected) so it's never
        // empty on open; the first refresh then selects what's actually configured.
        RebuildPagefileOptions(applied: -1, recommended: _memoryService.GetRecommendedPagefileMb());

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
            // Pull the In use / Cached / Free split (derives Total/Available) and update the trend line.
            await RefreshBreakdownAsync();

            // Compressed store is heavier to read (process enumeration) — sample it on a full
            // refresh and roughly every 8 s otherwise, not on every 1 s tick.
            if (fullRefresh || (++_tick % 8 == 0))
            {
                _compressedMb = await Task.Run(() => _memoryService.GetCompressedMemoryMb());
                OnPropertyChanged(nameof(HasCompressed));
                OnPropertyChanged(nameof(CompressedGb));
            }

            // Update recommended text based on detected RAM
            int rec = _memoryService.GetRecommendedPagefileMb();
            RecommendedPagefileText = $"Recommended for {TotalRamMb / 1024} GB RAM: {PagefileGb(rec)}";

            if (fullRefresh)
            {
                // Registry read for configured sizes (fast) + WMI for current running size
                var (init, max, isSystemManaged) = await RunOnLargeStackAsync(() => _memoryService.GetPagefileSettings());
                var (allocMb, usedMb)            = await RunOnLargeStackAsync(() => _memoryService.GetCurrentPagefileUsageMb());

                if (!isSystemManaged && init > 0)
                {
                    // A fixed size is configured: the dropdown opens on it.
                    RebuildPagefileOptions(applied: init, recommended: rec);
                    string usageNote = allocMb > 0 ? $"  ·  {usedMb:N0} MB in use now" : string.Empty;
                    CurrentPagefileText = $"Set to {PagefileGb(init)} fixed{usageNote}";
                }
                else
                {
                    // Windows manages it: the dropdown opens on "Windows decides".
                    RebuildPagefileOptions(applied: 0, recommended: rec);
                    string runningNote = allocMb > 0 ? $"currently {allocMb:N0} MB" : "size varies";
                    CurrentPagefileText = $"Windows decides  ·  {runningNote}";
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

    /// <summary>
    /// Applies the dropdown's choice: a fixed size (initial = maximum, so it never resizes on
    /// the fly) or "Windows decides", which restores Windows' own system-managed default.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyPagefile))]
    private async Task ConfigurePagefileAsync()
    {
        var choice = SelectedPagefileOption;
        if (choice == null) return;

        IsLoading = true;
        try
        {
            TweakResult result;
            if (choice.Mb == 0)
            {
                StatusMessage = "Letting Windows decide the page file size...";
                result = await _memoryService.RevertToManagedPagefileAsync();
            }
            else
            {
                StatusMessage = $"Setting a fixed {PagefileGb(choice.Mb)} page file...";
                result = await _memoryService.ConfigurePagefileAsync(choice.Mb, choice.Mb);
            }

            StatusMessage = result.Message;
            if (result.Success)
            {
                _appliedPagefileMb = choice.Mb;
                CurrentPagefileText = choice.Mb == 0
                    ? "Windows decides (restart required)"
                    : $"Set to {PagefileGb(choice.Mb)} fixed (restart required)";
                OnPropertyChanged(nameof(CanApplyPagefile));
                ConfigurePagefileCommand.NotifyCanExecuteChanged();
            }
            else
                _log.Error("MemoryViewModel", $"Page file change failed: {result.Message}");
        }
        catch (Exception ex)
        {
            _log.Error("MemoryViewModel", "Page file change threw an unexpected exception", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// Rebuilds the dropdown and selects what's configured (<paramref name="applied"/>: 0 = Windows
    /// decides, -1 = not read yet, which selects the recommended size). A fixed size that isn't
    /// one of the offered ones (set by an older version, or by hand) is listed as "(current)" so
    /// the dropdown always shows the truth.
    /// </summary>
    private void RebuildPagefileOptions(int applied, int recommended)
    {
        _appliedPagefileMb = applied;

        var sizes = MemoryService.PagefileSizeOptionsMb.ToList();
        if (applied > 0 && !sizes.Contains(applied)) sizes.Add(applied);
        sizes.Sort((a, b) => b.CompareTo(a));

        PagefileOptions.Clear();
        foreach (int mb in sizes)
        {
            string label = PagefileGb(mb);
            if (mb == recommended)                                   label += " (recommended)";
            else if (!MemoryService.PagefileSizeOptionsMb.Contains(mb)) label += " (current)";
            PagefileOptions.Add(new PagefileOption(mb, label));
        }
        PagefileOptions.Add(new PagefileOption(0, "Windows decides"));

        int select = applied >= 0 ? applied : recommended;
        SelectedPagefileOption = PagefileOptions.FirstOrDefault(o => o.Mb == select)
                                 ?? PagefileOptions.FirstOrDefault(o => o.Mb == recommended);
        OnPropertyChanged(nameof(CanApplyPagefile));
        ConfigurePagefileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"16 GB", or "4.9 GB" for a size that isn't a whole number of gigabytes.</summary>
    internal static string PagefileGb(int mb) =>
        mb % 1024 == 0 ? $"{mb / 1024} GB" : $"{mb / 1024.0:0.#} GB";

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

            // Refresh the breakdown + trend line immediately after
            await RefreshBreakdownAsync();

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

/// <summary>One choice in the page file dropdown. <see cref="Mb"/> 0 means "Windows decides".</summary>
public sealed record PagefileOption(int Mb, string Label);
