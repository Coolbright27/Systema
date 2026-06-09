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
using Systema.Services;

namespace Systema.ViewModels;

/// <summary>One Dell thermal profile option for the combo boxes.
/// Value = raw BIOS value, Label = friendly name, Note = optional small grey hint.</summary>
public sealed record ThermalModeOption(string Value, string Label, string Note);

public partial class DellViewModel : ObservableObject, IDisposable
{
    private readonly ThermalManagementService _thermal;
    private readonly SettingsService           _settings;
    private readonly PowerPlanService          _powerPlan;
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

    public DellViewModel(ThermalManagementService thermal, SettingsService settings, PowerPlanService powerPlan)
    {
        _thermal   = thermal;
        _settings  = settings;
        _powerPlan = powerPlan;

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
                _loadingThermal = false;

                // Set-and-forget: apply the right profile for the current power state now.
                ApplyThermalForCurrentPower();
            });
        });

        // Re-apply the AC/battery thermal preference on every plug/unplug.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
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
        // On a desktop (no battery) the "battery" preference is irrelevant — always AC.
        string mode = (IsLaptop && IsOnBattery) ? ThermalModeBattery : ThermalModeAc;
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

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
