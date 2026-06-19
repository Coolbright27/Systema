// ════════════════════════════════════════════════════════════════════════════
// NvidiaGpuViewModel.cs  ·  NVIDIA dGPU control panel (NVIDIA-only sidebar section)
// ════════════════════════════════════════════════════════════════════════════
//
// Backs Views/NvidiaView.xaml. Detects the present NVIDIA adapter and resolves the
// PowerMizer power-management state against the LIVE registry. Writes target the
// PRESENT adapter only (see NvidiaGpuService). Mirrors IntelGpuViewModel's pattern.
// ════════════════════════════════════════════════════════════════════════════

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Systema.Core;
using Systema.Services;

namespace Systema.ViewModels;

public partial class NvidiaGpuViewModel : ObservableObject, IDisposable
{
    private readonly NvidiaGpuService _service;
    private readonly SettingsService  _settings;
    private static readonly LoggerService _log = LoggerService.Instance;

    private List<NvidiaAdapter> _adapters = new();
    private bool _loading;

    [ObservableProperty] private bool   _isNvidiaPresent;
    [ObservableProperty] private string _adapterName   = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool   _showRebootHint;

    // GPU power management (PowerMizer). On = idle power saving (driver default / adaptive);
    // Off = prefer maximum performance. The Nvidia analog of Intel's RC6 render standby.
    [ObservableProperty] private bool   _powerSavingOn = true;
    [ObservableProperty] private string _powerSavingDetected = "";

    // Opt-in: re-apply the saved choice after NVIDIA driver updates (which wipe the values).
    [ObservableProperty] private bool   _reapplyAfterDriverUpdate;

    private const string OnText  = "On — driver default (adaptive, saves power)";
    private const string OffText = "Off — prefer maximum performance";

    public NvidiaGpuViewModel(NvidiaGpuService service, SettingsService settings)
    {
        _service  = service;
        _settings = settings;

        _adapters = _service.DetectNvidiaAdapters();
        IsNvidiaPresent = _adapters.Count > 0;
        if (!IsNvidiaPresent) { StatusMessage = "No NVIDIA GPU detected."; return; }

        AdapterName = _adapters[0].DriverDesc;
        _reapplyAfterDriverUpdate = _settings.NvidiaGpuReapplyEnabled;
        LoadFromRegistry();
    }

    [RelayCommand]
    private void Detect()
    {
        if (!IsNvidiaPresent) return;
        _adapters = _service.DetectNvidiaAdapters();
        IsNvidiaPresent = _adapters.Count > 0;
        if (IsNvidiaPresent) { AdapterName = _adapters[0].DriverDesc; LoadFromRegistry(); }
        StatusMessage = "Re-read current NVIDIA settings from the registry.";
    }

    /// <summary>
    /// Opens NVIDIA's own Control Panel — the classic desktop client if installed, else the
    /// modern Microsoft Store app. Pure convenience: it launches NVIDIA's UI and changes
    /// nothing itself.
    /// </summary>
    [RelayCommand]
    private void OpenControlPanel()
    {
        try
        {
            string classic = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"NVIDIA Corporation\Control Panel Client\nvcplui.exe");
            if (System.IO.File.Exists(classic))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(classic) { UseShellExecute = true });
            }
            else
            {
                // No classic client — launch the modern Store package.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                    @"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIAControlPanel")
                { UseShellExecute = true });
            }
            StatusMessage = "Opened the NVIDIA Control Panel.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not open the NVIDIA Control Panel — try the Start menu or system-tray icon.";
            _log.Warn("NvidiaGpuViewModel", $"OpenControlPanel failed: {ex.Message}");
        }
    }

    private void LoadFromRegistry()
    {
        _loading = true;
        try
        {
            bool maxPerf = _service.IsMaxPerformance(_adapters[0].FullPath);
            PowerSavingOn = !maxPerf;
            PowerSavingDetected = maxPerf ? OffText : OnText;
            StatusMessage = $"Loaded current settings for {AdapterName}.";
        }
        finally { _loading = false; }
    }

    partial void OnPowerSavingOnChanged(bool value)
    {
        if (_loading) return;
        PowerSavingDetected = value ? OnText : OffText;
        _settings.NvidiaGpuPreferMaxPerformance = !value;
        Apply(_service.SetPowerSaving(_adapters, on: value), "GPU power management");
    }

    private void Apply(TweakResult result, string what)
    {
        StatusMessage = result.Message;
        if (result.Success)
        {
            ShowRebootHint = true;
            _log.Info("NvidiaGpuViewModel", $"Applied {what}: {result.Message}");
        }
        else _log.Warn("NvidiaGpuViewModel", $"Apply {what} failed: {result.Message}");
    }

    [RelayCommand]
    private void ResetSetting(string? id)
    {
        if (!IsNvidiaPresent) return;
        _loading = true;
        try { PowerSavingOn = true; PowerSavingDetected = OnText; }
        finally { _loading = false; }
        _settings.NvidiaGpuPreferMaxPerformance = false;
        Apply(_service.RevertAll(_adapters), "Reset");
    }

    [RelayCommand]
    private void RevertAll()
    {
        if (!IsNvidiaPresent) return;
        var r = _service.RevertAll(_adapters);
        LoadFromRegistry();
        ShowRebootHint = true;
        _settings.NvidiaGpuPreferMaxPerformance = false;
        StatusMessage = r.Message;
        _log.Info("NvidiaGpuViewModel", $"Revert all: {r.Message}");
    }

    partial void OnReapplyAfterDriverUpdateChanged(bool value)
    {
        _settings.NvidiaGpuReapplyEnabled = value;
        _log.Info("NvidiaGpuViewModel", $"Re-apply after driver update set to {value}.");
    }

    public void Dispose() { }
}
