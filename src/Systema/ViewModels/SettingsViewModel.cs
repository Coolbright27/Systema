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
    private readonly GameBoosterService  _gameBooster;
    private static readonly LoggerService _log = LoggerService.Instance;

    /// <summary>True when Auto-Pilot Mode is on — Start with Windows toggle is grayed out.</summary>
    public bool IsAutoPilotActive => _settings.AutoPilotModeEnabled;

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
            KeepRunningStatus = $"Failed to {(value ? "enable" : "disable")} watchdog: {ex.Message}";
            // Roll the toggle back so the UI reflects the actual state.
            _keepSystemaRunning = !value;
            OnPropertyChanged(nameof(KeepSystemaRunning));
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
    //
    // Comprehensive reset that unwinds EVERYTHING Systema has written:
    //
    //   SYSTEMA STATE
    //     • HKCU\Software\Systema (all user preferences + TaskSleep sub-key)
    //     • %APPDATA%\Systema\tasksleep_rules.json
    //     • %LOCALAPPDATA%\Systema\boost_state.json (crash recovery)
    //     • %LOCALAPPDATA%\Systema\crash_*.txt
    //     • %LOCALAPPDATA%\Systema\Logs\*.log (best effort — current session file stays locked)
    //     • "Systema" scheduled task (StartWithWindows)
    //     • "Systema Watchdog" scheduled task (KeepSystemaRunning)
    //
    //   WINDOWS TWEAKS (reverted to defaults)
    //     • Any active Game Boost is cleanly deactivated first, which restores:
    //         services stopped, Nagle, NIC power, DNS, power plan, Game Bar, MMCSS
    //         Games subkey, Wi-Fi/Bluetooth radios, sleep prevention
    //     • VSync-critical MMCSS SystemResponsiveness — forced back to 20 if it was 0
    //
    //   Reset order matters: deactivate boost BEFORE deleting state files, so the
    //   in-memory snapshot of pre-boost values is still available for restore.

    [RelayCommand]
    private async Task ResetAllSettingsAsync()
    {
        const string dialogMsg =
            "This will FULLY reset Systema:\n\n" +
            "• All user preferences and Task Sleep rules\n" +
            "• Startup task and Watchdog task\n" +
            "• Activity logs and crash reports\n" +
            "• Any active Game Boost (services & tweaks reverted)\n" +
            "• VSync-critical registry values restored to defaults\n\n" +
            "The app will restart when done.\n\nContinue?";

        var result = MessageBox.Show(dialogMsg, "Reset All Settings",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var steps = new List<string>();
        _log.Info("Settings", "Reset All Settings — starting full cleanup");

        // ── 1) Deactivate active Game Boost (uses in-memory snapshot to restore Windows tweaks) ──
        try
        {
            await _gameBooster.DisableManualBoostAsync();
            steps.Add("Game Boost deactivated");
        }
        catch (Exception ex) { _log.Warn("Settings", $"Reset: boost deactivate failed — {ex.Message}"); }

        // ── 2) Force VSync-critical registry repair (idempotent; also runs on next startup) ──
        try
        {
            _gameBooster.RepairRegistryNow();
            steps.Add("MMCSS SystemResponsiveness normalized");
        }
        catch (Exception ex) { _log.Warn("Settings", $"Reset: MMCSS repair failed — {ex.Message}"); }

        // ── 3) Remove scheduled tasks ──
        try { _watchdog.Disable(); steps.Add("Watchdog task removed"); }
        catch (Exception ex) { _log.Warn("Settings", $"Reset: watchdog disable failed — {ex.Message}"); }

        try
        {
            _settings.StartWithWindows = false;  // deletes "Systema" task AND HKCU Run fallback
            steps.Add("Startup task removed");
        }
        catch (Exception ex) { _log.Warn("Settings", $"Reset: startup task delete failed — {ex.Message}"); }

        // ── 4) Delete AppData files (best effort — active log file stays locked) ──
        TryDeleteAppDataState(steps);

        // ── 5) Delete HKCU\Software\Systema (all user preferences + TaskSleep sub-key) ──
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Systema", throwOnMissingSubKey: false);
            steps.Add("Registry cleared");
        }
        catch (Exception ex) { _log.Warn("Settings", $"Reset: registry delete failed — {ex.Message}"); }

        _log.Info("Settings", $"Reset complete — {steps.Count} step(s): {string.Join(", ", steps)}");

        // ── 6) Restart (next startup re-runs RepairVSyncCriticalRegistryValues as belt-and-braces) ──
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = exePath,
                    UseShellExecute = true,
                    Verb            = "runas"
                });
            }
        }
        catch (Exception ex) { _log.Warn("Settings", $"Reset: restart launch failed — {ex.Message}"); }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// Best-effort deletion of Systema state files under %APPDATA% and %LOCALAPPDATA%.
    /// The currently-open session log will remain locked; restart rotates it.
    /// </summary>
    private static void TryDeleteAppDataState(List<string> steps)
    {
        // %APPDATA%\Systema\tasksleep_rules.json
        try
        {
            var rulesFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Systema", "tasksleep_rules.json");
            if (File.Exists(rulesFile))
            {
                File.Delete(rulesFile);
                steps.Add("Task Sleep whitelist deleted");
            }
        }
        catch (Exception ex) { _log.Warn("Settings", $"Reset: tasksleep_rules.json — {ex.Message}"); }

        // %LOCALAPPDATA%\Systema\* — boost state, crash reports, log archive
        try
        {
            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Systema");
            if (!Directory.Exists(localDir)) return;

            var boostFile = Path.Combine(localDir, "boost_state.json");
            if (File.Exists(boostFile))
                try { File.Delete(boostFile); steps.Add("Crash-recovery state cleared"); }
                catch (Exception ex) { _log.Warn("Settings", $"Reset: boost_state.json — {ex.Message}"); }

            // Crash reports
            foreach (var f in Directory.GetFiles(localDir, "crash_*.txt"))
                try { File.Delete(f); } catch { /* individual crash dump — best effort */ }

            // Rotated log files (current session log is locked by the active LoggerService)
            var logsDir = Path.Combine(localDir, "Logs");
            if (Directory.Exists(logsDir))
            {
                int deleted = 0;
                foreach (var f in Directory.GetFiles(logsDir, "*.log"))
                    try { File.Delete(f); deleted++; } catch { /* active log stays locked */ }
                if (deleted > 0) steps.Add($"{deleted} archived log file(s) deleted");
            }
        }
        catch (Exception ex) { _log.Warn("Settings", $"Reset: LOCALAPPDATA cleanup — {ex.Message}"); }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsViewModel(
        SettingsService     settings,
        RestorePointService restoreService,
        UpdateService       updateService,
        WatchdogService     watchdog,
        GameBoosterService  gameBooster)
    {
        _settings       = settings;
        _restoreService = restoreService;
        _updateService  = updateService;
        _watchdog       = watchdog;
        _gameBooster    = gameBooster;

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
        SettingsService.AutoPilotModeChanged   += OnAutoPilotModeChanged;
    }

    private void OnAutoPilotModeChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            () => OnPropertyChanged(nameof(IsAutoPilotActive)));

    public void Dispose()
    {
        _updateService.StatusChanged             -= _onStatusChanged;
        _updateService.UpdateAvailableChanged    -= _onUpdateAvailableChanged;
        _updateService.IsDownloadingChanged      -= _onIsDownloadingChanged;
        _updateService.DownloadProgressChanged   -= _onDownloadProgressChanged;
        _updateService.IsReadyToInstallChanged   -= _onIsReadyToInstallChanged;
        SettingsService.AutoPilotModeChanged     -= OnAutoPilotModeChanged;
    }
}
