// ════════════════════════════════════════════════════════════════════════════
// AudioService.cs  ·  User-controlled Windows audio-stability toggles
// ════════════════════════════════════════════════════════════════════════════
//
// Reflect-current-state toggles for the Audio tab (Batch 1). Nothing is ever applied
// on install or launch — the ViewModel reads the live state and only writes when the
// USER flips a toggle. Each value is a plain registry setting that persists on its
// own, so (unlike the Graphics timer-resolution hold) no launch-time re-assert is
// needed. All writes are reversible.
//
//   1. Communications "ducking"  → HKCU UserDuckingPreference. When Windows detects a
//      call it auto-lowers every OTHER sound (default: −80%). Setting 3 = "do nothing"
//      stops it. Per-user, no admin, takes effect on the next communications session.
//        0 = mute all   1 = reduce 80% (Windows default)   2 = reduce 50%   3 = do nothing
//
//   2. Priority audio scheduling → HKLM MMCSS "Audio" task. Raising its Scheduling
//      Category + SFIO Priority to "High" (what the "Pro Audio" task already uses) keeps
//      audio threads from being starved under heavy CPU/GPU load, which is the real cause
//      of crackle/dropouts. Touches scheduling only — NOT any per-device effect/APO, so
//      it can't change how a device sounds. Needs a reboot (or audio-service restart) to
//      take effect for new streams. Restores to the Windows defaults (Medium / Normal).
//
// SAFETY NOTE: neither toggle touches device tuning, drivers, or the audio services
// themselves — they are exactly the kind of safe, reversible, documented registry
// settings the Graphics tab exposes. The device-level enhancement/spatial/exclusive
// toggles (Batch 2) are deliberately NOT here; those are per-device and must be opt-in.
// ════════════════════════════════════════════════════════════════════════════

using System.Security.AccessControl;
using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

public class AudioService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    // ── 1. Communications "ducking" (HKCU) ──────────────────────────────────
    private const string DuckingKey   = @"Software\Microsoft\Multimedia\Audio";
    private const string DuckingValue = "UserDuckingPreference";
    private const int    DuckingDoNothing   = 3;   // stop lowering other sounds
    private const int    DuckingWindowsDflt  = 1;  // reduce other sounds by 80%

    /// <summary>True when ducking is disabled (UserDuckingPreference = 3 / "do nothing").</summary>
    public bool IsDuckingDisabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DuckingKey);
            return key?.GetValue(DuckingValue) is int v && v == DuckingDoNothing;
        }
        catch (Exception ex) { Log.Warn("Audio", $"IsDuckingDisabled read failed: {ex.Message}"); return false; }
    }

    public TweakResult SetDuckingDisabled(bool disable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(DuckingKey, writable: true);
            if (key == null) return TweakResult.Fail("Could not open the audio ducking key.");
            key.SetValue(DuckingValue, disable ? DuckingDoNothing : DuckingWindowsDflt, RegistryValueKind.DWord);
            Log.Info("Audio", $"Communications ducking {(disable ? "disabled (do nothing)" : "restored to Windows default (−80%)")}");
            return TweakResult.Ok(disable
                ? "Other sounds will no longer be lowered during calls. Applies on your next call."
                : "Restored the Windows default (other sounds drop during calls). Applies on your next call.");
        }
        catch (Exception ex) { Log.Error("Audio", "SetDuckingDisabled failed", ex); return TweakResult.FromException(ex); }
    }

    // ── 2. Priority audio scheduling (HKLM MMCSS "Audio" task) ───────────────
    private const string AudioTaskKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Audio";
    private const string SchedCategory = "Scheduling Category";
    private const string SfioPriority  = "SFIO Priority";

    /// <summary>True when the MMCSS Audio task is boosted (Scheduling Category + SFIO Priority both "High").</summary>
    public bool IsAudioSchedulingBoosted()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AudioTaskKey);
            if (key == null) return false;
            return string.Equals(key.GetValue(SchedCategory) as string, "High", StringComparison.OrdinalIgnoreCase)
                && string.Equals(key.GetValue(SfioPriority)  as string, "High", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Warn("Audio", $"IsAudioSchedulingBoosted read failed: {ex.Message}"); return false; }
    }

    public TweakResult SetAudioSchedulingBoosted(bool boost)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(AudioTaskKey, writable: true);
            if (key == null) return TweakResult.Fail("Could not open the audio scheduling key (run as Administrator).");
            // Boost = the same High/High the "Pro Audio" task already uses; restore = Windows defaults.
            key.SetValue(SchedCategory, boost ? "High" : "Medium", RegistryValueKind.String);
            key.SetValue(SfioPriority,  boost ? "High" : "Normal", RegistryValueKind.String);
            Log.Info("Audio", $"MMCSS Audio task {(boost ? "boosted (High/High)" : "restored to defaults (Medium/Normal)")}");
            return TweakResult.Ok(boost
                ? "Audio scheduling prioritized. Restart your PC to apply."
                : "Audio scheduling restored to the Windows default. Restart your PC to apply.");
        }
        catch (Exception ex) { Log.Error("Audio", "SetAudioSchedulingBoosted failed", ex); return TweakResult.FromException(ex); }
    }

    // ── 3. Disable audio enhancements, device-wide ──────────────────────────
    // Each output endpoint carries its own "Disable all enhancements" flag — the same
    // one the classic Sound panel sets: PKEY_AudioEndpoint_Disable_SysFx ({1da5d803-…},5)
    // = 1 in the endpoint's Properties subkey. This applies it across EVERY active output
    // so the user gets one switch instead of a per-device picker. Reversible (back to 0).
    //
    // PERMISSIONS: these endpoint keys grant Administrators (and Users) SetValue but NOT
    // CreateSubKey. The normal writable open requests the full KEY_WRITE mask (which
    // includes CreateSubKey) and is therefore denied — the cause of the earlier "Windows
    // blocked changing this device" failures. We open requesting ONLY SetValue (+ read),
    // exactly the rights that ARE granted, so the write succeeds.
    private const string RenderDevicesKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    private const string DisableSysFxValue = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";   // Disable_SysFx
    private const int    DeviceStateActive = 1;   // DEVICE_STATE_ACTIVE

    // Spatial audio lives in the endpoint's FxProperties as the EFX (endpoint-effect) CLSID —
    // on this hardware it's the "Microsoft Audio Home Theater Effects" APO. Setting the EFX to
    // the null CLSID turns spatial (Windows Sonic / Dolby / DTS) off. We stash the original in
    // Systema's own HKCU key so it's exactly restorable. Endpoints without an EFX have no spatial.
    private const string FxEfxValue        = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},3";
    private const string NullClsid         = "{00000000-0000-0000-0000-000000000000}";
    private const string SystemaAudioKey   = @"Software\Systema\Audio";   // HKCU — saved spatial originals + intent
    private const string SpatialOrigPrefix = "SpatialEfx_";

    // Persisted INTENT (separate from live state). Reconnecting a device often rebuilds its
    // endpoint with effects back on, so the live value drifts off the user's choice. We store
    // what the user WANTS and re-assert it (ReinforceFromIntent) on a timer / device change.
    private const string EnhIntentValue     = "EnhancementsOffIntent";
    private const string SpatialIntentValue = "SpatialOffIntent";

    // Vendor effect APOs (Realtek / Waves / etc.) don't ride the Disable_SysFx bypass — they're
    // driver-injected APOs listed in the endpoint's effect-CLSID lists ({d04e05a6...},13/14/15/19/20).
    // We strip the vendor entries (each identified by a sibling "{clsid},100" value pointing at a
    // SWD\DRIVERENUM device) while keeping Microsoft's own APOs, saving originals for exact restore.
    private const string FxCompositeFmtId = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";
    private static readonly int[] FxListPids = { 13, 14, 15, 19, 20 };
    private const string FxListSavePrefix = "FxList_";

    /// <summary>Opens an endpoint's Properties subkey requesting only the rights actually
    /// granted on these keys (SetValue + read), so the write isn't denied for missing
    /// CreateSubKey rights. Returns null if the key is absent.</summary>
    private static RegistryKey? OpenEndpointPropsWritable(string endpointId) =>
        Registry.LocalMachine.OpenSubKey(
            $@"{RenderDevicesKey}\{endpointId}\Properties",
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.SetValue | RegistryRights.QueryValues);

    /// <summary>Same SetValue-only open as above, for the endpoint's FxProperties subkey.</summary>
    private static RegistryKey? OpenEndpointFxWritable(string endpointId) =>
        Registry.LocalMachine.OpenSubKey(
            $@"{RenderDevicesKey}\{endpointId}\FxProperties",
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.SetValue | RegistryRights.QueryValues);

    /// <summary>The registry ids of every active (plugged-in, enabled) audio output endpoint.</summary>
    private List<string> GetActiveOutputEndpointIds()
    {
        var ids = new List<string>();
        try
        {
            using var render = Registry.LocalMachine.OpenSubKey(RenderDevicesKey);
            if (render == null) return ids;
            foreach (var id in render.GetSubKeyNames())
            {
                try
                {
                    using var dev = render.OpenSubKey(id);
                    if (dev?.GetValue("DeviceState") is int s && s == DeviceStateActive) ids.Add(id);
                }
                catch (Exception ex) { Log.Warn("Audio", $"endpoint {id} state read failed: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn("Audio", $"GetActiveOutputEndpointIds failed: {ex.Message}"); }
        return ids;
    }

    /// <summary>True only when EVERY active output device has enhancements disabled.</summary>
    public bool AreEnhancementsDisabledEverywhere()
    {
        var ids = GetActiveOutputEndpointIds();
        if (ids.Count == 0) return false;
        foreach (var id in ids)
        {
            try
            {
                using var props = Registry.LocalMachine.OpenSubKey($@"{RenderDevicesKey}\{id}\Properties");
                if (props?.GetValue(DisableSysFxValue) is int v && v == 1) continue;
            }
            catch (Exception ex) { Log.Warn("Audio", $"enh read {id} failed: {ex.Message}"); }
            return false;   // at least one device still has enhancements on
        }
        return true;
    }

    /// <summary>Disables (or re-enables) audio enhancements on every active output device,
    /// verifying each write actually stuck, and recording the user's intent for reinforcement.</summary>
    public TweakResult SetEnhancementsDisabledEverywhere(bool disable)
    {
        WriteIntent(EnhIntentValue, disable);   // remember what the user wants, even if a device is locked

        var ids = GetActiveOutputEndpointIds();
        if (ids.Count == 0) return TweakResult.Fail("No active output devices were found.");

        int want = disable ? 1 : 0, ok = 0, fail = 0;
        foreach (var id in ids)
        {
            try
            {
                using var props = OpenEndpointPropsWritable(id);
                if (props == null) { fail++; continue; }
                props.SetValue(DisableSysFxValue, want, RegistryValueKind.DWord);
                // Some driver stacks (e.g. Realtek) also key off the same flag in FxProperties —
                // mirror it there when the key is reachable so the change is honoured consistently.
                try { using var fx = OpenEndpointFxWritable(id); fx?.SetValue(DisableSysFxValue, want, RegistryValueKind.DWord); }
                catch (Exception ex) { Log.Warn("Audio", $"enh FxProperties write {id} failed: {ex.Message}"); }
                // Verify the write took — read it back rather than assume success.
                if (props.GetValue(DisableSysFxValue) is int got && got == want) ok++; else fail++;
            }
            catch (Exception ex) { fail++; Log.Warn("Audio", $"enh write {id} failed: {ex.Message}"); }
        }
        Log.Info("Audio", $"Enhancements {(disable ? "disabled" : "re-enabled")} device-wide: {ok} verified, {fail} failed");

        // Also empty the endpoint-effect chain so Win11's "Audio enhancements" dropdown reads "Off",
        // and strip (or restore) driver-injected vendor APOs (Realtek/Waves) that ignore the
        // Disable_SysFx bypass above. Reversible via saved original effect lists.
        int fxChanged = 0;
        try
        {
            using var sys = Registry.CurrentUser.CreateSubKey(SystemaAudioKey, writable: true);
            if (sys != null)
            {
                if (disable) { foreach (var id in ids) fxChanged += StripVendorApos(id, sys); }
                else RestoreVendorApos(sys);
            }
        }
        catch (Exception ex) { Log.Warn("Audio", $"vendor APO {(disable ? "strip" : "restore")} failed: {ex.Message}"); }

        if (ok == 0) return TweakResult.Fail("Windows blocked the change on every device.");
        string msg = disable
            ? $"Audio enhancements disabled on {ok} device{(ok == 1 ? "" : "s")} (verified)."
            : $"Audio enhancements re-enabled on {ok} device{(ok == 1 ? "" : "s")} (verified).";
        if (disable && fxChanged > 0) msg += " Cleared the device effect chain (incl. Realtek/Waves) too.";
        msg += " Restart your PC to apply.";
        if (fail > 0) msg += $" ({fail} couldn't be changed.)";
        return TweakResult.Ok(msg);
    }

    // ── 4. Disable spatial audio (Windows Sonic / Dolby / DTS), device-wide ──
    // Spatial is the endpoint EFX in FxProperties. Turning it off = setting that CLSID to the
    // null GUID; we save each original in Systema's HKCU key so "on" restores it exactly.

    /// <summary>True when Systema currently has spatial audio turned off (an original is saved).</summary>
    public bool IsSpatialAudioDisabled()
    {
        try
        {
            using var sys = Registry.CurrentUser.OpenSubKey(SystemaAudioKey);
            return sys != null && sys.GetValueNames().Any(n => n.StartsWith(SpatialOrigPrefix, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { Log.Warn("Audio", $"IsSpatialAudioDisabled read failed: {ex.Message}"); return false; }
    }

    /// <summary>Turns spatial audio off (nulls the EFX, saving the original) or restores it,
    /// verifying each write and recording intent so reconnects can be re-asserted.</summary>
    public TweakResult SetSpatialAudioDisabled(bool disable)
    {
        WriteIntent(SpatialIntentValue, disable);   // remember the user's choice for reinforcement

        try
        {
            using var sys = Registry.CurrentUser.CreateSubKey(SystemaAudioKey, writable: true);
            if (sys == null) return TweakResult.Fail("Could not open Systema's settings key.");
            int ok = 0, fail = 0;

            if (disable)
            {
                foreach (var id in GetActiveOutputEndpointIds())
                {
                    try
                    {
                        using var fxRO = Registry.LocalMachine.OpenSubKey($@"{RenderDevicesKey}\{id}\FxProperties");
                        var cur = fxRO?.GetValue(FxEfxValue) as string;
                        // Skip devices with no spatial effect, or already off.
                        if (string.IsNullOrEmpty(cur) || cur.Equals(NullClsid, StringComparison.OrdinalIgnoreCase)) continue;
                        using var fw = OpenEndpointFxWritable(id);
                        if (fw == null) { fail++; continue; }
                        sys.SetValue(SpatialOrigPrefix + id, cur, RegistryValueKind.String);   // save original
                        fw.SetValue(FxEfxValue, NullClsid, RegistryValueKind.String);
                        // Verify it stuck.
                        if (string.Equals(fw.GetValue(FxEfxValue) as string, NullClsid, StringComparison.OrdinalIgnoreCase)) ok++; else fail++;
                    }
                    catch (Exception ex) { fail++; Log.Warn("Audio", $"spatial off {id} failed: {ex.Message}"); }
                }
                Log.Info("Audio", $"Spatial audio disabled: {ok} verified, {fail} failed");
                if (ok == 0 && fail == 0) return TweakResult.Ok("Spatial audio will stay off. No device has it active right now, and new ones get handled automatically.");
                if (ok == 0) return TweakResult.Fail("Windows blocked the change on every device.");
                string m = $"Spatial audio disabled on {ok} device{(ok == 1 ? "" : "s")} (verified). Restart your PC to apply.";
                if (fail > 0) m += $" ({fail} couldn't be changed.)";
                return TweakResult.Ok(m);
            }

            // Restore: re-apply every saved original, then clear it.
            foreach (var name in sys.GetValueNames().Where(n => n.StartsWith(SpatialOrigPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var id   = name.Substring(SpatialOrigPrefix.Length);
                var orig = sys.GetValue(name) as string;
                try
                {
                    using var fw = OpenEndpointFxWritable(id);
                    if (fw != null && !string.IsNullOrEmpty(orig)) { fw.SetValue(FxEfxValue, orig, RegistryValueKind.String); ok++; }
                }
                catch (Exception ex) { fail++; Log.Warn("Audio", $"spatial restore {id} failed: {ex.Message}"); }
                sys.DeleteValue(name, throwOnMissingValue: false);
            }
            Log.Info("Audio", $"Spatial audio restored: {ok} ok, {fail} failed");
            return TweakResult.Ok($"Spatial audio restored on {ok} device{(ok == 1 ? "" : "s")}. Restart your PC to apply.");
        }
        catch (Exception ex) { Log.Error("Audio", "SetSpatialAudioDisabled failed", ex); return TweakResult.FromException(ex); }
    }

    // ── 4b. Strip vendor effect APOs (Realtek / Waves) ──────────────────────

    private static string[] ReadList(RegistryKey k, string name)
    {
        var v = k.GetValue(name);
        if (v is string[] arr) return arr;
        if (v is string s && s.Length > 0) return new[] { s };
        return Array.Empty<string>();
    }

    /// <summary>CLSIDs in this endpoint's FxProperties that are driver-injected vendor APOs
    /// (Realtek/Waves/etc.) — flagged by a "{clsid},100" sibling pointing at a driver device.</summary>
    private static HashSet<string> VendorApoClsids(RegistryKey fxRO)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in fxRO.GetValueNames())
        {
            if (!n.EndsWith(",100", StringComparison.Ordinal)) continue;
            if (fxRO.GetValue(n) is string s &&
                (s.IndexOf("DRIVERENUM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 s.IndexOf("WAVESAPO",  StringComparison.OrdinalIgnoreCase) >= 0))
            {
                set.Add(n.Substring(0, n.Length - ",100".Length));
            }
        }
        return set;
    }

    /// <summary>True if any active endpoint has effect drift while enhancements are meant to be off:
    /// a non-empty EFX list (pid 15 — backs Win11's "Audio enhancements" dropdown) or a vendor APO
    /// still listed (driver re-injected it on reconnect/reboot).</summary>
    private bool AnyActiveEndpointHasEffectDrift()
    {
        foreach (var id in GetActiveOutputEndpointIds())
        {
            try
            {
                using var fxRO = Registry.LocalMachine.OpenSubKey($@"{RenderDevicesKey}\{id}\FxProperties");
                if (fxRO == null) continue;
                if (ReadList(fxRO, $"{FxCompositeFmtId},15").Length > 0) return true;   // dropdown would read "on"
                var vendor = VendorApoClsids(fxRO);
                if (vendor.Count == 0) continue;
                foreach (var pid in FxListPids)
                    if (ReadList(fxRO, $"{FxCompositeFmtId},{pid}").Any(c => vendor.Contains(c))) return true;
            }
            catch { /* ignore one endpoint */ }
        }
        return false;
    }

    /// <summary>Clears the endpoint-effect list (so Win11's "Audio enhancements" dropdown reads
    /// "Off") and removes vendor APO CLSIDs (Realtek/Waves) from the other effect lists while
    /// keeping Microsoft's. Saves each original list first. Returns the number of lists changed.</summary>
    private int StripVendorApos(string id, RegistryKey sys)
    {
        int changed = 0;
        try
        {
            using var fxRO = Registry.LocalMachine.OpenSubKey($@"{RenderDevicesKey}\{id}\FxProperties");
            if (fxRO == null) return 0;
            var vendor = VendorApoClsids(fxRO);
            using var fw = OpenEndpointFxWritable(id);
            if (fw == null) return 0;
            foreach (var pid in FxListPids)
            {
                var lv  = $"{FxCompositeFmtId},{pid}";
                var cur = ReadList(fxRO, lv);
                if (cur.Length == 0) continue;
                // pid 15 is the EFX list that backs Windows 11's "Audio enhancements" dropdown — empty
                // it entirely (incl. Microsoft's default endpoint effect) so the dropdown reads "Off".
                // The other lists keep Microsoft's APOs and only drop vendor (Realtek/Waves) entries.
                var filtered = (pid == 15) ? Array.Empty<string>() : cur.Where(c => !vendor.Contains(c)).ToArray();
                if (filtered.Length == cur.Length) continue;   // nothing to change
                var saveName = $"{FxListSavePrefix}{id}__{pid}";
                if (sys.GetValue(saveName) == null) sys.SetValue(saveName, cur, RegistryValueKind.MultiString);   // keep the FIRST original
                fw.SetValue(lv, filtered, RegistryValueKind.MultiString);
                changed++;
            }
        }
        catch (Exception ex) { Log.Warn("Audio", $"StripVendorApos {id} failed: {ex.Message}"); }
        return changed;
    }

    /// <summary>Restores every saved vendor-APO effect list, then clears the saved originals.</summary>
    private void RestoreVendorApos(RegistryKey sys)
    {
        foreach (var name in sys.GetValueNames().Where(n => n.StartsWith(FxListSavePrefix, StringComparison.Ordinal)).ToList())
        {
            try
            {
                var rest = name.Substring(FxListSavePrefix.Length);
                int sep = rest.LastIndexOf("__", StringComparison.Ordinal);
                if (sep >= 0)
                {
                    var id  = rest.Substring(0, sep);
                    var pid = rest.Substring(sep + 2);
                    if (sys.GetValue(name) is string[] saved)
                    {
                        using var fw = OpenEndpointFxWritable(id);
                        fw?.SetValue($"{FxCompositeFmtId},{pid}", saved, RegistryValueKind.MultiString);
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Audio", $"RestoreVendorApos {name} failed: {ex.Message}"); }
            sys.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    // ── 5. Intent + reinforcement ───────────────────────────────────────────
    // The toggles reflect the user's saved INTENT (not the momentary live value), and a
    // background pass re-asserts that intent so a device that came back with effects on gets
    // corrected without the user having to touch the toggle again.

    public bool GetEnhancementsOffIntent() => ReadIntent(EnhIntentValue);
    public bool GetSpatialOffIntent()      => ReadIntent(SpatialIntentValue);

    private static bool ReadIntent(string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(SystemaAudioKey); return k?.GetValue(name) is int v && v == 1; }
        catch { return false; }
    }

    private static void WriteIntent(string name, bool on)
    {
        try { using var k = Registry.CurrentUser.CreateSubKey(SystemaAudioKey, writable: true); k?.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord); }
        catch (Exception ex) { Log.Warn("Audio", $"WriteIntent {name} failed: {ex.Message}"); }
    }

    /// <summary>True if any active output device currently has a (non-null) spatial EFX.</summary>
    private bool AnyActiveEndpointHasSpatial()
    {
        foreach (var id in GetActiveOutputEndpointIds())
        {
            try
            {
                using var fx = Registry.LocalMachine.OpenSubKey($@"{RenderDevicesKey}\{id}\FxProperties");
                var cur = fx?.GetValue(FxEfxValue) as string;
                if (!string.IsNullOrEmpty(cur) && !cur.Equals(NullClsid, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* ignore a single endpoint */ }
        }
        return false;
    }

    /// <summary>Re-asserts the user's saved intent only where the live state has drifted (e.g. a
    /// reconnected device came back with effects on). No-ops when nothing needs fixing. Returns
    /// true if it re-applied anything.</summary>
    public bool ReinforceFromIntent()
    {
        bool changed = false;
        try
        {
            if (GetEnhancementsOffIntent() && (!AreEnhancementsDisabledEverywhere() || AnyActiveEndpointHasEffectDrift()))
            {
                SetEnhancementsDisabledEverywhere(true);
                changed = true;
            }
            if (GetSpatialOffIntent() && AnyActiveEndpointHasSpatial())
            {
                SetSpatialAudioDisabled(true);
                changed = true;
            }
        }
        catch (Exception ex) { Log.Warn("Audio", $"ReinforceFromIntent failed: {ex.Message}"); }
        if (changed) Log.Info("Audio", "ReinforceFromIntent re-applied drifted audio settings");
        return changed;
    }
}
