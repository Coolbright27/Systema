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
    private static readonly LoggerService    _log = LoggerService.Instance;
    private int _isRefreshing;
    private volatile bool _hasLoadedFeaturesOnce;

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

    // ── Expander state ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _showWindowsFeatures;
    [RelayCommand] private void ToggleWindowsFeatures() => ShowWindowsFeatures = !ShowWindowsFeatures;

    public bool GamesInstalled { get; set; }

    /// <summary>True when Auto-Pilot Mode is on — Data Collection button is grayed out.</summary>
    public bool IsAutoPilotActive => _settings.AutoPilotModeEnabled;

    public ServicesViewModel(
        ServiceControlService   serviceControl,
        OptionalFeaturesService optFeatures,
        RestorePointService     restoreService,
        SettingsService         settings)
    {
        _serviceControl = serviceControl;
        _optFeatures    = optFeatures;
        _restoreService = restoreService;
        _settings       = settings;

        // Set initial state without triggering the toggle side effect.
        _suppressPrivacyToggleSideEffect = true;
        _privacyCleanupApplied =
            _serviceControl.AreTelemetryServicesDisabled()
            && _serviceControl.AreAllRecommendedDisabled(GamesInstalled);
        _suppressPrivacyToggleSideEffect = false;

        SettingsService.AutoPilotModeChanged += OnAutoPilotModeChanged;
    }

    private void OnAutoPilotModeChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(
            () => OnPropertyChanged(nameof(IsAutoPilotActive)));

    public void Dispose() => SettingsService.AutoPilotModeChanged -= OnAutoPilotModeChanged;

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
                _serviceControl.AreTelemetryServicesDisabled()
                && _serviceControl.AreAllRecommendedDisabled(GamesInstalled);
            _suppressPrivacyToggleSideEffect = false;

            // Load feature states only on first load (DISM is extremely slow — 30-60s)
            if (!_hasLoadedFeaturesOnce)
            {
                _hasLoadedFeaturesOnce = true;
                await LoadFeaturesAsync();
            }

            StatusMessage = $"{list.Count} services loaded.";
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
        if (_suppressPrivacyToggleSideEffect) return;
        _ = RunPrivacyToggleAsync(value);
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
                // Build a preview of what we'll disable so the user knows what to expect.
                var willDisable = Services
                    .Where(s => s.IsRecommended && s.ColorState != ServiceColorState.Red)
                    .ToList();
                bool telemetryNeedsDisable = !_serviceControl.AreTelemetryServicesDisabled();

                if (willDisable.Count == 0 && !telemetryNeedsDisable)
                {
                    StatusMessage = "Privacy cleanup already applied — nothing to do.";
                    return;
                }

                var preview = new System.Text.StringBuilder();
                preview.Append("Turning this ON will:\n");
                if (telemetryNeedsDisable)
                    preview.Append("\n  • Stop Microsoft data collection (telemetry services + scheduled tasks)");
                if (willDisable.Count > 0)
                {
                    preview.Append($"\n  • Disable {willDisable.Count} recommended background service")
                           .Append(willDisable.Count == 1 ? "" : "s")
                           .Append(":");
                    foreach (var s in willDisable.Take(8))
                        preview.Append("\n      – ").Append(s.DisplayName);
                    if (willDisable.Count > 8)
                        preview.Append($"\n      – …and {willDisable.Count - 8} more");
                }
                preview.Append("\n\nTurn the switch OFF later to restore these services. Proceed?");

                var confirm = MessageBox.Show(
                    preview.ToString(),
                    "Disable Privacy & Background Services",
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

                StatusMessage = "Applying privacy cleanup…";
                var result = await _serviceControl.DisablePrivacyAndRecommendedAsync(GamesInstalled);
                StatusMessage = result.Message;
            }
            else
            {
                // Turning OFF → restore.
                var confirm = MessageBox.Show(
                    "This will restore Microsoft data collection (telemetry services) and " +
                    "re-enable every Recommended background service to Manual start " +
                    "(they will start on demand, not at boot).\n\nProceed?",
                    "Restore Privacy & Background Services",
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
                var result = await _serviceControl.RestorePrivacyAndRecommendedAsync(GamesInstalled);
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
                _serviceControl.AreTelemetryServicesDisabled()
                && _serviceControl.AreAllRecommendedDisabled(GamesInstalled);
            _suppressPrivacyToggleSideEffect = false;
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
        StatusMessage = $"Restoring {featureName}... (this may take a few minutes)";
        try
        {
            StatusMessage = $"Running DISM to enable {featureName}...";
            var result = await _optFeatures.EnableFeatureAsync(featureName);
            StatusMessage = result.Success
                ? $"Restored: {featureName}. {(result.Message.Contains("3010") || result.Message.Contains("reboot") ? "Restart required." : "")}"
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
