// ════════════════════════════════════════════════════════════════════════════
// NvidiaGpuViewModel.cs  ·  NVIDIA dGPU control panel (NVIDIA-only sidebar section)
// ════════════════════════════════════════════════════════════════════════════
//
// Backs Views/NvidiaView.xaml. Detects the present NVIDIA adapter and resolves the
// PowerMizer power-management state against the LIVE registry. Writes target the
// PRESENT adapter only (see NvidiaGpuService). Mirrors IntelGpuViewModel's pattern.
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Runtime.InteropServices;
using Systema.Core;
using Systema.Services;

namespace Systema.ViewModels;

/// <summary>One entry in the power management mode dropdown.</summary>
public sealed record PowerModeOption(uint Value, string Label, string Note);

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

        // The driver holds ONE global power mode, so a separate battery choice only exists if we
        // swap it when the plug goes in or out. PowerModeChanged fires on exactly that.
        if (IsLaptop)
        {
            _powerModeChanged = (_, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.StatusChange) ApplyModeForCurrentPowerSource();
            };
            Microsoft.Win32.SystemEvents.PowerModeChanged += _powerModeChanged;
        }
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

            LoadPowerModes();

            StatusMessage = $"Loaded current settings for {AdapterName}.";
        }
        finally { _loading = false; }
    }

    // ── Power management mode (NVAPI PREFERRED_PSTATE) ───────────────────────
    //
    // The same dropdown as the NVIDIA app's global settings. It is a driver PROFILE setting and
    // applies IMMEDIATELY — no restart — which is the whole reason a separate battery choice is
    // possible: the driver keeps ONE global value, so Systema swaps it when the power source
    // changes rather than storing two values in the driver.

    private readonly PowerPlanService _power = new();
    public bool IsLaptop { get; } = new PowerPlanService().HasBattery();

    // Every mode NVIDIA defines, in the app's order. Naming and values confirmed on hardware:
    // 0 is Adaptive (0.7.284 wrongly labelled it "Optimal power"), 2 is the driver default the
    // app calls Normal, 1 is Prefer maximum performance.
    //
    // This is the CANDIDATE list. What the user actually sees is filtered to whatever the
    // installed driver reports as available — see LoadPowerModes.
    // THE NVIDIA APP'S OWN LIST, IN ITS OWN ORDER, WITH ITS OWN LABELS.
    //
    // Verified against the Power management mode dropdown in the NVIDIA app (T1200, 2026-08-12):
    //
    //     Optimal power                         → 5
    //     Prefer maximum performance            → 1
    //     Adaptive                              → 0
    //     NVIDIA driver-controlled (Default)    → 2
    //     Prefer consistent performance         → 3
    //
    // Value 4 (PREFER_MIN in NVAPI's headers) is deliberately absent — NVIDIA defines it but does
    // not offer it in the UI, so neither do we.
    //
    // Why this list is hardcoded rather than queried: NVAPI cannot report it. Probing a live
    // driver showed EnumAvailableSettingValues returns -160 for EVERY setting (Power management
    // mode, Frame Rate Limiter and Vertical Sync alike), and SetSetting accepts any value 0-8
    // without validation. The NVIDIA app builds its dropdown from its own resources. See the note
    // in NvapiService for the full probe results.
    private static readonly PowerModeOption[] AllPowerModes =
    {
        new(NvapiService.PStateOptimalPower,   "Optimal power",
            "lowest power, drops clocks hardest"),
        new(NvapiService.PStateMaxPerf,        "Prefer maximum performance",
            "holds full clocks"),
        new(NvapiService.PStateAdaptive,       "Adaptive",
            "clocks follow the load"),
        new(NvapiService.PStateDriverManaged,  "NVIDIA driver-controlled (Default)",
            "the driver decides"),
        new(NvapiService.PStateConsistentPerf, "Prefer consistent performance",
            "steadier clocks, less boosting"),
    };

    /// <summary>Only the modes this GPU and driver actually offer.</summary>
    public ObservableCollection<PowerModeOption> PowerModeOptions { get; } = new();

    [ObservableProperty] private uint _powerModeAc      = NvapiService.PStateDriverManaged;
    [ObservableProperty] private uint _powerModeBattery = NvapiService.PStateDriverManaged;

    /// <summary>
    /// Reads the driver's CURRENT mode and shows it for whichever source the machine is on right
    /// now. On a fresh install that is the machine's real state, not a value Systema invented —
    /// the other source falls back to a saved choice if one exists, or to the same live value.
    /// </summary>
    private void LoadPowerModes()
    {
        uint live = _nvapi.GetPowerMode(out bool settingPresent);
        bool onBattery = IsLaptop && _power.IsOnBattery();

        // Pre-Turing cards don't get "NVIDIA driver-controlled" or "Prefer consistent performance"
        // — NVIDIA's own app gates those on Turing and newer (see NvapiService.GetGpuArchitecture
        // for how that was established). Unknown architecture keeps the full list: showing an
        // extra mode is a smaller failure than hiding one the user actually has.
        uint arch = _nvapi.GetGpuArchitecture();
        bool turingPlus = arch == 0 || arch >= NvapiService.ArchTuring;

        var shown = (turingPlus
                ? AllPowerModes
                : AllPowerModes.Where(m => m.Value != NvapiService.PStateDriverManaged
                                        && m.Value != NvapiService.PStateConsistentPerf))
            .ToList();

        // Whatever the card is ACTUALLY set to must be selectable — the driver accepts values
        // outside NVIDIA's own enum, so a machine can genuinely be sitting on one and a blank box
        // would be worse. But only for a value read from the driver: on a GTX 1060 the "setting
        // not present" fallback was 2, this branch re-added 2, and the Turing gate that had just
        // excluded it was silently undone. A default is not evidence of anything.
        if (settingPresent && shown.All(m => m.Value != live))
        {
            var known = AllPowerModes.FirstOrDefault(m => m.Value == live);
            shown.Add(known ?? new PowerModeOption(live, $"Mode {live}", "reported by your driver"));
            _log.Warn("NvidiaGpuViewModel",
                      $"Driver is on mode {live}, which is not in the list for this GPU " +
                      $"(arch 0x{arch:X}) — adding it so the current state stays visible");
        }

        // Only rebuild when the list ACTUALLY changed. Clearing a collection a ComboBox is bound
        // to nulls its selection, and that null writes straight back through the TwoWay binding —
        // which is what made "Re-detect" look like it reset the power mode.
        if (!PowerModeOptions.Select(o => o.Value).SequenceEqual(shown.Select(o => o.Value)))
        {
            PowerModeOptions.Clear();
            foreach (var m in shown) PowerModeOptions.Add(m);
        }

        // DESKTOP: one mode, and the driver is the only source of truth — mirror it. Nothing
        // re-applies it afterwards (the power-source watcher is laptop-only), so picking a mode
        // here simply sets it once and that is the end of it.
        //
        // LAPTOP: two saved choices. Show the driver's value for the source we are ON, because
        // that IS the live state, and the SAVED choice for the other, which cannot be observed
        // right now. Reading `live` into both would overwrite the user's choice for the other
        // power source every time this ran — including on every Re-detect.
        if (!IsLaptop)
        {
            PowerModeAc = live;
        }
        else if (onBattery)
        {
            PowerModeBattery = live;
            PowerModeAc      = ParseSavedMode(_settings.NvidiaPowerModeAc, live);
        }
        else
        {
            PowerModeAc      = live;
            PowerModeBattery = ParseSavedMode(_settings.NvidiaPowerModeBattery, live);
        }

        // Everything needed to action a "my NVIDIA app shows a different list" report without
        // another round of guessing: the card, its architecture, the driver, and what we chose to
        // show. NVIDIA gates this list themselves and we are mirroring their rule, so a mismatch
        // is a data point about their rule, not a bug we can reason our way to.
        _log.Info("NvidiaGpuViewModel",
                  $"Power modes — GPU='{AdapterName}' arch=0x{arch:X} driver={_nvapi.GetDriverVersion()} " +
                  $"turingPlus={turingPlus} shown=[{string.Join(", ", shown.Select(m => $"{m.Value}:{m.Label}"))}] " +
                  $"live={live} AC={PowerModeAc} battery={PowerModeBattery} onBattery={onBattery}");
    }

    private static uint ParseSavedMode(string saved, uint fallback) =>
        uint.TryParse(saved, out uint v) ? v : fallback;

    partial void OnPowerModeAcChanged(uint value)
    {
        if (_loading) return;
        _settings.NvidiaPowerModeAc = value.ToString();
        if (!IsLaptop || !_power.IsOnBattery())
            Apply(_nvapi.SetPowerMode(value), "GPU power management mode");
    }

    partial void OnPowerModeBatteryChanged(uint value)
    {
        if (_loading) return;
        _settings.NvidiaPowerModeBattery = value.ToString();
        if (IsLaptop && _power.IsOnBattery())
            Apply(_nvapi.SetPowerMode(value), "GPU power management mode");
    }

    /// <summary>
    /// Applies the mode that matches the power source we just moved to. The driver holds one
    /// global value, so "a different mode on battery" only exists because we swap it here.
    /// </summary>
    private void ApplyModeForCurrentPowerSource()
    {
        if (!IsNvidiaPresent) return;
        uint want = (IsLaptop && _power.IsOnBattery()) ? PowerModeBattery : PowerModeAc;
        if (_nvapi.GetPowerMode() == want) return;   // already right — don't churn the driver

        var r = _nvapi.SetPowerMode(want);
        _log.Info("NvidiaGpuViewModel",
                  $"Power source changed — applied pstate {want}: {r.Message}");
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

    private Microsoft.Win32.PowerModeChangedEventHandler? _powerModeChanged;

    public void Dispose()
    {
        if (_powerModeChanged != null)
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= _powerModeChanged;
            _powerModeChanged = null;
        }
    }
}
