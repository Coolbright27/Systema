// ════════════════════════════════════════════════════════════════════════════
// ServicesViewModel.cs  ·  Windows services and optional features management
// ════════════════════════════════════════════════════════════════════════════
//
// Lists Windows services (with Recommended/Expert categorization) and allows
// enabling, disabling, and restarting them via ServiceControlService. Also
// exposes optional Windows features (via OptionalFeaturesService/DISM) and a
// restore-point creation command. Implements IAutoRefreshable.
//
// RELATED FILES
//   ServiceControlService.cs        — service enumeration and state changes
//   OptionalFeaturesService.cs      — DISM-based optional feature toggle
//   RestorePointService.cs          — WMI restore point creation
//   Views/ServicesView.xaml         — binds service list and feature toggles
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Core;
using Systema.Models;
using Systema.Services;

namespace Systema.ViewModels;

public partial class ServicesViewModel : ObservableObject, IAutoRefreshable, IDisposable
{
    private readonly ServiceControlService   _serviceControl;
    private readonly OptionalFeaturesService _optFeatures;
    private readonly RestorePointService     _restoreService;
    private readonly SettingsService         _settings;
    private readonly GameBoosterService      _gameBooster;
    private static readonly LoggerService    _log = LoggerService.Instance;
    private int _isRefreshing;

    [ObservableProperty] private ObservableCollection<ServiceInfo> _services = new();
    [ObservableProperty] private ObservableCollection<OptionalFeatureInfo> _optionalFeatures = new();
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private bool   _isFeatureLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // Two-way binding for the merged "Privacy & Background Services" toggle.
    // True when both telemetry services AND every Recommended service for this PC
    // are currently disabled. Flipping the toggle fires OnPrivacyCleanupAppliedChanged,
    // which runs the appropriate disable / restore action. When refresh logic writes
    // this property from the current system state we set _suppressPrivacyToggleSideEffect
    // first so the partial-method handler skips the round-trip.
    [ObservableProperty] private bool   _privacyCleanupApplied;
    [ObservableProperty] private bool   _isPrivacyCleanupBusy;
    private bool _suppressPrivacyToggleSideEffect;

    // "No Telemetry Pro" — the maximal telemetry kill (policy + services + tasks). Reflects live state;
    // flipping it fires OnNoTelemetryProChanged. Suppressed while refresh writes the reflected value.
    [ObservableProperty] private bool   _noTelemetryPro;
    [ObservableProperty] private string _noTelemetryStatusText = string.Empty;
    private bool _suppressNoTelemetrySideEffect;
    private bool _noTelemetryBusy;
    public bool HasNoTelemetryStatus => !string.IsNullOrEmpty(NoTelemetryStatusText);

    // ── Expander state ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _showWindowsFeatures;
    [RelayCommand] private void ToggleWindowsFeatures() => ShowWindowsFeatures = !ShowWindowsFeatures;

    /// <summary>
    /// Mirrors <see cref="GameBoosterService.GamesInstalled"/>. Initialised in the
    /// constructor and kept live via the GamesInstalledChanged event. Used by
    /// <see cref="ServiceControlService.AreAllRecommendedDisabled"/> so the
    /// Privacy &amp; Background Services toggle reflects the same set of services
    /// the Dashboard Auto-Pilot actually touches — without this sync, the toggle
    /// kept reading false after Auto-Pilot finished because the two paths
    /// disagreed about whether Xbox services should count as "Recommended."
    /// </summary>
    public bool GamesInstalled { get; private set; }

    /// <summary>True when Auto-Pilot Mode is on — Data Collection button is grayed out.</summary>
    public bool IsAutoPilotActive => _settings.AutoPilotModeEnabled;

    // ── Dashboard (Design C) ────────────────────────────────────────────────────
    // Status tiles + recommended-cleanup summary. These are computed over the live
    // Services / OptionalFeatures collections, which are replaced via Clear()+Add()
    // (same instance) so they don't raise CollectionChanged for the counts — we
    // re-raise them manually through RaiseDashboardStats() after every refresh.
    public int TotalBackgroundTasks => Services.Count;
    public int RecommendedOffCount  => Services.Count(s => s.IsRecommended && !s.IsOptimized);
    public int AlreadyOptimizedCount => Services.Count(s => s.IsOptimized);

    // Recommended cleanup = the privacy/telemetry bundle (DisablePrivacyAndRecommendedAsync):
    // stop Microsoft telemetry services + tasks, and optimize the Recommended background
    // services for this PC. It deliberately does NOT touch advertising ID or Delivery
    // Optimization, so the copy here describes only what actually happens.
    public int RecommendedServiceCount => Services.Count(s => s.IsRecommended);

    public string RecommendedSummaryText =>
        $"Sets the {RecommendedServiceCount} background service{(RecommendedServiceCount == 1 ? "" : "s")} " +
        "that are safe for most PCs to Manual, so they only start when something needs them. Internet, " +
        "Windows Update, security, printing, and search are never touched. Telemetry has its own toggle below.";

    public string RecommendedStatusText =>
        PrivacyCleanupApplied
            ? "On. The safe background services only start when needed."
            : "Off. Those background services are running normally.";

    public string PerformanceCategorySubtitle => $"{Services.Count} background service{(Services.Count == 1 ? "" : "s")}";
    public string ExtrasCategorySubtitle =>
        OptionalFeatures.Count > 0 ? $"{OptionalFeatures.Count} optional feature{(OptionalFeatures.Count == 1 ? "" : "s")}"
                                   : (IsFeatureLoading ? "Loading…" : "Scanning Windows…");

    private void RaiseDashboardStats()
    {
        OnPropertyChanged(nameof(TotalBackgroundTasks));
        OnPropertyChanged(nameof(RecommendedOffCount));
        OnPropertyChanged(nameof(AlreadyOptimizedCount));
        OnPropertyChanged(nameof(RecommendedServiceCount));
        OnPropertyChanged(nameof(RecommendedSummaryText));
        OnPropertyChanged(nameof(RecommendedStatusText));
        OnPropertyChanged(nameof(PerformanceCategorySubtitle));
        OnPropertyChanged(nameof(ExtrasCategorySubtitle));
    }

    // ── Category drill-down ─────────────────────────────────────────────────────
    // "" = dashboard overview; "privacy" / "performance" / "extras" = detail view.
    [ObservableProperty] private string _selectedCategory = string.Empty;

    public bool   IsOverview            => string.IsNullOrEmpty(SelectedCategory);
    public bool   IsPrivacyCategory     => SelectedCategory == "privacy";
    public bool   IsPerformanceCategory => SelectedCategory == "performance";
    public bool   IsExtrasCategory      => SelectedCategory == "extras";
    public string SelectedCategoryTitle => SelectedCategory switch
    {
        "privacy"     => "Privacy & data",
        "performance" => "Services",
        "extras"      => "Windows extras",
        _             => string.Empty
    };

    partial void OnSelectedCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsPrivacyCategory));
        OnPropertyChanged(nameof(IsPerformanceCategory));
        OnPropertyChanged(nameof(IsExtrasCategory));
        OnPropertyChanged(nameof(SelectedCategoryTitle));
    }

    [RelayCommand] private void OpenCategory(string category) => SelectedCategory = category ?? string.Empty;
    [RelayCommand] private void BackToOverview() => SelectedCategory = string.Empty;

    public ServicesViewModel(
        ServiceControlService   serviceControl,
        OptionalFeaturesService optFeatures,
        RestorePointService     restoreService,
        SettingsService         settings,
        GameBoosterService      gameBooster)
    {
        _serviceControl = serviceControl;
        _optFeatures    = optFeatures;
        _restoreService = restoreService;
        _settings       = settings;
        _gameBooster    = gameBooster;

        // Initial GamesInstalled snapshot — kept in sync via GamesInstalledChanged below.
        // Without this, the toggle's status check used the wrong "recommended" set
        // and the toggle showed OFF after Auto-Pilot finished (Xbox services were
        // skipped because games ARE installed, but our check expected them gone).
        GamesInstalled = gameBooster.GamesInstalled;
        gameBooster.GamesInstalledChanged += OnGamesInstalledChanged;

        // Set initial state without triggering the toggle side effect.
        _suppressPrivacyToggleSideEffect = true;
        _privacyCleanupApplied =
            _serviceControl.AreTelemetryServicesDisabled()
            && _serviceControl.AreAllRecommendedDisabled(_gameBooster.GamesInstalled);
        _suppressPrivacyToggleSideEffect = false;

        SettingsService.AutoPilotModeChanged += OnAutoPilotModeChanged;
        SettingsService.OptimizationsApplied += OnOptimizationsApplied;
    }

    /// <summary>
    /// Fired by DashboardViewModel after Auto-Pilot / "Apply settings once" finishes.
    /// We refresh so the Privacy &amp; Background Services toggle reflects the
    /// post-cleanup service state instead of the stale pre-cleanup snapshot.
    /// Without this hook the toggle stayed OFF after Auto-Pilot finished, while
    /// IsAutoPilotActive flipped to true and grayed out the rows below — exactly
    /// the bug the user reported in v0.7.9.
    /// </summary>
    private void OnOptimizationsApplied(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => _ = DoRefreshAsync());
    }

    /// <summary>
    /// Fired by GameBoosterService when the installed-games state changes (e.g. user
    /// installs Steam, the periodic re-scan finds a new game). Updates our cached
    /// flag AND triggers a refresh so the Privacy toggle re-evaluates against the
    /// correct "recommended" set immediately.
    /// </summary>
    private void OnGamesInstalledChanged(bool nowInstalled)
    {
        GamesInstalled = nowInstalled;
        // Refresh so PrivacyCleanupApplied re-evaluates with the new flag.
        Application.Current?.Dispatcher.BeginInvoke(() => _ = DoRefreshAsync());
    }

    private void OnAutoPilotModeChanged(object? sender, EventArgs e)
    {
        // Three things change when Auto-Pilot mode flips:
        //  1. IsAutoPilotActive — drives the gray-out triggers in the XAML.
        //  2. Auto-Pilot just finished running its cleanup steps (on toggle ON)
        //     or is about to drift back (on toggle OFF) — refresh so the
        //     PrivacyCleanupApplied toggle re-reads the actual service state
        //     instead of staying stale.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(IsAutoPilotActive));
            _ = DoRefreshAsync();
        });
    }

    public void Dispose()
    {
        SettingsService.AutoPilotModeChanged -= OnAutoPilotModeChanged;
        SettingsService.OptimizationsApplied -= OnOptimizationsApplied;
        if (_gameBooster != null)
            _gameBooster.GamesInstalledChanged -= OnGamesInstalledChanged;
    }

    public Task RefreshAsync() => DoRefreshAsync();

    [RelayCommand]
    private Task RefreshCommandAsync() => DoRefreshAsync();

    private async Task DoRefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0) return;
        IsLoading = true;
        try
        {
            var list = await Task.Run(() => _serviceControl.GetServiceStatuses(GamesInstalled));
            Services.Clear();
            foreach (var svc in list) Services.Add(svc);

            // Update the toggle reflection without re-firing the disable / restore action.
            _suppressPrivacyToggleSideEffect = true;
            PrivacyCleanupApplied =
                _serviceControl.AreAllRecommendedDisabled(_gameBooster.GamesInstalled);
            _suppressPrivacyToggleSideEffect = false;

            _suppressNoTelemetrySideEffect = true;
            NoTelemetryPro = _serviceControl.IsNoTelemetryProEnabled();
            _suppressNoTelemetrySideEffect = false;

            // Optional Windows features are no longer listed in-app — the "Windows extras"
            // card opens Windows' own panel (optionalfeatures.exe) directly — so we skip the
            // slow DISM enumeration entirely.

            StatusMessage = $"{list.Count} services loaded.";
            RaiseDashboardStats();
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", "Failed to load services", ex);
            StatusMessage = $"Error loading services: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    private async Task LoadFeaturesAsync()
    {
        try
        {
            var features = await _optFeatures.GetAllFeaturesAsync();
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                OptionalFeatures.Clear();
                foreach (var f in features) OptionalFeatures.Add(f);
                RaiseDashboardStats();
            });
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", "Failed to load optional features", ex);
        }
    }

    [RelayCommand]
    private async Task DisableServiceAsync(ServiceInfo svc)
    {
        // BITS must not be fully disabled — Windows Update relies on it.
        // Silently redirect to "Set to Manual" which is safe and keeps updates working.
        if (svc.ServiceName.Equals("BITS", StringComparison.OrdinalIgnoreCase))
        {
            await SetManualBitsAsync(svc);
            return;
        }

        IsLoading = true;
        StatusMessage = $"Disabling {svc.DisplayName}...";
        try
        {
            var result = await _serviceControl.DisableServiceAsync(svc.ServiceName);
            StatusMessage = result.Message;
            await DoRefreshAsync();
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", $"Failed to disable {svc.ServiceName}", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// BITS is required by Windows Update — fully disabling it can break update downloads.
    /// Redirect "Disable" to "Set to Manual" with an explanation message.
    /// </summary>
    private async Task SetManualBitsAsync(ServiceInfo svc)
    {
        // Warn the user and let them decide
        var choice = MessageBox.Show(
            "Background Intelligent Transfer (BITS) is used by Windows Update to download updates.\n\n" +
            "Fully disabling BITS can prevent Windows from installing security patches.\n\n" +
            "• Set to Manual (recommended) — BITS will only run when needed, saving resources without breaking updates.\n" +
            "• Disable anyway — Not recommended. You may need to re-enable it if updates stop working.\n\n" +
            "Set to Manual instead of Disable?",
            "BITS — Windows Update Dependency",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        IsLoading = true;
        try
        {
            if (choice == MessageBoxResult.Yes)
            {
                StatusMessage = "Setting BITS to Manual (safe — Windows Update still works)...";
                var result = await _serviceControl.SetManualAsync(svc.ServiceName);
                StatusMessage = result.Success
                    ? "BITS set to Manual. Windows Update will still work normally."
                    : result.Message;
            }
            else
            {
                StatusMessage = "Disabling BITS (not recommended)...";
                var result = await _serviceControl.DisableServiceAsync(svc.ServiceName);
                StatusMessage = result.Success
                    ? "BITS disabled. Re-enable if Windows Update stops working."
                    : result.Message;
            }
            await DoRefreshAsync();
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", "Failed to change BITS service state", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task SetManualAsync(ServiceInfo svc)
    {
        IsLoading = true;
        try
        {
            var result = await _serviceControl.SetManualAsync(svc.ServiceName);
            StatusMessage = result.Message;
            await DoRefreshAsync();
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", $"Failed to set {svc.ServiceName} to manual", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task EnableServiceAsync(ServiceInfo svc)
    {
        IsLoading = true;
        try
        {
            var result = await _serviceControl.EnableServiceAsync(svc.ServiceName);
            StatusMessage = result.Message;
            await DoRefreshAsync();
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", $"Failed to enable {svc.ServiceName}", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    /// <summary>Single on/off toggle for a service (redesigned tab): off = optimize, on = restore.
    /// Reuses the safe Disable path (which auto-redirects BITS to Manual) and Enable path; both
    /// refresh the list afterward, so the switch reflects the real result (and reverts on failure).</summary>
    [RelayCommand]
    private async Task ToggleServiceAsync(ServiceInfo svc)
    {
        if (svc.IsOptimized) await EnableServiceAsync(svc);
        else                 await DisableServiceAsync(svc);
    }

    /// <summary>Single button for a Windows feature: removes it if present, restores it if removed.
    /// The Disable path creates a restore point first.</summary>
    [RelayCommand]
    private async Task ToggleFeatureAsync(OptionalFeatureInfo f)
    {
        if (f.IsEnabled) await DisableOptionalFeatureAsync(f.Name);
        else             await EnableOptionalFeatureAsync(f.Name);
    }

    /// <summary>Banner "Apply all" / "Undo" — drive the existing one-click privacy bundle.</summary>
    [RelayCommand] private void ApplyAllRecommended() => PrivacyCleanupApplied = true;
    [RelayCommand] private void UndoAllRecommended()  => PrivacyCleanupApplied = false;

    /// <summary>
    /// Toggle handler for the merged "Privacy &amp; Background Services" switch.
    /// Generated by CommunityToolkit MVVM from the <c>_privacyCleanupApplied</c>
    /// ObservableProperty.
    ///
    /// ON  → disable all telemetry services + every Recommended optional service.
    /// OFF → restore telemetry to Auto, set previously-disabled Recommended
    ///       services back to Manual (start on demand, no boot cost).
    ///
    /// Skipped when <c>_suppressPrivacyToggleSideEffect</c> is true so refresh
    /// logic and the constructor can write the property without firing the
    /// disable / restore round-trip.
    /// </summary>
    partial void OnPrivacyCleanupAppliedChanged(bool value)
    {
        OnPropertyChanged(nameof(RecommendedStatusText));
        if (_suppressPrivacyToggleSideEffect) return;
        _ = RunPrivacyToggleAsync(value);
    }

    partial void OnIsFeatureLoadingChanged(bool value)
        => OnPropertyChanged(nameof(ExtrasCategorySubtitle));

    // ── No Telemetry Pro toggle ────────────────────────────────────────────────
    partial void OnNoTelemetryStatusTextChanged(string value) => OnPropertyChanged(nameof(HasNoTelemetryStatus));

    partial void OnNoTelemetryProChanged(bool value)
    {
        if (_suppressNoTelemetrySideEffect) return;
        _ = RunNoTelemetryToggleAsync(value);
    }

    private async Task RunNoTelemetryToggleAsync(bool turnOn)
    {
        if (_noTelemetryBusy) return;               // ignore taps while a run is in flight
        _noTelemetryBusy = true;
        IsLoading = true;
        NoTelemetryStatusText = turnOn ? "Turning off all Windows telemetry…" : "Restoring telemetry to Windows defaults…";
        try
        {
            var r = await _serviceControl.SetNoTelemetryProAsync(turnOn);
            NoTelemetryStatusText = r.Message;
            StatusMessage         = r.Message;
            if (!r.Success)
            {
                _suppressNoTelemetrySideEffect = true;
                NoTelemetryPro = !turnOn;           // revert the switch on failure
                _suppressNoTelemetrySideEffect = false;
            }
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", "No Telemetry Pro toggle failed", ex);
            NoTelemetryStatusText = $"Error: {ex.Message}";
            _suppressNoTelemetrySideEffect = true;
            NoTelemetryPro = !turnOn;
            _suppressNoTelemetrySideEffect = false;
        }
        finally { _noTelemetryBusy = false; IsLoading = false; }
    }

    private async Task RunPrivacyToggleAsync(bool turnOn)
    {
        // Re-entrancy guard: ignore taps while a previous toggle is still running.
        if (IsPrivacyCleanupBusy) return;
        IsPrivacyCleanupBusy = true;
        IsLoading            = true;

        try
        {
            if (turnOn)
            {
                // Build a preview of what we'll switch off so the user knows what to expect.
                var willDisable = Services
                    .Where(s => s.IsRecommended && s.ColorState != ServiceColorState.Red)
                    .ToList();

                if (willDisable.Count == 0)
                {
                    StatusMessage = "Service Cleanup already applied — nothing to do.";
                    return;
                }

                var preview = new System.Text.StringBuilder();
                preview.Append("Turning this ON will switch these background services to Manual so they only start when something needs them:\n");
                preview.Append($"\n  • {willDisable.Count} service")
                       .Append(willDisable.Count == 1 ? "" : "s")
                       .Append(":");
                foreach (var s in willDisable.Take(8))
                    preview.Append("\n      – ").Append(s.DisplayName);
                if (willDisable.Count > 8)
                    preview.Append($"\n      – …and {willDisable.Count - 8} more");
                preview.Append("\n\nTurn the switch OFF later to restore these services. Proceed?");

                var confirm = MessageBox.Show(
                    preview.ToString(),
                    "Service Cleanup",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information,
                    MessageBoxResult.OK);

                if (confirm != MessageBoxResult.OK)
                {
                    // User cancelled — flip the switch back without re-running.
                    _suppressPrivacyToggleSideEffect = true;
                    PrivacyCleanupApplied = false;
                    _suppressPrivacyToggleSideEffect = false;
                    StatusMessage = "Cancelled.";
                    return;
                }

                StatusMessage = "Applying Service Cleanup…";
                var result = await _serviceControl.DisableRecommendedServicesAsync(GamesInstalled);
                StatusMessage = result.Message;
            }
            else
            {
                // Turning OFF → restore.
                var confirm = MessageBox.Show(
                    "This will re-enable every background service this cleanup turned off, setting them " +
                    "back to Manual start (they start on demand, not at boot).\n\nProceed?",
                    "Restore Services",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel);

                if (confirm != MessageBoxResult.OK)
                {
                    // User cancelled — flip switch back to ON without re-running.
                    _suppressPrivacyToggleSideEffect = true;
                    PrivacyCleanupApplied = true;
                    _suppressPrivacyToggleSideEffect = false;
                    StatusMessage = "Cancelled.";
                    return;
                }

                StatusMessage = "Restoring services…";
                var result = await _serviceControl.RestoreRecommendedServicesAsync(GamesInstalled);
                StatusMessage = result.Message;
            }

            await DoRefreshAsync();
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", "Privacy toggle failed", ex);
            StatusMessage = $"Error: {ex.Message}";
            // Reflect actual state in case the partial action left things mid-flight.
            _suppressPrivacyToggleSideEffect = true;
            PrivacyCleanupApplied =
                _serviceControl.AreAllRecommendedDisabled(_gameBooster.GamesInstalled);
            _suppressPrivacyToggleSideEffect = false;

            _suppressNoTelemetrySideEffect = true;
            NoTelemetryPro = _serviceControl.IsNoTelemetryProEnabled();
            _suppressNoTelemetrySideEffect = false;
        }
        finally
        {
            IsLoading            = false;
            IsPrivacyCleanupBusy = false;
        }
    }

    [RelayCommand]
    private void OpenServicesMsc()
    {
        try
        {
            Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", "Failed to open services.msc", ex);
            StatusMessage = $"Error opening services.msc: {ex.Message}";
        }
    }

    /// <summary>Opens Windows' built-in "Turn Windows features on or off" panel
    /// (optionalfeatures.exe) — the native place to add/remove the optional features
    /// Systema lists here. Windows performs the change and handles any restart, so
    /// Systema doesn't run DISM itself.</summary>
    [RelayCommand]
    private void OpenWindowsFeatures()
    {
        try
        {
            Process.Start(new ProcessStartInfo("optionalfeatures.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", "Failed to open Windows Features panel", ex);
            StatusMessage = $"Couldn't open Windows Features: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisableOptionalFeatureAsync(string featureName)
    {
        IsFeatureLoading = true;
        StatusMessage = $"Removing {featureName}... (this may take a few minutes)";
        try
        {
            var restoreCreated = await MaybeCreateRestorePointAsync($"Systema - Remove {featureName}");
            if (!restoreCreated.HasValue) { StatusMessage = "Operation cancelled."; return; }

            StatusMessage = $"Running DISM to remove {featureName}...";
            var result = await _optFeatures.DisableFeatureAsync(featureName);
            StatusMessage = result.Success
                ? $"Removed: {featureName}. {(result.Message.Contains("3010") || result.Message.Contains("reboot") ? "Restart required." : "")}"
                : result.Message;

            // Refresh the features list (don't reset the flag — just reload directly)
            await LoadFeaturesAsync();
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", $"Failed to disable feature {featureName}", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsFeatureLoading = false; }
    }

    [RelayCommand]
    private async Task EnableOptionalFeatureAsync(string featureName)
    {
        IsFeatureLoading = true;
        StatusMessage = $"Adding {featureName}... (this may take a few minutes)";
        try
        {
            StatusMessage = $"Running DISM to enable {featureName}...";
            var result = await _optFeatures.EnableFeatureAsync(featureName);
            StatusMessage = result.Success
                ? $"Added: {featureName}. {(result.Message.Contains("3010") || result.Message.Contains("reboot") ? "Restart required." : "")}"
                : result.Message;

            // Refresh the features list (don't reset the flag — just reload directly)
            await LoadFeaturesAsync();
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", $"Failed to enable feature {featureName}", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsFeatureLoading = false; }
    }

    private async Task<bool?> MaybeCreateRestorePointAsync(string description)
    {
        if (_settings.SkipRestorePoint)
        {
            _log.Info("ServicesViewModel", "Restore point skipped (user preference)");
            return false;
        }

        var result = MessageBox.Show(
            "Would you like Systema to create a Windows System Restore point before proceeding?\n\n" +
            "\u2022 Yes  \u2014 Create a restore point (recommended)\n" +
            "\u2022 No   \u2014 Skip this time\n" +
            "\u2022 Cancel \u2014 Abort the operation\n\n" +
            "You can permanently disable restore point prompts in Settings.",
            "Create Restore Point?",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        switch (result)
        {
            case MessageBoxResult.Yes:
                StatusMessage = "Creating restore point...";
                var outcome = await _restoreService.CreateAsync(description);
                if (!outcome.Success)
                    _log.Warn("ServicesViewModel", $"Restore point failed: {outcome.Message}");
                return true;
            case MessageBoxResult.No:
                return false;
            default:
                return null;
        }
    }
}
