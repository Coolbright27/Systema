// ════════════════════════════════════════════════════════════════════════════
// IntelGpuViewModel.cs  ·  Intel iGPU control panel (Intel-only sidebar section)
// ════════════════════════════════════════════════════════════════════════════
//
// Backs Views/IntelView.xaml. Detects the Intel adapter(s) and resolves each setting
// against the LIVE registry. Writes target the ACTIVE (present) GPU — see
// IntelGpuService.DetectIntelAdapters / PrimaryAdapter.
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Systema.Core;
using Systema.Services;

namespace Systema.ViewModels;

public partial class IntelGpuViewModel : ObservableObject, IDisposable
{
    private readonly IntelGpuService _service;
    private readonly SettingsService _settings;
    private static readonly LoggerService _log = LoggerService.Instance;

    private List<IntelAdapter> _adapters = new();

    // Value names from the last registry read — kept so DPST support can be re-evaluated
    // once async laptop detection resolves.
    private string[] _lastValueNames = Array.Empty<string>();

    private static readonly string[] PowerPolicyNames = { IntelGpuService.PowerPolicy };
    private static readonly string[] Rc6Names         = { IntelGpuService.RC6 };
    private static readonly string[] DpstNames        = { IntelGpuService.DpstEnable };
    private static readonly string[] DrrsNames        = { IntelGpuService.DrrsEnabled, IntelGpuService.Psr2DrrsEnable };

    private string _powerPolicyName = IntelGpuService.PowerPolicy;
    private string _rc6Name         = IntelGpuService.RC6;

    // Suppresses apply-on-change handlers while populating controls from the registry.
    private bool _loading;

    public const string PolicyDefault  = "Default (driver-controlled)";
    public const string PolicyBalanced = "Balanced";
    public const string PolicyMax      = "Max Performance";

    [ObservableProperty] private bool   _isIntelPresent;
    [ObservableProperty] private bool   _isLaptop;
    [ObservableProperty] private string _adapterName = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool   _showRebootHint;
    [ObservableProperty] private bool   _anySettingSupported;

    // ── Power Policy ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _showPowerPolicy;
    public ObservableCollection<string> PowerPolicyOptions { get; } =
        new() { PolicyDefault, PolicyBalanced, PolicyMax };
    [ObservableProperty] private string _selectedPowerPolicy = PolicyDefault;
    [ObservableProperty] private string _powerPolicyDetected = "";

    // ── RC6 Render Standby ─────────────────────────────────────────────────────
    [ObservableProperty] private bool   _showRc6;
    [ObservableProperty] private bool   _rc6On;
    [ObservableProperty] private string _rc6Detected = "";

    // NOTE: Panel Self Refresh (PSR) toggle was REMOVED. Disabling PSR made some laptop
    // panels flicker or go completely black on battery — and a black screen is impossible
    // to recover from in-app. PSR is now always left at the driver default (On). Reset /
    // Revert All still DELETE any stale PSR2Disable/PanelSelfRefreshEnable an older build
    // wrote, so a previously-affected machine self-heals.

    // ── Display Power Saving (DPST) ────────────────────────────────────────────
    [ObservableProperty] private bool   _showDpst;
    [ObservableProperty] private bool   _dpstOn;
    [ObservableProperty] private string _dpstDetected = "";

    // ── Dynamic Refresh Switching (DRRS) ───────────────────────────────────────
    [ObservableProperty] private bool   _showDrrs;
    [ObservableProperty] private bool   _drrsOn;
    [ObservableProperty] private string _drrsDetected = "";
    [ObservableProperty] private bool   _drrsNoEffect;
    [ObservableProperty] private string _drrsNoEffectNote = "";

    // ── Frame Buffer Compression (FBC) ─────────────────────────────────────────
    [ObservableProperty] private bool   _showFbc;
    [ObservableProperty] private bool   _fbcOn;
    [ObservableProperty] private string _fbcDetected = "";

    // Opt-in: re-apply the saved profile after Intel driver updates.
    [ObservableProperty] private bool   _reapplyAfterDriverUpdate;

    public IntelGpuViewModel(IntelGpuService service, SettingsService settings)
    {
        _service  = service;
        _settings = settings;

        _adapters = _service.DetectIntelAdapters();
        IsIntelPresent = _adapters.Count > 0;
        if (!IsIntelPresent) { StatusMessage = "No Intel integrated GPU detected."; return; }

        AdapterName = _adapters[0].DriverDesc;
        _reapplyAfterDriverUpdate = _settings.IntelGpuReapplyEnabled;

        LoadFromRegistry();

        _ = Task.Run(() =>
        {
            bool laptop  = _service.IsLaptop();
            var  refresh = _service.GetIntelRefreshRange();

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                IsLaptop = laptop;

                if (IntelGpuService.IsSingleRefreshRate(refresh.Min, refresh.Max))
                {
                    DrrsNoEffect = true;
                    int hz = IntelGpuService.NormalizeRefreshHz(refresh.Max);
                    DrrsNoEffectNote = $"Your screen reports a single {hz}Hz rate, so this won't have a visible effect. It only affects the built-in laptop screen, never external monitors.";
                }
                else
                {
                    DrrsNoEffect = false;
                }

                ShowDpst = IntelGpuService.IsDpstSupported(_lastValueNames, IsLaptop);
                ShowFbc  = HasToken(_lastValueNames, "FBC");
            });
        });
    }

    [RelayCommand]
    private void Detect()
    {
        if (!IsIntelPresent) return;
        _adapters = _service.DetectIntelAdapters();
        IsIntelPresent = _adapters.Count > 0;
        if (IsIntelPresent) { AdapterName = _adapters[0].DriverDesc; LoadFromRegistry(); }
        StatusMessage = "Re-read current Intel settings from the registry.";
    }

    private void LoadFromRegistry()
    {
        _loading = true;
        try
        {
            string path = _adapters[0].FullPath;
            string[] names = _service.GetValueNames(path);
            _lastValueNames = names;

            // ── Power Policy ──
            ShowPowerPolicy = true;
            var pp = _service.ResolveFeature(path, PowerPolicyNames);
            _powerPolicyName = pp.Name ?? IntelGpuService.PowerPolicy;
            SelectedPowerPolicy = pp.Value switch { 2 => PolicyMax, 1 => PolicyBalanced, _ => PolicyDefault };
            PowerPolicyDetected = pp.Value switch { 2 => "Max Performance", 1 => "Balanced", _ => "Driver default" };

            // ── RC6 ──
            ShowRc6 = true;
            _rc6Name = IntelGpuService.RC6;
            var rc6 = _service.ResolveFeature(path, Rc6Names);
            if (rc6.Name != null) { Rc6On = rc6.Value != 0; Rc6Detected = Describe(rc6.Value, isDefault: false); }
            else                  { Rc6On = true;           Rc6Detected = Describe(null, isDefault: true, knownDefaultOn: true); }

            // ── PSR removed (could black the panel) — left at driver default. ──

            // ── DPST ──
            ShowDpst = IntelGpuService.IsDpstSupported(names, IsLaptop);
            var dpst = _service.ResolveFeature(path, DpstNames);
            if (dpst.Name != null) { DpstOn = dpst.Value != 0; DpstDetected = Describe(dpst.Value, isDefault: false); }
            else                   { DpstOn = true;            DpstDetected = Describe(null, isDefault: true, knownDefaultOn: true); }

            // ── DRRS ──
            LoadPanelToggle(path, HasToken(names, "DRRS", "MediaRefreshRate"),
                DrrsNames, IntelGpuService.DrrsEnabled,
                _ => { }, v => { ShowDrrs = v.show; DrrsOn = v.on; DrrsDetected = v.cap; });

            // ── FBC ──
            ShowFbc = HasToken(names, "FBC");
            var fbc = _service.ResolveFeature(path, new[] { IntelGpuService.FbcEnable });
            if (fbc.Name != null) { FbcOn = fbc.Value != 0; FbcDetected = Describe(fbc.Value, isDefault: false); }
            else                  { FbcOn = true;          FbcDetected = Describe(null, isDefault: true, knownDefaultOn: true); }

            AnySettingSupported = true;
            StatusMessage = $"Loaded current settings for {AdapterName}.";
        }
        finally { _loading = false; }
    }

    private void LoadPanelToggle(string path, bool supported, string[] readAliases, string writeName,
                                 Action<string> setName, Action<(bool show, bool on, string cap)> set)
    {
        if (!supported) { setName(writeName); set((false, false, "")); return; }
        var r = _service.ResolveFeature(path, readAliases);
        setName(r.Name ?? writeName);
        if (r.Name != null) set((true, r.Value != 0, Describe(r.Value, isDefault: false)));
        else                set((true, true, Describe(null, isDefault: true, knownDefaultOn: true)));
    }

    private static bool HasToken(string[] names, params string[] tokens)
    {
        foreach (var n in names)
            foreach (var t in tokens)
                if (n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static string Describe(int? value, bool isDefault, bool knownDefaultOn = true)
    {
        if (isDefault) return $"Driver default ({(knownDefaultOn ? "On" : "Off")})";
        return value != 0 ? "On" : "Off";
    }

    // ── Apply handlers ─────────────────────────────────────────────────────────

    partial void OnSelectedPowerPolicyChanged(string value)
    {
        if (_loading) return;
        TweakResult r = value == PolicyMax      ? _service.WriteValue(_adapters, _powerPolicyName, 2)
                      : value == PolicyBalanced ? _service.WriteValue(_adapters, _powerPolicyName, 1)
                      :                           _service.ResetValue(_adapters, _powerPolicyName);
        PowerPolicyDetected = value == PolicyDefault ? "Driver default" : value;
        Apply(r, _powerPolicyName);
    }

    partial void OnRc6OnChanged(bool value)
    {
        if (_loading) return;
        Rc6Detected = value ? "On" : "Off";
        Apply(_service.SetRc6(_adapters, on: value), _rc6Name);
    }

    partial void OnDpstOnChanged(bool value)
    {
        if (_loading) return;
        var r = _service.SetDpst(_adapters, value);
        DpstDetected = value ? "On" : "Off";
        Apply(r, "DPST");
    }

    partial void OnDrrsOnChanged(bool value)
    {
        if (_loading) return;
        var r = _service.SetDrrs(_adapters, value);
        DrrsDetected = value ? "On" : "Off";
        Apply(r, "DRRS");
    }

    partial void OnFbcOnChanged(bool value)
    {
        if (_loading) return;
        var r = _service.SetFbc(_adapters, value);
        FbcDetected = value ? "On" : "Off";
        Apply(r, "FBC");
    }

    private void Apply(TweakResult result, string what)
    {
        StatusMessage = result.Message;
        if (result.Success)
        {
            ShowRebootHint = true;
            PersistProfileIfOptedIn();
            _log.Info("IntelGpuViewModel", $"Applied {what}: {result.Message}");
        }
        else _log.Warn("IntelGpuViewModel", $"Apply {what} failed: {result.Message}");
    }

    // ── Per-row reset + global revert ──────────────────────────────────────────

    [RelayCommand]
    private void ResetSetting(string? id)
    {
        if (string.IsNullOrEmpty(id) || !IsIntelPresent) return;

        _loading = true;
        TweakResult r;
        try
        {
            switch (id)
            {
                case "PowerPolicy":
                    SelectedPowerPolicy = PolicyDefault;
                    PowerPolicyDetected = "Driver default";
                    r = _service.ResetValue(_adapters, _powerPolicyName);
                    break;
                case "RC6":
                    Rc6On = true;
                    Rc6Detected = Describe(null, isDefault: true, knownDefaultOn: true);
                    r = _service.ResetRc6(_adapters);
                    break;
                case "DPST":
                    DpstOn = true; DpstDetected = Describe(null, true, true);
                    _service.ResetValue(_adapters, IntelGpuService.DpstLevel);
                    _service.ResetValue(_adapters, IntelGpuService.DpstExtraDimming);
                    r = _service.ResetValue(_adapters, IntelGpuService.DpstEnable);
                    break;
                case "DRRS":
                    DrrsOn = true; DrrsDetected = Describe(null, true, true);
                    r = Reset2(IntelGpuService.DrrsEnabled, IntelGpuService.Psr2DrrsEnable);
                    break;
                case "FBC":
                    FbcOn = true; FbcDetected = Describe(null, true, true);
                    r = _service.ResetValue(_adapters, IntelGpuService.FbcEnable);
                    break;
                default:
                    return;
            }
        }
        finally { _loading = false; }

        Apply(r, id);
    }

    private TweakResult Reset2(string a, string b)
    {
        var r1 = _service.ResetValue(_adapters, a);
        _service.ResetValue(_adapters, b);
        return r1;
    }

    [RelayCommand]
    private void RevertAll()
    {
        if (!IsIntelPresent) return;
        var r = _service.RevertAll(_adapters);
        LoadFromRegistry();
        ShowRebootHint = true;
        StatusMessage = r.Message;
        _settings.IntelGpuProfile = null;
        _log.Info("IntelGpuViewModel", $"Revert all: {r.Message}");
    }

    partial void OnReapplyAfterDriverUpdateChanged(bool value)
    {
        _settings.IntelGpuReapplyEnabled = value;
        if (value) PersistProfileIfOptedIn(); else _settings.IntelGpuProfile = null;
        _log.Info("IntelGpuViewModel", $"Re-apply after driver update set to {value}.");
    }

    private void PersistProfileIfOptedIn()
    {
        if (!ReapplyAfterDriverUpdate) return;
        var profile = _service.ReadProfile(_adapters[0].FullPath);
        var snapshot = new Dictionary<string, int>();
        foreach (var kv in profile)
            if (kv.Value.Current is int v) snapshot[kv.Key] = v;
        _settings.IntelGpuProfile = snapshot.Count > 0 ? snapshot : null;
    }

    public void Dispose()
    {
    }
}
