// ════════════════════════════════════════════════════════════════════════════
// ToolsViewModel.cs  ·  Miscellaneous advanced system tweaks and utilities
// ════════════════════════════════════════════════════════════════════════════
//
// Aggregates one-shot tweak commands that don't belong to other tabs: Realtek
// Audio Manager removal, CPU core parking toggle, DNS flush, Windows Update
// insider/preview block, restore point creation, and Process Lasso ProBalance
// settings. Each command delegates to its respective service.
//
// RELATED FILES
//   RealtekCleanerService.cs       — wmic silent uninstall of Realtek Audio Manager
//   CoreParkingService.cs          — writes CPMINCORES and creates startup task
//   DnsService.cs                  — DNS flush helper
//   WindowsUpdateTweaksService.cs  — Group Policy registry blocks for insider builds
//   ProcessLassoService.cs         — reads/writes ProBalance registry settings
//   RestorePointService.cs         — WMI restore point creation
//   Views/ToolsView.xaml           — binds all tweak buttons
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

public partial class ToolsViewModel : ObservableObject, IAutoRefreshable
{
    // ── Services ──────────────────────────────────────────────────────────────
    private readonly RealtekCleanerService        _realtek;
    private readonly CoreParkingService           _coreParking;
    private readonly RestorePointService          _restore;
    private readonly SettingsService              _settings;
    private readonly DnsService                   _dnsService;
    private readonly WindowsUpdateTweaksService   _wuTweaks;

    private static readonly LoggerService _log = LoggerService.Instance;

    // Guard to prevent OnXxxChanged callbacks from triggering commands during load
    private bool _loading;

    // Refresh concurrency guard
    private int _isRefreshing;

    // ── General state ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusMessage = string.Empty;

    // ── DNS Switcher ──────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<DnsProfile> _dnsProfiles = new();
    [ObservableProperty] private DnsProfile? _selectedDnsProfile;
    [ObservableProperty] private string _currentDns = string.Empty;
    [ObservableProperty] private bool   _isDnsLoading;

    // ── Realtek Cleaner ───────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<RealtekEntry> _realtekEntries = new();
    [ObservableProperty] private bool   _isRealtekLoading;
    [ObservableProperty] private string _realtekStatusMessage = string.Empty;
    [ObservableProperty] private bool   _realtekScanned;
    [ObservableProperty] private bool   _hasRealtekEntries;
    [ObservableProperty] private bool   _hasRealtekHardware;

    // ── Core Parking ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _coreParkingEnforced;
    [ObservableProperty] private bool _isCoreParkingLoading;

    // ── Block Preview Updates ─────────────────────────────────────────────────
    [ObservableProperty] private bool _blockPreviewUpdates;
    [ObservableProperty] private bool _isPreviewUpdatesLoading;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ToolsViewModel(
        RealtekCleanerService        realtek,
        CoreParkingService           coreParking,
        RestorePointService          restore,
        SettingsService              settings,
        DnsService                   dnsService,
        WindowsUpdateTweaksService   wuTweaks)
    {
        _realtek     = realtek;
        _coreParking = coreParking;
        _restore     = restore;
        _settings    = settings;
        _dnsService  = dnsService;
        _wuTweaks    = wuTweaks;

        // Populate DNS profiles
        foreach (var p in DnsService.Profiles)
            DnsProfiles.Add(p);
        SelectedDnsProfile = DnsProfiles.FirstOrDefault();
    }

    // ── IAutoRefreshable ──────────────────────────────────────────────────────

    public Task RefreshAsync() => DoRefreshAsync();

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async Task DoRefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0) return;
        try
        {
            bool parkingOn       = await Task.Run(() => _coreParking.IsCoreParkingEnforced());
            bool hasRealtek      = await Task.Run(() => _realtek.HasRealtekHardware());
            string currentDns    = await Task.Run(() => _dnsService.GetCurrentDns());
            bool savedParkingPref = _settings.CoreParkingEnabled;
            bool previewBlocked  = await Task.Run(() => _wuTweaks.IsPreviewUpdatesBlocked());

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _loading = true;
                try
                {
                    HasRealtekHardware  = hasRealtek;
                    CurrentDns          = currentDns;

                    // Core parking: reflect saved preference OR actual system state.
                    CoreParkingEnforced = savedParkingPref || parkingOn;

                    // Preview updates: reflect actual registry state (source of truth).
                    BlockPreviewUpdates = previewBlocked;
                    // Keep persisted pref in sync with actual state
                    if (_settings.BlockPreviewUpdatesEnabled != previewBlocked)
                        _settings.BlockPreviewUpdatesEnabled = previewBlocked;

                    // Scan Realtek entries only if we haven't scanned yet in this session
                    // and Realtek hardware is detected
                    if (!RealtekScanned && hasRealtek)
                        _ = ScanRealtekAsync();
                }
                finally
                {
                    _loading = false;
                }
            });

            // If the user previously enabled core parking but the scheduled task is gone
            // (OEM tool or Windows removed it), re-create it silently.
            if (savedParkingPref && !parkingOn)
            {
                _log.Info("ToolsViewModel", "Core parking was enabled but scheduled task is missing — re-enforcing.");
                await _coreParking.EnableForcedCoreParking();
            }
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", "DoRefreshAsync failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    // ── DNS commands ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ApplyDnsAsync()
    {
        if (SelectedDnsProfile == null) return;
        IsDnsLoading = true;
        StatusMessage = $"Applying DNS: {SelectedDnsProfile.Name}...";
        try
        {
            var result = await _dnsService.ApplyProfileAsync(SelectedDnsProfile);
            StatusMessage = result.Message;
            CurrentDns = await Task.Run(() => _dnsService.GetCurrentDns());
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", "ApplyDns failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsDnsLoading = false; }
    }

    [RelayCommand]
    private async Task ResetDnsAsync()
    {
        IsDnsLoading = true;
        StatusMessage = "Resetting DNS to System Default (DHCP)...";
        try
        {
            var dhcp = DnsProfiles.FirstOrDefault(p => string.IsNullOrEmpty(p.Primary));
            if (dhcp != null)
            {
                SelectedDnsProfile = dhcp;
                var result = await _dnsService.ApplyProfileAsync(dhcp);
                StatusMessage = result.Message;
                CurrentDns = await Task.Run(() => _dnsService.GetCurrentDns());
            }
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", "ResetDns failed", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsDnsLoading = false; }
    }

    // ── Realtek commands ──────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ScanRealtekAsync()
    {
        IsRealtekLoading     = true;
        RealtekStatusMessage = "Scanning for Realtek bloatware...";
        try
        {
            var entries = await Task.Run(() => _realtek.GetRealtekBloatEntries());
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                RealtekEntries.Clear();
                foreach (var e in entries)
                    RealtekEntries.Add(e);

                HasRealtekEntries    = entries.Count > 0;
                RealtekScanned       = true;
                RealtekStatusMessage = entries.Count > 0
                    ? $"Found {entries.Count} Realtek bloatware item(s)."
                    : "No Realtek bloatware found.";
            });
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", "ScanRealtek failed", ex);
            RealtekStatusMessage = $"Error: {ex.Message}";
        }
        finally { IsRealtekLoading = false; }
    }

    [RelayCommand]
    private async Task RemoveRealtekAsync()
    {
        if (RealtekEntries.Count == 0)
        {
            RealtekStatusMessage = "Nothing to remove — run Scan first.";
            return;
        }

        var confirm = MessageBox.Show(
            $"This will silently uninstall {RealtekEntries.Count} Realtek item(s):\n\n" +
            string.Join("\n", RealtekEntries.Select(e => $"  \u2022 {e.DisplayName}")) +
            "\n\nThis action cannot be undone without manually reinstalling them. " +
            "The core audio driver will NOT be affected.\n\nContinue?",
            "Remove Realtek Bloatware",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        IsRealtekLoading     = true;
        RealtekStatusMessage = "Uninstalling Realtek bloatware...";
        try
        {
            var result = await _realtek.RemoveRealtekBloatAsync();
            RealtekStatusMessage = result.Message;
            StatusMessage        = result.Message;

            if (result.Success)
            {
                await ScanRealtekAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", "RemoveRealtek failed", ex);
            RealtekStatusMessage = $"Error: {ex.Message}";
        }
        finally { IsRealtekLoading = false; }
    }

    // ── Core Parking callbacks ────────────────────────────────────────────────

    partial void OnCoreParkingEnforcedChanged(bool value)
    {
        if (_loading) return;
        _ = ExecuteCoreParkingToggleAsync(value);
    }

    private async Task ExecuteCoreParkingToggleAsync(bool enable)
    {
        IsCoreParkingLoading = true;
        StatusMessage        = enable ? "Enabling forced core parking..." : "Disabling forced core parking...";
        try
        {
            TweakResult result = enable
                ? await _coreParking.EnableForcedCoreParking()
                : await _coreParking.DisableForcedCoreParking();

            StatusMessage = result.Message;

            if (result.Success)
            {
                _settings.CoreParkingEnabled = enable;
            }
            else
            {
                _loading = true;
                CoreParkingEnforced = !enable;
                _loading = false;
            }
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", $"CoreParking toggle ({enable}) failed", ex);
            StatusMessage = $"Error: {ex.Message}";
            _loading = true;
            CoreParkingEnforced = !enable;
            _loading = false;
        }
        finally { IsCoreParkingLoading = false; }
    }

    // ── Block Preview Updates callbacks ───────────────────────────────────────

    partial void OnBlockPreviewUpdatesChanged(bool value)
    {
        if (_loading) return;
        _ = ExecutePreviewUpdatesToggleAsync(value);
    }

    private async Task ExecutePreviewUpdatesToggleAsync(bool block)
    {
        IsPreviewUpdatesLoading = true;
        StatusMessage = block
            ? "Blocking Windows preview updates..."
            : "Restoring Windows preview update defaults...";
        try
        {
            TweakResult result = block
                ? await _wuTweaks.BlockPreviewUpdatesAsync()
                : await _wuTweaks.AllowPreviewUpdatesAsync();

            StatusMessage = result.Message;

            if (result.Success)
            {
                _settings.BlockPreviewUpdatesEnabled = block;
            }
            else
            {
                // Revert toggle on failure
                _loading = true;
                BlockPreviewUpdates = !block;
                _loading = false;
            }
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", $"BlockPreviewUpdates toggle ({block}) failed", ex);
            StatusMessage = $"Error: {ex.Message}";
            _loading = true;
            BlockPreviewUpdates = !block;
            _loading = false;
        }
        finally { IsPreviewUpdatesLoading = false; }
    }
}
