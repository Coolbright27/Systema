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
            Log.Info("GraphicsTweaks", $"MPO {(disable ? "disabled (OverlayTestMode=5)" : "restored to default")}");
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
            Log.Info("GraphicsTweaks", $"HAGS {(enable ? "enabled (HwSchMode=2)" : "disabled (HwSchMode=1)")}");
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
            Log.Info("GraphicsTweaks", $"TdrDelay {(extend ? $"set to {TdrDelaySeconds}s" : "removed (restored default)")}");
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
            Log.Info("GraphicsTweaks", $"Windowed-game optimizations {(enable ? "enabled" : "disabled")} (\"{updated}\")");
            return TweakResult.Ok($"Optimizations for windowed games {(enable ? "enabled" : "disabled")}. Restart any open games to apply.");
        }
        catch (Exception ex) { Log.Error("GraphicsTweaks", "SetWindowedOptimizations failed", ex); return TweakResult.FromException(ex); }
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
