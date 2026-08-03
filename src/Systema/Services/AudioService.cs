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
using System.ServiceProcess;
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
    private const string EnhDevPrefix       = "EnhDev_";   // HKCU markers: output endpoints we flipped, so toggle-off can reset them even when unplugged
    private const string SpatialIntentValue = "SpatialOffIntent";

    // Microphone (capture) side. Same machinery as Render, pointed at the Capture device class.
    // A separate intent key and FX-list save prefix keep it independent of the output toggle.
    private const string CaptureDevicesKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
    private const string MicEnhIntentValue  = "MicEnhancementsOffIntent";
    private const string MicEnhDevPrefix    = "MicEnhDev_";   // HKCU markers: mic endpoints we flipped (same role as EnhDevPrefix)
    private const string MicFxSavePrefix    = "MicFxList_";

    // Audio effects (APOs) are driver-injected and listed in the endpoint's effect-CLSID lists. Windows
    // exposes TWO effect property sets and a driver can use either (or both): the plain FX set
    // ({d04e05a6...},N — slots 13/14/15/19/20 on speakers, 17 on mics) and the COMPOSITE FX set
    // ({d3993a3f...},N — slots 5/6/7/9/11/12), which is where vendor packs like Realtek/Waves actually
    // park their APOs on many machines. Neither set rides the Disable_SysFx bypass, so to turn ALL
    // enhancements off we empty every list in BOTH sets (ClearAllFx), saving each original under
    // FxListSavePrefix (keyed by the full value name) for an exact restore.
    private const string FxCompositeFmtId  = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d}";   // PKEY_FX_*
    private const string FxCompositeFmtId2 = "{d3993a3f-99c2-4402-b5ec-a92a0367664b}";   // PKEY_CompositeFX_* (Realtek/Waves)
    private const string FxListSavePrefix  = "FxList_";

    /// <summary>Opens an endpoint's Properties subkey requesting only the rights actually
    /// granted on these keys (SetValue + read), so the write isn't denied for missing
    /// CreateSubKey rights. Returns null if the key is absent.</summary>
    private static RegistryKey? OpenEndpointPropsWritable(string devicesKey, string endpointId) =>
        Registry.LocalMachine.OpenSubKey(
            $@"{devicesKey}\{endpointId}\Properties",
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.SetValue | RegistryRights.QueryValues);

    /// <summary>Same SetValue-only open as above, for the endpoint's FxProperties subkey.</summary>
    private static RegistryKey? OpenEndpointFxWritable(string devicesKey, string endpointId) =>
        Registry.LocalMachine.OpenSubKey(
            $@"{devicesKey}\{endpointId}\FxProperties",
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.SetValue | RegistryRights.QueryValues);

    /// <summary>The registry ids of every active (plugged-in, enabled) endpoint in the device
    /// class (Render for speakers/headphones, Capture for microphones).</summary>
    private List<string> GetActiveEndpointIds(string devicesKey)
    {
        var ids = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(devicesKey);
            if (root == null) return ids;
            foreach (var id in root.GetSubKeyNames())
            {
                try
                {
                    using var dev = root.OpenSubKey(id);
                    if (dev?.GetValue("DeviceState") is int s && s == DeviceStateActive) ids.Add(id);
                }
                catch (Exception ex) { Log.Warn("Audio", $"endpoint {id} state read failed: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn("Audio", $"GetActiveEndpointIds failed: {ex.Message}"); }
        return ids;
    }

    /// <summary>True only when EVERY active endpoint in the class has enhancements disabled.</summary>
    private bool AreEnhancementsDisabled(string devicesKey)
    {
        var ids = GetActiveEndpointIds(devicesKey);
        if (ids.Count == 0) return false;
        foreach (var id in ids)
        {
            try
            {
                using var props = Registry.LocalMachine.OpenSubKey($@"{devicesKey}\{id}\Properties");
                if (props?.GetValue(DisableSysFxValue) is int v && v == 1) continue;
            }
            catch (Exception ex) { Log.Warn("Audio", $"enh read {id} failed: {ex.Message}"); }
            return false;   // at least one device still has enhancements on
        }
        return true;
    }

    /// <summary>True when every active OUTPUT device has enhancements disabled.</summary>
    public bool AreEnhancementsDisabledEverywhere() => AreEnhancementsDisabled(RenderDevicesKey);

    /// <summary>Disables (or re-enables) ALL enhancements on every endpoint in the class: sets the
    /// Disable_SysFx flag and empties the ENTIRE effect chain (every list in both effect property sets,
    /// whatever slot it's in), so no APO loads at all (vendor packs like Realtek/Waves included) and
    /// Win11's dropdown reads "Off". Records intent for reinforcement. On toggle-off it returns EVERY
    /// device we touched to default, including ones currently UNPLUGGED — their MMDevices registry keys
    /// persist, and we remember each touched device under <paramref name="touchedPrefix"/>. Reversible.</summary>
    private TweakResult SetEnhancements(string devicesKey, string intentKey, string fxSavePrefix, string touchedPrefix, string noun, bool disable)
    {
        WriteIntent(intentKey, disable);   // remember what the user wants, even if a device is locked

        using var sys = Registry.CurrentUser.CreateSubKey(SystemaAudioKey, writable: true);
        int ok = 0, fail = 0;

        if (disable)
        {
            var ids = GetActiveEndpointIds(devicesKey);
            if (ids.Count == 0) return TweakResult.Fail("No active devices were found.");

            foreach (var id in ids)
            {
                try
                {
                    using var props = OpenEndpointPropsWritable(devicesKey, id);
                    if (props == null) { fail++; continue; }
                    props.SetValue(DisableSysFxValue, 1, RegistryValueKind.DWord);
                    // Some driver stacks (e.g. Realtek) also key off the same flag in FxProperties.
                    try { using var fx = OpenEndpointFxWritable(devicesKey, id); fx?.SetValue(DisableSysFxValue, 1, RegistryValueKind.DWord); }
                    catch (Exception ex) { Log.Warn("Audio", $"enh FxProperties write {id} failed: {ex.Message}"); }
                    if (props.GetValue(DisableSysFxValue) is int got && got == 1)
                    {
                        ok++;
                        sys?.SetValue(touchedPrefix + id, 1, RegistryValueKind.DWord);   // remember it, so we can reset it later even if it unplugs
                    }
                    else fail++;
                }
                catch (Exception ex) { fail++; Log.Warn("Audio", $"enh write {id} failed: {ex.Message}"); }
            }

            // Empty the ENTIRE effect chain (both property sets, both forms, any slot) so no APO loads at
            // all (Realtek/Waves/Nahimic included).
            int fxChanged = 0;
            if (sys != null) foreach (var id in ids) fxChanged += ClearAllFx(devicesKey, fxSavePrefix, id, sys);

            // Verify the clear actually took: read back and confirm nothing is left registered.
            bool anyLeft = AnyFxPresent(devicesKey);

            Log.Info("Audio", $"{noun} disabled: {ok} verified, {fail} failed, effect-chain clean={(!anyLeft)}");
            if (ok == 0) return TweakResult.Fail("Windows blocked the change on every device.");
            string m = $"{noun} disabled on {ok} device{(ok == 1 ? "" : "s")} (verified).";
            if (fxChanged > 0) m += " Cleared the effect chain (incl. Realtek/Waves) too.";
            if (anyLeft) m += " Note: one device kept an effect Windows won't let us clear.";
            m += " Restart your PC to apply.";
            if (fail > 0) m += $" ({fail} couldn't be changed.)";
            return TweakResult.Ok(m);
        }

        // ── Restore (toggle off) ── reset every device we touched, plus anything active now. A touched
        // device that's currently unplugged is reset too: its registry keys live on, so the write lands.
        var targets = new HashSet<string>(GetActiveEndpointIds(devicesKey), StringComparer.OrdinalIgnoreCase);
        if (sys != null)
            foreach (var n in sys.GetValueNames().Where(n => n.StartsWith(touchedPrefix, StringComparison.Ordinal)).ToList())
                targets.Add(n.Substring(touchedPrefix.Length));

        foreach (var id in targets)
        {
            try
            {
                using var props = OpenEndpointPropsWritable(devicesKey, id);
                if (props != null)
                {
                    props.SetValue(DisableSysFxValue, 0, RegistryValueKind.DWord);
                    try { using var fx = OpenEndpointFxWritable(devicesKey, id); fx?.SetValue(DisableSysFxValue, 0, RegistryValueKind.DWord); }
                    catch (Exception ex) { Log.Warn("Audio", $"enh FxProperties reset {id} failed: {ex.Message}"); }
                    ok++;
                }
                else fail++;
            }
            catch (Exception ex) { fail++; Log.Warn("Audio", $"enh reset {id} failed: {ex.Message}"); }
            sys?.DeleteValue(touchedPrefix + id, throwOnMissingValue: false);
        }

        // Put the saved effect lists back (registry, so unplugged devices too).
        if (sys != null) RestoreVendorApos(devicesKey, fxSavePrefix, sys);

        Log.Info("Audio", $"{noun} restored: {ok} ok, {fail} failed");
        string msg = ok == 0 && fail == 0
            ? $"{noun} were already at the default."
            : $"{noun} restored on {ok} device{(ok == 1 ? "" : "s")} (unplugged ones included). Restart your PC to apply.";
        if (fail > 0) msg += $" ({fail} couldn't be changed.)";
        return TweakResult.Ok(msg);
    }

    /// <summary>Disables (or re-enables) ALL audio enhancements on every active OUTPUT device by
    /// clearing the entire effect chain (Microsoft, Realtek, Waves, everything) for a raw signal, AND
    /// stopping the vendor DSP services (Waves/Realtek/Nahimic) so nothing re-injects processing.
    /// Reversible via saved originals (both the effect chain and each service's Start type).</summary>
    public TweakResult SetEnhancementsDisabledEverywhere(bool disable)
    {
        var r   = SetEnhancements(RenderDevicesKey, EnhIntentValue, FxListSavePrefix, EnhDevPrefix, "Audio enhancements", disable);
        int svc = SetVendorAudioServices(disable);
        int agt = SetVendorAudioStartupAgents(disable);
        if (r.Success && disable && (svc > 0 || agt > 0))
            return TweakResult.Ok(r.Message + " Stopped the Realtek/Waves audio services and startup agents too.");
        return r;
    }

    /// <summary>Disables (or re-enables) ALL enhancements on every active MICROPHONE / input device by
    /// clearing the entire effect chain, including driver-injected vendor packs (Realtek/Waves/Nahimic)
    /// in whatever slot they sit that ordinary "disable enhancements" leaves running. Mics use different
    /// slots than speakers, so we empty every list rather than vendor-filtering. Reversible.</summary>
    public TweakResult SetMicEnhancementsDisabledEverywhere(bool disable) =>
        SetEnhancements(CaptureDevicesKey, MicEnhIntentValue, MicFxSavePrefix, MicEnhDevPrefix, "Microphone enhancements", disable);

    /// <summary>The one master switch behind the combined toggle: disables (or re-enables) ALL audio
    /// processing on BOTH outputs AND microphones — clears both effect chains and stops the shared
    /// vendor services/agents (Waves/Realtek/Intel) that process either side. Fully reversible.</summary>
    public TweakResult SetAllEnhancementsDisabledEverywhere(bool disable)
    {
        var rOut = SetEnhancementsDisabledEverywhere(disable);    // output FX + vendor services + startup agents
        var rMic = SetMicEnhancementsDisabledEverywhere(disable); // microphone FX (shared services already handled above)
        if (!rOut.Success) return rOut;   // surface the first real failure
        if (!rMic.Success) return rMic;
        return TweakResult.Ok(disable
            ? "All audio and microphone enhancements disabled. Cleared both effect chains and stopped the Realtek/Waves/Intel services and startup agents. Restart to fully apply."
            : "Audio and microphone enhancements re-enabled. Effect chains, services, and startup agents restored.");
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
                foreach (var id in GetActiveEndpointIds(RenderDevicesKey))
                {
                    try
                    {
                        using var fxRO = Registry.LocalMachine.OpenSubKey($@"{RenderDevicesKey}\{id}\FxProperties");
                        var cur = fxRO?.GetValue(FxEfxValue) as string;
                        // Skip devices with no spatial effect, or already off.
                        if (string.IsNullOrEmpty(cur) || cur.Equals(NullClsid, StringComparison.OrdinalIgnoreCase)) continue;
                        using var fw = OpenEndpointFxWritable(RenderDevicesKey, id);
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
                    using var fw = OpenEndpointFxWritable(RenderDevicesKey, id);
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

    /// <summary>True if this FxProperties value name is an effect-CLSID list in EITHER property set:
    /// the plain FX set ({d04e05a6...}) or the composite FX set ({d3993a3f...}, where Realtek/Waves sit).</summary>
    internal static bool IsFxListName(string name) =>
        name.StartsWith(FxCompositeFmtId  + ",", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(FxCompositeFmtId2 + ",", StringComparison.OrdinalIgnoreCase);

    /// <summary>Restores every saved effect list for this class, then clears the originals.</summary>
    private void RestoreVendorApos(string devicesKey, string fxSavePrefix, RegistryKey sys)
    {
        foreach (var name in sys.GetValueNames().Where(n => n.StartsWith(fxSavePrefix, StringComparison.Ordinal)).ToList())
        {
            try
            {
                var rest = name.Substring(fxSavePrefix.Length);
                int sep = rest.LastIndexOf("__", StringComparison.Ordinal);
                if (sep >= 0)
                {
                    var id     = rest.Substring(0, sep);
                    var suffix = rest.Substring(sep + 2);
                    // New saves store the FULL value name ("{guid},pid"); older saves stored a bare pid
                    // (always the plain {d04e05a6...} set). Support both so prior saves still restore.
                    var valueName = suffix.Contains(',') ? suffix : $"{FxCompositeFmtId},{suffix}";
                    var saved = sys.GetValue(name);
                    using var fw = OpenEndpointFxWritable(devicesKey, id);
                    if (saved is string[] arr)      fw?.SetValue(valueName, arr, RegistryValueKind.MultiString);
                    else if (saved is string str)   fw?.SetValue(valueName, str, RegistryValueKind.String);
                }
            }
            catch (Exception ex) { Log.Warn("Audio", $"RestoreVendorApos {name} failed: {ex.Message}"); }
            sys.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    /// <summary>Nulls EVERY effect-CLSID registration on the endpoint across BOTH property sets (plain
    /// {d04e05a6...} and composite {d3993a3f...}), in BOTH forms: the MultiString list form (e.g. ,13/14/
    /// 15/19/20 on speakers, ,17 on mics, ,5/6/7/9/11/12 for composite chains) AND the single-CLSID String
    /// form (e.g. ,5 = WM LFX / ,6 = WM GFX / ,7 = effect proxy), so no APO is registered to load at all —
    /// a fully raw signal. The spatial slot (,3) is left alone; that's the spatial toggle's job. Saves
    /// each original (keyed by full value name) for an exact restore.</summary>
    private int ClearAllFx(string devicesKey, string fxSavePrefix, string id, RegistryKey sys)
    {
        int changed = 0;
        try
        {
            using var fxRO = Registry.LocalMachine.OpenSubKey($@"{devicesKey}\{id}\FxProperties");
            if (fxRO == null) return 0;
            using var fw = OpenEndpointFxWritable(devicesKey, id);
            if (fw == null) return 0;
            foreach (var name in fxRO.GetValueNames())
            {
                if (!IsFxListName(name)) continue;
                if (FxPid(name) == "3") continue;   // spatial EFX — owned by the spatial toggle, not enhancements
                var saveName = $"{fxSavePrefix}{id}__{name}";   // full value name so both property sets round-trip
                var kind = fxRO.GetValueKind(name);
                if (kind == RegistryValueKind.MultiString)
                {
                    if (ReadList(fxRO, name).Length == 0) continue;
                    if (sys.GetValue(saveName) == null) sys.SetValue(saveName, ReadList(fxRO, name), RegistryValueKind.MultiString);
                    fw.SetValue(name, Array.Empty<string>(), RegistryValueKind.MultiString);
                    changed++;
                }
                else if (kind == RegistryValueKind.String)
                {
                    var cur = fxRO.GetValue(name) as string ?? "";
                    if (cur.Length == 0 || cur.Equals(NullClsid, StringComparison.OrdinalIgnoreCase)) continue;
                    if (sys.GetValue(saveName) == null) sys.SetValue(saveName, cur, RegistryValueKind.String);
                    fw.SetValue(name, NullClsid, RegistryValueKind.String);
                    changed++;
                }
            }
        }
        catch (Exception ex) { Log.Warn("Audio", $"ClearAllFx {id} failed: {ex.Message}"); }
        return changed;
    }

    /// <summary>The pid (part after the last comma) of a "{guid},pid" FxProperties value name.</summary>
    private static string FxPid(string name) => name.Substring(name.LastIndexOf(',') + 1);

    /// <summary>True if any active endpoint still has an effect registered — in either form (a non-empty
    /// MultiString list, or a non-null single-CLSID String), in either property set, in any slot except
    /// the spatial one (,3). This is the drift check for the clear-everything path (outputs and mics), so
    /// a reconnected device that came back with effects — list OR single-CLSID — gets re-cleared.</summary>
    private bool AnyFxPresent(string devicesKey)
    {
        foreach (var id in GetActiveEndpointIds(devicesKey))
        {
            try
            {
                using var fxRO = Registry.LocalMachine.OpenSubKey($@"{devicesKey}\{id}\FxProperties");
                if (fxRO == null) continue;
                foreach (var name in fxRO.GetValueNames())
                {
                    if (!IsFxListName(name)) continue;
                    if (FxPid(name) == "3") continue;   // spatial slot — tracked separately
                    var kind = fxRO.GetValueKind(name);
                    if (kind == RegistryValueKind.MultiString && ReadList(fxRO, name).Length > 0) return true;
                    if (kind == RegistryValueKind.String)
                    {
                        var cur = fxRO.GetValue(name) as string ?? "";
                        if (cur.Length > 0 && !cur.Equals(NullClsid, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch { /* ignore one endpoint */ }
        }
        return false;
    }

    // ── 5. Intent + reinforcement ───────────────────────────────────────────
    // The toggles reflect the user's saved INTENT (not the momentary live value), and a
    // background pass re-asserts that intent so a device that came back with effects on gets
    // corrected without the user having to touch the toggle again.

    public bool GetEnhancementsOffIntent()    => ReadIntent(EnhIntentValue);
    public bool GetMicEnhancementsOffIntent() => ReadIntent(MicEnhIntentValue);
    public bool GetSpatialOffIntent()         => ReadIntent(SpatialIntentValue);

    /// <summary>The combined "disable all audio &amp; mic enhancements" state — on if EITHER the output or
    /// microphone off-intent is set. Used to reflect the single merged toggle and drive reinforcement,
    /// and it migrates users who previously had only one of the two old toggles on.</summary>
    public bool GetAllEnhancementsOffIntent() => GetEnhancementsOffIntent() || GetMicEnhancementsOffIntent();

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
        foreach (var id in GetActiveEndpointIds(RenderDevicesKey))
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
            // Combined master: if EITHER off-intent is set, keep BOTH outputs and mics raw and the
            // shared vendor services/agents stopped. (Either-intent also migrates users who had only
            // one of the two old toggles on — the other side gets brought in line on the next pass.)
            if (GetAllEnhancementsOffIntent())
            {
                bool renderDrift = !AreEnhancementsDisabled(RenderDevicesKey)  || AnyFxPresent(RenderDevicesKey);
                bool micDrift    = !AreEnhancementsDisabled(CaptureDevicesKey) || AnyFxPresent(CaptureDevicesKey);

                if (renderDrift || VendorServicesNeedReassert() || VendorStartupAgentsNeedReassert())
                {
                    SetEnhancementsDisabledEverywhere(true);   // output FX + vendor services + startup agents
                    changed = true;
                }
                if (micDrift)
                {
                    SetMicEnhancementsDisabledEverywhere(true); // microphone FX
                    changed = true;
                }
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

    // ── Vendor audio ENHANCEMENT services (Waves / Realtek / Nahimic / Intel SST) ─
    // "Disable all audio enhancements" also stops these so no vendor DSP runs and the signal goes
    // almost straight to the endpoint. This list is enhancement / console / effect / SST services.
    // NEVER add Audiosrv or AudioEndpointBuilder — those ARE core Windows audio and stopping them
    // kills all sound. IntelAudioService (Intel Smart Sound) is included because it was tested safe
    // to stop here, but it's the RISKIEST entry: on some machines Intel SST is the actual audio path,
    // so it's the one most likely to need a toggle-off if a device ever goes silent. Fully reversible:
    // each service's original Start type is captured before the first change, and on the way back we
    // ONLY restore services we ourselves disabled (so a service you disabled by hand is left alone).
    // Start values: 2 = Automatic, 3 = Manual, 4 = Disabled.
    internal static readonly string[] VendorAudioServices =
    {
        "WavesSysSvc",               // Waves Audio Services (MaxxAudio)
        "WavesAudioService",         // Waves Audio Universal Services
        "RtkAudioUniversalService",  // Realtek Audio Universal Service (UAD effects / console)
        "RtkAudioService",           // Realtek Audio Service (older naming)
        "NahimicService",            // Nahimic audio enhancement
        "IntelAudioService",         // Intel Smart Sound audio service (DSP). Tested safe to stop; riskiest entry.
    };
    private const string AudioSvcDefaultsKey = @"Software\Systema\AudioServiceDefaults"; // HKCU — captured Start values
    private static string SvcRegPath(string name) => $@"SYSTEM\CurrentControlSet\Services\{name}";

    /// <summary>Stops + disables the vendor enhancement services (disable=true), or restores the ones
    /// we disabled to the Start type they had before (disable=false). Only installed services are
    /// touched, and restore skips any service we never disabled. Returns how many were changed; never throws.</summary>
    private int SetVendorAudioServices(bool disable)
    {
        int changed = 0;
        foreach (var name in VendorAudioServices)
        {
            try
            {
                using var svcKey = Registry.LocalMachine.OpenSubKey(SvcRegPath(name), writable: true);
                if (svcKey == null) continue;                       // not installed on this machine
                int current = svcKey.GetValue("Start") is int s ? s : 3;

                if (disable)
                {
                    CaptureAudioServiceDefault(name, current);      // first value only — the true original
                    if (current != 4) { svcKey.SetValue("Start", 4, RegistryValueKind.DWord); changed++; }
                    if (TryStopService(name)) changed++;
                }
                else
                {
                    // Only restore services WE disabled; if there's no capture we never touched it, so leave it.
                    if (!TryTakeCapturedDefault(name, out int restore)) continue;
                    if (current != restore) { svcKey.SetValue("Start", restore, RegistryValueKind.DWord); changed++; }
                    if (restore == 2) TryStartService(name);        // only auto-start services get started back up
                }
            }
            catch (Exception ex) { Log.Warn("Audio", $"SetVendorAudioServices({name}) failed: {ex.Message}"); }
        }
        if (changed > 0)
            Log.Info("Audio", $"Vendor audio services {(disable ? "stopped/disabled" : "restored")} ({changed} change(s))");
        return changed;
    }

    /// <summary>True when a vendor enhancement service has crept back (not disabled, or still running)
    /// — so the 30 s reinforcement loop knows to re-disable it. Reads only; cheap.</summary>
    private static bool VendorServicesNeedReassert()
    {
        foreach (var name in VendorAudioServices)
        {
            try
            {
                using var svcKey = Registry.LocalMachine.OpenSubKey(SvcRegPath(name));
                if (svcKey == null) continue;
                if ((svcKey.GetValue("Start") is int s ? s : 3) != 4) return true;   // not disabled
                using var sc = new ServiceController(name);
                var st = sc.Status;
                if (st != ServiceControllerStatus.Stopped && st != ServiceControllerStatus.StopPending)
                    return true;                                                       // still running
            }
            catch { /* not installed / access — ignore */ }
        }
        return false;
    }

    private static bool TryStopService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) return false;
            if (!sc.CanStop) return false;
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
            return true;
        }
        catch { return false; }   // access / dependency / timeout — never fatal
    }

    private static void TryStartService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>Records a service's Start value before we first change it (first write wins, so our own
    /// Disabled(4) can never overwrite the real original).</summary>
    private static void CaptureAudioServiceDefault(string name, int currentStart)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AudioSvcDefaultsKey, writable: true);
            if (key != null && key.GetValue(name) == null)
                key.SetValue(name, currentStart, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Log.Warn("Audio", $"CaptureAudioServiceDefault({name}) failed: {ex.Message}"); }
    }

    /// <summary>Reads and clears the captured original Start for a service. Returns false when there's
    /// no capture (meaning Systema never disabled it, so the caller must leave it untouched).</summary>
    private static bool TryTakeCapturedDefault(string name, out int start)
    {
        start = 3;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AudioSvcDefaultsKey, writable: true);
            if (key?.GetValue(name) is int saved && saved is 2 or 3 or 4)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                start = saved;
                return true;
            }
        }
        catch (Exception ex) { Log.Warn("Audio", $"TryTakeCapturedDefault({name}) failed: {ex.Message}"); }
        return false;
    }

    // ── Vendor audio startup AGENTS (Run-key launchers) ──────────────────────
    // Vendors auto-start user-mode agent apps at logon, separate from their services (Waves' jack
    // agent, Realtek's console agent, etc.). Even with the services disabled these keep running and
    // can poke at the audio. "Disable all audio enhancements" removes their Run entries so they never
    // launch again, and stops any that are running now. Reversible: each Run value is saved before
    // removal and re-written on restore. Only audio-vendor agents are named here — audiodg.exe and
    // core Windows audio are NEVER killed.
    internal static readonly string[] VendorAudioRunEntries =
    {
        "WavesSvc", "WavesGUI", "MaxxAudioPro",                          // Waves
        "RtkAudUService", "RtHDVCpl", "RAVCpl64", "RtkNGUI64", "FMAPP",  // Realtek
        "NahimicSvc", "Nahimic",                                        // Nahimic
    };
    // Process names of those agents to stop immediately, so "off" takes effect this session too.
    internal static readonly string[] VendorAudioAgentProcesses =
    {
        "WavesSvc64", "WavesSvc", "MaxxAudioPro",
        "RtkAudUService64", "RtkAudUService", "RAVCpl64", "RtHDVCpl", "RtkNGUI64", "FMAPP",
        "NahimicSvc",
    };
    private const string AudioStartupDefaultsKey = @"Software\Systema\AudioStartupDefaults"; // HKCU — captured Run values
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Removes the vendor audio agents' Run-key launchers and stops any running ones
    /// (disable=true), or restores every Run entry we removed (disable=false). Never throws; returns
    /// how many changes were made.</summary>
    private int SetVendorAudioStartupAgents(bool disable)
    {
        int changed = 0;
        if (disable)
        {
            foreach (var (tag, root) in new[] { ("HKLM", Registry.LocalMachine), ("HKCU", Registry.CurrentUser) })
            {
                try
                {
                    using var run = root.OpenSubKey(RunKeyPath, writable: true);
                    if (run == null) continue;
                    foreach (var name in VendorAudioRunEntries)
                    {
                        if (run.GetValue(name) is not string val || val.Length == 0) continue;
                        CaptureAudioStartup($"{tag}\\{name}", val);      // first value wins — the true original
                        run.DeleteValue(name, throwOnMissingValue: false);
                        changed++;
                    }
                }
                catch (Exception ex) { Log.Warn("Audio", $"Disable vendor startup ({tag}) failed: {ex.Message}"); }
            }
            changed += KillVendorAudioAgents();
        }
        else
        {
            // Restore everything we captured, back to whichever Run key it came from.
            try
            {
                using var caps = Registry.CurrentUser.OpenSubKey(AudioStartupDefaultsKey, writable: true);
                if (caps != null)
                {
                    foreach (var capName in caps.GetValueNames())
                    {
                        int slash = capName.IndexOf('\\');
                        if (slash <= 0 || caps.GetValue(capName) is not string data)
                        {
                            caps.DeleteValue(capName, throwOnMissingValue: false);
                            continue;
                        }
                        var root = capName[..slash] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                        string runName = capName[(slash + 1)..];
                        using (var run = root.OpenSubKey(RunKeyPath, writable: true))
                            run?.SetValue(runName, data, RegistryValueKind.String);
                        caps.DeleteValue(capName, throwOnMissingValue: false);
                        changed++;
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Audio", $"Restore vendor startup failed: {ex.Message}"); }
        }
        if (changed > 0)
            Log.Info("Audio", $"Vendor audio startup agents {(disable ? "disabled/stopped" : "restored")} ({changed} change(s))");
        return changed;
    }

    /// <summary>True when a vendor agent Run entry exists or an agent process is running — used by the
    /// reinforcement loop to know when to re-disable. Reads only.</summary>
    private static bool VendorStartupAgentsNeedReassert()
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var run = root.OpenSubKey(RunKeyPath);
                if (run == null) continue;
                foreach (var name in VendorAudioRunEntries)
                    if (run.GetValue(name) != null) return true;
            }
            catch { }
        }
        foreach (var pname in VendorAudioAgentProcesses)
        {
            try
            {
                var ps = System.Diagnostics.Process.GetProcessesByName(pname);
                bool any = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                if (any) return true;
            }
            catch { }
        }
        return false;
    }

    private static int KillVendorAudioAgents()
    {
        int killed = 0;
        foreach (var pname in VendorAudioAgentProcesses)
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(pname))
                {
                    try { p.Kill(); killed++; }
                    catch { /* already gone / access — non-fatal */ }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }
        return killed;
    }

    /// <summary>Saves a Run value before we remove it (first write wins, so our own removal can't lose
    /// the real original). Value name is tagged with the hive so restore knows where it belongs.</summary>
    private static void CaptureAudioStartup(string capName, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AudioStartupDefaultsKey, writable: true);
            if (key != null && key.GetValue(capName) == null)
                key.SetValue(capName, value, RegistryValueKind.String);
        }
        catch (Exception ex) { Log.Warn("Audio", $"CaptureAudioStartup({capName}) failed: {ex.Message}"); }
    }
}
