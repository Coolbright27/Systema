// ════════════════════════════════════════════════════════════════════════════
// AudioViewModel.cs  ·  "Audio" sidebar tab
// ════════════════════════════════════════════════════════════════════════════
//
// Audio-stability toggles.
//
//   Reflect-only (live registry state, no drift problem):
//     • DisableDucking         → stop Windows lowering other sounds during calls (HKCU)
//     • BoostAudioScheduling   → raise the MMCSS Audio task priority (HKLM), reboot to apply
//
//   Intent-backed + reinforced (these drift when a device reconnects, so the toggle reflects
//   the user's saved INTENT and a 30 s pass re-asserts it where the live state slipped):
//     • DisableAllEnhancements → turn off the enhancement/effects (APO) layer on every output device
//     • DisableSpatialAudio    → null the spatial EFX (Windows Sonic / Dolby / DTS) on every device
//     • DisableMicEnhancements → turn off ALL processing on every microphone (incl. Realtek/Waves)
//
// RELATED FILES
//   Services/AudioService.cs — the registry reads/writes, intent persistence, ReinforceFromIntent
//   Views/AudioView.xaml     — the cards
// ════════════════════════════════════════════════════════════════════════════

using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Systema.Core;
using Systema.Services;

namespace Systema.ViewModels;

public partial class AudioViewModel : ObservableObject, IAutoRefreshable, IDisposable
{
    private readonly AudioService _audio;
    private readonly DispatcherTimer _reinforceTimer;

    // True only while reading live state into the controls — suppresses the
    // OnChanged → apply round-trip so reflecting state never writes anything.
    private bool _loading;

    [ObservableProperty] private bool _disableDucking;
    [ObservableProperty] private bool _boostAudioScheduling;
    [ObservableProperty] private bool _disableAllEnhancements;
    [ObservableProperty] private bool _disableSpatialAudio;
    [ObservableProperty] private bool _disableMicEnhancements;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public AudioViewModel(AudioService audio)
    {
        _audio = audio;
        LoadState();

        // Re-assert intent now (covers a device that reconnected while Systema was closed),
        // then keep re-asserting on a timer so reconnects mid-session are corrected too.
        _ = Task.Run(() => _audio.ReinforceFromIntent());
        _reinforceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _reinforceTimer.Tick += (_, _) => _ = Task.Run(() => _audio.ReinforceFromIntent());
        _reinforceTimer.Start();
    }

    /// <summary>Reads display state: live for the reflect-only toggles, saved INTENT for the
    /// reinforced ones (so a drifted-but-wanted setting still reads as on).</summary>
    private void LoadState()
    {
        _loading = true;
        try
        {
            DisableDucking         = _audio.IsDuckingDisabled();
            BoostAudioScheduling   = _audio.IsAudioSchedulingBoosted();
            DisableAllEnhancements = _audio.GetEnhancementsOffIntent();
            DisableSpatialAudio    = _audio.GetSpatialOffIntent();
            DisableMicEnhancements = _audio.GetMicEnhancementsOffIntent();
        }
        finally { _loading = false; }
    }

    public Task RefreshAsync()
    {
        LoadState();
        return Task.CompletedTask;
    }

    public void Dispose() => _reinforceTimer.Stop();

    // ── Reflect-only toggle handlers ─────────────────────────────────────────

    partial void OnDisableDuckingChanged(bool value)
    {
        if (_loading) return;
        var r = _audio.SetDuckingDisabled(value);
        StatusMessage = r.Message;
        if (!r.Success) { _loading = true; DisableDucking = !value; _loading = false; }
    }

    partial void OnBoostAudioSchedulingChanged(bool value)
    {
        if (_loading) return;
        var r = _audio.SetAudioSchedulingBoosted(value);
        StatusMessage = r.Message;
        if (!r.Success) { _loading = true; BoostAudioScheduling = !value; _loading = false; }
    }

    // ── Intent-backed toggle handlers ────────────────────────────────────────
    // The Set call persists intent + applies + verifies; the toggle stays at the user's choice
    // (intent), so a device that needs a reboot to reflect doesn't make the switch flip back.

    partial void OnDisableAllEnhancementsChanged(bool value)
    {
        if (_loading) return;
        StatusMessage = _audio.SetEnhancementsDisabledEverywhere(value).Message;
    }

    partial void OnDisableSpatialAudioChanged(bool value)
    {
        if (_loading) return;
        StatusMessage = _audio.SetSpatialAudioDisabled(value).Message;
    }

    partial void OnDisableMicEnhancementsChanged(bool value)
    {
        if (_loading) return;
        StatusMessage = _audio.SetMicEnhancementsDisabledEverywhere(value).Message;
    }
}
