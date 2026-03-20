// ════════════════════════════════════════════════════════════════════════════
// VisualViewModel.cs  ·  Windows visual effects toggles and power plan selection
// ════════════════════════════════════════════════════════════════════════════
//
// Exposes individual boolean properties for each Windows visual effect (via
// AnimationService) and a power plan picker (via PowerPlanService). Changes are
// applied immediately and broadcast via WM_SETTINGCHANGE. Implements
// IAutoRefreshable to keep displayed state in sync with the OS.
//
// RELATED FILES
//   AnimationService.cs       — granular registry toggles + WM_SETTINGCHANGE broadcast
//   PowerPlanService.cs       — power plan switching and max CPU % cap
//   Views/VisualView.xaml     — toggle switches and power plan dropdown
// ════════════════════════════════════════════════════════════════════════════

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Systema.Core;
using Systema.Services;
using static Systema.Core.ThreadHelper;

namespace Systema.ViewModels;

public partial class VisualViewModel : ObservableObject, IAutoRefreshable, IDisposable
{
    private readonly AnimationService _animationService;
    private readonly PowerPlanService _powerPlanService;
    private readonly SettingsService  _settings;
    private static readonly LoggerService _log = LoggerService.Instance;

    // Suppress OnPropertyChanged callbacks during bulk load (preset / refresh)
    private bool _loading;

    // ── Granular animation toggles ─────────────────────────────────────────────
    [ObservableProperty] private bool _animateControlsEnabled;
    [ObservableProperty] private bool _animateWindowsEnabled;
    [ObservableProperty] private bool _fadeMenusEnabled;
    [ObservableProperty] private bool _showDragContentsEnabled;
    [ObservableProperty] private bool _smoothFontsEnabled;
    [ObservableProperty] private bool _tooltipAnimationEnabled;
    [ObservableProperty] private bool _fadeOutMenuItemsEnabled;
    [ObservableProperty] private bool _cursorShadowEnabled;
    [ObservableProperty] private bool _windowShadowEnabled;
    [ObservableProperty] private bool _comboBoxAnimationEnabled;
    [ObservableProperty] private bool _listboxSmoothScrollingEnabled;
    [ObservableProperty] private bool _listviewWatermarkEnabled;
    [ObservableProperty] private bool _iconLabelShadowEnabled;
    [ObservableProperty] private bool _aeroPeekEnabled;
    [ObservableProperty] private bool _taskbarThumbnailPreviewsEnabled;

    /// <summary>Coarse flag: all animations are off (used by legacy code and DashboardVM).</summary>
    public bool AnimationsDisabled => !AnimateWindowsEnabled && !AnimateControlsEnabled && !FadeMenusEnabled;

    // ── Preset tracking ("none" | "speed" | "default" | "") ───────────────────
    [ObservableProperty] private string _activePreset = string.Empty;

    // ── Expander state ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _showIndividualControls;
    [RelayCommand] private void ToggleIndividualControls() => ShowIndividualControls = !ShowIndividualControls;

    [ObservableProperty] private string _activePowerPlan    = "Unknown";
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _statusMessage      = string.Empty;
    [ObservableProperty] private bool   _hasBattery;
    [ObservableProperty] private bool   _isOnBattery;
    /// <summary>True when a battery-aware plan (Balanced on Battery / Max Battery Life) is active this session.</summary>
    [ObservableProperty] private bool   _isBatteryPlanActive;
    /// <summary>Which battery optimization is active: "balanced" | "max" | ""</summary>
    private string _activeBatteryOpt = string.Empty;

    /// <summary>True when a high/ultimate performance plan is currently active.</summary>
    public bool IsHighPerformancePlanActive =>
        ActivePowerPlan.Contains("High",        StringComparison.OrdinalIgnoreCase) ||
        ActivePowerPlan.Contains("Ultimate",    StringComparison.OrdinalIgnoreCase) ||
        ActivePowerPlan.Contains("Performance", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when battery optimization is active but the system is currently on AC power (cap is dormant).</summary>
    public bool IsOnAcWithBatteryPlanActive => IsBatteryPlanActive && !IsOnBattery;

    partial void OnActivePowerPlanChanged(string value) =>
        OnPropertyChanged(nameof(IsHighPerformancePlanActive));

    partial void OnIsOnBatteryChanged(bool value) =>
        OnPropertyChanged(nameof(IsOnAcWithBatteryPlanActive));

    partial void OnIsBatteryPlanActiveChanged(bool value) =>
        OnPropertyChanged(nameof(IsOnAcWithBatteryPlanActive));

    // ── Pending state (Apply / Cancel pattern) ─────────────────────────────────
    private bool _pendingAnimateControls;
    private bool _pendingAnimateWindows;
    private bool _pendingFadeMenus;
    private bool _pendingShowDragContents;
    private bool _pendingSmoothFonts;
    private bool _pendingTooltipAnimation;
    private bool _pendingFadeOutMenuItems;
    private bool _pendingCursorShadow;
    private bool _pendingWindowShadow;
    private bool _pendingComboBoxAnimation;
    private bool _pendingListboxSmoothScrolling;
    private bool _pendingListviewWatermark;
    private bool _pendingIconLabelShadow;
    private bool _pendingAeroPeek;
    private bool _pendingTaskbarThumbnailPreviews;
    [ObservableProperty] private bool _hasPendingChanges;

    public VisualViewModel(AnimationService animationService, PowerPlanService powerPlanService,
                           SettingsService settings)
    {
        _animationService = animationService;
        _powerPlanService = powerPlanService;
        _settings         = settings;
        _hasBattery       = _powerPlanService.HasBattery();
        _isOnBattery      = _powerPlanService.IsOnBattery();

        // Restore persisted battery opt state so badge / stop-button show on restart.
        string savedOpt = _settings.BatteryOptimizationMode;
        if (!string.IsNullOrEmpty(savedOpt))
        {
            _activeBatteryOpt = savedOpt;
            _isBatteryPlanActive = true;

            // If we're already on battery right now, re-apply the cap immediately so a
            // Windows Update or plan change can't silently undo it between sessions.
            if (_isOnBattery)
            {
                string optSnapshot = savedOpt;
                Task.Run(async () =>
                {
                    TweakResult result = optSnapshot == "max"
                        ? await _powerPlanService.SetMaxBatteryLifeAsync()
                        : await _powerPlanService.SetBalancedOnBatteryAsync();
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        StatusMessage = result.Success ? "Battery optimization re-applied." : result.Message);
                });
            }
        }

        // Auto-restore battery optimization when the user plugs in, re-apply when they unplug.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        bool nowOnBattery = _powerPlanService.IsOnBattery();

        // PowerModeChanged fires on a system thread — marshal all UI-property writes to the UI thread.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsOnBattery = nowOnBattery;

            // Use persisted setting — not just the in-memory flag — so optimization
            // survives app restarts and correctly re-applies after any power plan reset.
            string activeOpt = _activeBatteryOpt;
            if (string.IsNullOrEmpty(activeOpt)) return;

            if (!nowOnBattery)
            {
                _log.Info("VisualViewModel", "AC power detected — battery optimization cap inactive while plugged in");
                StatusMessage = "Plugged in — battery optimization is inactive while on AC power.";
            }
            else
            {
                // Unplugged — re-apply to ensure the cap is active (guards against Windows resetting it)
                _log.Info("VisualViewModel", "Battery power detected — re-applying battery optimization");
                StatusMessage = "On battery — re-applying battery optimization…";
                string optSnapshot = activeOpt;
                Task.Run(async () =>
                {
                    TweakResult result = optSnapshot == "max"
                        ? await _powerPlanService.SetMaxBatteryLifeAsync()
                        : await _powerPlanService.SetBalancedOnBatteryAsync();
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => StatusMessage = result.Message);
                });
            }
        });
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    // ── IAutoRefreshable ──────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        try
        {
            if (!HasPendingChanges)
                LoadFromService();
            // GetActivePlan() spawns powercfg.exe — use a large-stack thread (8 MB) so
            // AV/EDR CreateProcess hooks cannot overflow the ~1 MB threadpool stack.
            ActivePowerPlan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            OnPropertyChanged(nameof(AnimationsDisabled));
        }
        catch (Exception ex)
        {
            _log.Error("VisualViewModel", "Refresh failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private void LoadFromService()
    {
        _loading = true;
        try
        {
            AnimateControlsEnabled           = _animationService.AnimateControlsEnabled;
            AnimateWindowsEnabled            = _animationService.AnimateWindowsEnabled;
            FadeMenusEnabled                 = _animationService.FadeMenusEnabled;
            ShowDragContentsEnabled          = _animationService.ShowWindowContentsWhileDraggingEnabled;
            SmoothFontsEnabled               = _animationService.SmoothFontsEnabled;
            TooltipAnimationEnabled          = _animationService.TooltipAnimationEnabled;
            FadeOutMenuItemsEnabled          = _animationService.FadeOutMenuItemsEnabled;
            CursorShadowEnabled              = _animationService.CursorShadowEnabled;
            WindowShadowEnabled              = _animationService.WindowShadowEnabled;
            ComboBoxAnimationEnabled         = _animationService.ComboBoxAnimationEnabled;
            ListboxSmoothScrollingEnabled    = _animationService.ListboxSmoothScrollingEnabled;
            ListviewWatermarkEnabled         = _animationService.ListviewWatermarkEnabled;
            IconLabelShadowEnabled           = _animationService.IconLabelShadowEnabled;
            AeroPeekEnabled                  = _animationService.AeroPeekEnabled;
            TaskbarThumbnailPreviewsEnabled  = _animationService.TaskbarThumbnailPreviewsEnabled;
            _pendingAnimateControls          = AnimateControlsEnabled;
            _pendingAnimateWindows           = AnimateWindowsEnabled;
            _pendingFadeMenus               = FadeMenusEnabled;
            _pendingShowDragContents        = ShowDragContentsEnabled;
            _pendingSmoothFonts             = SmoothFontsEnabled;
            _pendingTooltipAnimation        = TooltipAnimationEnabled;
            _pendingFadeOutMenuItems        = FadeOutMenuItemsEnabled;
            _pendingCursorShadow            = CursorShadowEnabled;
            _pendingWindowShadow            = WindowShadowEnabled;
            _pendingComboBoxAnimation       = ComboBoxAnimationEnabled;
            _pendingListboxSmoothScrolling  = ListboxSmoothScrollingEnabled;
            _pendingListviewWatermark       = ListviewWatermarkEnabled;
            _pendingIconLabelShadow         = IconLabelShadowEnabled;
            _pendingAeroPeek               = AeroPeekEnabled;
            _pendingTaskbarThumbnailPreviews = TaskbarThumbnailPreviewsEnabled;
            HasPendingChanges = false;
            RefreshActivePreset();
        }
        finally { _loading = false; }
    }

    [RelayCommand]
    private void Refresh() { _ = RefreshAsync(); StatusMessage = "Refreshed."; }

    // ── Individual toggle partial callbacks ────────────────────────────────────

    partial void OnAnimateControlsEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingAnimateControls = value;
        HasPendingChanges = true;
        OnPropertyChanged(nameof(AnimationsDisabled));
        ActivePreset = string.Empty;
    }

    partial void OnAnimateWindowsEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingAnimateWindows = value;
        HasPendingChanges = true;
        OnPropertyChanged(nameof(AnimationsDisabled));
        ActivePreset = string.Empty;
    }

    partial void OnFadeMenusEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingFadeMenus = value;
        HasPendingChanges = true;
        OnPropertyChanged(nameof(AnimationsDisabled));
        ActivePreset = string.Empty;
    }

    partial void OnShowDragContentsEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingShowDragContents = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    partial void OnSmoothFontsEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingSmoothFonts = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    partial void OnTooltipAnimationEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingTooltipAnimation = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    partial void OnFadeOutMenuItemsEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingFadeOutMenuItems = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    partial void OnCursorShadowEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingCursorShadow = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    partial void OnWindowShadowEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingWindowShadow = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    partial void OnComboBoxAnimationEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingComboBoxAnimation = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    partial void OnListboxSmoothScrollingEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingListboxSmoothScrolling = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    partial void OnListviewWatermarkEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingListviewWatermark = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    partial void OnIconLabelShadowEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingIconLabelShadow = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    partial void OnAeroPeekEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingAeroPeek = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    partial void OnTaskbarThumbnailPreviewsEnabledChanged(bool value)
    {
        if (_loading) return;
        _pendingTaskbarThumbnailPreviews = value;
        HasPendingChanges = true;
        ActivePreset = string.Empty;
    }

    // ── Apply / Cancel ────────────────────────────────────────────────────────

    [RelayCommand]
    private void ApplyChanges()
    {
        IsLoading = true;
        try
        {
            _animationService.AnimateControlsEnabled                 = _pendingAnimateControls;
            _animationService.AnimateWindowsEnabled                  = _pendingAnimateWindows;
            _animationService.FadeMenusEnabled                       = _pendingFadeMenus;
            _animationService.ShowWindowContentsWhileDraggingEnabled = _pendingShowDragContents;
            _animationService.SmoothFontsEnabled                     = _pendingSmoothFonts;
            _animationService.TooltipAnimationEnabled                = _pendingTooltipAnimation;
            _animationService.FadeOutMenuItemsEnabled                = _pendingFadeOutMenuItems;
            _animationService.CursorShadowEnabled                    = _pendingCursorShadow;
            _animationService.WindowShadowEnabled                    = _pendingWindowShadow;
            _animationService.ComboBoxAnimationEnabled               = _pendingComboBoxAnimation;
            _animationService.ListboxSmoothScrollingEnabled          = _pendingListboxSmoothScrolling;
            _animationService.ListviewWatermarkEnabled               = _pendingListviewWatermark;
            _animationService.IconLabelShadowEnabled                 = _pendingIconLabelShadow;
            _animationService.AeroPeekEnabled                        = _pendingAeroPeek;
            _animationService.TaskbarThumbnailPreviewsEnabled        = _pendingTaskbarThumbnailPreviews;
            HasPendingChanges = false;
            RefreshActivePreset();
            OnPropertyChanged(nameof(AnimationsDisabled));
            StatusMessage = "Changes applied.";
            _log.Info("VisualViewModel", "Granular animation changes applied");
        }
        catch (Exception ex)
        {
            _log.Error("VisualViewModel", "Apply changes failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void CancelChanges() { LoadFromService(); StatusMessage = "Changes cancelled."; }

    // ── Presets — run service call on background thread to avoid UI freeze ─────

    [RelayCommand]
    private async Task ApplyNoAnimationsAsync()
    {
        IsLoading = true;
        try
        {
            // AnimationService calls SystemParametersInfo + SendMessageTimeout — use large-stack thread.
            await RunOnLargeStackAsync(() => _animationService.ApplyNoAnimations());
            _loading = true;
            try
            {
                AnimateControlsEnabled          = false;
                AnimateWindowsEnabled           = false;
                FadeMenusEnabled                = false;
                ShowDragContentsEnabled         = false;
                SmoothFontsEnabled              = false;
                TooltipAnimationEnabled         = false;
                FadeOutMenuItemsEnabled         = false;
                CursorShadowEnabled             = false;
                WindowShadowEnabled             = false;
                ComboBoxAnimationEnabled        = false;
                ListboxSmoothScrollingEnabled   = false;
                ListviewWatermarkEnabled        = false;
                IconLabelShadowEnabled          = false;
                AeroPeekEnabled                 = false;
                TaskbarThumbnailPreviewsEnabled = false;
            }
            finally { _loading = false; }
            HasPendingChanges = false;
            ActivePreset = "none";
            OnPropertyChanged(nameof(AnimationsDisabled));
            StatusMessage = "All animations disabled.";
        }
        catch (Exception ex) { _log.Error("VisualViewModel", "NoAnimations preset failed", ex); StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ApplyOptimizeForSpeedAsync()
    {
        IsLoading = true;
        try
        {
            await RunOnLargeStackAsync(() => _animationService.ApplyOptimizeForSpeed());
            _loading = true;
            try
            {
                AnimateControlsEnabled          = true;
                AnimateWindowsEnabled           = false;
                FadeMenusEnabled                = false;
                ShowDragContentsEnabled         = true;
                SmoothFontsEnabled              = true;
                TooltipAnimationEnabled         = false;
                FadeOutMenuItemsEnabled         = false;
                CursorShadowEnabled             = false;
                WindowShadowEnabled             = false;
                ComboBoxAnimationEnabled        = false;
                ListboxSmoothScrollingEnabled   = false;
                ListviewWatermarkEnabled        = false;
                IconLabelShadowEnabled          = true;
                AeroPeekEnabled                 = true;
                TaskbarThumbnailPreviewsEnabled = true;
            }
            finally { _loading = false; }
            HasPendingChanges = false;
            ActivePreset = "speed";
            OnPropertyChanged(nameof(AnimationsDisabled));
            StatusMessage = "Optimized for speed. Animate controls, font smoothing, and drag content preserved.";
        }
        catch (Exception ex) { _log.Error("VisualViewModel", "OptimizeForSpeed preset failed", ex); StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ApplyWindowsDefaultAsync()
    {
        IsLoading = true;
        try
        {
            await RunOnLargeStackAsync(() => _animationService.ApplyWindowsDefault());
            _loading = true;
            try
            {
                AnimateControlsEnabled          = true;
                AnimateWindowsEnabled           = true;
                FadeMenusEnabled                = true;
                ShowDragContentsEnabled         = true;
                SmoothFontsEnabled              = true;
                TooltipAnimationEnabled         = true;
                FadeOutMenuItemsEnabled         = true;
                CursorShadowEnabled             = true;
                WindowShadowEnabled             = true;
                ComboBoxAnimationEnabled        = true;
                ListboxSmoothScrollingEnabled   = true;
                ListviewWatermarkEnabled        = true;
                IconLabelShadowEnabled          = true;
                AeroPeekEnabled                 = true;
                TaskbarThumbnailPreviewsEnabled = true;
            }
            finally { _loading = false; }
            HasPendingChanges = false;
            ActivePreset = "default";
            OnPropertyChanged(nameof(AnimationsDisabled));
            StatusMessage = "All animations restored to Windows defaults.";
        }
        catch (Exception ex) { _log.Error("VisualViewModel", "WindowsDefault preset failed", ex); StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    private void RefreshActivePreset()
    {
        bool noAnimations = !AnimateControlsEnabled && !AnimateWindowsEnabled && !FadeMenusEnabled
                            && !ShowDragContentsEnabled && !SmoothFontsEnabled
                            && !TooltipAnimationEnabled && !FadeOutMenuItemsEnabled
                            && !CursorShadowEnabled && !WindowShadowEnabled
                            && !ComboBoxAnimationEnabled && !ListboxSmoothScrollingEnabled
                            && !ListviewWatermarkEnabled && !IconLabelShadowEnabled
                            && !AeroPeekEnabled && !TaskbarThumbnailPreviewsEnabled;
        bool optimized    = AnimateControlsEnabled && !AnimateWindowsEnabled && !FadeMenusEnabled
                            && ShowDragContentsEnabled && SmoothFontsEnabled
                            && !TooltipAnimationEnabled && !FadeOutMenuItemsEnabled
                            && !CursorShadowEnabled && !WindowShadowEnabled
                            && !ComboBoxAnimationEnabled && !ListboxSmoothScrollingEnabled
                            && !ListviewWatermarkEnabled && IconLabelShadowEnabled
                            && AeroPeekEnabled && TaskbarThumbnailPreviewsEnabled;
        bool allDefault   = AnimateControlsEnabled && AnimateWindowsEnabled && FadeMenusEnabled
                            && ShowDragContentsEnabled && SmoothFontsEnabled
                            && TooltipAnimationEnabled && FadeOutMenuItemsEnabled
                            && CursorShadowEnabled && WindowShadowEnabled
                            && ComboBoxAnimationEnabled && ListboxSmoothScrollingEnabled
                            && ListviewWatermarkEnabled && IconLabelShadowEnabled
                            && AeroPeekEnabled && TaskbarThumbnailPreviewsEnabled;

        ActivePreset = noAnimations ? "none"
                     : optimized   ? "speed"
                     : allDefault  ? "default"
                     : string.Empty;
    }

    // ── Legacy combined toggle (for DashboardViewModel compatibility) ──────────
    [RelayCommand]
    private void ToggleAnimations()
    {
        IsLoading = true;
        try
        {
            TweakResult result = AnimationsDisabled
                ? _animationService.RestoreAnimations()
                : _animationService.DisableAnimations();
            StatusMessage = result.Message;
            LoadFromService();
        }
        catch (Exception ex) { _log.Error("VisualViewModel", "Animation toggle failed", ex); StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    // ── Power plan — all GetActivePlan() calls run on background thread ────────

    [RelayCommand]
    private async Task SetHighPerformanceAsync()
    {
        IsLoading = true;
        StatusMessage = "Activating performance power plan...";
        try
        {
            var result = await _powerPlanService.SetHighPerformanceAsync();
            ActivePowerPlan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            // Note: don't touch IsBatteryPlanActive — power plan and battery CPU cap are independent settings
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _log.Error("VisualViewModel", "Set high performance plan failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task SetBalancedAsync()
    {
        IsLoading = true;
        StatusMessage = "Restoring balanced plan...";
        try
        {
            var result = await _powerPlanService.SetBalancedAsync();
            ActivePowerPlan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            // Note: don't touch IsBatteryPlanActive — power plan and battery CPU cap are independent settings
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _log.Error("VisualViewModel", "Set balanced plan failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    // ── Battery-aware power plans (laptop only) ────────────────────────────────

    [RelayCommand]
    private async Task SetBalancedOnBatteryAsync()
    {
        IsLoading = true;
        IsOnBattery = _powerPlanService.IsOnBattery();

        // Only apply when actually on battery — the /setdcvalueindex only affects DC mode,
        // but we warn the user so they know it won't kick in until they unplug.
        if (!IsOnBattery)
        {
            StatusMessage = "⚠ You're currently on AC power. The cap will take effect the next time you switch to battery.";
            _log.Info("VisualViewModel", "SetBalancedOnBattery called while on AC — applying anyway, effective on next battery use");
        }
        else
        {
            StatusMessage = "Applying Balanced on Battery (99% CPU cap on battery)...";
        }

        try
        {
            var result = await _powerPlanService.SetBalancedOnBatteryAsync();
            ActivePowerPlan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            if (result.Success)
            {
                IsBatteryPlanActive  = true;
                _activeBatteryOpt    = "balanced";
                _settings.BatteryOptimizationMode = "balanced";
            }
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _log.Error("VisualViewModel", "Set balanced on battery failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task SetMaxBatteryLifeAsync()
    {
        IsLoading = true;
        IsOnBattery = _powerPlanService.IsOnBattery();

        if (!IsOnBattery)
        {
            StatusMessage = "⚠ You're on AC power. The 80% CPU cap will take effect the next time you switch to battery.";
            _log.Info("VisualViewModel", "SetMaxBatteryLife called while on AC — applying anyway, effective on next battery use");
        }
        else
        {
            StatusMessage = "Applying Max Battery Life...";
        }

        try
        {
            var result = await _powerPlanService.SetMaxBatteryLifeAsync();
            ActivePowerPlan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            if (result.Success)
            {
                IsBatteryPlanActive = true;
                _activeBatteryOpt   = "max";
                _settings.BatteryOptimizationMode = "max";
            }
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _log.Error("VisualViewModel", "Set max battery life failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task StopBatteryOptimizationAsync()
    {
        IsLoading = true;
        StatusMessage = "Removing battery CPU cap...";
        try
        {
            var result = await _powerPlanService.StopBatteryOptimizationAsync();
            ActivePowerPlan     = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            IsBatteryPlanActive = false;
            _activeBatteryOpt   = string.Empty;
            _settings.BatteryOptimizationMode = string.Empty;
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _log.Error("VisualViewModel", "Stop battery optimization failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }
}
