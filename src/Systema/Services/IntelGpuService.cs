// ════════════════════════════════════════════════════════════════════════════
// IntelGpuService.cs  ·  Read / detect / write / revert Intel iGPU registry tweaks
// ════════════════════════════════════════════════════════════════════════════
//
// Settings live under the display-adapter class key:
//   HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-...}\####
//
// ACTIVE-ADAPTER TARGETING (Code 43 history):
//   Some machines have MORE THAN ONE Intel display-class subkey — a live GPU plus a
//   leftover/ghost instance from a Windows image migration (e.g. a Dell Precision 5560
//   that carries a phantom Comet Lake DEV_9BC4 alongside its real Tiger Lake DEV_9A60).
//   Writing the same values onto every instance bricked the driver at boot (Code 43).
//   So we resolve the ONE adapter whose PCI device is actually PRESENT (via WMI →
//   Enum\<pnp>\Driver → class subkey) and target only that. Every write/reset/revert
//   goes to this single primary adapter — never a ghost, never "all of them".
// ════════════════════════════════════════════════════════════════════════════

using System.Management;
using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

/// <summary>A single Intel display adapter matched under the display-class key.</summary>
public sealed class IntelAdapter
{
    public string SubKey { get; init; } = "";        // e.g. "0000"
    public string FullPath { get; init; } = "";      // full path relative to HKLM
    public string DriverDesc { get; init; } = "";    // e.g. "Intel(R) UHD Graphics"
    public string ProviderName { get; init; } = "";
    /// <summary>True when this adapter maps to a device that is physically present (the live GPU).</summary>
    public bool IsActive { get; init; }
}

/// <summary>Tri-state value for one managed Intel setting. null Current ⇒ driver default.</summary>
public sealed class IntelSettingValue
{
    public string ValueName { get; init; } = "";
    public int? Current { get; init; }
    public bool IsDefault => Current is null;
}

public class IntelGpuService
{
    private const string DisplayClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly LoggerService _log = LoggerService.Instance;

    // Hive everything is read/written from. Defaults to HKLM; tests inject HKCU.
    private readonly RegistryKey _hive;

    public IntelGpuService(RegistryKey? hive = null) => _hive = hive ?? Registry.LocalMachine;

    // ── Managed value names (the ONLY values this service reads/writes/deletes) ──
    public const string PowerPolicy            = "PowerPolicy";
    public const string RC6                    = "RC6";
    public const string RC6Dc                  = "RC6_DC";
    public const string PanelSelfRefreshEnable = "PanelSelfRefreshEnable";
    public const string Psr2Disable            = "PSR2Disable";            // PSR2: 1=OFF, 0/absent=On (inverted)
    public const string DpstEnable             = "DPSTEnable";
    public const string DpstLevel              = "PowerDpstAggressivenessLevel"; // 6=normal(on), 1=minimal(off)
    public const string DpstExtraDimming       = "Dpst6_3ApplyExtraDimming";
    public const string DrrsEnabled            = "DRRSEnabled";
    public const string Psr2DrrsEnable         = "Psr2DrrsEnable";
    public const string FbcEnable              = "FBCEnable";

    // ── ABANDONED keys (caused Code 43 — never written, only deleted to self-heal) ──
    public const string RC6p          = "RC6p";
    public const string RC6pDc        = "RC6p_DC";
    public const string RC6pp         = "RC6pp";
    public const string RC6ppDc       = "RC6pp_DC";
    public const string DpstGpsLevel  = "PowerGpsAggressivenessLevel";
    public const string MsiSupported  = "MSISupported";

    public static readonly IReadOnlyList<string> ManagedValueNames = new[]
    {
        PowerPolicy,
        RC6, RC6Dc,
        PanelSelfRefreshEnable, Psr2Disable,
        DpstEnable, DpstLevel, DpstExtraDimming,
        DrrsEnabled, Psr2DrrsEnable,
        FbcEnable
    };

    /// <summary>Display-class keys Systema used to write but abandoned (deleted on Revert/heal).</summary>
    public static readonly IReadOnlyList<string> AbandonedValueNames = new[]
    {
        RC6p, RC6pDc, RC6pp, RC6ppDc, DpstGpsLevel
    };

    // ── Pure helpers ────────────────────────────────────────────────────────────

    public static bool IsIntelAdapter(string? driverDesc, string? providerName)
    {
        return Contains(driverDesc, "intel") || Contains(providerName, "intel");
        static bool Contains(string? s, string token) =>
            !string.IsNullOrWhiteSpace(s) && s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool IsDpstSupported(IEnumerable<string>? adapterValueNames, bool isLaptop)
    {
        if (isLaptop) return true;
        return adapterValueNames?.Any(n => n.IndexOf("DPST", StringComparison.OrdinalIgnoreCase) >= 0) == true;
    }

    /// <summary>Maps a DWORD / REG_SZ-digits / small REG_BINARY value to an int, or null when absent.</summary>
    public static int? ParseValue(object? raw)
    {
        switch (raw)
        {
            case null:   return null;
            case int i:  return i;
            case uint u: return unchecked((int)u);
            case long l: return (int)l;
            case string s when int.TryParse(s.Trim(), out int sv): return sv;
            case byte[] b when b.Length is > 0 and <= 4:
                int v = 0;
                for (int k = 0; k < b.Length; k++) v |= b[k] << (8 * k);
                return v;
            default: return null;
        }
    }

    // ── Detection ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates Intel display-class adapters. When the set of PHYSICALLY-PRESENT Intel
    /// GPUs can be resolved, NON-PRESENT (ghost/leftover) instances are dropped entirely —
    /// so a phantom adapter from a Windows image migration can never be displayed, written
    /// to, or float to primary. The present adapter is placed first. If presence can't be
    /// resolved (WMI hiccup), falls back to including all in lowest-subkey order.
    /// </summary>
    public List<IntelAdapter> DetectIntelAdapters()
    {
        var result = new List<IntelAdapter>();
        var presentSubs = GetPresentAdapterSubKeys();          // present, integrated Intel iGPUs
        string? firstPresent = presentSubs.Count > 0 ? presentSubs.OrderBy(x => x, StringComparer.Ordinal).First() : null;
        try
        {
            using var classKey = _hive.OpenSubKey(DisplayClassPath, writable: false);
            if (classKey == null)
            {
                _log.Warn("IntelGpuService", $"Display-class key not found: {DisplayClassPath}");
                return result;
            }

            foreach (string sub in classKey.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                try
                {
                    using var adapterKey = classKey.OpenSubKey(sub, writable: false);
                    if (adapterKey == null) continue;

                    string driverDesc   = adapterKey.GetValue("DriverDesc")   as string ?? "";
                    string providerName = adapterKey.GetValue("ProviderName") as string ?? "";
                    if (!IsIntelAdapter(driverDesc, providerName)) continue;

                    // Drop NON-PRESENT (ghost) and DEDICATED instances when presence is known.
                    // This keeps phantom/discrete adapters out of the panel entirely.
                    if (presentSubs.Count > 0 && !presentSubs.Contains(sub))
                    {
                        _log.Info("IntelGpuService", $"Skipping non-present/dedicated Intel adapter {sub}: '{driverDesc}'");
                        continue;
                    }

                    // When presence is known, EVERY adapter we keep is a present integrated iGPU,
                    // so it is a valid write target. (Unknown ⇒ IsActive stays false ⇒ writes go
                    // to the single primary only, the conservative fallback.)
                    bool isActive = presentSubs.Count > 0 && presentSubs.Contains(sub);
                    result.Add(new IntelAdapter
                    {
                        SubKey = sub,
                        FullPath = $@"{DisplayClassPath}\{sub}",
                        DriverDesc = driverDesc,
                        ProviderName = providerName,
                        IsActive = isActive
                    });
                    _log.Info("IntelGpuService", $"Intel adapter detected at {sub}: '{driverDesc}'{(isActive ? " [ACTIVE iGPU]" : "")}");
                }
                catch (Exception ex)
                {
                    _log.Warn("IntelGpuService", $"Could not read adapter subkey '{sub}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("IntelGpuService", "DetectIntelAdapters failed", ex);
        }

        // Stable base order (0000 before 0002), then float the first present iGPU to the front
        // so the panel's display (which reads adapters[0]) reflects a real GPU.
        result.Sort((a, b) => string.CompareOrdinal(a.SubKey, b.SubKey));
        if (firstPresent != null)
        {
            int idx = result.FindIndex(a => string.Equals(a.SubKey, firstPresent, StringComparison.OrdinalIgnoreCase));
            if (idx > 0)
            {
                var act = result[idx];
                result.RemoveAt(idx);
                result.Insert(0, act);
            }
        }

        if (result.Count == 0)
            _log.Info("IntelGpuService", "No Intel display adapter found — Intel panel will stay hidden.");
        return result;
    }

    /// <summary>
    /// The display-class subkeys (e.g. "0002") of every physically-present, INTEGRATED Intel
    /// GPU. WMI lists present devices only; each maps to its class subkey via
    /// HKLM\...\Enum\&lt;pnp&gt;\Driver = "{4d36e968-...}\&lt;subkey&gt;". DEDICATED Intel GPUs
    /// (e.g. discrete Arc on a non-zero PCI bus) are EXCLUDED — only built-in iGPUs (PCI bus 0)
    /// are returned. An empty set means presence is unknown (WMI failed); callers then fall
    /// back to including all adapters and writing to the primary only.
    /// </summary>
    public HashSet<string> GetPresentAdapterSubKeys()
    {
        var subs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
            foreach (ManagementObject mo in s.Get())
            {
                string name = mo["Name"]?.ToString() ?? "";
                string pnp  = mo["PNPDeviceID"]?.ToString() ?? "";
                if (name.IndexOf("intel", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (string.IsNullOrEmpty(pnp)) continue;

                using var dev = _hive.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{pnp}", writable: false);
                if (dev == null) continue;

                // Built-in iGPU only — skip dedicated Intel cards (discrete Arc sits on a
                // non-zero PCI bus; the integrated GPU is always PCI bus 0).
                if (!IsIntegratedLocation(dev.GetValue("LocationInformation") as string))
                {
                    _log.Info("IntelGpuService", $"Skipping DEDICATED Intel GPU '{name}' (not bus 0): {pnp}");
                    continue;
                }

                if (dev.GetValue("Driver") is string drv && drv.Length >= 4)
                {
                    int slash = drv.LastIndexOf('\\');
                    string sub = slash >= 0 ? drv[(slash + 1)..] : drv;
                    if (sub.Length == 4 && sub.All(char.IsDigit))
                    {
                        subs.Add(sub);
                        _log.Info("IntelGpuService", $"Present integrated Intel GPU '{name}' → adapter subkey {sub} (pnp {pnp})");
                    }
                }
            }
        }
        catch (Exception ex) { _log.Warn("IntelGpuService", $"GetPresentAdapterSubKeys failed: {ex.Message}"); }
        return subs;
    }

    /// <summary>
    /// True when a device's LocationInformation says PCI bus 0 (the integrated iGPU lives at
    /// 00:02.0). The raw value ends in a locale-proof "(bus,device,function)" triplet, e.g.
    /// "...;(0,2,0)". Unparseable/empty ⇒ assume integrated (don't over-exclude).
    /// </summary>
    public static bool IsIntegratedLocation(string? locationInfo)
    {
        if (string.IsNullOrEmpty(locationInfo)) return true;
        var m = System.Text.RegularExpressions.Regex.Match(locationInfo, @"\((\d+),(\d+),(\d+)\)\s*$");
        if (!m.Success) return true;
        return m.Groups[1].Value == "0";   // PCI bus 0 = integrated
    }

    /// <summary>
    /// The first adapter — used for the panel's display and as the conservative single write
    /// target when presence is unknown.
    /// </summary>
    public static IntelAdapter? PrimaryAdapter(IReadOnlyList<IntelAdapter> adapters) =>
        adapters != null && adapters.Count > 0 ? adapters[0] : null;

    /// <summary>
    /// The adapters that writes/resets/reverts apply to. When presence is KNOWN, this is EVERY
    /// present integrated iGPU (IsActive == true) — so settings apply to all built-in Intel
    /// GPUs, never a ghost or dedicated card. When presence is UNKNOWN (no adapter is marked
    /// active, e.g. WMI failed), it falls back to the single primary adapter — the safe choice
    /// that avoids ever writing to a phantom.
    /// </summary>
    public static IReadOnlyList<IntelAdapter> WriteTargets(IReadOnlyList<IntelAdapter> adapters)
    {
        if (adapters == null || adapters.Count == 0) return Array.Empty<IntelAdapter>();
        var active = adapters.Where(a => a.IsActive).ToList();
        if (active.Count > 0) return active;
        var p = adapters[0];
        return new[] { p };
    }

    // ── Read ────────────────────────────────────────────────────────────────────

    public Dictionary<string, IntelSettingValue> ReadProfile(string adapterFullPath)
    {
        var map = new Dictionary<string, IntelSettingValue>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = _hive.OpenSubKey(adapterFullPath, writable: false);
            foreach (string name in ManagedValueNames)
                map[name] = new IntelSettingValue { ValueName = name, Current = ParseValue(key?.GetValue(name)) };
        }
        catch (Exception ex) { _log.Error("IntelGpuService", $"ReadProfile failed for {adapterFullPath}", ex); }
        return map;
    }

    public (string? Name, int? Value) ResolveFeature(string adapterFullPath, IReadOnlyList<string> candidates)
    {
        try
        {
            using var key = _hive.OpenSubKey(adapterFullPath, writable: false);
            if (key == null) return (null, null);
            foreach (string c in candidates)
            {
                object? raw = key.GetValue(c);
                if (raw == null) continue;
                int? v = ParseValue(raw);
                if (v != null) return (c, v);
            }
        }
        catch (Exception ex) { _log.Warn("IntelGpuService", $"ResolveFeature failed for {adapterFullPath}: {ex.Message}"); }
        return (null, null);
    }

    public string[] GetValueNames(string adapterFullPath)
    {
        try
        {
            using var key = _hive.OpenSubKey(adapterFullPath, writable: false);
            return key?.GetValueNames() ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            _log.Warn("IntelGpuService", $"GetValueNames failed for {adapterFullPath}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    // ── Write / Reset (every present integrated iGPU) ────────────────────────────

    public TweakResult WriteValue(IReadOnlyList<IntelAdapter> adapters, string valueName, int value)
    {
        if (!ManagedValueNames.Contains(valueName))
            return TweakResult.Fail($"Refused to write unmanaged value '{valueName}'.");

        var targets = WriteTargets(adapters);
        if (targets.Count == 0) return TweakResult.Fail("No Intel adapter to write to.");

        int written = 0;
        foreach (var a in targets)
        {
            try
            {
                using var key = _hive.OpenSubKey(a.FullPath, writable: true);
                if (key == null) { _log.Warn("IntelGpuService", $"Adapter key not writable: {a.FullPath}"); continue; }
                WritePreservingKind(key, valueName, value);
                written++;
                _log.Info("IntelGpuService", $"Set {valueName}={value} on {(a.IsActive ? "ACTIVE iGPU" : "primary")} adapter {a.SubKey} ('{a.DriverDesc}')");
            }
            catch (Exception ex)
            {
                _log.Error("IntelGpuService", $"WriteValue {valueName} on {a.SubKey} failed", ex);
            }
        }
        return written > 0
            ? TweakResult.Ok($"{valueName} applied to {written} Intel iGPU(s).")
            : TweakResult.Fail($"Could not write {valueName} to any Intel iGPU.");
    }

    /// <summary>Writes an int preserving the value's existing registry kind (DWORD/Binary/String).</summary>
    private static void WritePreservingKind(RegistryKey key, string valueName, int value)
    {
        RegistryValueKind kind = RegistryValueKind.DWord;
        try { if (key.GetValue(valueName) != null) kind = key.GetValueKind(valueName); }
        catch { /* fall back to DWORD */ }

        switch (kind)
        {
            case RegistryValueKind.Binary:
                key.SetValue(valueName, BitConverter.GetBytes(value), RegistryValueKind.Binary);
                break;
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                key.SetValue(valueName, value.ToString(), RegistryValueKind.String);
                break;
            default:
                key.SetValue(valueName, value, RegistryValueKind.DWord);
                break;
        }
    }

    public TweakResult ResetValue(IReadOnlyList<IntelAdapter> adapters, string valueName)
    {
        if (!ManagedValueNames.Contains(valueName))
            return TweakResult.Fail($"Refused to delete unmanaged value '{valueName}'.");

        var targets = WriteTargets(adapters);
        if (targets.Count == 0) return TweakResult.Fail("No Intel adapter to reset.");

        int cleared = 0;
        foreach (var a in targets)
        {
            try
            {
                using var key = _hive.OpenSubKey(a.FullPath, writable: true);
                if (key == null) continue;
                key.DeleteValue(valueName, throwOnMissingValue: false);
                cleared++;
                _log.Info("IntelGpuService", $"Reset {valueName} to default on adapter {a.SubKey} ('{a.DriverDesc}')");
            }
            catch (Exception ex)
            {
                _log.Error("IntelGpuService", $"ResetValue {valueName} on {a.SubKey} failed", ex);
            }
        }
        return cleared > 0
            ? TweakResult.Ok($"{valueName} reset to driver default.")
            : TweakResult.Fail($"Could not reset {valueName}.");
    }

    // ── RC6 Render Standby (plain on/off; writes RC6 = AC and RC6_DC = battery) ───

    public TweakResult SetRc6(IReadOnlyList<IntelAdapter> adapters, bool on)
    {
        var primary = WriteValue(adapters, RC6, on ? 1 : 0);
        WriteValue(adapters, RC6Dc, on ? 1 : 0);
        return primary.Success
            ? TweakResult.Ok(on ? "RC6 Render Standby enabled." : "RC6 Render Standby disabled.")
            : primary;
    }

    public TweakResult ResetRc6(IReadOnlyList<IntelAdapter> adapters)
    {
        ResetValue(adapters, RC6Dc);
        return ResetValue(adapters, RC6);
    }

    // ── Panel features ───────────────────────────────────────────────────────────
    // NOTE: SetPsr was REMOVED — disabling Panel Self Refresh black-screened some laptop
    // panels on battery. PanelSelfRefreshEnable / PSR2Disable stay in ManagedValueNames so
    // RevertAll still DELETES any stale PSR override an older build wrote (self-heal), but
    // nothing writes them anymore — PSR is always left at the driver default.

    /// <summary>Display Power Saving (DPST): enable flag + aggressiveness level + extra-dimming.</summary>
    public TweakResult SetDpst(IReadOnlyList<IntelAdapter> adapters, bool on)
    {
        var r = WriteValue(adapters, DpstEnable, on ? 1 : 0);
        WriteValue(adapters, DpstLevel, on ? 6 : 1);
        WriteValue(adapters, DpstExtraDimming, on ? 1 : 0);
        return r.Success
            ? TweakResult.Ok(on ? "Display Power Saving (DPST) enabled." : "Display Power Saving (DPST) disabled.")
            : r;
    }

    /// <summary>Dynamic Refresh Switching. Writes both driver naming variants.</summary>
    public TweakResult SetDrrs(IReadOnlyList<IntelAdapter> adapters, bool on)
    {
        var r = WriteValue(adapters, DrrsEnabled, on ? 1 : 0);
        WriteValue(adapters, Psr2DrrsEnable, on ? 1 : 0);
        return r.Success
            ? TweakResult.Ok(on ? "Dynamic Refresh Switching enabled." : "Dynamic Refresh Switching disabled.")
            : r;
    }

    /// <summary>Frame Buffer Compression.</summary>
    public TweakResult SetFbc(IReadOnlyList<IntelAdapter> adapters, bool on)
    {
        var r = WriteValue(adapters, FbcEnable, on ? 1 : 0);
        return r.Success
            ? TweakResult.Ok(on ? "Frame Buffer Compression enabled." : "Frame Buffer Compression disabled.")
            : r;
    }


    // ── MSI override healing (never writes MSISupported, only clears stale ones) ──

    public static string BuildMsiPath(string pnpDeviceId) =>
        $@"SYSTEM\CurrentControlSet\Enum\{pnpDeviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    public List<string> GetIntelGpuMsiPaths()
    {
        var paths = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
            foreach (ManagementObject mo in s.Get())
            {
                string name = mo["Name"]?.ToString() ?? "";
                string pnp  = mo["PNPDeviceID"]?.ToString() ?? "";
                if (name.IndexOf("intel", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    pnp.StartsWith("PCI", StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(BuildMsiPath(pnp));
                }
            }
        }
        catch (Exception ex) { _log.Warn("IntelGpuService", $"GetIntelGpuMsiPaths failed: {ex.Message}"); }
        return paths;
    }

    /// <summary>HEALING ONLY: clears a stale MSISupported override left by the removed MSI feature.</summary>
    public int CleanupMsiOverride(IReadOnlyList<string> msiPaths)
    {
        if (msiPaths == null || msiPaths.Count == 0) return 0;
        int cleared = 0;
        foreach (var p in msiPaths)
        {
            try
            {
                using var k = _hive.OpenSubKey(p, writable: true);
                if (k?.GetValue(MsiSupported) != null)
                {
                    k.DeleteValue(MsiSupported, throwOnMissingValue: false);
                    cleared++;
                    _log.Info("IntelGpuService", $"Cleared stale MSISupported override at {p}");
                }
            }
            catch (Exception ex) { _log.Error("IntelGpuService", $"CleanupMsiOverride failed at {p}", ex); }
        }
        return cleared;
    }

    // ── DRRS effectiveness ───────────────────────────────────────────────────────

    public (int Min, int Max, int Current) GetIntelRefreshRange()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, MinRefreshRate, MaxRefreshRate, CurrentRefreshRate FROM Win32_VideoController");
            foreach (ManagementObject mo in s.Get())
            {
                string name = mo["Name"]?.ToString() ?? "";
                if (name.IndexOf("intel", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int min = ToInt(mo["MinRefreshRate"]);
                int max = ToInt(mo["MaxRefreshRate"]);
                int cur = ToInt(mo["CurrentRefreshRate"]);
                if (max > 0) return (min, max, cur);
            }
        }
        catch (Exception ex) { _log.Warn("IntelGpuService", $"GetIntelRefreshRange failed: {ex.Message}"); }
        return (0, 0, 0);

        static int ToInt(object? o) => o != null && int.TryParse(o.ToString(), out int v) ? v : 0;
    }

    public static bool IsSingleRefreshRate(int min, int max) => min > 0 && max > 0 && min == max;

    public static int NormalizeRefreshHz(int hz)
    {
        int[] common = { 24, 30, 48, 50, 60, 75, 90, 100, 120, 144, 165, 240 };
        foreach (int c in common) if (Math.Abs(hz - c) <= 1) return c;
        return hz;
    }

    // ── Revert (every present integrated iGPU) ───────────────────────────────────

    public TweakResult RevertAll(IReadOnlyList<IntelAdapter> adapters)
    {
        var targets = WriteTargets(adapters);
        if (targets.Count == 0) return TweakResult.Fail("No Intel adapter to revert.");

        int cleared = 0;
        foreach (var a in targets)
        {
            try
            {
                using var key = _hive.OpenSubKey(a.FullPath, writable: true);
                if (key == null) continue;
                foreach (string name in ManagedValueNames) key.DeleteValue(name, throwOnMissingValue: false);
                foreach (string name in AbandonedValueNames) key.DeleteValue(name, throwOnMissingValue: false);
                cleared++;
                _log.Info("IntelGpuService", $"Reverted all Intel settings on adapter {a.SubKey}.");
            }
            catch (Exception ex)
            {
                _log.Error("IntelGpuService", $"RevertAll on {a.SubKey} failed", ex);
            }
        }
        return cleared > 0
            ? TweakResult.Ok("All Intel settings reverted to driver defaults.")
            : TweakResult.Fail("Could not revert Intel settings.");
    }

    // ── Laptop detection ─────────────────────────────────────────────────────────

    public bool IsLaptop()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            foreach (ManagementObject _ in s.Get()) return true;
        }
        catch (Exception ex) { _log.Warn("IntelGpuService", $"Win32_Battery probe failed: {ex.Message}"); }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementObject mo in s.Get())
            {
                if (mo["ChassisTypes"] is not ushort[] types) continue;
                foreach (ushort t in types)
                    if ((t >= 8 && t <= 11) || t == 14 || (t >= 30 && t <= 32)) return true;
            }
        }
        catch (Exception ex) { _log.Warn("IntelGpuService", $"Win32_SystemEnclosure probe failed: {ex.Message}"); }
        return false;
    }
}
