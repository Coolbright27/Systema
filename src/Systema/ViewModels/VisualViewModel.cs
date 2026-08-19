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

using System.Collections.ObjectModel;
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

    // NOTE: The Dell BIOS Thermal Profile feature moved to DellViewModel / DellView
    // (the Dell sidebar section). The persisted keys are unchanged so existing
    // settings carry over.

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
    /// <summary>
    /// User-controlled toggle: keep High Performance plan active; auto-restores on plug-in.
    /// Persisted to HKCU so a hibernate-resume or app restart doesn't lose the setting.
    /// </summary>
    [ObservableProperty] private bool   _performanceModeEnabled;
    /// <summary>True when a battery-aware plan (Balanced on Battery / Max Battery Life) is active this session.</summary>
    [ObservableProperty] private bool   _isBatteryPlanActive;
    /// <summary>Which battery optimization mode is active: "" | "balanced" | "max"</summary>
    private string _activeBatteryOpt = string.Empty;
    /// <summary>
    /// The plan that was active before battery optimization was enabled.
    /// Backed by SettingsService so it survives app restarts and hibernate-resume.
    /// </summary>
    private string _planBeforeOpt = string.Empty;

    /// <summary>True when a high/ultimate performance plan is currently active.</summary>
    public bool IsHighPerformancePlanActive =>
        ActivePowerPlan.Contains("High",        StringComparison.OrdinalIgnoreCase) ||
        ActivePowerPlan.Contains("Ultimate",    StringComparison.OrdinalIgnoreCase) ||
        ActivePowerPlan.Contains("Performance", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The active battery optimization mode for the segmented-button UI.
    /// Mirrors _activeBatteryOpt; raised manually so XAML DataTriggers update.
    /// </summary>
    public string ActiveBatteryMode => _activeBatteryOpt;

    /// <summary>True when a CAP-based battery mode is active but the system is on AC power (cap is dormant).
    /// Excludes "performance" mode, which removes the cap and runs High Performance rather than capping.</summary>
    public bool IsOnAcWithBatteryPlanActive => IsBatteryPlanActive && !IsOnBattery && _activeBatteryOpt != "performance";

    partial void OnActivePowerPlanChanged(string value) =>
        OnPropertyChanged(nameof(IsHighPerformancePlanActive));

    /// <summary>
    /// True when Auto-Pilot Mode is active — controls bound to this property disable
    /// themselves so the user cannot override Auto-Pilot-managed settings.
    /// </summary>
    public bool IsAutoPilotActive => _settings.AutoPilotModeEnabled;

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

        // ── Restore persisted performance-mode toggle ──────────────────────────
        _performanceModeEnabled = _settings.PerformanceModeEnabled;

        // ── Restore persisted battery opt state ────────────────────────────────
        _planBeforeOpt = _settings.PlanBeforeOptimization;

        string savedOpt = _settings.BatteryOptimizationMode;
        if (!string.IsNullOrEmpty(savedOpt))
        {
            _activeBatteryOpt    = savedOpt;
            _isBatteryPlanActive = true;

            if (_isOnBattery)
            {
                // On battery at startup — re-apply the cap so a Windows Update or
                // plan reset can't silently undo it between sessions.
                string optSnapshot = savedOpt;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        TweakResult result = await ApplyBatteryOptForModeAsync(optSnapshot);
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                            StatusMessage = result.Success ? "Battery optimization re-applied." : result.Message);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("VisualViewModel", "Battery optimization re-apply failed", ex);
                    }
                });
            }
        }

        // ── Re-apply High Performance on AC startup ────────────────────────────
        // Covers hibernate-resume, app-restart after battery drain, and fresh boots.
        if (_performanceModeEnabled && !_isOnBattery)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    TweakResult result = await _powerPlanService.SetHighPerformanceAsync();
                    string plan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        _activePowerPlan = plan;
                        OnPropertyChanged(nameof(ActivePowerPlan));
                        StatusMessage = result.Success ? "Performance Mode active." : result.Message;
                    });
                }
                catch (Exception ex) { _log.Error("VisualViewModel", "Startup HP restore failed", ex); }
            });
        }

        // Auto-restore / re-apply on power-state transitions.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // Re-raise IsAutoPilotActive whenever the mode is toggled from Dashboard.
        SettingsService.AutoPilotModeChanged += OnAutoPilotModeChanged;
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
            // survives app restarts and is respected even when set by Auto-Pilot.
            string activeOpt = !string.IsNullOrEmpty(_activeBatteryOpt)
                ? _activeBatteryOpt
                : _settings.BatteryOptimizationMode;
            if (string.IsNullOrEmpty(activeOpt)) return;

            // Sync in-memory flag if it was set externally (e.g. by Auto-Pilot via settings)
            if (string.IsNullOrEmpty(_activeBatteryOpt))
            {
                _activeBatteryOpt  = activeOpt;
                IsBatteryPlanActive = true;
            }

            if (!nowOnBattery)
            {
                // ── Plugged back in ────────────────────────────────────────────

                // Not while a game boost is running. Game Booster may have switched to High
                // Performance for the session; restoring the pre-optimization plan here undid it
                // mid-game (seen live: the plan went back to Balanced eight seconds into a boost).
                // Game Booster restores the plan itself when the session ends, at which point this
                // handler is free to act again.
                if (Systema.Core.BoostedGameRegistry.SessionActive)
                {
                    _log.Info("VisualViewModel",
                              "AC power — power plan left alone, a game boost session is running");
                    return;
                }

                if (PerformanceModeEnabled)
                {
                    // User has Performance Mode toggled on — always restore HP,
                    // regardless of battery-opt state. This is the fix for the
                    // "laptop dies on battery, doesn't come back to perf mode" bug.
                    _log.Info("VisualViewModel", "AC power — restoring High Performance (PerformanceMode on)");
                    StatusMessage = "Plugged in — restoring High Performance…";
                    Task.Run(async () =>
                    {
                        TweakResult result = await _powerPlanService.SetHighPerformanceAsync();
                        string plan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            ActivePowerPlan = plan;
                            StatusMessage   = result.Message;
                        });
                    });
                }
                else if (!string.IsNullOrEmpty(activeOpt))
                {
                    // Battery opt is active, no performance mode — restore the plan
                    // that was running before the user enabled battery optimization.
                    string restorePlan = !string.IsNullOrEmpty(_planBeforeOpt) ? _planBeforeOpt : "Balanced";
                    _log.Info("VisualViewModel", $"AC power — restoring pre-opt plan: {restorePlan}");
                    StatusMessage = $"Plugged in — restoring {restorePlan} plan…";
                    string planSnapshot = restorePlan;
                    Task.Run(async () =>
                    {
                        TweakResult result = await _powerPlanService.RestorePlanAsync(planSnapshot);
                        string plan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            ActivePowerPlan = plan;
                            StatusMessage   = result.Message;
                        });
                    });
                }
            }
            else
            {
                // ── Unplugged — apply the chosen battery mode (cap, or High Performance for "performance") ──
                _log.Info("VisualViewModel", "Battery power detected — applying battery mode");
                StatusMessage = "On battery — applying your battery mode…";
                string optSnapshot = activeOpt;
                Task.Run(async () =>
                {
                    TweakResult result = await ApplyBatteryOptForModeAsync(optSnapshot);
                    string plan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        ActivePowerPlan = plan;
                        StatusMessage   = result.Message;
                    });
                });
            }
        });
    }

    private void OnAutoPilotModeChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            () => OnPropertyChanged(nameof(IsAutoPilotActive)));

    public void Dispose()
    {
        SystemEvents.PowerModeChanged          -= OnPowerModeChanged;
        SettingsService.AutoPilotModeChanged   -= OnAutoPilotModeChanged;
    }

    // ── IAutoRefreshable ──────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        try
        {
            if (!HasPendingChanges && !IsLoading)
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
            // Re-sync PerformanceModeEnabled from settings so the toggle reflects the
            // persisted value even when Auto-Pilot changed it in this session (M-2 fix).
            // Safe to use the property setter here because _loading=true suppresses
            // OnPerformanceModeEnabledChanged (which would otherwise trigger a plan switch).
            PerformanceModeEnabled = _settings.PerformanceModeEnabled;

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

    // ── Performance Mode toggle ────────────────────────────────────────────────

    /// <summary>
    /// Fires when the user flips the Performance Mode toggle.
    /// Persists the choice and immediately applies or reverts the power plan
    /// (provided the system is on AC — on battery we leave the battery plan alone).
    /// </summary>
    partial void OnPerformanceModeEnabledChanged(bool value)
    {
        if (_loading) return;
        _settings.PerformanceModeEnabled = value;

        if (value)
        {
            // Only apply immediately if we're on AC.  If the user flips this on
            // while running on battery the plan stays conservative — it will
            // auto-restore the next time they plug in.
            if (!_powerPlanService.IsOnBattery())
            {
                IsLoading = true;
                StatusMessage = "Activating High Performance plan…";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        TweakResult result = await _powerPlanService.SetHighPerformanceAsync();
                        string plan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            ActivePowerPlan = plan;
                            StatusMessage   = result.Message;
                            IsLoading       = false;
                        });
                    }
                    catch (Exception ex)
                    {
                        _log.Error("VisualViewModel", "Performance Mode enable failed", ex);
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            StatusMessage = $"Error: {ex.Message}";
                            IsLoading     = false;
                        });
                    }
                });
            }
            else
            {
                StatusMessage = "Performance Mode on — will activate next time you plug in.";
            }
        }
        else
        {
            // The "Performance" battery mode only makes sense while Performance Mode is on. If it was
            // selected, clear it so it doesn't linger on a now-hidden segment or re-apply High
            // Performance on the next unplug.
            if (_activeBatteryOpt == "performance")
            {
                _activeBatteryOpt                 = string.Empty;
                _settings.BatteryOptimizationMode = string.Empty;
                IsBatteryPlanActive               = false;
                OnPropertyChanged(nameof(ActiveBatteryMode));
                OnPropertyChanged(nameof(IsOnAcWithBatteryPlanActive));
            }

            // Always restore Balanced when toggling off (if on AC).
            // Battery opt will take over on the next unplug via OnPowerModeChanged.
            if (!_powerPlanService.IsOnBattery())
            {
                IsLoading = true;
                StatusMessage = "Restoring Balanced plan…";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        TweakResult result = await _powerPlanService.SetBalancedAsync();
                        string plan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            ActivePowerPlan = plan;
                            StatusMessage   = result.Message;
                            IsLoading       = false;
                        });
                    }
                    catch (Exception ex)
                    {
                        _log.Error("VisualViewModel", "Performance Mode disable failed", ex);
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            StatusMessage = $"Error: {ex.Message}";
                            IsLoading     = false;
                        });
                    }
                });
            }
            else
            {
                StatusMessage = "Performance Mode off.";
            }
        }
    }

    // ── Battery mode — single command, three modes ─────────────────────────────

    /// <summary>
    /// Unified battery mode command. <paramref name="mode"/> is "" (off),
    /// "balanced", or "max". Called directly from XAML button CommandParameter.
    /// </summary>
    [RelayCommand]
    private async Task SetBatteryModeAsync(string? mode)
    {
        mode ??= "";
        switch (mode)
        {
            case "performance": await ApplyPerformanceOnBatteryAsync(); break;
            case "balanced":    await ApplyBalancedOnBatteryAsync();    break;
            case "max":         await ApplyMaxBatteryLifeAsync();       break;
            default:            await ApplyStopBatteryOptAsync();       break;
        }
        OnPropertyChanged(nameof(ActiveBatteryMode));
        OnPropertyChanged(nameof(IsOnAcWithBatteryPlanActive));
    }

    /// <summary>The power-plan side of a battery mode (no UI/state writes). Shared by the startup
    /// re-apply and the on-unplug transition so all modes behave the same in every path.</summary>
    private async Task<TweakResult> ApplyBatteryOptForModeAsync(string mode)
    {
        switch (mode)
        {
            case "performance":
                // Full speed on battery: drop any DC cap and force High Performance.
                await _powerPlanService.RemoveBatteryCpuCapAsync();
                return await _powerPlanService.SetHighPerformanceAsync();
            case "max":
            {
                var maxResult = await _powerPlanService.SetMaxBatteryLifeAsync();
                // Same as the button path: Max Life implies deepest parking on its own.
                await RunOnLargeStackAsync(() => Systema.Services.CoreParkingService.SetMinCoresEverywhere(0));
                return maxResult;
            }
            default:
                return await _powerPlanService.SetBalancedOnBatteryAsync();
        }
    }

    // ── Internal power plan helpers ────────────────────────────────────────────

    /// <summary>"Performance" battery mode: keep full CPU speed on battery by removing any DC cap and
    /// running High Performance. Only offered while Performance Mode is on. Reversible via the other
    /// segments (Off restores Balanced and clears the cap state).</summary>
    private async Task ApplyPerformanceOnBatteryAsync()
    {
        IsLoading = true;
        IsOnBattery = _powerPlanService.IsOnBattery();
        StatusMessage = IsOnBattery
            ? "Switching to High Performance on battery…"
            : "Battery Mode set. High Performance activates when you unplug.";
        try
        {
            _planBeforeOpt = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            _settings.PlanBeforeOptimization = _planBeforeOpt;

            var result = await ApplyBatteryOptForModeAsync("performance");
            ActivePowerPlan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            if (result.Success)
            {
                IsBatteryPlanActive               = true;
                _activeBatteryOpt                 = "performance";
                _settings.BatteryOptimizationMode = "performance";
            }
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _log.Error("VisualViewModel", "Performance-on-battery failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    private async Task ApplyBalancedOnBatteryAsync()
    {
        IsLoading = true;
        IsOnBattery = _powerPlanService.IsOnBattery();
        StatusMessage = IsOnBattery
            ? "Switching to Balanced plan…"
            : "Battery Mode set — Balanced plan activates when you unplug.";
        try
        {
            // Snapshot the current plan before overwriting it, then persist so a
            // reboot or hibernate-resume still knows what to restore on plug-in.
            _planBeforeOpt = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            _settings.PlanBeforeOptimization = _planBeforeOpt;

            var result = await _powerPlanService.SetBalancedOnBatteryAsync();
            ActivePowerPlan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            if (result.Success)
            {
                IsBatteryPlanActive               = true;
                _activeBatteryOpt                 = "balanced";
                _settings.BatteryOptimizationMode = "balanced";
            }
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _log.Error("VisualViewModel", "Balanced-on-battery failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    private async Task ApplyMaxBatteryLifeAsync()
    {
        IsLoading = true;
        IsOnBattery = _powerPlanService.IsOnBattery();
        StatusMessage = IsOnBattery
            ? "Switching to Power Saver plan…"
            : "Battery Mode set — Max Life activates when you unplug.";
        try
        {
            _planBeforeOpt = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            _settings.PlanBeforeOptimization = _planBeforeOpt;

            var result = await _powerPlanService.SetMaxBatteryLifeAsync();

            // Max Life parks as hard as the machine allows, whether or not Core Efficiency is on.
            // Runs AFTER the plan switch: SetMaxBatteryLifeAsync changes the active scheme, so
            // writing min cores first would land on the plan being switched away from.
            await RunOnLargeStackAsync(() => Systema.Services.CoreParkingService.SetMinCoresEverywhere(0));
            ActivePowerPlan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            if (result.Success)
            {
                IsBatteryPlanActive               = true;
                _activeBatteryOpt                 = "max";
                _settings.BatteryOptimizationMode = "max";
            }
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _log.Error("VisualViewModel", "Max battery life failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    private async Task ApplyStopBatteryOptAsync()
    {
        IsLoading = true;
        StatusMessage = "Removing battery mode…";
        try
        {
            // Only remove the CPU cap — do NOT force any power plan change here.
            // The active plan is independently controlled by the Performance Mode toggle.
            await _powerPlanService.RemoveBatteryCpuCapAsync();

            IsBatteryPlanActive               = false;
            _activeBatteryOpt                 = string.Empty;
            _planBeforeOpt                    = string.Empty;
            _settings.BatteryOptimizationMode = string.Empty;
            _settings.PlanBeforeOptimization  = string.Empty;

            // Set the right plan based on Performance Mode (don't clobber the user's choice).
            if (!_powerPlanService.IsOnBattery())
            {
                if (PerformanceModeEnabled)
                {
                    await _powerPlanService.SetHighPerformanceAsync();
                    StatusMessage = "Battery mode off — High Performance plan active.";
                }
                else
                {
                    await _powerPlanService.SetBalancedAsync();
                    StatusMessage = "Battery mode off — Balanced plan restored.";
                }
                ActivePowerPlan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
            }
            else
            {
                ActivePowerPlan = await RunOnLargeStackAsync(() => _powerPlanService.GetActivePlan());
                StatusMessage   = "Battery mode off.";
            }
        }
        catch (Exception ex)
        {
            _log.Error("VisualViewModel", "Stop battery opt failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }
}
