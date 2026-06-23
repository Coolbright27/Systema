// ════════════════════════════════════════════════════════════════════════════
// GraphicsViewModel.cs  ·  "Graphics" sidebar tab
// ════════════════════════════════════════════════════════════════════════════
//
// Three Windows graphics toggles (MPO, HAGS, windowed-game optimizations) that
// REFLECT the live system state and only change it when the user flips a toggle —
// nothing is applied on install or launch. Each toggle, once set, is reinforced on
// launch (App.xaml.cs) so the user's choice survives Windows/driver resets. All
// three require a restart (PC, or the game for windowed optimizations) to take effect.
//
// RELATED FILES
//   Services/GraphicsTweaksService.cs — the registry reads/writes
//   Views/GraphicsView.xaml           — the three cards + Intel link
//   App.xaml.cs                       — launch-time reinforcement of saved prefs
// ════════════════════════════════════════════════════════════════════════════

using CommunityToolkit.Mvvm.ComponentModel;
using Systema.Core;
using Systema.Services;

namespace Systema.ViewModels;

public partial class GraphicsViewModel : ObservableObject, IAutoRefreshable, IDisposable
{
    private readonly GraphicsTweaksService _gfx;
    private readonly SettingsService       _settings;
    private static readonly LoggerService _log = LoggerService.Instance;

    // True only while reading live state into the toggles — suppresses the
    // OnChanged → apply round-trip so reflecting state never writes anything.
    private bool _loading;

    [ObservableProperty] private bool _disableMpo;
    [ObservableProperty] private bool _hardwareGpuScheduling;
    [ObservableProperty] private bool _hagsSupported = true;
    [ObservableProperty] private bool _windowedOptimizations;
    [ObservableProperty] private bool _extendGpuRecoveryTimeout;
    [ObservableProperty] private bool _forceTimerResolution;
    [ObservableProperty] private string _timerResolutionDisplay = "—";
    [ObservableProperty] private bool _disableGameDvr;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>True when Auto-Pilot Mode is on. MPO is an Auto-Pilot-managed setting,
    /// so its toggle grays out while Auto-Pilot is on (same pattern as Core Parking).</summary>
    public bool IsAutoPilotActive => _settings.AutoPilotModeEnabled;

    public GraphicsViewModel(GraphicsTweaksService gfx, SettingsService settings)
    {
        _gfx      = gfx;
        _settings = settings;
        SettingsService.AutoPilotModeChanged += OnAutoPilotModeChanged;
        LoadState();
    }

    private void OnAutoPilotModeChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            () => OnPropertyChanged(nameof(IsAutoPilotActive)));

    public void Dispose() => SettingsService.AutoPilotModeChanged -= OnAutoPilotModeChanged;

    /// <summary>Reads the live system state into the toggles without applying anything.</summary>
    private void LoadState()
    {
        _loading = true;
        try
        {
            DisableMpo               = _gfx.IsMpoDisabled();
            HagsSupported            = _gfx.IsHagsSupported();
            HardwareGpuScheduling    = _gfx.IsHagsEnabled();
            WindowedOptimizations    = _gfx.IsWindowedOptimizationsEnabled();
            ExtendGpuRecoveryTimeout = _gfx.IsTdrDelayExtended();
            ForceTimerResolution     = _gfx.IsTimerResolutionForced();
            TimerResolutionDisplay   = _gfx.GetTimerResolutionText();
            DisableGameDvr           = _gfx.IsGameDvrDisabled();
        }
        finally { _loading = false; }
    }

    public Task RefreshAsync()
    {
        LoadState();
        return Task.CompletedTask;
    }

    // ── Toggle handlers — write only on a real user change, then persist the pref ─

    partial void OnDisableMpoChanged(bool value)
    {
        if (_loading) return;
        // MPO is managed by Auto-Pilot while it's on — don't let the (grayed) toggle apply.
        if (IsAutoPilotActive) { _loading = true; DisableMpo = !value; _loading = false; return; }
        var r = _gfx.SetMpoDisabled(value);
        StatusMessage = r.Message;
        if (!r.Success) { _loading = true; DisableMpo = !value; _loading = false; }
    }

    partial void OnHardwareGpuSchedulingChanged(bool value)
    {
        if (_loading) return;
        var r = _gfx.SetHags(value);
        StatusMessage = r.Message;
        if (!r.Success) { _loading = true; HardwareGpuScheduling = !value; _loading = false; }
    }

    partial void OnWindowedOptimizationsChanged(bool value)
    {
        if (_loading) return;
        var r = _gfx.SetWindowedOptimizations(value);
        StatusMessage = r.Message;
        if (!r.Success) { _loading = true; WindowedOptimizations = !value; _loading = false; }
    }

    partial void OnExtendGpuRecoveryTimeoutChanged(bool value)
    {
        if (_loading) return;
        // Auto-Pilot-managed while it's on — don't let the (grayed) toggle apply.
        if (IsAutoPilotActive) { _loading = true; ExtendGpuRecoveryTimeout = !value; _loading = false; return; }
        var r = _gfx.SetTdrDelayExtended(value);
        StatusMessage = r.Message;
        if (!r.Success) { _loading = true; ExtendGpuRecoveryTimeout = !value; _loading = false; }
    }

    partial void OnForceTimerResolutionChanged(bool value)
    {
        if (_loading) return;
        var r = _gfx.SetTimerResolution(value);
        StatusMessage = r.Message;
        if (!r.Success) { _loading = true; ForceTimerResolution = !value; _loading = false; }
        // Reflect the new live value straight away (it changes the moment we request it).
        TimerResolutionDisplay = _gfx.GetTimerResolutionText();
    }

    partial void OnDisableGameDvrChanged(bool value)
    {
        if (_loading) return;
        var r = _gfx.SetGameDvrDisabled(value);
        StatusMessage = r.Message;
        if (!r.Success) { _loading = true; DisableGameDvr = !value; _loading = false; }
    }
}
