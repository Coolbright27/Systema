// ════════════════════════════════════════════════════════════════════════════
// BloatwareViewModel.cs  ·  App Cleanup tab
// ════════════════════════════════════════════════════════════════════════════
//
// Scans for pre-installed Microsoft apps that are safe to remove, lets the
// user select which ones to uninstall, creates a restore point first (unless
// the user has disabled that in Settings), then removes each selected app
// via PowerShell one at a time so partial failures are reported clearly.
//
// FLOW
//   1. Load → ScanAsync() runs — lists installed catalogue apps
//   2. User checks boxes → IsSelected on BloatwareEntry updates
//   3. "Remove Selected" clicked → confirm dialog → create restore point
//      → remove each app → refresh scan → show summary
//
// RELATED FILES
//   BloatwareService.cs         — scan + per-app remove logic
//   RestorePointService.cs      — safety restore point before removal
//   SettingsService.cs          — reads SkipRestorePoint preference
//   Models/BloatwareEntry.cs    — single app data shape
//   Views/BloatwareView.xaml    — bound XAML UI
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Core;
using Systema.Models;
using Systema.Services;

namespace Systema.ViewModels;

public partial class BloatwareViewModel : ObservableObject, IAutoRefreshable
{
    private readonly BloatwareService    _bloatware;
    private readonly RestorePointService _restore;
    private readonly SettingsService     _settings;
    private static readonly LoggerService _log = LoggerService.Instance;

    private bool _hasScanned;
    private int  _isRefreshing;

    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<BloatwareEntry> _apps = new();
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private bool   _isRemoving;
    [ObservableProperty] private string _statusMessage   = string.Empty;
    [ObservableProperty] private string _scanSummary     = string.Empty;
    [ObservableProperty] private bool   _hasApps;
    [ObservableProperty] private bool   _hasScannedOnce;

    /// <summary>Inverse of IsRemoving — used to enable/disable UI elements.</summary>
    public bool IsNotRemoving => !IsRemoving;

    partial void OnIsRemovingChanged(bool value) => OnPropertyChanged(nameof(IsNotRemoving));

    public int SelectedCount => Apps.Count(a => a.IsSelected);

    // ── Constructor ───────────────────────────────────────────────────────────

    public BloatwareViewModel(
        BloatwareService    bloatware,
        RestorePointService restore,
        SettingsService     settings)
    {
        _bloatware = bloatware;
        _restore   = restore;
        _settings  = settings;
    }

    // ── IAutoRefreshable ──────────────────────────────────────────────────────

    public Task RefreshAsync()
    {
        // Auto-scan once on first navigation; subsequent timer ticks do nothing
        if (!_hasScanned)
        {
            _hasScanned = true;
            return DoScanAsync();
        }
        return Task.CompletedTask;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task RescanAsync() => DoScanAsync();

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var app in Apps) app.IsSelected = true;
        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var app in Apps) app.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>Called from the view when a checkbox is toggled so SelectedCount stays current.</summary>
    [RelayCommand]
    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var toRemove = Apps.Where(a => a.IsSelected).ToList();
        if (toRemove.Count == 0)
        {
            StatusMessage = "No apps selected. Tick the boxes next to the apps you want to remove.";
            return;
        }

        // ── Confirmation dialog ───────────────────────────────────────────────
        string list = string.Join("\n", toRemove.Select(a => $"  • {a.DisplayName}"));
        var confirm = MessageBox.Show(
            $"You are about to remove {toRemove.Count} app{(toRemove.Count == 1 ? "" : "s")}:\n\n" +
            $"{list}\n\n" +
            "These apps can be reinstalled from the Microsoft Store if you change your mind.\n\n" +
            "Are you sure you want to remove them?",
            "Remove Pre-installed Apps",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        IsRemoving = true;
        StatusMessage = string.Empty;

        // ── Safety restore point ──────────────────────────────────────────────
        if (!_settings.SkipRestorePoint)
        {
            StatusMessage = "Creating a safety restore point before making changes...";
            var rp = await _restore.CreateAsync("Systema — Before App Cleanup");
            if (!rp.Success)
            {
                StatusMessage = $"Warning: Could not create restore point ({rp.Message}). Proceeding anyway.";
                _log.Warn("BloatwareViewModel", $"Restore point creation failed: {rp.Message}");
            }
            else
            {
                _log.Info("BloatwareViewModel", "Safety restore point created.");
            }
        }

        // ── Remove each app ───────────────────────────────────────────────────
        int succeeded = 0;
        int failed    = 0;
        var failures  = new List<string>();

        foreach (var app in toRemove)
        {
            StatusMessage = $"Removing {app.DisplayName}...";
            var result = await _bloatware.RemoveAsync(app);
            if (result.Success)
            {
                succeeded++;
                Apps.Remove(app); // remove immediately so the UI updates without waiting for a full reload
            }
            else
            {
                failed++;
                failures.Add(app.DisplayName);
            }
        }

        // ── Summary ───────────────────────────────────────────────────────────
        string summary;
        if (failed == 0)
        {
            summary = succeeded == 1
                ? "1 app removed successfully. A restart is recommended."
                : $"{succeeded} apps removed successfully. A restart is recommended.";
        }
        else if (succeeded == 0)
        {
            summary = $"Could not remove {failed} app{(failed == 1 ? "" : "s")}: {string.Join(", ", failures)}";
        }
        else
        {
            summary = $"{succeeded} removed, {failed} failed: {string.Join(", ", failures)}";
        }

        StatusMessage = summary;
        _log.Info("BloatwareViewModel", $"Removal complete — {succeeded} OK, {failed} failed.");

        IsRemoving = false;

        // Re-scan to update the list
        await DoScanAsync();
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private async Task DoScanAsync()
    {
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0) return;
        IsLoading     = true;
        StatusMessage = "Scanning for pre-installed apps...";
        try
        {
            var found = await _bloatware.ScanAsync();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                Apps.Clear();
                foreach (var entry in found)
                    Apps.Add(entry);

                HasApps      = found.Count > 0;
                HasScannedOnce = true;

                ScanSummary = found.Count == 0
                    ? "No removable pre-installed apps found — your system is clean."
                    : $"{found.Count} pre-installed app{(found.Count == 1 ? "" : "s")} found. " +
                      "Select the ones you don't need and click Remove.";

                StatusMessage = string.Empty;
                OnPropertyChanged(nameof(SelectedCount));
            });
        }
        catch (Exception ex)
        {
            _log.Error("BloatwareViewModel", "Scan failed", ex);
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }
}
