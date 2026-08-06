// ════════════════════════════════════════════════════════════════════════════
// ThermalManagementService.cs  ·  Dell BIOS thermal-profile control
// ════════════════════════════════════════════════════════════════════════════
//
// Reads and writes the Dell "Thermal Management" BIOS attribute (the same setting
// exposed by the MyDell / Dell Power Manager app: Optimized, Cool, Quiet,
// Ultra Performance). Uses the Dell Client BIOS WMI provider in
// root\dcim\sysman\biosattributes — identical plumbing to BatteryPauseService.
//
// AC vs Battery: the BIOS stores ONE global thermal value, not a split. Dell
// Power Manager fakes the AC/DC behaviour by rewriting that value when the power
// source changes. VisualViewModel does the same: it persists two preferences
// (AC mode + battery mode) and calls SetMode() on every plug/unplug.
//
// SUPPORT GATING (mirrors BatteryPauseService)
//   • Not a Dell                       → NotDell
//   • Dell but no BIOS WMI provider    → DriverMissing
//   • Dell + provider but no thermal
//     attribute on this model/BIOS      → NotSupported
//   • Thermal attribute present         → Supported (+ AvailableModes populated)
//
// DEFENDER SAFETY
//   System.Management WMI only. No process spawning, no driver loading.
// ════════════════════════════════════════════════════════════════════════════

using System.Management;

namespace Systema.Services;

public enum ThermalSupport
{
    Unknown,
    NotDell,
    DriverMissing,
    NotSupported,
    Supported,
}

/// <summary>
/// Dell BIOS thermal-profile reader/writer. Self-discovers the thermal attribute
/// name and its allowed values from the current machine's BIOS, so the UI only
/// ever offers modes the hardware actually supports.
/// </summary>
public sealed class ThermalManagementService
{
    private static readonly LoggerService _log = LoggerService.Instance;

    private const string Ns = @"root\dcim\sysman\biosattributes";

    // Candidate attribute names across Dell BIOS generations. First one that
    // EXISTS wins (presence of the attribute, not PossibleValues — see below).
    private static readonly string[] CandidateAttrNames =
    {
        "ThermalManagement",
        "Thermal Management",
        "Thermal",
        "ThermalMode",
        "ThermalConfiguration",
    };

    // Standard Dell BIOS ThermalManagement enum values. Used as the mode list
    // when the BIOS leaves EnumerationAttribute.PossibleValues empty — which it
    // does for ThermalManagement on many Latitude/Precision/XPS BIOS versions
    // (confirmed on the test machine: current=UltraPerformance, possible=[]).
    // SetAttribute doesn't validate against PossibleValues anyway; the BIOS
    // validates on write, and these four are the canonical Dell values.
    private static readonly string[] DefaultDellThermalModes =
    {
        "Optimized",
        "Cool",
        "Quiet",
        "UltraPerformance",
    };

    private ThermalSupport _support = ThermalSupport.Unknown;
    private string  _vendor       = "";
    private string  _model        = "";
    private string  _attrName     = "";
    private string  _statusMessage = "Not yet detected.";
    private string[] _modes        = System.Array.Empty<string>();
    // Current BIOS value captured during DetectSupport. Dell's per-attribute CurrentValue
    // read is flaky (see ReadCurrentValueSafe), so we cache the one detection read and use
    // it as a fallback — otherwise a later failed read leaves the selectors blank.
    private string? _currentValue;

    public ThermalSupport Support       => _support;
    public bool           IsSupported   => _support == ThermalSupport.Supported;
    public string         StatusMessage => _statusMessage;
    public string         AttributeName => _attrName;
    /// <summary>BIOS-reported allowed values, e.g. ["Optimized","Cool","Quiet","UltraPerformance"].</summary>
    public IReadOnlyList<string> AvailableModes => _modes;

    // ── Detection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Probes hardware + the Dell BIOS WMI provider. Caches the result. Safe to
    /// call repeatedly. Heavy WMI work — call from a worker thread.
    /// </summary>
    public ThermalSupport DetectSupport()
    {
        if (_support != ThermalSupport.Unknown) return _support;

        try
        {
            ReadManufacturerAndModel();

            if (!_vendor.ToLowerInvariant().Contains("dell"))
            {
                _support = ThermalSupport.NotDell;
                _statusMessage = "Thermal profiles are only available on Dell systems.";
                return _support;
            }

            // Confirm the Dell BIOS WMI provider is present AND usable. On a Dell,
            // ANY failure here (missing namespace/class, orphaned namespace whose
            // provider DLL was uninstalled → provider-load failure, etc.) means the
            // thermal interface isn't available — treat them all as DriverMissing so
            // the UI shows the "install Dell Command | Monitor" guidance rather than
            // silently disappearing.
            try
            {
                using var iface = new ManagementClass(Ns, "BIOSAttributeInterface", null);
                _ = iface.GetMethodParameters("SetAttribute"); // throws if class/namespace/provider missing
            }
            catch (Exception probeEx)
            {
                _support = ThermalSupport.DriverMissing;
                _log.Info("ThermalManagementService",
                    $"Detection: Dell BIOS WMI provider not usable ({probeEx.GetType().Name}: {probeEx.Message})");
                _statusMessage =
                    "Dell thermal control needs the Dell BIOS WMI provider, which isn't installed. " +
                    "Install “Dell Command | Monitor”, a lightweight, headless WMI provider " +
                    "(no MyDell, SupportAssist, or TechHub bloat) that exposes the BIOS thermal setting. " +
                    "Once it's installed, this card switches to the thermal selectors automatically.";
                return _support;
            }

            // Enumerate attribute NAMES only. This Dell provider's WQL parser
            // rejects WHERE clauses and multi-column projections, and a "SELECT *"
            // aborts mid-enumeration with "Not found" because some unrelated
            // attribute has a broken property provider. The single-column
            // "SELECT AttributeName" is the only form that reliably enumerates
            // every attribute — proven by the dump succeeding on this machine.
            //
            // We find the thermal attribute by name, then read CurrentValue ONLY
            // on that one matched instance (a lazy per-instance fetch), so the
            // broken sibling attributes never get touched.
            string? foundAttr = null;
            string? current   = null;
            var allNames = new List<string>();
            bool enumFailed = false;

            try
            {
                using var s = new ManagementObjectSearcher(Ns,
                    "SELECT AttributeName FROM EnumerationAttribute");
                foreach (ManagementObject item in s.Get())
                {
                    string? name = null;
                    try { name = item["AttributeName"]?.ToString(); } catch { }
                    if (string.IsNullOrEmpty(name)) { item.Dispose(); continue; }
                    allNames.Add(name!);

                    if (foundAttr == null &&
                        CandidateAttrNames.Contains(name!, StringComparer.OrdinalIgnoreCase))
                    {
                        foundAttr = name;
                        // Best-effort current value — fetch only on THIS instance.
                        try { current = item["CurrentValue"]?.ToString(); }
                        catch (Exception ex)
                        {
                            _log.Info("ThermalManagementService",
                                $"CurrentValue read for '{name}' skipped ({ex.Message}) — will default the selector");
                        }
                    }
                    item.Dispose();
                }
            }
            catch (Exception ex)
            {
                enumFailed = true;
                _log.Warn("ThermalManagementService", $"Attribute enumeration failed: {ex.Message}");
            }

            _log.Info("ThermalManagementService",
                $"BIOS EnumerationAttributes present: [{string.Join(", ", allNames)}]");

            // Fallback: on many Dell BIOS/provider states the EnumerationAttribute
            // projection is rejected ("Invalid object" / "Invalid query") even though
            // the BIOSAttributeInterface probe above succeeded and SetAttribute works
            // fine — BatteryPauseService hits the exact same thing and still treats the
            // provider as usable. When enumeration THREW or returned NOTHING, don't hide
            // the whole card: the provider is present and SetAttribute is BIOS-validated
            // on write, so offer the canonical ThermalManagement attribute + default
            // modes. We only fall through to NotSupported when enumeration genuinely
            // worked (returned a populated list) but contained no thermal attribute —
            // i.e. a Dell model that truly lacks the setting.
            if (foundAttr == null && (enumFailed || allNames.Count == 0))
            {
                foundAttr = "ThermalManagement";
                try { current = ReadCurrentValueSafe(foundAttr); } catch { /* best-effort */ }
                _log.Info("ThermalManagementService",
                    "Detection: attribute enumeration unavailable — using canonical ThermalManagement fallback " +
                    "(provider present, SetAttribute is BIOS-validated on write)");
            }

            if (foundAttr != null)
            {
                _attrName = foundAttr;
                _currentValue = current;                // cache the detection read for GetCurrentMode fallback
                _modes    = DefaultDellThermalModes;   // BIOS PossibleValues is unreliable here
                if (!string.IsNullOrEmpty(current) &&
                    !_modes.Contains(current!, StringComparer.OrdinalIgnoreCase))
                {
                    _modes = _modes.Append(current!).ToArray();
                }
                _support = ThermalSupport.Supported;
                _statusMessage = $"Dell thermal control available on {Describe()}.";
                _log.Info("ThermalManagementService",
                    $"Detection: attribute '{foundAttr}' current='{current ?? "(unread)"}' modes=[{string.Join(", ", _modes)}]");
                return _support;
            }

            _support = ThermalSupport.NotSupported;
            _statusMessage = $"No thermal-profile BIOS setting on {Describe()}.";
            _log.Info("ThermalManagementService", "Detection: no thermal attribute found");
            return _support;
        }
        catch (Exception ex)
        {
            _support = ThermalSupport.NotSupported;
            _statusMessage = "Thermal-profile detection failed on this device.";
            _log.Warn("ThermalManagementService", $"DetectSupport failed: {ex.Message}");
            return _support;
        }
    }

    // ── Read / Write ──────────────────────────────────────────────────────────

    /// <summary>Current BIOS thermal value, or null if unknown / unsupported.</summary>
    public string? GetCurrentMode()
    {
        if (!IsSupported) return null;
        // A fresh read wins (reflects a live change), but Dell's CurrentValue read often
        // fails; fall back to the value captured at detection so we never return null when
        // we actually read a real value once.
        string? live = ReadCurrentValueSafe(_attrName);
        if (!string.IsNullOrEmpty(live)) { _currentValue = live; return live; }
        return _currentValue;
    }

    /// <summary>
    /// Writes the given thermal mode to the BIOS. The value must be one of
    /// <see cref="AvailableModes"/> (raw BIOS value, not the friendly label).
    /// Returns true on confirmed success. No reboot needed on models that
    /// support runtime thermal switching (same as Dell Power Manager).
    /// </summary>
    public bool SetMode(string biosValue)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(biosValue)) return false;
        if (!_modes.Contains(biosValue, StringComparer.OrdinalIgnoreCase))
        {
            _log.Warn("ThermalManagementService",
                $"SetMode('{biosValue}') rejected — not in BIOS PossibleValues [{string.Join(", ", _modes)}]");
            return false;
        }
        bool ok = SetAttr(_attrName, biosValue);
        if (ok)
        {
            var verify = ReadCurrentValueSafe(_attrName);
            _log.Info("ThermalManagementService", $"SetMode('{biosValue}') applied — BIOS now reports '{verify ?? "(unread)"}'");
        }
        return ok;
    }

    // ── Friendly-label mapping ─────────────────────────────────────────────────

    /// <summary>Human-friendly label for a raw BIOS thermal value.</summary>
    public static string FriendlyLabel(string biosValue) => biosValue?.ToLowerInvariant() switch
    {
        "optimized"        => "Optimized",
        "cool"             => "Cool",
        "quiet"            => "Quiet",
        "ultraperformance" => "Ultra Performance",
        "ultra performance"=> "Ultra Performance",
        _ => biosValue ?? "Unknown",
    };

    /// <summary>Optional small grey hint shown beside a thermal mode in the dropdown.</summary>
    public static string FriendlyNote(string biosValue) => biosValue?.ToLowerInvariant() switch
    {
        "ultraperformance"  => "Uses more power",
        "ultra performance" => "Uses more power",
        "quiet"             => "Quieter fans, may cause heat issues on older Dell BIOS",
        "cool"              => "Cooler surface",
        _ => "",
    };

    // ── WMI helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads CurrentValue for a single attribute WITHOUT tripping the Dell
    /// provider's broken siblings. We enumerate with the single-column
    /// "SELECT AttributeName" (the only reliably-working form on this provider),
    /// find the matching instance, then lazily fetch CurrentValue on just that one
    /// instance — so a sibling attribute whose property provider returns "Not found"
    /// can't abort the whole read (which is what "SELECT *" did).
    /// Returns null if not found or unreadable.
    /// </summary>
    private static string? ReadCurrentValueSafe(string attrName)
    {
        try
        {
            using var s = new ManagementObjectSearcher(Ns,
                "SELECT AttributeName FROM EnumerationAttribute");
            foreach (ManagementObject item in s.Get())
            {
                using (item)
                {
                    string? name = null;
                    try { name = item["AttributeName"]?.ToString(); } catch { }
                    if (!string.Equals(name, attrName, StringComparison.OrdinalIgnoreCase)) continue;
                    try { return item["CurrentValue"]?.ToString(); }
                    catch (Exception ex)
                    {
                        _log.Info("ThermalManagementService", $"ReadCurrentValueSafe('{attrName}') CurrentValue unreadable: {ex.Message}");
                        return null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("ThermalManagementService", $"ReadCurrentValueSafe('{attrName}') failed: {ex.Message}");
        }
        return null;
    }

    private static bool SetAttr(string attributeName, string value)
    {
        try
        {
            using var ifaceClass = new ManagementClass(Ns, "BIOSAttributeInterface", null);
            using var instances  = ifaceClass.GetInstances();
            foreach (ManagementObject inst in instances)
            {
                using (inst)
                {
                    var inParams = inst.GetMethodParameters("SetAttribute");
                    inParams["AttributeName"]  = attributeName;
                    inParams["AttributeValue"] = value;
                    inParams["SecHandle"]   = new byte[0];
                    inParams["SecHndCount"] = 0u;
                    inParams["SecType"]     = 0u;

                    var result = inst.InvokeMethod("SetAttribute", inParams, null);
                    var rc = Convert.ToInt32(result?["Status"] ?? result?["ReturnValue"] ?? -1);
                    if (rc == 0) return true;
                    _log.Warn("ThermalManagementService", $"SetAttr({attributeName}={value}) → rc={rc}");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("ThermalManagementService", $"SetAttr({attributeName}={value}) threw: {ex.Message}");
        }
        return false;
    }

    private void ReadManufacturerAndModel()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var item in s.Get())
            {
                _vendor = (item["Manufacturer"]?.ToString() ?? "").Trim();
                _model  = (item["Model"]?.ToString()        ?? "").Trim();
                return;
            }
        }
        catch { }
    }

    /// <summary>True when the machine has a battery (laptop). Used to gate the battery selector.</summary>
    public static bool HasBattery()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            foreach (var _ in s.Get()) return true;
        }
        catch { }
        return false;
    }

    private string Describe() =>
        string.IsNullOrEmpty(_vendor) ? "this PC"
            : string.IsNullOrEmpty(_model) ? _vendor : $"{_vendor} {_model}";
}
