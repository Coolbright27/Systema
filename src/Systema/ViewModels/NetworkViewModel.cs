// ════════════════════════════════════════════════════════════════════════════
// NetworkViewModel.cs  ·  DNS profile switching and Defender CFA toggle
// ════════════════════════════════════════════════════════════════════════════
//
// Exposes a list of named DNS profiles (from DnsService) and lets the user
// apply one via direct registry writes to Tcpip adapter settings. Also toggles
// Windows Defender Controlled Folder Access (CFA) via DefenderService without
// touching NetworkInterface APIs that can crash certain drivers.
//
// RELATED FILES
//   DnsService.cs             — registry-based DNS profile application
//   DefenderService.cs        — CFA/RTP toggle via PowerShell + registry reads
//   Views/NetworkView.xaml    — DNS profile picker and CFA toggle button
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Core;
using Systema.Models;
using Systema.Services;
using Systema.Views;

namespace Systema.ViewModels;

public partial class NetworkViewModel : ObservableObject, IAutoRefreshable
{
    private readonly DnsService      _dnsService;
    private readonly DefenderService _defenderService;
    private static readonly LoggerService _log = LoggerService.Instance;

    private int      _isRefreshing;
    private bool     _hasShownError;
    private DateTime _lastAutoRefresh = DateTime.MinValue;

    private static readonly TimeSpan AutoRefreshCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    [ObservableProperty] private ObservableCollection<DnsProfile> _dnsProfiles = new();
    [ObservableProperty] private DnsProfile? _selectedProfile;
    [ObservableProperty] private string _currentDns    = "Detecting...";
    [ObservableProperty] private bool   _controlledFolderAccessEnabled;
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public NetworkViewModel(DnsService dnsService, DefenderService defenderService)
    {
        _dnsService      = dnsService;
        _defenderService = defenderService;

        foreach (var p in DnsService.Profiles)
            DnsProfiles.Add(p);
        SelectedProfile = DnsProfiles[0];
    }

    // ── IAutoRefreshable ──────────────────────────────────────────────────────

    public Task RefreshAsync()
    {
        if (DateTime.Now - _lastAutoRefresh < AutoRefreshCooldown)
            return Task.CompletedTask;
        _lastAutoRefresh = DateTime.Now;
        return DoRefreshAsync(userInitiated: false);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task RefreshCommandAsync()
    {
        _hasShownError   = false;
        _lastAutoRefresh = DateTime.MinValue;
        return DoRefreshAsync(userInitiated: true);
    }

    [RelayCommand]
    private async Task ApplyDnsAsync()
    {
        if (SelectedProfile == null) return;
        IsLoading     = true;
        StatusMessage = $"Applying {SelectedProfile.Name} DNS...";
        try
        {
            CrashGuard.Mark("Network — ApplyDns starting");
            var applyTask = _dnsService.ApplyProfileAsync(SelectedProfile);
            if (await Task.WhenAny(applyTask, Task.Delay(TimeSpan.FromSeconds(30))) != applyTask)
                throw new TimeoutException("DNS apply timed out after 30 s.");

            var result = await applyTask;
            StatusMessage = result.Message;

            CurrentDns = await Task.Run(() => _dnsService.GetCurrentDns());
            _lastAutoRefresh = DateTime.Now;
            CrashGuard.Clear();
        }
        catch (Exception ex)
        {
            _log.Error("NetworkViewModel", "DNS apply failed", ex);
            StatusMessage = $"Error: {ex.Message}";
            CrashGuard.Clear();
            CrashReportWindow.ShowError(ex, "Network & Security — Apply DNS");
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ToggleControlledFolderAccessAsync()
    {
        IsLoading = true;
        try
        {
            CrashGuard.Mark("Network — Toggling Controlled Folder Access");
            TweakResult result;
            if (ControlledFolderAccessEnabled)
                result = await _defenderService.DisableControlledFolderAccessAsync();
            else
                result = await _defenderService.EnableControlledFolderAccessAsync();

            if (result.Success)
                ControlledFolderAccessEnabled = !ControlledFolderAccessEnabled;
            StatusMessage = result.Message;
            CrashGuard.Clear();
        }
        catch (Exception ex)
        {
            _log.Error("NetworkViewModel", "CFA toggle failed", ex);
            StatusMessage = $"Error: {ex.Message}";
            CrashGuard.Clear();
            CrashReportWindow.ShowError(ex, "Network & Security — Defender Toggle");
        }
        finally { IsLoading = false; }
    }

    // ── Core refresh ─────────────────────────────────────────────────────────

    private async Task DoRefreshAsync(bool userInitiated)
    {
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0) return;
        IsLoading = true;
        try
        {
            // ── Step 1: DNS read (registry only) ──
            CrashGuard.Mark("Network — DNS registry read starting");
            string dns;
            try
            {
                var dnsTask = Task.Run(() => _dnsService.GetCurrentDns());
                if (await Task.WhenAny(dnsTask, Task.Delay(ReadTimeout)) != dnsTask)
                    throw new TimeoutException($"DNS read timed out after {ReadTimeout.TotalSeconds:0} s.");
                dns = await dnsTask;
            }
            catch (Exception ex)
            {
                _log.Warn("NetworkViewModel", $"DNS read failed: {ex.Message}");
                dns = $"Error: {ex.Message}";
            }
            CurrentDns = dns;

            // ── Step 2: Defender CFA check (registry only) ──
            CrashGuard.Mark("Network — Defender registry read starting");
            try
            {
                ControlledFolderAccessEnabled = await Task.Run(
                    () => _defenderService.IsControlledFolderAccessEnabled());
            }
            catch (Exception ex)
            {
                _log.Warn("NetworkViewModel", $"Defender check failed: {ex.Message}");
                // Leave the previous value; don't crash the whole refresh
            }

            CrashGuard.Mark("Network — Refresh complete");

            StatusMessage    = $"Last refreshed: {DateTime.Now:HH:mm:ss}";
            _lastAutoRefresh = DateTime.Now;
            _hasShownError   = false;
        }
        catch (Exception ex)
        {
            _log.Error("NetworkViewModel", $"Refresh failed (userInitiated={userInitiated})", ex);
            StatusMessage = $"Error: {ex.Message}";

            if (userInitiated || !_hasShownError)
            {
                _hasShownError = true;
                CrashReportWindow.ShowError(ex, "Network & Security — Refresh");
            }
        }
        finally
        {
            IsLoading = false;
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }
}
