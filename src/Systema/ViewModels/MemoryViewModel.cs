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

    public long UsedRamMb => TotalRamMb - AvailableRamMb;
    public double RamUsagePercent => TotalRamMb > 0 ? (double)UsedRamMb / TotalRamMb * 100 : 0;
    public string UsedRamGb  => (UsedRamMb / 1024.0).ToString("0.0");
    public string FreeRamGb  => (AvailableRamMb / 1024.0).ToString("0.0");
    public string TotalRamGb => (TotalRamMb / 1024.0).ToString("0");

    // ── Task Manager-style memory card ───────────────────────────────────────
    // Live figures from MemoryService.GetMemoryDetails (the same sources Task Manager reads),
    // refreshed each tick while the page is open.
    private MemoryDetails _details = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    private long _compressedMb = -1;

    /// <summary>"1.5 GB"-style text, one decimal, the way Task Manager writes memory sizes.</summary>
    internal static string Gb(long mb) => $"{mb / 1024.0:0.0} GB";

    // Composition bar: star widths so the four segments fill the track in proportion.
    public GridLength InUseStar    => new(Math.Max(1, _details.InUseMb),    GridUnitType.Star);
    public GridLength ModifiedStar => new(Math.Max(0, _details.ModifiedMb), GridUnitType.Star);
    public GridLength StandbyStar  => new(Math.Max(0, _details.StandbyMb),  GridUnitType.Star);
    public GridLength FreeStar     => new(Math.Max(0, _details.FreeMb),     GridUnitType.Star);

    // Composition bar tooltips (Task Manager explains the segments on hover, not with a legend).
    public string InUseTip    => $"In use: {Gb(_details.InUseMb)}\nMemory used by processes, drivers and the system.";
    public string ModifiedTip => $"Modified: {Gb(_details.ModifiedMb)}\nMemory whose contents must be written to disk before it can be reused.";
    public string StandbyTip  => $"Standby: {Gb(_details.StandbyMb)}\nCached data not in use right now. Given to apps the moment they need it.";
    public string FreeTip     => $"Free: {Gb(_details.FreeMb)}\nMemory not in use at all.";

    // The stats block under the graph.
    public string InUseText     => Gb(_details.InUseMb);
    public bool   HasCompressed => _compressedMb >= 0;
    public string CompressedText => _compressedMb >= 0 ? $"({Gb(_compressedMb)})" : "";
    public string AvailableText => Gb(_details.AvailableMb);
    public string CommittedText => _details.CommitLimitMb > 0
        ? $"{_details.CommittedMb / 1024.0:0.0}/{_details.CommitLimitMb / 1024.0:0.0} GB" : "—";
    public string CachedText       => Gb(_details.CachedMb);
    public string PagedPoolText    => _details.PagedPoolMb > 0    ? Gb(_details.PagedPoolMb)    : "—";
    public string NonPagedPoolText => _details.NonPagedPoolMb > 0 ? Gb(_details.NonPagedPoolMb) : "—";
    /// <summary>The graph's top label: usable memory (what 100% on the graph means).</summary>
    public string UsableText => Gb(TotalRamMb);

    // Installed hardware (read once, in the background; blank lines are hidden).
    [ObservableProperty] private string _installedText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMemorySpeed))]      private string _memorySpeed = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSlotsUsed))]        private string _slotsUsed = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFormFactor))]       private string _formFactor = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasHardwareReserved))] private string _hardwareReserved = "";
    public bool HasMemorySpeed      => !string.IsNullOrEmpty(MemorySpeed);
    public bool HasSlotsUsed        => !string.IsNullOrEmpty(SlotsUsed);
    public bool HasFormFactor       => !string.IsNullOrEmpty(FormFactor);
    public bool HasHardwareReserved => !string.IsNullOrEmpty(HardwareReserved);
    private bool _hardwareRequested;

    // ── Usage graph: the last minute, one sample per refresh tick ──
    private const int GraphSamples = 60;
    private readonly Queue<double> _usageHistory = new();
    /// <summary>Memory in use as a fraction of total (0..1), oldest first, for the UsageGraph.</summary>
    [ObservableProperty] private double[] _usageHistoryValues = Array.Empty<double>();

    private int _tick;

    private void RaiseBreakdown()
    {
        foreach (var name in new[]
        {
            nameof(InUseStar), nameof(ModifiedStar), nameof(StandbyStar), nameof(FreeStar),
            nameof(InUseTip), nameof(ModifiedTip), nameof(StandbyTip), nameof(FreeTip),
            nameof(InUseText), nameof(AvailableText), nameof(CommittedText), nameof(CachedText),
            nameof(PagedPoolText), nameof(NonPagedPoolText), nameof(UsableText),
        })
            OnPropertyChanged(name);
    }

    /// <summary>Pulls the live figures, derives Total/Available from them, and samples the graph.
    /// Single source of the page's live numbers.</summary>
    private async Task RefreshBreakdownAsync()
    {
        _details = await Task.Run(() => _memoryService.GetMemoryDetails());
        TotalRamMb     = _details.TotalMb;
        AvailableRamMb = _details.StandbyMb + _details.FreeMb;
        RaiseRamStats();
        RaiseBreakdown();
        SampleGraph();

        if (!_hardwareRequested)
        {
            _hardwareRequested = true;
            var hw = await Task.Run(() => _memoryService.GetMemoryHardware());
            InstalledText    = Gb(hw.InstalledMb);
            MemorySpeed      = hw.Speed;
            SlotsUsed        = hw.SlotsUsed;
            FormFactor       = hw.FormFactor;
            HardwareReserved = hw.HardwareReserved;
        }
    }

    private void SampleGraph()
    {
        if (TotalRamMb <= 0) return;
        _usageHistory.Enqueue((double)UsedRamMb / TotalRamMb);
        while (_usageHistory.Count > GraphSamples) _usageHistory.Dequeue();
        UsageHistoryValues = _usageHistory.ToArray();
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
                OnPropertyChanged(nameof(CompressedText));
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
                    // A fixed size is configured: the dropdown opens on it (or the nearest choice).
                    RebuildPagefileOptions(applied: init, recommended: rec);
                    // Windows only resizes the page file at boot, so a live size that differs from
                    // the configured one means a restart is still pending.
                    string note = allocMb > 0 && allocMb != init ? " (restart required)"
                                : allocMb > 0                    ? $"  ·  {usedMb:N0} MB in use now"
                                : string.Empty;
                    CurrentPagefileText = $"Set to {PagefileGb(init)} fixed{note}";
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
    /// one of the offered ones (set by hand, another tool, or an older version) selects the nearest
    /// offered size, so 30.5 GB shows 32 GB; the line under the dropdown still gives the exact size.
    /// </summary>
    private void RebuildPagefileOptions(int applied, int recommended)
    {
        int shown = applied > 0 ? MemoryService.NearestPagefileOptionMb(applied) : applied;
        _appliedPagefileMb = shown;

        PagefileOptions.Clear();
        foreach (int mb in MemoryService.PagefileSizeOptionsMb)
            PagefileOptions.Add(new PagefileOption(mb, mb == recommended ? $"{PagefileGb(mb)} (recommended)" : PagefileGb(mb)));
        PagefileOptions.Add(new PagefileOption(0, "Windows decides"));

        int select = shown >= 0 ? shown : recommended;
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
}

/// <summary>One choice in the page file dropdown. <see cref="Mb"/> 0 means "Windows decides".</summary>
public sealed record PagefileOption(int Mb, string Label);
