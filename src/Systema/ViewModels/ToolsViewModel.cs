// ════════════════════════════════════════════════════════════════════════════
// ToolsViewModel.cs  ·  Miscellaneous advanced system tweaks and utilities
// ════════════════════════════════════════════════════════════════════════════
//
// Aggregates one-shot tweak commands that don't belong to other tabs: Realtek
// Audio Manager removal, CPU core parking toggle, DNS flush, Windows Update
// insider/preview block, Fast Startup disable, NTFS last-access timestamp
// disable, and restore point creation. Each command delegates to its service.
//
// RELATED FILES
//   RealtekCleanerService.cs       — wmic silent uninstall of Realtek Audio Manager
//   CoreParkingService.cs          — writes CPMINCORES and creates startup task
//   DnsService.cs                  — DNS flush helper
//   WindowsUpdateTweaksService.cs  — Group Policy registry blocks for insider builds
//   SystemStabilityService.cs      — Fast Startup and NTFS last-access tweaks
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

public partial class ToolsViewModel : ObservableObject, IAutoRefreshable, IDisposable
{
    // ── Services ──────────────────────────────────────────────────────────────
    private readonly RealtekCleanerService        _realtek;
    private readonly CoreParkingService           _coreParking;
    private readonly RestorePointService          _restore;
    private readonly SettingsService              _settings;
    private readonly DnsService                   _dnsService;
    private readonly WindowsUpdateTweaksService   _wuTweaks;
    private readonly SystemStabilityService       _stability;
    private readonly Win11CleanupService          _cleanup;

    private static readonly LoggerService _log = LoggerService.Instance;

    /// <summary>
    /// True when Auto-Pilot Mode is active — XAML binds this to IsEnabled (inverted)
    /// so Auto-Pilot-managed controls are grayed out when the mode is on.
    /// </summary>
    public bool IsAutoPilotActive => _settings.AutoPilotModeEnabled;

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

    // ── Disable Suggestions / Nags (on by default, reinforced) ────────────────
    [ObservableProperty] private bool _disableSuggestions;
    [ObservableProperty] private bool _isSuggestionsLoading;

    // ── Disable Bing/Web Search in Start (off by default) ─────────────────────
    [ObservableProperty] private bool _disableWebSearch;
    [ObservableProperty] private bool _isWebSearchLoading;

    // ── NTFS Last-Access Timestamps ───────────────────────────────────────────
    [ObservableProperty] private bool _ntfsLastAccessDisabled;
    [ObservableProperty] private bool _isNtfsLastAccessLoading;

    // Responsiveness tweaks (Foreground Priority Boost + Instant App Focus) moved
    // to the Systema Engine tab — see TaskSleepViewModel.

    // ── Laptop detection ──────────────────────────────────────────────────────
    /// <summary>
    /// True when the system has a battery. Battery-only cards are hidden on desktops.
    /// </summary>
    [ObservableProperty] private bool _hasBattery;

    // ── Sleep → Hibernate (battery) ───────────────────────────────────────────
    [ObservableProperty] private bool _sleepToHibernateEnabled;
    [ObservableProperty] private bool _isSleepToHibernateLoading;
    [ObservableProperty] private int  _sleepToHibernateMinutes = 30;

    // ── Sleep → Hibernate (AC / plugged-in) ───────────────────────────────────
    [ObservableProperty] private bool _sleepToHibernateAcEnabled;
    [ObservableProperty] private bool _isSleepToHibernateAcLoading;
    [ObservableProperty] private int  _sleepToHibernateAcMinutes = 30;

    /// <summary>Available timeout options shown in both Sleep → Hibernate ComboBoxes (minutes).</summary>
    public IReadOnlyList<int> SleepToHibernateOptions { get; } =
        new[] { 5, 15, 30, 45, 60, 120 };

    // ── Constructor ───────────────────────────────────────────────────────────

    public ToolsViewModel(
        RealtekCleanerService        realtek,
        CoreParkingService           coreParking,
        RestorePointService          restore,
        SettingsService              settings,
        DnsService                   dnsService,
        WindowsUpdateTweaksService   wuTweaks,
        SystemStabilityService       stability,
        Win11CleanupService          cleanup)
    {
        _realtek     = realtek;
        _coreParking = coreParking;
        _restore     = restore;
        _settings    = settings;
        _dnsService  = dnsService;
        _wuTweaks    = wuTweaks;
        _stability   = stability;
        _cleanup     = cleanup;

        // Populate DNS profiles
        foreach (var p in DnsService.Profiles)
            DnsProfiles.Add(p);
        SelectedDnsProfile = DnsProfiles.FirstOrDefault();

        SettingsService.AutoPilotModeChanged += OnAutoPilotModeChanged;
    }

    private void OnAutoPilotModeChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            () => OnPropertyChanged(nameof(IsAutoPilotActive)));

    public void Dispose() => SettingsService.AutoPilotModeChanged -= OnAutoPilotModeChanged;

    // ── IAutoRefreshable ──────────────────────────────────────────────────────

    public Task RefreshAsync() => DoRefreshAsync();

    // ── Refresh ───────────────────────────────────────────────────────────────

    private async Task DoRefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0) return;
        try
        {
            bool parkingOn         = await Task.Run(() => _coreParking.IsCoreParkingEnforced());
            bool hasRealtek        = await Task.Run(() => _realtek.HasRealtekHardware());
            string currentDns      = await Task.Run(() => _dnsService.GetCurrentDns());
            bool savedParkingPref  = _settings.CoreParkingEnabled;
            bool previewBlocked    = await Task.Run(() => _wuTweaks.IsPreviewUpdatesBlocked());

            // Auto-heal: if the saved preference says "ON" but the UX opt-in value
            // got cleared by Windows (or wiped by ScrubLegacyWufbPolicyKeys on app
            // startup), silently re-write it so the user's choice is honoured again.
            if (!previewBlocked && _settings.BlockPreviewUpdatesEnabled && !IsPreviewUpdatesLoading)
            {
                _log.Info("ToolsViewModel", "Preview block cleared by Windows — re-applying (auto-heal)");
                var heal = await _wuTweaks.BlockPreviewUpdatesAsync();
                previewBlocked = heal.Success;
            }

            bool ntfsLastAccessOff = await Task.Run(() => _stability.IsNtfsLastAccessDisabled());

            // Disable Suggestions (#3): on by default. Auto-heal — if the pref is ON
            // but Windows (or a feature update) re-enabled the nags, silently re-apply
            // so they "never come back". This mirrors the preview-block heal above.
            bool suggestionsOff = await Task.Run(() => _cleanup.IsConsumerContentDisabled());
            if (!suggestionsOff && _settings.DisableSuggestionsEnabled && !IsSuggestionsLoading)
            {
                _log.Info("ToolsViewModel", "Suggestions re-enabled by Windows — re-applying (auto-heal)");
                var heal = await _cleanup.DisableConsumerContentAsync();
                suggestionsOff = heal.Success;
            }

            // Disable Web Search (#4): off by default — reflect actual state, reinforce when on.
            bool webSearchOff = await Task.Run(() => _cleanup.IsWebSearchDisabled());
            if (!webSearchOff && _settings.DisableWebSearchEnabled && !IsWebSearchLoading)
            {
                var heal = await _cleanup.DisableWebSearchAsync();
                webSearchOff = heal.Success;
            }

            bool hasBattery        = await Task.Run(() => _stability.HasBattery());

            // Sleep → Hibernate (battery): reflect actual powercfg state, and auto-heal — if the saved
            // preference is ON but Windows or the power plan wiped it, silently re-apply so it sticks.
            bool sleepHibernateOn  = await Task.Run(() => _stability.IsSleepToHibernateEnabled());
            if (!sleepHibernateOn && _settings.SleepToHibernateEnabled && !IsSleepToHibernateLoading)
            {
                _log.Info("ToolsViewModel", "Sleep → Hibernate (battery) drifted off — re-applying (auto-heal)");
                var heal = await _stability.EnableSleepToHibernateAsync(_settings.SleepToHibernateMinutes);
                sleepHibernateOn = heal.Success;
            }
            int  sleepHibMinutes   = sleepHibernateOn
                ? await Task.Run(() => _stability.GetSleepToHibernateMinutes())
                : _settings.SleepToHibernateMinutes;

            bool sleepHibAcOn      = await Task.Run(() => _stability.IsSleepToHibernateAcEnabled());
            if (!sleepHibAcOn && _settings.SleepToHibernateAcEnabled && !IsSleepToHibernateAcLoading)
            {
                _log.Info("ToolsViewModel", "Sleep → Hibernate (AC) drifted off — re-applying (auto-heal)");
                var heal = await _stability.EnableSleepToHibernateAcAsync(_settings.SleepToHibernateAcMinutes);
                sleepHibAcOn = heal.Success;
            }
            int  sleepHibAcMinutes = sleepHibAcOn
                ? await Task.Run(() => _stability.GetSleepToHibernateAcMinutes())
                : _settings.SleepToHibernateAcMinutes;

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _loading = true;
                try
                {
                    HasRealtekHardware  = hasRealtek;
                    CurrentDns          = currentDns;
                    HasBattery          = hasBattery;

                    // Core parking: reflect saved preference OR actual system state.
                    CoreParkingEnforced = savedParkingPref || parkingOn;

                    // Preview updates: reflect actual registry state (source of truth).
                    BlockPreviewUpdates = previewBlocked;
                    // Keep persisted pref in sync with actual state
                    if (_settings.BlockPreviewUpdatesEnabled != previewBlocked)
                        _settings.BlockPreviewUpdatesEnabled = previewBlocked;

                    // NTFS last-access: reflect actual fsutil state.
                    NtfsLastAccessDisabled = ntfsLastAccessOff;

                    // Suggestions / web search: reflect actual registry state, keep prefs in sync.
                    DisableSuggestions = suggestionsOff;
                    if (_settings.DisableSuggestionsEnabled != suggestionsOff)
                        _settings.DisableSuggestionsEnabled = suggestionsOff;
                    DisableWebSearch = webSearchOff;
                    if (_settings.DisableWebSearchEnabled != webSearchOff)
                        _settings.DisableWebSearchEnabled = webSearchOff;

                    // Sleep → Hibernate (battery): reflect actual powercfg state.
                    SleepToHibernateEnabled = sleepHibernateOn;
                    SleepToHibernateMinutes = sleepHibMinutes;

                    // Sleep → Hibernate (AC): reflect actual powercfg state.
                    SleepToHibernateAcEnabled = sleepHibAcOn;
                    SleepToHibernateAcMinutes = sleepHibAcMinutes;

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

    // ── Disable Suggestions / Nags callbacks ──────────────────────────────────

    partial void OnDisableSuggestionsChanged(bool value)
    {
        if (_loading) return;
        _ = ExecuteSuggestionsToggleAsync(value);
    }

    private async Task ExecuteSuggestionsToggleAsync(bool disable)
    {
        IsSuggestionsLoading = true;
        StatusMessage = disable
            ? "Turning off Windows suggestions and nags..."
            : "Restoring Windows suggestions...";
        try
        {
            TweakResult result = disable
                ? await _cleanup.DisableConsumerContentAsync()
                : await _cleanup.RestoreConsumerContentAsync();

            StatusMessage = result.Message;
            if (result.Success)
                _settings.DisableSuggestionsEnabled = disable;
            else
            {
                _loading = true; DisableSuggestions = !disable; _loading = false;
            }
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", $"DisableSuggestions toggle ({disable}) failed", ex);
            StatusMessage = $"Error: {ex.Message}";
            _loading = true; DisableSuggestions = !disable; _loading = false;
        }
        finally { IsSuggestionsLoading = false; }
    }

    // ── Disable Bing/Web Search callbacks ─────────────────────────────────────

    partial void OnDisableWebSearchChanged(bool value)
    {
        if (_loading) return;
        _ = ExecuteWebSearchToggleAsync(value);
    }

    private async Task ExecuteWebSearchToggleAsync(bool disable)
    {
        IsWebSearchLoading = true;
        StatusMessage = disable
            ? "Removing Bing/web results from Start search..."
            : "Restoring Start search web results...";
        try
        {
            TweakResult result = disable
                ? await _cleanup.DisableWebSearchAsync()
                : await _cleanup.RestoreWebSearchAsync();

            StatusMessage = result.Message;
            if (result.Success)
                _settings.DisableWebSearchEnabled = disable;
            else
            {
                _loading = true; DisableWebSearch = !disable; _loading = false;
            }
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", $"DisableWebSearch toggle ({disable}) failed", ex);
            StatusMessage = $"Error: {ex.Message}";
            _loading = true; DisableWebSearch = !disable; _loading = false;
        }
        finally { IsWebSearchLoading = false; }
    }

    // ── NTFS Last-Access callbacks ────────────────────────────────────────────

    partial void OnNtfsLastAccessDisabledChanged(bool value)
    {
        if (_loading) return;
        _ = ExecuteNtfsLastAccessToggleAsync(value);
    }

    private async Task ExecuteNtfsLastAccessToggleAsync(bool disable)
    {
        IsNtfsLastAccessLoading = true;
        StatusMessage = disable
            ? "Disabling NTFS last-access timestamps..."
            : "Re-enabling NTFS last-access timestamps...";
        try
        {
            TweakResult result = disable
                ? await _stability.DisableNtfsLastAccessAsync()
                : await _stability.EnableNtfsLastAccessAsync();

            StatusMessage = result.Message;

            if (result.Success)
            {
                _settings.NtfsLastAccessDisabled = disable;
            }
            else
            {
                _loading = true;
                NtfsLastAccessDisabled = !disable;
                _loading = false;
            }
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", $"NtfsLastAccess toggle ({disable}) failed", ex);
            StatusMessage = $"Error: {ex.Message}";
            _loading = true;
            NtfsLastAccessDisabled = !disable;
            _loading = false;
        }
        finally { IsNtfsLastAccessLoading = false; }
    }

    // Responsiveness callbacks (Foreground Priority Boost + Instant App Focus +
    // Maximum System Responsiveness) live on the Systema Engine tab — see TaskSleepViewModel.

    // ── Sleep → Hibernate (battery) callbacks ────────────────────────────────

    partial void OnSleepToHibernateEnabledChanged(bool value)
    {
        if (_loading) return;
        _ = ExecuteSleepToHibernateToggleAsync(value);
    }

    partial void OnSleepToHibernateMinutesChanged(int value)
    {
        if (_loading) return;
        _settings.SleepToHibernateMinutes = value;
        if (SleepToHibernateEnabled && !IsSleepToHibernateLoading)
            _ = ExecuteSleepToHibernateToggleAsync(enable: true);
    }

    private async Task ExecuteSleepToHibernateToggleAsync(bool enable)
    {
        IsSleepToHibernateLoading = true;
        int minutes = SleepToHibernateMinutes;
        StatusMessage = enable
            ? $"Enabling Sleep → Hibernate ({minutes} min on battery)..."
            : "Disabling Sleep → Hibernate on battery (restoring Windows default)...";
        try
        {
            TweakResult result = enable
                ? await _stability.EnableSleepToHibernateAsync(minutes)
                : await _stability.DisableSleepToHibernateAsync();

            StatusMessage = result.Message;

            if (result.Success)
                _settings.SleepToHibernateEnabled = enable;
            else
            {
                _loading = true;
                SleepToHibernateEnabled = !enable;
                _loading = false;
            }
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", $"SleepToHibernate (DC) toggle ({enable}) failed", ex);
            StatusMessage = $"Error: {ex.Message}";
            _loading = true;
            SleepToHibernateEnabled = !enable;
            _loading = false;
        }
        finally { IsSleepToHibernateLoading = false; }
    }

    // ── Sleep → Hibernate (AC) callbacks ─────────────────────────────────────

    partial void OnSleepToHibernateAcEnabledChanged(bool value)
    {
        if (_loading) return;
        _ = ExecuteSleepToHibernateAcToggleAsync(value);
    }

    partial void OnSleepToHibernateAcMinutesChanged(int value)
    {
        if (_loading) return;
        _settings.SleepToHibernateAcMinutes = value;
        if (SleepToHibernateAcEnabled && !IsSleepToHibernateAcLoading)
            _ = ExecuteSleepToHibernateAcToggleAsync(enable: true);
    }

    private async Task ExecuteSleepToHibernateAcToggleAsync(bool enable)
    {
        IsSleepToHibernateAcLoading = true;
        int minutes = SleepToHibernateAcMinutes;
        StatusMessage = enable
            ? $"Enabling Sleep → Hibernate ({minutes} min on AC)..."
            : "Disabling Sleep → Hibernate on AC (restoring Windows default)...";
        try
        {
            TweakResult result = enable
                ? await _stability.EnableSleepToHibernateAcAsync(minutes)
                : await _stability.DisableSleepToHibernateAcAsync();

            StatusMessage = result.Message;

            if (result.Success)
                _settings.SleepToHibernateAcEnabled = enable;
            else
            {
                _loading = true;
                SleepToHibernateAcEnabled = !enable;
                _loading = false;
            }
        }
        catch (Exception ex)
        {
            _log.Error("ToolsViewModel", $"SleepToHibernate (AC) toggle ({enable}) failed", ex);
            StatusMessage = $"Error: {ex.Message}";
            _loading = true;
            SleepToHibernateAcEnabled = !enable;
            _loading = false;
        }
        finally { IsSleepToHibernateAcLoading = false; }
    }
}
