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
using System.Runtime.InteropServices;
using Systema.Core;
using Systema.Services;

namespace Systema.ViewModels;

public partial class NvidiaGpuViewModel : ObservableObject, IDisposable, IAutoRefreshable
{
    private readonly NvidiaGpuService _service;
    private readonly SettingsService  _settings;
    private readonly NvapiService     _nvapi = new();
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

    // ── Max Frame Rate (FPS cap) via NVAPI DRS — the same limiter the NVIDIA app uses ──
    [ObservableProperty] private bool   _isFpsCapAvailable;
    [ObservableProperty] private int    _fpsCapInput;
    [ObservableProperty] private string _fpsCapCurrentText = "";
    private int  _lastLoadedCap = -1;   // last value read from the driver (guards input-box stomping)
    private bool _fpsRefreshing;         // re-entrancy guard for the periodic RefreshAsync

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
        LoadFpsCap();
    }

    [RelayCommand]
    private void Detect()
    {
        if (!IsNvidiaPresent) return;
        _adapters = _service.DetectNvidiaAdapters();
        IsNvidiaPresent = _adapters.Count > 0;
        if (IsNvidiaPresent) { AdapterName = _adapters[0].DriverDesc; LoadFromRegistry(); }
        LoadFpsCap();
        StatusMessage = "Re-read current NVIDIA settings.";
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
                // No classic client — launch the modern Store package by its AUMID
                // (PackageFamilyName!AppId). The AppId is "NVIDIACorp.NVIDIAControlPanel",
                // NOT just "NVIDIAControlPanel" — the old value silently opened nothing
                // because explorer.exe starts fine even with a bad AppsFolder target.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                    @"shell:AppsFolder\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel")
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

    // ── Max Frame Rate (FPS cap) — reads/writes the SAME DRS setting the NVIDIA app uses ──

    private void LoadFpsCap()
    {
        IsFpsCapAvailable = _nvapi.IsAvailable();
        if (!IsFpsCapAvailable) { FpsCapCurrentText = ""; return; }
        int cur = _nvapi.GetMaxFrameRate();
        FpsCapInput = cur;                                  // reflect the REAL value (0 when off)
        _lastLoadedCap = cur;
        FpsCapCurrentText = cur > 0
            ? $"Currently capped at {cur} FPS"
            : "Currently: Off (no frame limit)";
    }

    /// <summary>
    /// Periodic/on-navigate refresh (IAutoRefreshable). Re-reads the live FPS cap so a change
    /// made elsewhere — the Dashboard's "Cap FPS to monitor refresh rate" recommendation, or the
    /// NVIDIA app itself — shows here immediately instead of only after an app restart. The NVAPI
    /// read is done off the UI thread, and the editable box is only refilled when the user hasn't
    /// typed a different value (so we never stomp a number they're in the middle of entering).
    /// </summary>
    public async Task RefreshAsync()
    {
        if (!IsFpsCapAvailable || _fpsRefreshing) return;
        _fpsRefreshing = true;
        try
        {
            int cur = await Task.Run(() => _nvapi.GetMaxFrameRate());
            if (FpsCapInput == _lastLoadedCap) FpsCapInput = cur;   // user hasn't edited → keep in sync
            _lastLoadedCap = cur;
            FpsCapCurrentText = cur > 0
                ? $"Currently capped at {cur} FPS"
                : "Currently: Off (no frame limit)";
        }
        finally { _fpsRefreshing = false; }
    }

    [RelayCommand]
    private void ApplyFpsCap()
    {
        if (!IsFpsCapAvailable) return;
        int v = FpsCapInput;
        if (v <= 0) { ResetFpsCap(); return; }              // 0 (or blank) + Apply = reset
        v = Math.Clamp(v, 20, 999);                         // allowed range 20–999
        var r = _nvapi.SetMaxFrameRate(v);
        StatusMessage = r.Message;
        if (!r.Success) _log.Warn("NvidiaGpuViewModel", $"ApplyFpsCap failed: {r.Message}");
        LoadFpsCap();                                       // re-read so the box shows the real applied value
    }

    [RelayCommand]
    private void ResetFpsCap()
    {
        if (!IsFpsCapAvailable) return;
        var r = _nvapi.SetMaxFrameRate(0);                  // remove the override entirely
        StatusMessage = r.Message;
        LoadFpsCap();
    }

    [RelayCommand]
    private void UseMonitorRefresh()
    {
        int hz = GetPrimaryRefreshHz();
        if (hz <= 0) { StatusMessage = "Couldn't read your monitor's refresh rate."; return; }
        // Snap to the nearest multiple of 5 so a "59 Hz" panel becomes a clean 60, 74 -> 75, 76 -> 75.
        int rounded = Math.Clamp((int)(Math.Round(hz / 5.0) * 5), 20, 999);
        FpsCapInput = rounded;
        StatusMessage = $"Set to {rounded} FPS (your monitor runs at {hz} Hz) — click Apply to enforce it.";
    }

    /// <summary>Primary monitor's current refresh rate in Hz (0 if unreadable).</summary>
    private static int GetPrimaryRefreshHz()
    {
        try
        {
            var dm = new DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            return EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) ? (int)dm.dmDisplayFrequency : 0;
        }
        catch { return 0; }
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint   dmFields;
        public int    dmPositionX, dmPositionY;
        public uint   dmDisplayOrientation, dmDisplayFixedOutput;
        public short  dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint   dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint   dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    public void Dispose() { }
}
