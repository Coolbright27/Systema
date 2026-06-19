// ════════════════════════════════════════════════════════════════════════════
// NvidiaGpuService.cs  ·  Read / detect / write / revert NVIDIA dGPU power settings
// ════════════════════════════════════════════════════════════════════════════
//
// Settings live under the display-adapter class key, same as Intel:
//   HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-...}\####
//
// SAFETY (mirrors IntelGpuService — this machine was Code-43 bricked by Intel writes):
//   • Only PHYSICALLY-PRESENT NVIDIA adapters are ever written to (resolved via
//     Win32_VideoController → Enum\<pnp>\Driver → class subkey). Ghost/leftover
//     instances from an image migration are never touched.
//   • Only the documented PowerMizer values below are written or deleted — nothing
//     that disables hardware (no MSI, no scheduling, no device-disable flags), so a
//     bad write can't Code-43 the driver; the worst case is "ignored / more power".
//   • Reset / RevertAll DELETE the managed values, restoring the driver default — a
//     full, clean self-heal.
// ════════════════════════════════════════════════════════════════════════════

using System.Management;
using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

/// <summary>A single NVIDIA display adapter matched under the display-class key.</summary>
public sealed class NvidiaAdapter
{
    public string SubKey { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string DriverDesc { get; init; } = "";
    /// <summary>True when this adapter maps to a device that is physically present.</summary>
    public bool IsActive { get; init; }
}

public class NvidiaGpuService
{
    private const string DisplayClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly LoggerService _log = LoggerService.Instance;

    private readonly RegistryKey _hive;
    public NvidiaGpuService(RegistryKey? hive = null) => _hive = hive ?? Registry.LocalMachine;

    // ── PowerMizer (the ONLY values this service reads / writes / deletes) ──
    // Together these select the GPU's power-management state. Absent = driver default
    // (Optimal / Adaptive — idles down to save power). Present = "Prefer maximum
    // performance" (stays clocked up). PerfLevelSrc 0x2222 = max for both power sources.
    public const string PerfLevelSrc      = "PerfLevelSrc";
    public const string PowerMizerEnable  = "PowerMizerEnable";
    public const string PowerMizerLevel   = "PowerMizerLevel";    // on battery
    public const string PowerMizerLevelAC = "PowerMizerLevelAC";  // on AC

    private const int PerfLevelSrcMax = 0x2222;   // prefer max performance, AC + battery

    public static readonly IReadOnlyList<string> ManagedValueNames = new[]
    {
        PerfLevelSrc, PowerMizerEnable, PowerMizerLevel, PowerMizerLevelAC
    };

    // ── Pure helpers ────────────────────────────────────────────────────────────

    public static bool IsNvidiaAdapter(string? driverDesc, string? providerName) =>
        Contains(driverDesc, "nvidia") || Contains(providerName, "nvidia");
    private static bool Contains(string? s, string token) =>
        !string.IsNullOrWhiteSpace(s) && s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    public static int? ParseValue(object? raw) => raw switch
    {
        null      => null,
        int i     => i,
        uint u    => unchecked((int)u),
        long l    => (int)l,
        string s when int.TryParse(s.Trim(), out int sv) => sv,
        _         => null
    };

    // ── Detection ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates PHYSICALLY-PRESENT NVIDIA display-class adapters. Non-present (ghost)
    /// instances are dropped when presence is resolvable. The present adapter is first.
    /// </summary>
    public List<NvidiaAdapter> DetectNvidiaAdapters()
    {
        var result = new List<NvidiaAdapter>();
        var presentSubs = GetPresentAdapterSubKeys();
        string? firstPresent = presentSubs.Count > 0
            ? presentSubs.OrderBy(x => x, StringComparer.Ordinal).First() : null;
        try
        {
            using var classKey = _hive.OpenSubKey(DisplayClassPath, writable: false);
            if (classKey == null)
            {
                _log.Warn("NvidiaGpuService", $"Display-class key not found: {DisplayClassPath}");
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
                    if (!IsNvidiaAdapter(driverDesc, providerName)) continue;

                    // Drop NON-PRESENT (ghost) instances when presence is known.
                    if (presentSubs.Count > 0 && !presentSubs.Contains(sub))
                    {
                        _log.Info("NvidiaGpuService", $"Skipping non-present NVIDIA adapter {sub}: '{driverDesc}'");
                        continue;
                    }

                    bool isActive = presentSubs.Count > 0 && presentSubs.Contains(sub);
                    result.Add(new NvidiaAdapter
                    {
                        SubKey = sub,
                        FullPath = $@"{DisplayClassPath}\{sub}",
                        DriverDesc = driverDesc,
                        IsActive = isActive
                    });
                    _log.Info("NvidiaGpuService", $"NVIDIA adapter detected at {sub}: '{driverDesc}'{(isActive ? " [PRESENT]" : "")}");
                }
                catch (Exception ex)
                {
                    _log.Warn("NvidiaGpuService", $"Could not read adapter subkey '{sub}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("NvidiaGpuService", "DetectNvidiaAdapters failed", ex);
        }

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
            _log.Info("NvidiaGpuService", "No NVIDIA display adapter found — NVIDIA panel will stay hidden.");
        return result;
    }

    /// <summary>The display-class subkeys of every physically-present NVIDIA GPU.</summary>
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
                if (name.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (string.IsNullOrEmpty(pnp)) continue;

                using var dev = _hive.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{pnp}", writable: false);
                if (dev == null) continue;

                if (dev.GetValue("Driver") is string drv && drv.Length >= 4)
                {
                    int slash = drv.LastIndexOf('\\');
                    string sub = slash >= 0 ? drv[(slash + 1)..] : drv;
                    if (sub.Length == 4 && sub.All(char.IsDigit))
                    {
                        subs.Add(sub);
                        _log.Info("NvidiaGpuService", $"Present NVIDIA GPU '{name}' → adapter subkey {sub} (pnp {pnp})");
                    }
                }
            }
        }
        catch (Exception ex) { _log.Warn("NvidiaGpuService", $"GetPresentAdapterSubKeys failed: {ex.Message}"); }
        return subs;
    }

    /// <summary>Adapters writes apply to: the present one(s), or the primary as the safe fallback.</summary>
    public static IReadOnlyList<NvidiaAdapter> WriteTargets(IReadOnlyList<NvidiaAdapter> adapters)
    {
        if (adapters == null || adapters.Count == 0) return Array.Empty<NvidiaAdapter>();
        var active = adapters.Where(a => a.IsActive).ToList();
        if (active.Count > 0) return active;
        return new[] { adapters[0] };
    }

    // ── Read ────────────────────────────────────────────────────────────────────

    public string[] GetValueNames(string adapterFullPath)
    {
        try
        {
            using var key = _hive.OpenSubKey(adapterFullPath, writable: false);
            return key?.GetValueNames() ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            _log.Warn("NvidiaGpuService", $"GetValueNames failed for {adapterFullPath}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public int? ReadValue(string adapterFullPath, string valueName)
    {
        try
        {
            using var key = _hive.OpenSubKey(adapterFullPath, writable: false);
            return ParseValue(key?.GetValue(valueName));
        }
        catch (Exception ex)
        {
            _log.Warn("NvidiaGpuService", $"ReadValue {valueName} failed for {adapterFullPath}: {ex.Message}");
            return null;
        }
    }

    public Dictionary<string, int> ReadProfile(string adapterFullPath)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in ManagedValueNames)
            if (ReadValue(adapterFullPath, name) is int v) map[name] = v;
        return map;
    }

    /// <summary>True when "Prefer maximum performance" is currently in force (PerfLevelSrc = max).</summary>
    public bool IsMaxPerformance(string adapterFullPath) =>
        ReadValue(adapterFullPath, PerfLevelSrc) == PerfLevelSrcMax;

    // ── Write / Reset (present adapters only, managed values only) ────────────────

    public TweakResult WriteValue(IReadOnlyList<NvidiaAdapter> adapters, string valueName, int value)
    {
        if (!ManagedValueNames.Contains(valueName))
            return TweakResult.Fail($"Refused to write unmanaged value '{valueName}'.");

        var targets = WriteTargets(adapters);
        if (targets.Count == 0) return TweakResult.Fail("No NVIDIA adapter to write to.");

        int written = 0;
        foreach (var a in targets)
        {
            try
            {
                using var key = _hive.OpenSubKey(a.FullPath, writable: true);
                if (key == null) { _log.Warn("NvidiaGpuService", $"Adapter key not writable: {a.FullPath}"); continue; }
                key.SetValue(valueName, value, RegistryValueKind.DWord);
                written++;
                _log.Info("NvidiaGpuService", $"Set {valueName}={value} on adapter {a.SubKey} ('{a.DriverDesc}')");
            }
            catch (Exception ex)
            {
                _log.Error("NvidiaGpuService", $"WriteValue {valueName} on {a.SubKey} failed", ex);
            }
        }
        return written > 0
            ? TweakResult.Ok($"{valueName} applied to {written} NVIDIA GPU(s).")
            : TweakResult.Fail($"Could not write {valueName} to any NVIDIA GPU.");
    }

    public TweakResult ResetValue(IReadOnlyList<NvidiaAdapter> adapters, string valueName)
    {
        if (!ManagedValueNames.Contains(valueName))
            return TweakResult.Fail($"Refused to delete unmanaged value '{valueName}'.");

        var targets = WriteTargets(adapters);
        if (targets.Count == 0) return TweakResult.Fail("No NVIDIA adapter to reset.");

        int cleared = 0;
        foreach (var a in targets)
        {
            try
            {
                using var key = _hive.OpenSubKey(a.FullPath, writable: true);
                if (key == null) continue;
                key.DeleteValue(valueName, throwOnMissingValue: false);
                cleared++;
            }
            catch (Exception ex)
            {
                _log.Error("NvidiaGpuService", $"ResetValue {valueName} on {a.SubKey} failed", ex);
            }
        }
        return cleared > 0 ? TweakResult.Ok($"{valueName} reset to driver default.") : TweakResult.Fail($"Could not reset {valueName}.");
    }

    /// <summary>
    /// GPU power management. on = idle power saving (driver default — deletes the override).
    /// off = "Prefer maximum performance" — writes PerfLevelSrc + PowerMizer for BOTH AC and
    /// battery (the safe reinforcement, like Intel's RC6 + RC6_DC pair). Fully reversible.
    /// </summary>
    public TweakResult SetPowerSaving(IReadOnlyList<NvidiaAdapter> adapters, bool on)
    {
        if (on) return RevertAll(adapters);   // back to driver default (adaptive — saves power)

        // Prefer maximum performance — write AC + battery together.
        WriteValue(adapters, PowerMizerEnable, 1);
        WriteValue(adapters, PowerMizerLevel, 1);
        WriteValue(adapters, PowerMizerLevelAC, 1);
        var r = WriteValue(adapters, PerfLevelSrc, PerfLevelSrcMax);
        return r.Success
            ? TweakResult.Ok("GPU set to prefer maximum performance (idle power saving off).")
            : r;
    }

    public TweakResult RevertAll(IReadOnlyList<NvidiaAdapter> adapters)
    {
        var targets = WriteTargets(adapters);
        if (targets.Count == 0) return TweakResult.Fail("No NVIDIA adapter to revert.");

        int cleared = 0;
        foreach (var a in targets)
        {
            try
            {
                using var key = _hive.OpenSubKey(a.FullPath, writable: true);
                if (key == null) continue;
                foreach (string name in ManagedValueNames) key.DeleteValue(name, throwOnMissingValue: false);
                cleared++;
                _log.Info("NvidiaGpuService", $"Reverted all NVIDIA power settings on adapter {a.SubKey}.");
            }
            catch (Exception ex)
            {
                _log.Error("NvidiaGpuService", $"RevertAll on {a.SubKey} failed", ex);
            }
        }
        return cleared > 0
            ? TweakResult.Ok("All NVIDIA settings reverted to driver defaults.")
            : TweakResult.Fail("Could not revert NVIDIA settings.");
    }
}
