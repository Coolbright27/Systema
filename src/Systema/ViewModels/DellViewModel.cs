// ════════════════════════════════════════════════════════════════════════════
// DellViewModel.cs  ·  Dell-only sidebar section
// ════════════════════════════════════════════════════════════════════════════
//
// Backs Views/DellView.xaml. Detects whether the machine is a Dell (via
// Win32_ComputerSystem.Manufacturer) so the sidebar section appears on Dell
// systems and stays hidden elsewhere.
//
// Hosts the Dell BIOS Thermal Profile feature (moved here from Visual & Power):
// a set-and-forget AC + battery thermal-mode picker that re-applies on plug/unplug.
// The persisted preference keys (SettingsService.ThermalModeAc / ThermalModeBattery)
// are unchanged, so settings configured before the move are remembered.
//
// RELATED FILES
//   Views/DellView.xaml               — the panel UI (thermal selectors)
//   Services/ThermalManagementService — Dell BIOS thermal WMI provider
//   MainViewModel.cs                  — exposes IsDellPresent / HasOnDeviceSections
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using System.Management;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using Systema.Core;
using Systema.Services;

namespace Systema.ViewModels;

/// <summary>One Dell thermal profile option for the combo boxes.
/// Value = raw BIOS value, Label = friendly name, Note = optional small grey hint.</summary>
public sealed record ThermalModeOption(string Value, string Label, string Note);

/// <summary>One Dell charging-mode row for the selectable list.
/// Value = raw BIOS value, Label/Description = friendly text, Recommended = shows the pill.</summary>
public sealed record ChargeModeOption(string Value, string Label, string Description, bool Recommended);

public partial class DellViewModel : ObservableObject, IDisposable, IAutoRefreshable
{
    private readonly ThermalManagementService _thermal;
    private readonly SettingsService           _settings;
    private readonly PowerPlanService          _powerPlan;
    private readonly BatteryPauseService       _batteryPause;
    private readonly GameBoosterService        _gameBooster;
    private static readonly LoggerService _log = LoggerService.Instance;

    [ObservableProperty] private bool   _isDellPresent;
    [ObservableProperty] private string _manufacturer = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _statusMessage = "";

    // ── Dell thermal profile (set-and-forget, AC + battery) ────────────────────
    /// <summary>True only on a Dell whose BIOS exposes a thermal-profile attribute.</summary>
    [ObservableProperty] private bool _thermalSupported;
    /// <summary>True on a Dell where the BIOS WMI provider is missing — show install guidance.</summary>
    [ObservableProperty] private bool _thermalNeedsProvider;
    /// <summary>True when the thermal card should be shown at all (supported OR needs-provider).</summary>
    public bool ThermalCardVisible => ThermalSupported || ThermalNeedsProvider;
    /// <summary>True when the machine has a battery — gates the battery selector.</summary>
    [ObservableProperty] private bool _isLaptop;
    [ObservableProperty] private bool _isOnBattery;
    [ObservableProperty] private string _thermalStatus = "Checking for Dell thermal control…";
    /// <summary>Allowed thermal modes for this exact machine (from BIOS PossibleValues).</summary>
    public ObservableCollection<ThermalModeOption> ThermalModes { get; } = new();
    /// <summary>Raw BIOS thermal value applied on AC / desktop.</summary>
    [ObservableProperty] private string _thermalModeAc = "";
    /// <summary>Raw BIOS thermal value applied on battery.</summary>
    [ObservableProperty] private string _thermalModeBattery = "";
    // Suppress the apply side-effect while populating the selectors at startup.
    private bool _loadingThermal;

    // ── Dell charging mode ─────────────────────────────────────────────────────
    /// <summary>True only on a Dell laptop whose BIOS exposes the charging attribute.</summary>
    [ObservableProperty] private bool _chargingSupported;
    /// <summary>Charge-mode rows offered by this machine's BIOS.</summary>
    public ObservableCollection<ChargeModeOption> ChargeModes { get; } = new();
    /// <summary>Raw BIOS value of the selected charge mode ("Adaptive", "Custom", ...).</summary>
    [ObservableProperty] private string _selectedChargeMode = "";
    /// <summary>True when Custom is selected — reveals the start/stop pickers.</summary>
    public bool IsCustomMode =>
        string.Equals(SelectedChargeMode, "Custom", StringComparison.OrdinalIgnoreCase);
    /// <summary>Percent presets for the custom start/stop pickers.</summary>
    public ObservableCollection<int> ChargeStartOptions { get; } = new();
    public ObservableCollection<int> ChargeStopOptions  { get; } = new();
    /// <summary>Custom start threshold — charge when at or below this percent.</summary>
    [ObservableProperty] private int _customStart = 75;
    /// <summary>Custom stop threshold — stop when at or above this percent.</summary>
    [ObservableProperty] private int _customStop = 80;
    /// <summary>True while a Game Boost session owns charging — greys the card out.</summary>
    [ObservableProperty] private bool _isBatteryPauseActive;
    [ObservableProperty] private string _chargingStatus = "";
    // Suppress the apply side-effect while populating the pickers / snapping thresholds.
    private bool _loadingCharging;

    private Action<string>? _onBoostActivatedCharging;
    private Action?         _onBoostDeactivatedCharging;

    public DellViewModel(ThermalManagementService thermal, SettingsService settings, PowerPlanService powerPlan,
                         BatteryPauseService batteryPause, GameBoosterService gameBooster)
    {
        _thermal      = thermal;
        _settings     = settings;
        _powerPlan    = powerPlan;
        _batteryPause = batteryPause;
        _gameBooster  = gameBooster;

        // ── Manufacturer detection (sync — sidebar visibility is needed at shell build) ──
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var item in s.Get())
            {
                Manufacturer = (item["Manufacturer"]?.ToString() ?? "").Trim();
                Model        = (item["Model"]?.ToString()        ?? "").Trim();
                break;
            }
        }
        catch (Exception ex) { _log.Warn("DellViewModel", $"Manufacturer probe failed: {ex.Message}"); }

        IsDellPresent = IsDellManufacturer(Manufacturer);
        _log.Info("DellViewModel", $"Dell detection: manufacturer='{Manufacturer}' model='{Model}' present={IsDellPresent}");

        _isOnBattery = _powerPlan.IsOnBattery();

        // ── Dell thermal-profile detection (worker thread — WMI can take 50-300ms) ──
        _ = Task.Run(() =>
        {
            ThermalSupport support = _thermal.DetectSupport();
            var     modes   = _thermal.AvailableModes.ToList();
            string? current = support == ThermalSupport.Supported ? _thermal.GetCurrentMode() : null;
            bool    isLaptop = ThermalManagementService.HasBattery();
            string  status   = _thermal.StatusMessage;

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ThermalStatus    = status;
                IsLaptop         = isLaptop;
                ThermalSupported = support == ThermalSupport.Supported;
                // On a Dell where only the WMI provider is missing, surface the card
                // with install guidance instead of hiding it silently.
                ThermalNeedsProvider = support == ThermalSupport.DriverMissing;
                OnPropertyChanged(nameof(ThermalCardVisible));
                if (!ThermalSupported) return;

                _loadingThermal = true;
                ThermalModes.Clear();
                foreach (var m in modes)
                    ThermalModes.Add(new ThermalModeOption(
                        m,
                        ThermalManagementService.FriendlyLabel(m),
                        ThermalManagementService.FriendlyNote(m)));

                // Restore saved prefs (carried over from before the move); fall back to
                // whatever the BIOS currently reports so the dropdowns never start blank.
                ThermalModeAc      = PickValid(_settings.ThermalModeAc,      current, modes);
                ThermalModeBattery = PickValid(_settings.ThermalModeBattery, current, modes);

                // Fresh install (no saved pref yet): adopt the machine's real current BIOS
                // value as the saved preference for BOTH AC and battery, so it persists and
                // the selectors show the real value instead of starting blank. Writing
                // _settings directly (not via the observable setters, which are suppressed by
                // _loadingThermal) records the value without re-applying it — it's already the
                // live BIOS mode, so nothing on the hardware changes.
                if (string.IsNullOrEmpty(_settings.ThermalModeAc) && !string.IsNullOrEmpty(ThermalModeAc))
                    _settings.ThermalModeAc = ThermalModeAc;
                if (string.IsNullOrEmpty(_settings.ThermalModeBattery) && !string.IsNullOrEmpty(ThermalModeBattery))
                    _settings.ThermalModeBattery = ThermalModeBattery;
                _loadingThermal = false;

                // Set-and-forget: apply the right profile for the current power state now.
                ApplyThermalForCurrentPower();
            });
        });

        // Re-apply the AC/battery thermal preference on every plug/unplug.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // ── Charging-mode detection (worker thread — WMI probe) ────────────────────
        // A boost that is already running when the tab first builds means the BIOS is
        // showing the pause value, not the user's real mode, so gate that in.
        IsBatteryPauseActive = _gameBooster.BoostActive;
        _ = Task.Run(() =>
        {
            bool supported = _batteryPause.DetectSupport() == BatteryPauseSupport.Supported;
            var  modes     = supported ? _batteryPause.GetChargeModes() : new List<string>();
            // Only trust the live BIOS value when no boost is overriding charging.
            string? live   = supported && !_gameBooster.BoostActive
                ? _batteryPause.GetCurrentChargeMode()
                : null;

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                ChargingSupported = supported;
                if (!supported) return;

                _loadingCharging = true;

                ChargeStartOptions.Clear();
                for (int p = 50; p <= 95; p += 5) ChargeStartOptions.Add(p);
                ChargeStopOptions.Clear();
                for (int p = 55; p <= 100; p += 5) ChargeStopOptions.Add(p);

                ChargeModes.Clear();
                foreach (var m in modes)
                {
                    var (label, desc, rec) = ChargeFriendly(m);
                    ChargeModes.Add(new ChargeModeOption(m, label, desc, rec));
                }

                // Seed custom thresholds from the saved preference (falls back to 75/80).
                CustomStart = _settings.DellChargeStart;
                CustomStop  = _settings.DellChargeStop;

                // Pick what to show: the live BIOS mode when readable, else the saved
                // preference, else the machine's current mode name. If live is a Custom
                // string, split its thresholds out into the pickers.
                string chosen = "";
                if (!string.IsNullOrEmpty(live))
                {
                    if (live.StartsWith("Custom:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = live.Split(':');
                        if (parts.Length >= 3
                            && int.TryParse(parts[1], out int s) && int.TryParse(parts[2], out int e))
                        {
                            CustomStart = ClampStart(s);
                            CustomStop  = ClampStop(CustomStart, e);
                        }
                        chosen = "Custom";
                    }
                    else chosen = live;
                    // Record the freshly-read live mode as the saved preference.
                    _settings.DellChargeMode  = chosen;
                    _settings.DellChargeStart = CustomStart;
                    _settings.DellChargeStop  = CustomStop;
                }
                else if (!string.IsNullOrEmpty(_settings.DellChargeMode))
                {
                    chosen = _settings.DellChargeMode;
                }

                if (!string.IsNullOrEmpty(chosen)
                    && modes.Contains(chosen, StringComparer.OrdinalIgnoreCase))
                    SelectedChargeMode = modes.First(m => string.Equals(m, chosen, StringComparison.OrdinalIgnoreCase));

                _loadingCharging = false;
                OnPropertyChanged(nameof(IsCustomMode));
            });
        });

        // Grey the charging card while Game Boost owns charging, and re-sync from the
        // BIOS once the boost restores the user's mode.
        _onBoostActivatedCharging = _ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            () => IsBatteryPauseActive = true);
        _onBoostDeactivatedCharging = () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsBatteryPauseActive = false;
            ReloadChargeFromBios();
        });
        _gameBooster.BoostActivated   += _onBoostActivatedCharging;
        _gameBooster.BoostDeactivated += _onBoostDeactivatedCharging;
    }

    /// <summary>Friendly label, description and "recommended" flag for a raw BIOS charge mode.</summary>
    internal static (string Label, string Description, bool Recommended) ChargeFriendly(string value) =>
        value.ToLowerInvariant() switch
        {
            "adaptive"  => ("Adaptive",      "Optimizes charging to your typical usage pattern.", true),
            "standard"  => ("Standard",      "For switching between battery power and an external power source.", false),
            "express"   => ("ExpressCharge", "Charges the battery over a shorter period of time.", false),
            "primacuse" => ("Always AC",     "Best when you mostly run plugged into a power source.", false),
            "custom"    => ("Custom",        "Set your own start and stop charge thresholds.", false),
            _           => (value,           "", false),
        };

    /// <summary>Snap a start percent to the nearest 5% inside the BIOS-allowed 50-95 range.</summary>
    internal static int ClampStart(int v) => Math.Min(95, Math.Max(50, (v / 5) * 5));

    /// <summary>Snap a stop percent to 5% steps, never below start + 5, never above 100.</summary>
    internal static int ClampStop(int start, int v) => Math.Min(100, Math.Max(start + 5, (v / 5) * 5));

    /// <summary>Applies the selected charge mode to the BIOS unless loading or a boost owns charging.</summary>
    private void ApplyChargeMode()
    {
        if (_loadingCharging || !ChargingSupported || IsBatteryPauseActive) return;
        string mode = SelectedChargeMode;
        if (string.IsNullOrEmpty(mode)) return;

        string payload = string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase)
            ? $"Custom:{CustomStart}:{CustomStop}"
            : mode;

        Task.Run(() =>
        {
            bool ok = _batteryPause.SetChargeMode(payload);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                ChargingStatus = ok
                    ? $"Charging mode set to {ChargeFriendly(mode).Label}."
                    : "Could not change the charging mode. The Dell BIOS provider may not be installed.");
        });
    }

    /// <summary>Re-reads the BIOS charge mode into the pickers (after a boost restores it).</summary>
    private void ReloadChargeFromBios()
    {
        if (!ChargingSupported) return;
        Task.Run(() =>
        {
            string? live = _batteryPause.GetCurrentChargeMode();
            if (string.IsNullOrEmpty(live)) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _loadingCharging = true;
                if (live.StartsWith("Custom:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = live.Split(':');
                    if (parts.Length >= 3
                        && int.TryParse(parts[1], out int s) && int.TryParse(parts[2], out int e))
                    {
                        CustomStart = ClampStart(s);
                        CustomStop  = ClampStop(CustomStart, e);
                    }
                    SelectByValue("Custom");
                }
                else SelectByValue(live);
                _loadingCharging = false;
                OnPropertyChanged(nameof(IsCustomMode));
            });
        });
    }

    private void SelectByValue(string value)
    {
        var match = ChargeModes.FirstOrDefault(m =>
            string.Equals(m.Value, value, StringComparison.OrdinalIgnoreCase));
        if (match != null) SelectedChargeMode = match.Value;
    }

    /// <summary>True when the system manufacturer identifies as Dell. Pure + testable.</summary>
    public static bool IsDellManufacturer(string? manufacturer) =>
        !string.IsNullOrWhiteSpace(manufacturer) &&
        manufacturer.IndexOf("dell", StringComparison.OrdinalIgnoreCase) >= 0;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        bool nowOnBattery = _powerPlan.IsOnBattery();
        // PowerModeChanged fires on a system thread — marshal UI-property writes to the UI thread.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsOnBattery = nowOnBattery;
            ApplyThermalForCurrentPower();
        });
    }

    /// <summary>
    /// Picks the persisted preference if still valid, else the current BIOS value (when
    /// we could read it). Returns "" when NOTHING is known — deliberately leaving the
    /// selector blank so we never auto-write a guessed default on first run.
    /// </summary>
    private static string PickValid(string saved, string? current, List<string> modes)
    {
        if (!string.IsNullOrEmpty(saved) && modes.Contains(saved, StringComparer.OrdinalIgnoreCase))
            return modes.First(m => string.Equals(m, saved, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(current) && modes.Contains(current, StringComparer.OrdinalIgnoreCase))
            return modes.First(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase));
        return "";   // nothing known — leave unselected, do not auto-apply
    }

    /// <summary>Applies the AC or battery thermal preference based on the current power state.</summary>
    private void ApplyThermalForCurrentPower()
    {
        if (!ThermalSupported) return;
        // Read the PERSISTED preference (not the in-memory selector) so a change made elsewhere —
        // e.g. the Dashboard "Ultra Performance on AC" recommendation — is honored on plug/unplug
        // instead of being reverted to the stale selector value.
        // On a desktop (no battery) the "battery" preference is irrelevant — always AC.
        string mode = (IsLaptop && IsOnBattery) ? _settings.ThermalModeBattery : _settings.ThermalModeAc;
        if (string.IsNullOrEmpty(mode)) return;
        string snapshot = mode;
        Task.Run(() => _thermal.SetMode(snapshot));
    }

    partial void OnThermalModeAcChanged(string value)
    {
        if (_loadingThermal || !ThermalSupported) return;
        _settings.ThermalModeAc = value;
        // Apply immediately if we're currently on AC (or it's a desktop).
        if (!IsLaptop || !IsOnBattery)
        {
            string snapshot = value;
            Task.Run(() => _thermal.SetMode(snapshot));
            StatusMessage = $"Thermal profile (AC): {ThermalManagementService.FriendlyLabel(value)}";
        }
    }

    partial void OnThermalModeBatteryChanged(string value)
    {
        if (_loadingThermal || !ThermalSupported) return;
        _settings.ThermalModeBattery = value;
        // Apply immediately only if we're actually on battery right now.
        if (IsLaptop && IsOnBattery)
        {
            string snapshot = value;
            Task.Run(() => _thermal.SetMode(snapshot));
            StatusMessage = $"Thermal profile (battery): {ThermalManagementService.FriendlyLabel(value)}";
        }
    }

    // ── Charging-mode change handlers ──────────────────────────────────────────

    partial void OnSelectedChargeModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomMode));
        if (_loadingCharging || !ChargingSupported) return;
        _settings.DellChargeMode = value;
        ApplyChargeMode();
    }

    partial void OnCustomStartChanged(int value)
    {
        if (_loadingCharging) return;
        // Keep at least a 5% gap: bump stop up if the user raised start past it.
        if (CustomStop < value + 5)
        {
            _loadingCharging = true;
            CustomStop = Math.Min(100, value + 5);
            _loadingCharging = false;
        }
        _settings.DellChargeStart = value;
        _settings.DellChargeStop  = CustomStop;
        if (IsCustomMode) ApplyChargeMode();
    }

    partial void OnCustomStopChanged(int value)
    {
        if (_loadingCharging) return;
        // Enforce the 5% gap the BIOS requires: snap stop back up to start + 5.
        if (value < CustomStart + 5)
        {
            _loadingCharging = true;
            CustomStop = CustomStart + 5;
            _loadingCharging = false;
        }
        _settings.DellChargeStop = CustomStop;
        if (IsCustomMode) ApplyChargeMode();
    }

    /// <summary>
    /// On-navigate / periodic refresh (IAutoRefreshable). Re-syncs the AC/battery selectors from
    /// the persisted preferences and the live power state, so a change made elsewhere — e.g. the
    /// Dashboard "Ultra Performance on AC" recommendation — is reflected here without an app restart.
    /// Cheap: no WMI, just settings reads. Guarded by _loadingThermal so it never re-applies.
    /// </summary>
    public Task RefreshAsync()
    {
        if (!ThermalSupported) return Task.CompletedTask;
        _loadingThermal = true;
        try
        {
            IsOnBattery = _powerPlan.IsOnBattery();
            var modes = ThermalModes.Select(m => m.Value).ToList();
            if (!string.IsNullOrEmpty(_settings.ThermalModeAc) &&
                modes.Contains(_settings.ThermalModeAc, StringComparer.OrdinalIgnoreCase))
                ThermalModeAc = modes.First(m => string.Equals(m, _settings.ThermalModeAc, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(_settings.ThermalModeBattery) &&
                modes.Contains(_settings.ThermalModeBattery, StringComparer.OrdinalIgnoreCase))
                ThermalModeBattery = modes.First(m => string.Equals(m, _settings.ThermalModeBattery, StringComparison.OrdinalIgnoreCase));
        }
        finally { _loadingThermal = false; }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_onBoostActivatedCharging   != null) _gameBooster.BoostActivated   -= _onBoostActivatedCharging;
        if (_onBoostDeactivatedCharging != null) _gameBooster.BoostDeactivated -= _onBoostDeactivatedCharging;
    }
}
