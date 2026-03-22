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

public partial class ServicesViewModel : ObservableObject, IAutoRefreshable
{
    private readonly ServiceControlService   _serviceControl;
    private readonly OptionalFeaturesService _optFeatures;
    private readonly RestorePointService     _restoreService;
    private readonly SettingsService         _settings;
    private static readonly LoggerService    _log = LoggerService.Instance;
    private int _isRefreshing;
    private bool _hasLoadedFeaturesOnce;

    [ObservableProperty] private ObservableCollection<ServiceInfo> _services = new();
    [ObservableProperty] private ObservableCollection<OptionalFeatureInfo> _optionalFeatures = new();
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private bool   _isFeatureLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool   _telemetryKillerActive;

    // ── Expander state ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _showWindowsFeatures;
    [RelayCommand] private void ToggleWindowsFeatures() => ShowWindowsFeatures = !ShowWindowsFeatures;

    public bool GamesInstalled { get; set; }

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

        _telemetryKillerActive = _serviceControl.AreTelemetryServicesDisabled();
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
            TelemetryKillerActive = _serviceControl.AreTelemetryServicesDisabled();

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

    [RelayCommand]
    private async Task DisableAllRecommendedAsync()
    {
        var recommended = Services.Where(s => s.IsRecommended && s.ColorState != ServiceColorState.Red).ToList();
        if (recommended.Count == 0)
        {
            StatusMessage = "All recommended services are already disabled.";
            return;
        }

        // Confirm before batch-disabling
        var names = string.Join("\n  • ", recommended.Select(s => s.DisplayName));
        var confirm = MessageBox.Show(
            $"This will disable {recommended.Count} background service{(recommended.Count == 1 ? "" : "s")} that are safe to turn off for most users:\n\n  • {names}\n\nYou can re-enable any service here at any time.\n\nProceed?",
            "Disable Recommended Services",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.OK);

        if (confirm != MessageBoxResult.OK) return;

        IsLoading = true;
        int disabled = 0;
        try
        {
            foreach (var svc in recommended)
            {
                StatusMessage = $"Disabling {svc.DisplayName}... ({disabled + 1}/{recommended.Count})";
                try
                {
                    await _serviceControl.DisableServiceAsync(svc.ServiceName);
                    disabled++;
                }
                catch (Exception ex)
                {
                    _log.Warn("ServicesViewModel", $"Could not disable {svc.ServiceName}: {ex.Message}");
                }
            }
            StatusMessage = $"Disabled {disabled}/{recommended.Count} recommended services.";
            await DoRefreshAsync();
        }
        finally
        {
            IsLoading = false;
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

            // Refresh the features list
            _hasLoadedFeaturesOnce = false;
            await LoadFeaturesAsync();
            _hasLoadedFeaturesOnce = true;
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

            _hasLoadedFeaturesOnce = false;
            await LoadFeaturesAsync();
            _hasLoadedFeaturesOnce = true;
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", $"Failed to enable feature {featureName}", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsFeatureLoading = false; }
    }

    [RelayCommand]
    private async Task ToggleTelemetryKillerAsync()
    {
        IsLoading = true;
        try
        {
            TweakResult result;
            if (TelemetryKillerActive)
            {
                StatusMessage = "Restoring telemetry services...";
                result = await _serviceControl.RestoreTelemetryServicesAsync();
            }
            else
            {
                StatusMessage = "Killing telemetry services and tasks...";
                result = await _serviceControl.DisableAllTelemetryServicesAsync();
            }

            StatusMessage = result.Message;
            TelemetryKillerActive = _serviceControl.AreTelemetryServicesDisabled();
            await DoRefreshAsync();
        }
        catch (Exception ex)
        {
            _log.Error("ServicesViewModel", "Telemetry Killer toggle failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsLoading = false; }
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
