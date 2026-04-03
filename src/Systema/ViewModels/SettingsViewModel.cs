// ════════════════════════════════════════════════════════════════════════════
// SettingsViewModel.cs  ·  User preference persistence for the Settings tab
// ════════════════════════════════════════════════════════════════════════════
//
// Reads and writes user preferences through SettingsService (HKCU\Software\Systema).
// Each property setter calls SaveSettings so changes are persisted immediately
// without an explicit Save button.
//
// Update behaviour
//   UpdateService drives the auto-update loop entirely in the background.
//   This ViewModel just subscribes to its events and reflects state in the UI.
//   The manual "Check for Updates" button calls UpdateService.CheckNowAsync().
//
// RELATED FILES
//   SettingsService.cs           — registry read/write for all user preferences
//   RestorePointService.cs       — used to open the Restore Point Manager window
//   UpdateService.cs             — fully-automatic silent updater
//   Views/SettingsView.xaml      — binds preference toggles and labels
// ════════════════════════════════════════════════════════════════════════════

using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Services;
using Systema.Views;

namespace Systema.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService     _settings;
    private readonly RestorePointService _restoreService;
    private readonly UpdateService       _updateService;
    private readonly WatchdogService     _watchdog;
    private static readonly LoggerService _log = LoggerService.Instance;

    // Event handlers stored for cleanup in Dispose()
    private readonly Action<string> _onStatusChanged;
    private readonly Action<bool>   _onUpdateAvailableChanged;
    private readonly Action<bool>   _onIsDownloadingChanged;
    private readonly Action<int>    _onDownloadProgressChanged;
    private readonly Action<bool>   _onIsReadyToInstallChanged;

    // ── Restore Point ─────────────────────────────────────────────────────────

    [ObservableProperty] private bool _skipRestorePoint;

    partial void OnSkipRestorePointChanged(bool value)
    {
        _settings.SkipRestorePoint = value;
        _log.Info("Settings", $"SkipRestorePoint set to {value}");
    }

    // ── Game Booster ──────────────────────────────────────────────────────────

    [ObservableProperty] private int _gameCheckIntervalMinutes;

    partial void OnGameCheckIntervalMinutesChanged(int value)
    {
        _settings.GameCheckIntervalMinutes = value;
        _log.Info("Settings", $"GameCheckIntervalMinutes set to {value}");
    }

    [ObservableProperty] private bool _xboxServicesUserOverride;

    partial void OnXboxServicesUserOverrideChanged(bool value)
    {
        _settings.XboxServicesUserOverride = value;
        _log.Info("Settings", $"XboxServicesUserOverride set to {value}");
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _startWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        _settings.StartWithWindows = value;
        _log.Info("Settings", $"StartWithWindows set to {value}");
    }

    [ObservableProperty] private bool _keepSystemaRunning;
    [ObservableProperty] private string _keepRunningStatus = string.Empty;

    partial void OnKeepSystemaRunningChanged(bool value)
    {
        _log.Info("Settings", $"KeepSystemaRunning set to {value}");
        try
        {
            if (value)
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                _watchdog.Enable(exePath);
                KeepRunningStatus = "Watchdog task active — Systema will restart if closed.";
            }
            else
            {
                _watchdog.Disable();
                KeepRunningStatus = "Watchdog removed — Systema can be closed normally.";
            }
            // Persist only after the operation succeeds
            _settings.KeepSystemaRunning = value;
        }
        catch (Exception ex)
        {
            KeepRunningStatus = $"Failed: {ex.Message}";
        }
    }

    // ── Export / Import ───────────────────────────────────────────────────────

    [ObservableProperty] private string _exportImportStatus = string.Empty;

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title        = "Export Systema Settings",
            Filter       = "JSON Settings File (*.json)|*.json",
            FileName     = $"Systema_Settings_{DateTime.Now:yyyy-MM-dd}",
            DefaultExt   = ".json",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = _settings.ExportToJson();
            await File.WriteAllTextAsync(dlg.FileName, json);
            ExportImportStatus = $"Settings exported to: {Path.GetFileName(dlg.FileName)}";
            _log.Info("Settings", $"Settings exported to {dlg.FileName}");
        }
        catch (Exception ex)
        {
            ExportImportStatus = $"Export failed: {ex.Message}";
            _log.Error("Settings", "Export failed", ex);
        }
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title      = "Import Systema Settings",
            Filter     = "JSON Settings File (*.json)|*.json",
            DefaultExt = ".json",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = await File.ReadAllTextAsync(dlg.FileName);
            bool ok  = _settings.ImportFromJson(json);
            ExportImportStatus = ok
                ? "Settings imported successfully. Restart Systema to apply all changes."
                : "Import failed — the file may be corrupt or from an incompatible version.";
            _log.Info("Settings", ok ? "Settings imported OK" : "Settings import failed (bad file)");
        }
        catch (Exception ex)
        {
            ExportImportStatus = $"Import failed: {ex.Message}";
            _log.Error("Settings", "Import failed", ex);
        }
    }

    // ── Updates ───────────────────────────────────────────────────────────────
    // UpdateService owns the auto-update loop. This VM reflects its state.

    [ObservableProperty] private bool   _autoUpdateEnabled;

    partial void OnAutoUpdateEnabledChanged(bool value)
    {
        _settings.AutoUpdateEnabled = value;
        _log.Info("Settings", $"AutoUpdateEnabled set to {value}");
    }

    [ObservableProperty] private string _updateStatus     = "Checking for updates...";
    [ObservableProperty] private bool   _updateAvailable;
    [ObservableProperty] private bool   _isCheckingUpdate;
    [ObservableProperty] private bool   _isDownloadingUpdate;
    [ObservableProperty] private int    _downloadProgress;
    [ObservableProperty] private bool   _isReadyToInstall;

    // Derived helpers used by button visibility / IsEnabled bindings
    public bool IsNotCheckingUpdate  => !IsCheckingUpdate;
    public bool IsNotDownloading     => !IsDownloadingUpdate;
    public bool CanCheckNow          => !IsCheckingUpdate && !IsDownloadingUpdate;

    partial void OnIsCheckingUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotCheckingUpdate));
        OnPropertyChanged(nameof(CanCheckNow));
    }

    partial void OnIsDownloadingUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotDownloading));
        OnPropertyChanged(nameof(CanCheckNow));
        InstallNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsReadyToInstallChanged(bool value) =>
        InstallNowCommand.NotifyCanExecuteChanged();

    public static string CurrentVersionString => UpdateService.GetCurrentVersionString();

    /// <summary>
    /// Manual "Check for Updates" — triggers an immediate check, bypassing the schedule.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        try   { await _updateService.CheckNowAsync(); }
        catch (Exception ex) { _log.Warn("SettingsViewModel", $"CheckForUpdates failed: {ex.Message}"); }
        finally { IsCheckingUpdate = false; }
    }

    /// <summary>
    /// Manual "Install Now" — bypasses the CPU idle gate and installs immediately.
    /// Only available when the installer is already downloaded.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallNow))]
    private async Task InstallNowAsync()
    {
        try { await _updateService.InstallNowAsync(); }
        catch (Exception ex) { _log.Warn("SettingsViewModel", $"InstallNow failed: {ex.Message}"); }
    }

    private bool CanInstallNow() => IsReadyToInstall && !IsDownloadingUpdate;

    // ── Restore Point Manager ─────────────────────────────────────────────────

    [RelayCommand]
    private void ManageRestorePoints()
    {
        _log.Info("Settings", "User opened Restore Point Manager");
        RestorePointManagerWindow.Show(_restoreService, Application.Current.MainWindow);
    }

    // ── Diagnostic Report ─────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenDiagnostics()
    {
        _log.Info("Settings", "User opened Diagnostic Report window");
        DiagnosticsReportWindow.Show();
    }

    // ── Reset All Settings ───────────────────────────────────────────────────

    [RelayCommand]
    private void ResetAllSettings()
    {
        var result = MessageBox.Show(
            "This will reset ALL Systema settings to their defaults and restart the app.\n\nAre you sure?",
            "Reset All Settings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            // Delete all Systema registry keys (includes TaskSleep sub-key)
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Systema", throwOnMissingSubKey: false); }
            catch (Exception ex) { _log.Warn("Settings", $"Registry delete failed: {ex.Message}"); }

            // Delete Task Sleep whitelist JSON
            var rulesPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Systema", "tasksleep_rules.json");
            if (File.Exists(rulesPath))
                try { File.Delete(rulesPath); } catch { }

            _log.Info("Settings", "All settings reset to defaults — restarting");

            // Restart the app
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _log.Error("Settings", "Reset failed", ex);
            MessageBox.Show($"Reset failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsViewModel(
        SettingsService     settings,
        RestorePointService restoreService,
        UpdateService       updateService,
        WatchdogService     watchdog)
    {
        _settings       = settings;
        _restoreService = restoreService;
        _updateService  = updateService;
        _watchdog       = watchdog;

        // Load persisted values without triggering OnChanged (avoids a redundant write)
        _skipRestorePoint         = _settings.SkipRestorePoint;
        _gameCheckIntervalMinutes = _settings.GameCheckIntervalMinutes;
        _xboxServicesUserOverride = _settings.XboxServicesUserOverride;
        _startWithWindows         = _settings.StartWithWindows;
        _autoUpdateEnabled        = _settings.AutoUpdateEnabled;
        _keepSystemaRunning       = _watchdog.IsEnabled; // read live from Task Scheduler

        // Subscribe to UpdateService events — must dispatch to UI thread since
        // the auto-update loop runs on a background thread.
        // Handlers stored as fields so Dispose() can unsubscribe them.
        _onStatusChanged = status =>
            Application.Current?.Dispatcher.Invoke(() => UpdateStatus = status);
        _onUpdateAvailableChanged = available =>
            Application.Current?.Dispatcher.Invoke(() => UpdateAvailable = available);
        _onIsDownloadingChanged = downloading =>
            Application.Current?.Dispatcher.Invoke(() => IsDownloadingUpdate = downloading);
        _onDownloadProgressChanged = pct =>
            Application.Current?.Dispatcher.Invoke(() => DownloadProgress = pct);
        _onIsReadyToInstallChanged = ready =>
            Application.Current?.Dispatcher.Invoke(() => IsReadyToInstall = ready);

        _updateService.StatusChanged           += _onStatusChanged;
        _updateService.UpdateAvailableChanged  += _onUpdateAvailableChanged;
        _updateService.IsDownloadingChanged    += _onIsDownloadingChanged;
        _updateService.DownloadProgressChanged += _onDownloadProgressChanged;
        _updateService.IsReadyToInstallChanged += _onIsReadyToInstallChanged;
    }

    public void Dispose()
    {
        _updateService.StatusChanged           -= _onStatusChanged;
        _updateService.UpdateAvailableChanged  -= _onUpdateAvailableChanged;
        _updateService.IsDownloadingChanged    -= _onIsDownloadingChanged;
        _updateService.DownloadProgressChanged -= _onDownloadProgressChanged;
        _updateService.IsReadyToInstallChanged -= _onIsReadyToInstallChanged;
    }
}
