// ════════════════════════════════════════════════════════════════════════════
// GraphicsTweaksService.cs  ·  User-controlled Windows graphics toggles
// ════════════════════════════════════════════════════════════════════════════
//
// Exposes three graphics settings that Windows itself surfaces (Settings → Display
// → Graphics), as reflect-current-state toggles. Nothing is ever applied on install
// or launch — the ViewModel reads the live state and only writes when the USER
// flips a toggle. A separate launch-time reinforcement (App.xaml.cs) re-asserts a
// value ONLY when the user has an explicit saved preference for it.
//
//   1. Multi-Plane Overlay (MPO)  → HKLM\...\Dwm\OverlayTestMode = 5 disables it.
//      Disabling MPO is Microsoft's own documented workaround for the flicker /
//      stutter / poor-frame-timing caused by some GPU drivers' MPO integration.
//   2. Hardware-accelerated GPU Scheduling (HAGS) → HKLM\...\GraphicsDrivers\HwSchMode
//      (2 = on, 1 = off). Same toggle as the Windows Graphics settings page.
//   3. Optimizations for windowed games → HKCU UserGpuPreferences
//      DirectXUserGlobalSettings "SwapEffectUpgradeEnable" (1 = on, 0 = off).
//
// MPO + HAGS live under HKLM and need admin (Systema runs elevated) and a REBOOT
// to take effect. Windowed optimizations are HKCU and take effect on next game launch.
//
// SAFETY NOTE: MPO (DWM) and HAGS (GraphicsDrivers) are the GPU-adjacent areas that
// historically broke VSync when changed automatically. Here they are MANUAL,
// reflect-state toggles that change nothing unless the user opts in — the safe way
// to expose exactly what Windows already exposes.
// ════════════════════════════════════════════════════════════════════════════

using System.Runtime.InteropServices;
using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

public class GraphicsTweaksService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    private const string DwmKey      = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string GfxDrvKey   = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string GpuPrefKey  = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string GpuPrefValue = "DirectXUserGlobalSettings";

    // ════════════════════════════════════════════════════════════════════════
    //  1 — Multi-Plane Overlay (MPO)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>True when MPO is disabled (OverlayTestMode = 5).</summary>
    public bool IsMpoDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DwmKey);
            return key?.GetValue("OverlayTestMode") is int v && v == 5;
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"IsMpoDisabled read failed: {ex.Message}"); return false; }
    }

    public TweakResult SetMpoDisabled(bool disable)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(DwmKey, writable: true);
            if (key == null) return TweakResult.Fail("Could not open the DWM key (run as Administrator).");
            if (disable) key.SetValue("OverlayTestMode", 5, RegistryValueKind.DWord);
            else         key.DeleteValue("OverlayTestMode", throwOnMissingValue: false); // restore Windows default
            // Verify the registry actually reflects the change (a group policy or driver tool can reject it).
            bool now = key.GetValue("OverlayTestMode") is int v && v == 5;
            if (now != disable)
                return TweakResult.Fail(disable
                    ? "Couldn't disable Multi-Plane Overlay (the value didn't stick). A driver tool or policy may control it."
                    : "Couldn't restore Multi-Plane Overlay to default (the value didn't clear).");
            Log.Info("GraphicsTweaks", $"MPO {(disable ? "disabled (OverlayTestMode=5)" : "restored to default")} — verified");
            return TweakResult.Ok(disable
                ? "Multi-Plane Overlay disabled. Restart your PC to apply."
                : "Multi-Plane Overlay restored to the Windows default. Restart your PC to apply.");
        }
        catch (Exception ex) { Log.Error("GraphicsTweaks", "SetMpoDisabled failed", ex); return TweakResult.FromException(ex); }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  2 — Hardware-accelerated GPU Scheduling (HAGS)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>True when HAGS is enabled (HwSchMode = 2).</summary>
    public bool IsHagsEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GfxDrvKey);
            return key?.GetValue("HwSchMode") is int v && v == 2;
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"IsHagsEnabled read failed: {ex.Message}"); return false; }
    }

    /// <summary>True when the GPU/driver exposes HAGS at all (HwSchMode present, not 0).</summary>
    public bool IsHagsSupported()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GfxDrvKey);
            return key?.GetValue("HwSchMode") is int v && v != 0;
        }
        catch { return false; }
    }

    public TweakResult SetHags(bool enable)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(GfxDrvKey, writable: true);
            if (key == null) return TweakResult.Fail("Could not open the GraphicsDrivers key (run as Administrator).");
            key.SetValue("HwSchMode", enable ? 2 : 1, RegistryValueKind.DWord);
            bool now = key.GetValue("HwSchMode") is int v && v == 2;
            if (now != enable)
                return TweakResult.Fail("Couldn't change GPU scheduling (the value didn't stick). A driver tool or policy may control it.");
            Log.Info("GraphicsTweaks", $"HAGS {(enable ? "enabled (HwSchMode=2)" : "disabled (HwSchMode=1)")} — verified");
            return TweakResult.Ok($"Hardware-accelerated GPU scheduling {(enable ? "enabled" : "disabled")}. Restart your PC to apply.");
        }
        catch (Exception ex) { Log.Error("GraphicsTweaks", "SetHags failed", ex); return TweakResult.FromException(ex); }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  4 — Extend GPU recovery timeout (TdrDelay)
    // ════════════════════════════════════════════════════════════════════════
    // Windows resets the GPU ("display driver stopped responding and has recovered"
    // — the black-screen flash) after only ~2s. Raising TdrDelay to 10s lets the GPU
    // recover from a brief stall instead of hard-resetting, killing many of those
    // black blips. HKLM, admin, restart to apply. OFF deletes the value so it goes
    // straight back to the Windows default (no leftover override).

    private const int TdrDelaySeconds = 10;

    /// <summary>True when the extended 10-second TdrDelay is in force.</summary>
    public bool IsTdrDelayExtended()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GfxDrvKey);
            return key?.GetValue("TdrDelay") is int v && v == TdrDelaySeconds;
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"IsTdrDelayExtended read failed: {ex.Message}"); return false; }
    }

    public TweakResult SetTdrDelayExtended(bool extend)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(GfxDrvKey, writable: true);
            if (key == null) return TweakResult.Fail("Could not open the GraphicsDrivers key (run as Administrator).");
            if (extend) key.SetValue("TdrDelay", TdrDelaySeconds, RegistryValueKind.DWord);
            else        key.DeleteValue("TdrDelay", throwOnMissingValue: false); // restore Windows default (~2s)
            bool now = key.GetValue("TdrDelay") is int v && v == TdrDelaySeconds;
            if (now != extend)
                return TweakResult.Fail(extend
                    ? "Couldn't extend the GPU recovery timeout (the value didn't stick)."
                    : "Couldn't restore the GPU recovery timeout to default (the value didn't clear).");
            Log.Info("GraphicsTweaks", $"TdrDelay {(extend ? $"set to {TdrDelaySeconds}s" : "removed (restored default)")} — verified");
            return TweakResult.Ok(extend
                ? $"GPU recovery timeout extended to {TdrDelaySeconds} seconds. Restart your PC to apply."
                : "GPU recovery timeout restored to the Windows default. Restart your PC to apply.");
        }
        catch (Exception ex) { Log.Error("GraphicsTweaks", "SetTdrDelayExtended failed", ex); return TweakResult.FromException(ex); }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  3 — Optimizations for windowed games
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>True when "Optimizations for windowed games" is on (SwapEffectUpgradeEnable=1).</summary>
    public bool IsWindowedOptimizationsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GpuPrefKey);
            string s = key?.GetValue(GpuPrefValue) as string ?? "";
            return ParseToken(s, "SwapEffectUpgradeEnable") == "1";
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"IsWindowedOptimizationsEnabled read failed: {ex.Message}"); return false; }
    }

    public TweakResult SetWindowedOptimizations(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuPrefKey, writable: true);
            if (key == null) return TweakResult.Fail("Could not open the DirectX preferences key.");
            string cur = key.GetValue(GpuPrefValue) as string ?? "";
            string updated = UpsertToken(cur, "SwapEffectUpgradeEnable", enable ? "1" : "0");
            key.SetValue(GpuPrefValue, updated, RegistryValueKind.String);
            bool now = ParseToken(key.GetValue(GpuPrefValue) as string ?? "", "SwapEffectUpgradeEnable") == "1";
            if (now != enable)
                return TweakResult.Fail("Couldn't change windowed-game optimizations (the value didn't stick).");
            Log.Info("GraphicsTweaks", $"Windowed-game optimizations {(enable ? "enabled" : "disabled")} (\"{updated}\") — verified");
            return TweakResult.Ok($"Optimizations for windowed games {(enable ? "enabled" : "disabled")}. Restart any open games to apply.");
        }
        catch (Exception ex) { Log.Error("GraphicsTweaks", "SetWindowedOptimizations failed", ex); return TweakResult.FromException(ex); }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  5 — Stable timer resolution (0.5 ms)
    // ════════════════════════════════════════════════════════════════════════
    // The Windows system timer idles around 15.6 ms and only rises when an app asks for
    // finer granularity. Pinning it to 0.5 ms makes the scheduler tick more often, which
    // can tighten frame pacing / input latency and steady out FPS in some games — at the
    // cost of the CPU waking more often (more heat and power draw, worst on a laptop).
    //
    // Since Win10 2004 a process's timer request is honoured PER-PROCESS, not globally.
    // GlobalTimerResolutionRequests=1 (HKLM, reboot) restores the old global behaviour so a
    // single process's request raises the timer for the whole system; Systema then provides
    // that request via NtSetTimerResolution while it runs (re-issued on launch — App.xaml.cs).
    // Nothing is applied on install — the toggle is OFF until the user opts in, and OFF here
    // removes the value and releases the request.
    private const string KernelKey            = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const uint   TargetResolution100ns = 5000;   // 0.5 ms, in 100-ns units
    private const string SystemaKey           = @"Software\Systema";        // HKCU — Systema metadata
    private const string TimerSetAtValue      = "TimerResolutionSetAtUtc";  // when the global policy was enabled (DateTime ticks)

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint Minimum, out uint Maximum, out uint Current);
    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint DesiredResolution, bool SetResolution, out uint CurrentResolution);

    /// <summary>The current system timer resolution in milliseconds (live).</summary>
    public double GetTimerResolutionMs()
    {
        try { if (NtQueryTimerResolution(out _, out _, out uint cur) == 0) return cur / 10000.0; }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"NtQueryTimerResolution failed: {ex.Message}"); }
        return 0;
    }

    /// <summary>Human-readable current resolution, e.g. "0.50 ms (high-resolution)" or
    /// "15.60 ms (Windows default)".</summary>
    public string GetTimerResolutionText()
    {
        double ms = GetTimerResolutionMs();
        if (ms <= 0) return "unknown";
        string s = $"{ms:0.00} ms";
        if (ms >= 15.0)      s += " (Windows default)";
        else if (ms <= 0.6)  s += " (high-resolution)";
        return s;
    }

    /// <summary>True when Systema's global high-resolution timer request is in force
    /// (GlobalTimerResolutionRequests = 1).</summary>
    public bool IsTimerResolutionForced()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KernelKey);
            return key?.GetValue("GlobalTimerResolutionRequests") is int v && v == 1;
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"IsTimerResolutionForced read failed: {ex.Message}"); return false; }
    }

    // ── System-wide activation state ──────────────────────────────────────────
    // GlobalTimerResolutionRequests is read by the kernel at BOOT, so enabling it doesn't affect
    // other processes until the next restart. We stamp WHEN it was enabled and compare against the
    // machine's boot time to tell "restart still pending" apart from "active system-wide".

    private static DateTime BootTimeUtc() =>
        DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static void StampTimerEnabledAt(long ticks)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SystemaKey, writable: true);
            key?.SetValue(TimerSetAtValue, ticks, RegistryValueKind.QWord);
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"StampTimerEnabledAt failed: {ex.Message}"); }
    }

    /// <summary>Records/clears the timestamp of when the global timer policy was enabled.</summary>
    private static void SetTimerEnabledStamp(bool on)
    {
        if (on) { StampTimerEnabledAt(DateTime.UtcNow.Ticks); return; }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SystemaKey, writable: true);
            key?.DeleteValue(TimerSetAtValue, throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"SetTimerEnabledStamp(off) failed: {ex.Message}"); }
    }

    /// <summary>True when the global 0.5 ms policy is not just set but ACTIVE — the machine has
    /// booted since it was enabled, so GlobalTimerResolutionRequests is in effect for every process.
    /// False when the flag is set but a restart is still pending.</summary>
    public bool IsTimerResolutionActiveSystemWide()
    {
        if (!IsTimerResolutionForced()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SystemaKey);
            if (key?.GetValue(TimerSetAtValue) is long ticks)
                return BootTimeUtc() > new DateTime(ticks, DateTimeKind.Utc);

            // Legacy: flag on but no stamp (enabled by a build before this tracking existed). It was
            // set in a prior session, so it's already active — backfill a boot-time stamp so the
            // state stays correct from here on.
            StampTimerEnabledAt(BootTimeUtc().Ticks);
            return true;
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"IsTimerResolutionActiveSystemWide failed: {ex.Message}"); return true; }
    }

    // ── Holding the 0.5 ms request + reinforcing it if it drifts ──────────────
    // NtSetTimerResolution is held only while THIS process keeps requesting it, so a one-shot
    // call can quietly drift back. We simply re-issue it on a short loop so anything that
    // changes the resolution is corrected within seconds. Deliberately uses ONLY the same
    // documented ntdll timer API — no process/power-state calls — to keep the unsigned binary
    // conservative. (Trade-off: with a background process throttled by Win11, the resolution
    // can briefly dip to 1 ms between re-pins instead of holding rock-steady at 0.5 ms.)
    private CancellationTokenSource? _timerHoldCts;

    /// <summary>Issues the 0.5 ms request and keeps it pinned by re-asserting it every ~20 s,
    /// so if the resolution drifts it's corrected automatically. Called on launch (when opted
    /// in) and when the user turns the toggle on. Safe to call repeatedly.</summary>
    public void StartTimerResolutionHold()
    {
        StopTimerResolutionHold();
        var cts = new CancellationTokenSource();
        _timerHoldCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    NtSetTimerResolution(TargetResolution100ns, true, out _);   // re-pin if it drifted back
                    await Task.Delay(TimeSpan.FromSeconds(20), cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("GraphicsTweaks", $"Timer-hold loop failed: {ex.Message}"); }
        }, cts.Token);
        Log.Info("GraphicsTweaks", "Timer-resolution hold started (20 s re-pin).");
    }

    /// <summary>Stops the re-pin loop and releases the request.</summary>
    public void StopTimerResolutionHold()
    {
        _timerHoldCts?.Cancel();
        _timerHoldCts = null;
        try { NtSetTimerResolution(TargetResolution100ns, false, out _); } catch { /* best effort */ }
    }

    public TweakResult SetTimerResolution(bool on)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(KernelKey, writable: true);
            if (key == null) return TweakResult.Fail("Could not open the kernel key (run as Administrator).");
            if (on)
            {
                key.SetValue("GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord);
                SetTimerEnabledStamp(true);   // record when, so we can detect the pending restart
                StartTimerResolutionHold();   // issue + keep pinned (re-pin loop)
                Log.Info("GraphicsTweaks", "Timer resolution forced to 0.5 ms (GlobalTimerResolutionRequests=1 + hold started)");
                return TweakResult.Ok("Timer resolution set to 0.5 ms and kept pinned. Restart your PC so it applies system-wide.");
            }
            StopTimerResolutionHold();        // stop the loop, release the request
            SetTimerEnabledStamp(false);
            key.DeleteValue("GlobalTimerResolutionRequests", throwOnMissingValue: false);
            Log.Info("GraphicsTweaks", "Timer resolution restored to Windows default (hold stopped + flag removed)");
            return TweakResult.Ok("Timer resolution restored to the Windows default. Restart your PC to fully apply.");
        }
        catch (Exception ex) { Log.Error("GraphicsTweaks", "SetTimerResolution failed", ex); return TweakResult.FromException(ex); }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  6 — Game DVR / Game Bar background capture
    // ════════════════════════════════════════════════════════════════════════
    // The Xbox Game Bar's background recording ("Capture") keeps a rolling buffer while
    // you play — overhead that adds frame-time variance and a little input latency.
    // Turning it off removes that overhead. Two HKCU values: GameDVR_Enabled (the user
    // Game DVR switch) and AppCaptureEnabled (the Game Bar capture switch). Reflect-state
    // only — nothing is applied on install; the toggle mirrors the live value and writes
    // only when the user flips it. Reversible; applies on the next game launch.
    private const string GameConfigStoreKey = @"System\GameConfigStore";
    private const string GameDvrKey         = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";

    /// <summary>True when Game DVR / Game Bar background capture is OFF (GameDVR_Enabled = 0).</summary>
    public bool IsGameDvrDisabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GameConfigStoreKey);
            return key?.GetValue("GameDVR_Enabled") is int v && v == 0;
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"IsGameDvrDisabled read failed: {ex.Message}"); return false; }
    }

    public TweakResult SetGameDvrDisabled(bool disable)
    {
        try
        {
            using (var gcs = Registry.CurrentUser.CreateSubKey(GameConfigStoreKey, writable: true))
                gcs?.SetValue("GameDVR_Enabled", disable ? 0 : 1, RegistryValueKind.DWord);
            using (var gdvr = Registry.CurrentUser.CreateSubKey(GameDvrKey, writable: true))
                gdvr?.SetValue("AppCaptureEnabled", disable ? 0 : 1, RegistryValueKind.DWord);
            // Verify the primary switch took (GameDVR_Enabled is the one Windows and games read).
            using (var chk = Registry.CurrentUser.OpenSubKey(GameConfigStoreKey))
            {
                bool now = chk?.GetValue("GameDVR_Enabled") is int v && v == 0;
                if (now != disable)
                    return TweakResult.Fail("Couldn't change Game Bar background capture (the value didn't stick).");
            }
            Log.Info("GraphicsTweaks", $"Game DVR capture {(disable ? "disabled" : "enabled")} (GameDVR_Enabled={(disable ? 0 : 1)}) — verified");
            return TweakResult.Ok(disable
                ? "Game Bar background capture turned off — less overhead while gaming. Restart any open games to apply."
                : "Game Bar background capture restored to the Windows default. Restart any open games to apply.");
        }
        catch (Exception ex) { Log.Error("GraphicsTweaks", "SetGameDvrDisabled failed", ex); return TweakResult.FromException(ex); }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  6 — Priority graphics scheduling (MMCSS multimedia tasks)
    // ════════════════════════════════════════════════════════════════════════
    // The mirror of the Audio tab's "Priority audio scheduling". Windows' Multimedia Class Scheduler
    // (MMCSS) has per-workload tasks; raising the GRAPHICAL ones — Games (3D/DirectX), Playback (video),
    // Capture (screen capture / streaming) — to the top SAFE tier makes the scheduler favour those
    // threads so background work stutters them less.
    //
    // Safety line: we cap at Scheduling Category "High" (MMCSS's highest tier) and thread Priority 6 —
    // NOT true realtime. Realtime-priority threads can starve the OS, input, and the compositor and
    // freeze the machine. GPU Priority is left at its default so we don't contend with DWM's GPU work.
    //
    // NOTE on "Window Manager" (DWM): it is included here at the owner's explicit request. Boosting
    // DWM's THREAD SCHEDULING priority is a different mechanism from changing the present/flip path
    // (MPO, GPU scheduling mode) that caused Systema's past VSync/tearing regression — giving DWM more
    // CPU priority generally helps it hit its frame deadlines, not miss them. DWM is still the
    // sensitive one, so it's fully reversible: if any tearing/jank appears, toggling off restores
    // every task's original values exactly (captured before the first change).
    private const string MmcssTasksKey     = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks";
    private const string GfxSchedDefaultsKey = @"Software\Systema\GfxSchedDefaults";   // HKCU — captured originals
    // Visual MMCSS tasks. Includes "Window Manager" (DWM, the desktop compositor) per explicit request.
    internal static readonly string[] GraphicsMmcssTasks = { "Games", "Playback", "Capture", "Window Manager" };

    /// <summary>True when the graphics MMCSS tasks are boosted (checks the Games task: Scheduling
    /// Category + SFIO Priority both "High").</summary>
    public bool IsGraphicsSchedulingBoosted()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{MmcssTasksKey}\Games");
            return key != null
                && string.Equals(key.GetValue("Scheduling Category") as string, "High", StringComparison.OrdinalIgnoreCase)
                && string.Equals(key.GetValue("SFIO Priority")       as string, "High", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"IsGraphicsSchedulingBoosted read failed: {ex.Message}"); return false; }
    }

    public TweakResult SetGraphicsSchedulingBoosted(bool boost)
    {
        try
        {
            int changed = 0;
            foreach (var task in GraphicsMmcssTasks)
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{MmcssTasksKey}\{task}", writable: true);
                if (key == null) continue;   // task not present on this machine

                if (boost)
                {
                    CaptureGfxSched(task, "Scheduling Category", key.GetValue("Scheduling Category") as string ?? "Medium");
                    CaptureGfxSched(task, "SFIO Priority",       key.GetValue("SFIO Priority")       as string ?? "Normal");
                    CaptureGfxSchedInt(task, "Priority",         key.GetValue("Priority") is int pv ? pv : 2);
                    key.SetValue("Scheduling Category", "High", RegistryValueKind.String);
                    key.SetValue("SFIO Priority",       "High", RegistryValueKind.String);
                    key.SetValue("Priority", 6, RegistryValueKind.DWord);   // 6 = boosted, still below realtime
                }
                else
                {
                    key.SetValue("Scheduling Category", TakeGfxSched(task, "Scheduling Category", "Medium"), RegistryValueKind.String);
                    key.SetValue("SFIO Priority",       TakeGfxSched(task, "SFIO Priority",       "Normal"), RegistryValueKind.String);
                    key.SetValue("Priority",            TakeGfxSchedInt(task, "Priority", 2),               RegistryValueKind.DWord);
                }
                changed++;
            }
            Log.Info("GraphicsTweaks", $"Graphics scheduling {(boost ? "boosted (High/High/6)" : "restored to defaults")} on {changed} task(s): {string.Join(", ", GraphicsMmcssTasks)}");
            return TweakResult.Ok(boost
                ? "Graphics scheduling prioritized for games, video, and capture. Restart your PC to apply."
                : "Graphics scheduling restored to the Windows default. Restart your PC to apply.");
        }
        catch (Exception ex) { Log.Error("GraphicsTweaks", "SetGraphicsSchedulingBoosted failed", ex); return TweakResult.FromException(ex); }
    }

    private static void CaptureGfxSched(string task, string name, string value)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(GfxSchedDefaultsKey, writable: true);
            if (k != null && k.GetValue($"{task}|{name}") == null) k.SetValue($"{task}|{name}", value, RegistryValueKind.String);
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"CaptureGfxSched({task}) failed: {ex.Message}"); }
    }
    private static void CaptureGfxSchedInt(string task, string name, int value)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(GfxSchedDefaultsKey, writable: true);
            if (k != null && k.GetValue($"{task}|{name}") == null) k.SetValue($"{task}|{name}", value, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"CaptureGfxSchedInt({task}) failed: {ex.Message}"); }
    }
    private static string TakeGfxSched(string task, string name, string fallback)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(GfxSchedDefaultsKey, writable: true);
            if (k?.GetValue($"{task}|{name}") is string s) { k.DeleteValue($"{task}|{name}", throwOnMissingValue: false); return s; }
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"TakeGfxSched({task}) failed: {ex.Message}"); }
        return fallback;
    }
    private static int TakeGfxSchedInt(string task, string name, int fallback)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(GfxSchedDefaultsKey, writable: true);
            if (k?.GetValue($"{task}|{name}") is int v) { k.DeleteValue($"{task}|{name}", throwOnMissingValue: false); return v; }
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"TakeGfxSchedInt({task}) failed: {ex.Message}"); }
        return fallback;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Reinforcement — re-assert the user's saved graphics choices
    // ════════════════════════════════════════════════════════════════════════
    // A GPU driver update or Windows feature update can reset HwSchMode / OverlayTestMode / TdrDelay,
    // and Windows re-enables Game DVR after some updates. This re-applies ONLY the choices the user
    // explicitly made, and ONLY when the live value has actually drifted off it — never on a fresh
    // install and never a redundant write. MPO and TdrDelay are Auto-Pilot-managed, so when Auto-Pilot
    // is on we leave those to it. hagsPref / windowedPref: -1 = no preference (skip).

    /// <summary>Re-asserts drifted graphics tweaks the user set. Safe to call on every launch.</summary>
    public void ReinforceGraphicsFromIntent(bool mpoDisabled, int hagsPref, bool tdrExtended,
                                            int windowedPref, bool gameDvrDisabled, bool autoPilotActive)
    {
        try
        {
            if (!autoPilotActive)
            {
                if (mpoDisabled && !IsMpoDisabled())
                    Log.Info("GraphicsTweaks", $"MPO reinforced on drift: {SetMpoDisabled(true).Message}");
                if (tdrExtended && !IsTdrDelayExtended())
                    Log.Info("GraphicsTweaks", $"TdrDelay reinforced on drift: {SetTdrDelayExtended(true).Message}");
            }
            if (gameDvrDisabled && !IsGameDvrDisabled())
                Log.Info("GraphicsTweaks", $"Game DVR reinforced on drift: {SetGameDvrDisabled(true).Message}");
            if (hagsPref >= 0 && IsHagsSupported() && IsHagsEnabled() != (hagsPref == 1))
                Log.Info("GraphicsTweaks", $"HAGS reinforced on drift: {SetHags(hagsPref == 1).Message}");
            if (windowedPref >= 0 && IsWindowedOptimizationsEnabled() != (windowedPref == 1))
                Log.Info("GraphicsTweaks", $"Windowed optimizations reinforced on drift: {SetWindowedOptimizations(windowedPref == 1).Message}");
        }
        catch (Exception ex) { Log.Warn("GraphicsTweaks", $"ReinforceGraphicsFromIntent failed: {ex.Message}"); }
    }

    // ── DirectXUserGlobalSettings token helpers ("Key1=Val1;Key2=Val2;") ───────
    private static string ParseToken(string s, string keyName)
    {
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals(keyName, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }
        return "";
    }

    private static string UpsertToken(string s, string keyName, string value)
    {
        var parts = new List<string>();
        bool replaced = false;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals(keyName, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"{keyName}={value}");
                replaced = true;
            }
            else parts.Add(part.Trim());
        }
        if (!replaced) parts.Add($"{keyName}={value}");
        // Windows stores these with a trailing ';'
        return string.Join(";", parts) + ";";
    }
}
