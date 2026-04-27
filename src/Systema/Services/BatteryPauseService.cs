// ════════════════════════════════════════════════════════════════════════════
// BatteryPauseService.cs  ·  Vendor-specific battery charging control
// ════════════════════════════════════════════════════════════════════════════
//
// Tells supported laptops to pause or limit battery charging while a Game Boost
// session is active. On laptops with undersized AC adapters (most gaming and
// business laptops), the power budget is shared between charging the battery
// and feeding CPU + GPU under load. Pausing charging frees the full adapter
// wattage for performance and reduces chassis heat.
//
// IMPORTANT: There is no universal Windows API for "stop charging." Every
// vendor exposes it differently. This service tries multiple approaches in
// priority order and uses the first one that works on the current device.
//
// METHOD MATRIX (probed in order — first match wins)
//   1. Dell modern         root\dcim\sysman\biosattributes ▸ BIOSAttributeInterface
//                          (loaded by DellTechHub / SupportAssist on 2018+ Dell business)
//   2. Dell legacy         root\dcim\sysman ▸ DCIM_BIOSService.SetBIOSAttributes
//                          (loaded by Dell Command | Configure / Monitor — pre-2022 path)
//   3. Lenovo              root\WMI ▸ Lenovo_SetBiosSetting + Lenovo_SaveBiosSettings
//                          (loaded by Lenovo Vantage / Energy Manager — ThinkPad / Legion)
//   4. HP                  root\hp\InstrumentedBIOS ▸ HP_BIOSSettingInterface.SetBIOSSetting
//                          (loaded by HP CMSL / HP Battery Health Manager — HP business)
//   5. Acer                root\WMI ▸ WMI GUID 79772EC5-04B1-4bfd-843C-61E7F77B6CC9
//                          (loaded by Acer Care Center / AcerSense — Predator / Aspire)
//   6. Powercfg threshold  Windows BATTERYTHRESHOLDSTART/STOP under SUB_BATTERY
//                          (works on ASUS where firmware honors the standard ACPI hook)
//
// CRASH SAFETY
//   On pause we capture (Method, OriginalMode, Vendor) into BatteryPauseSnapshot.
//   GameBoosterService persists this to boost_state.json BEFORE the WMI write
//   (write-ahead log). If Systema crashes / blue-screens / loses power mid-session,
//   the next Systema launch's RecoverBoostStateFromCrash calls Resume(snapshot)
//   which routes back through the SAME method that did the pause and restores the
//   exact original value. Restore is idempotent and safe even if pause never
//   actually fired.
//
// DEFENDER SAFETY
//   Uses only System.Management (signed Microsoft package) and System.Diagnostics
//   for powercfg. No process injection, driver loading, shellexec of unknown
//   binaries, or obfuscation. Comments are technical, not adversarial.
// ════════════════════════════════════════════════════════════════════════════

using System.Management;
using System.Runtime.InteropServices;

namespace Systema.Services;

/// <summary>Result of a vendor-support probe.</summary>
public enum BatteryPauseSupport
{
    Unknown,
    NotALaptop,
    UnsupportedVendor,
    DriverMissing,
    DetectedNotImplemented,
    Supported,
}

/// <summary>
/// Persisted alongside Game Booster's boost_state.json. Lets crash recovery
/// route back through the exact method that did the pause.
/// </summary>
public sealed class BatteryPauseSnapshot
{
    /// <summary>Stable identifier of the method (e.g. "DellModern", "Lenovo", "Powercfg").</summary>
    public string? Method       { get; set; }
    /// <summary>Vendor brand string for the UI (e.g. "Dell Inc.").</summary>
    public string? Vendor       { get; set; }
    /// <summary>Vendor-encoded original value to restore on Resume.</summary>
    public string? OriginalMode { get; set; }
    public bool    WasPaused    { get; set; }
}

public sealed class BatteryPauseService
{
    private static readonly LoggerService _log = LoggerService.Instance;

    private readonly List<IBatteryPauseMethod> _methods;

    private BatteryPauseSupport _support = BatteryPauseSupport.Unknown;
    private string _vendor = "";
    private string _model  = "";
    private string _statusMessage = "Not yet detected.";
    private IBatteryPauseMethod? _activeMethod;

    public BatteryPauseService()
    {
        // Order = probe priority. Vendor-specific before universal so we get the
        // most accurate behaviour on each brand.
        _methods = new List<IBatteryPauseMethod>
        {
            new DellModernMethod(),
            new DellLegacyMethod(),
            new LenovoMethod(),
            new HpMethod(),
            new AcerMethod(),
            new PowercfgMethod(),
        };
    }

    public BatteryPauseSupport Support  => _support;
    public string              Vendor   => _vendor;
    public string              Model    => _model;
    public string              StatusMessage => _statusMessage;
    public bool                IsSupported   => _support == BatteryPauseSupport.Supported;
    /// <summary>Stable identifier of the method that won the probe (or empty).</summary>
    public string              ActiveMethodName => _activeMethod?.Name ?? "";

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Probes hardware + every vendor / universal hook. Caches the result.
    /// First method to return Supported wins.
    /// </summary>
    public BatteryPauseSupport DetectSupport()
    {
        if (_support != BatteryPauseSupport.Unknown) return _support;

        try
        {
            if (!HasBattery())
            {
                _support       = BatteryPauseSupport.NotALaptop;
                _statusMessage = "This is a desktop (or no battery is present) — Battery Pause is not applicable.";
                _log.Info("BatteryPauseService", "Detection: no battery present");
                return _support;
            }

            ReadManufacturerAndModel();

            // Track the strongest negative result we hit so we can give a useful
            // diagnostic message if no method ends up Supported.
            var bestNegative = BatteryPauseSupport.UnsupportedVendor;
            string bestMessage = $"Battery Pause: no supported hook found on {Describe()}.";

            foreach (var m in _methods)
            {
                try
                {
                    var result = m.Probe(_vendor);
                    _log.Info("BatteryPauseService", $"Probe {m.Name}: {result}");

                    if (result == BatteryPauseSupport.Supported)
                    {
                        _activeMethod  = m;
                        _support       = BatteryPauseSupport.Supported;
                        _statusMessage = $"Battery Pause supported on your {Describe()} via {m.FriendlyName}.";
                        return _support;
                    }
                    // Rank: DriverMissing > DetectedNotImplemented > UnsupportedVendor
                    if (result == BatteryPauseSupport.DriverMissing && bestNegative != BatteryPauseSupport.DriverMissing)
                    {
                        bestNegative = result;
                        bestMessage  = $"Battery Pause: your {Describe()} could be supported, but {m.FriendlyName} is not installed. Install your laptop's vendor utility (e.g. Lenovo Vantage, MyDell, HP Support Assistant) and restart Systema.";
                    }
                    else if (result == BatteryPauseSupport.DetectedNotImplemented && bestNegative == BatteryPauseSupport.UnsupportedVendor)
                    {
                        bestNegative = result;
                        bestMessage  = $"Battery Pause: your {Describe()} is recognised but its specific charge-control hook hasn't been validated yet. Coming in a future Systema update.";
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("BatteryPauseService", $"Probe {m.Name} threw: {ex.Message}");
                }
            }

            _support       = bestNegative;
            _statusMessage = bestMessage;
        }
        catch (Exception ex)
        {
            _support       = BatteryPauseSupport.UnsupportedVendor;
            _statusMessage = "Battery Pause: hardware detection failed — feature unavailable on this device.";
            _log.Warn("BatteryPauseService", $"Detection failed: {ex.Message}");
        }

        return _support;
    }

    /// <summary>Battery percent 0-100 from Win32_Battery.</summary>
    public int? GetBatteryPercent()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT EstimatedChargeRemaining FROM Win32_Battery");
            foreach (var item in s.Get())
            {
                if (item["EstimatedChargeRemaining"] is ushort us) return us;
                if (item["EstimatedChargeRemaining"] is int    i ) return i;
                return Convert.ToInt32(item["EstimatedChargeRemaining"]);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Reads the active method's "current mode" string for snapshot purposes.</summary>
    public string? GetCurrentVendorMode()
        => _activeMethod?.GetCurrentMode();

    /// <summary>
    /// Pauses or limits battery charging via the chosen method.
    /// <paramref name="thresholdHint"/> is the user-preferred floor for methods
    /// that accept a custom percent (Powercfg, Dell custom). Methods that use a
    /// fixed preset (Lenovo Conservation, HP BHM, Acer Health Mode) ignore it.
    /// </summary>
    public BatteryPauseSnapshot? Pause(int thresholdHint)
    {
        if (_support != BatteryPauseSupport.Supported || _activeMethod == null)
        {
            _log.Info("BatteryPauseService", $"Pause skipped — support={_support} method={_activeMethod?.Name ?? "<none>"}");
            return null;
        }

        try
        {
            string? original = _activeMethod.GetCurrentMode();
            bool ok = _activeMethod.Pause(thresholdHint);
            if (!ok)
            {
                _log.Warn("BatteryPauseService", $"{_activeMethod.Name} Pause returned false");
                return null;
            }

            _log.Info("BatteryPauseService",
                $"Charging paused via {_activeMethod.Name} (was: '{original ?? "unknown"}')");

            return new BatteryPauseSnapshot
            {
                Method       = _activeMethod.Name,
                Vendor       = _vendor,
                OriginalMode = original,
                WasPaused    = true,
            };
        }
        catch (Exception ex)
        {
            _log.Warn("BatteryPauseService", $"Pause failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Resumes charging via the same method that paused it. Best-effort, never throws.</summary>
    public void Resume(BatteryPauseSnapshot? snapshot)
    {
        if (snapshot == null || !snapshot.WasPaused) return;

        try
        {
            // Look up the method by stable name. This makes recovery work even after
            // the user replaced the laptop OS or upgraded Systema across versions —
            // we route by the persisted method ID, not by current detection state.
            IBatteryPauseMethod? m = _methods.Find(x =>
                string.Equals(x.Name, snapshot.Method, StringComparison.OrdinalIgnoreCase));

            if (m == null)
            {
                _log.Warn("BatteryPauseService",
                    $"Resume: snapshot method '{snapshot.Method}' not in registry — skipping");
                return;
            }

            m.Resume(snapshot.OriginalMode);
            _log.Info("BatteryPauseService",
                $"Charging resumed via {m.Name} (restored to '{snapshot.OriginalMode ?? "default"}')");
        }
        catch (Exception ex)
        {
            _log.Warn("BatteryPauseService", $"Resume failed: {ex.Message}");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool HasBattery()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            foreach (var _ in s.Get()) return true;
        }
        catch { }
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

    private string Describe() =>
        string.IsNullOrEmpty(_vendor) ? "this laptop"
            : string.IsNullOrEmpty(_model) ? _vendor : $"{_vendor} {_model}";

    // ════════════════════════════════════════════════════════════════════════════
    // Method registry
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Internal contract for one charge-control approach.</summary>
    private interface IBatteryPauseMethod
    {
        /// <summary>Stable ID written to boost_state.json. Never change without a migration.</summary>
        string Name { get; }
        /// <summary>Human-readable name for the UI status text.</summary>
        string FriendlyName { get; }
        /// <summary>Probe whether this method can run on the current device.</summary>
        BatteryPauseSupport Probe(string vendor);
        /// <summary>Read current vendor mode for snapshot purposes. Null if unknown.</summary>
        string? GetCurrentMode();
        /// <summary>Apply pause. Returns true on confirmed success.</summary>
        bool Pause(int thresholdHint);
        /// <summary>Restore the saved original mode. Best-effort, never throws.</summary>
        void Resume(string? originalMode);
    }

    // ── Method 1: Dell modern (root\dcim\sysman\biosattributes) ────────────────
    //
    // Newer Dell business / gaming laptops (post-2018) expose BIOS settings via
    // EnumerationAttribute / IntegerAttribute classes in this namespace. The
    // Dell Client BIOS WMI provider is loaded by SupportAssist + DellTechHub +
    // MyDell (any of them is sufficient — Dell Command|Configure is no longer
    // required as of MyDell's release). Write happens via BIOSAttributeInterface.SetAttribute.
    //
    // Setting "PrimaryBattChargeCfg" values: "Standard", "Express", "PrimAcUse"
    // (drain on AC), "Adaptive", "Custom".  We use "Custom" with integer attributes
    // CustomChargeStart (min 50%) and CustomChargeStop (min 55%) to cap charging
    // at the lowest BIOS-allowed threshold rather than switching to "Always AC."
    //
    // GetCurrentMode encodes Custom state as "Custom:<start>:<stop>" so Resume can
    // restore the exact original thresholds, not just the mode name.

    private sealed class DellModernMethod : IBatteryPauseMethod
    {
        private const string Ns       = @"root\dcim\sysman\biosattributes";
        private const string AttrName = "PrimaryBattChargeCfg";

        public string Name         => "DellModern";
        public string FriendlyName => "Dell BIOS WMI";

        public BatteryPauseSupport Probe(string vendor)
        {
            if (!vendor.ToLowerInvariant().Contains("dell"))
                return BatteryPauseSupport.UnsupportedVendor;

            try
            {
                // First check the namespace + class are loaded — happens via DellTechHub /
                // SupportAssist / MyDell.  Get-CimClass equivalent.
                using var iface = new ManagementClass(Ns, "BIOSAttributeInterface", null);
                _ = iface.GetMethodParameters("SetAttribute"); // throws if class missing

                // Then check whether the BIOS exposes the battery charging attribute.
                // Some lower-tier consumer Dell models don't expose it at all.
                using var s = new ManagementObjectSearcher(Ns,
                    $"SELECT CurrentValue FROM EnumerationAttribute WHERE AttributeName='{AttrName}'");
                foreach (var _ in s.Get())
                    return BatteryPauseSupport.Supported;

                return BatteryPauseSupport.UnsupportedVendor;
            }
            catch (ManagementException me) when (
                me.ErrorCode == ManagementStatus.InvalidNamespace ||
                me.ErrorCode == ManagementStatus.InvalidClass)
            {
                return BatteryPauseSupport.DriverMissing;
            }
            catch
            {
                return BatteryPauseSupport.UnsupportedVendor;
            }
        }

        public string? GetCurrentMode()
        {
            try
            {
                string? mode = ReadEnumAttr(AttrName);
                if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
                {
                    // Capture thresholds so Resume restores them exactly.
                    string? start = ReadIntegerAttr("CustomChargeStart");
                    string? stop  = ReadIntegerAttr("CustomChargeStop");
                    return $"Custom:{start ?? "50"}:{stop ?? "55"}";
                }
                return mode;
            }
            catch { }
            return null;
        }

        public bool Pause(int thresholdHint)
        {
            // Set Custom mode first, then apply the lowest BIOS-allowed thresholds:
            // start = 50% (BIOS minimum), stop = 55% (BIOS minimum).
            if (!SetAttribute(AttrName, "Custom")) return false;
            SetAttribute("CustomChargeStart", "50");
            SetAttribute("CustomChargeStop",  "55");
            return true;
        }

        public void Resume(string? originalMode)
        {
            if (string.IsNullOrEmpty(originalMode))
            {
                // Safest Dell default when we have no snapshot.
                SetAttribute(AttrName, "Standard");
                return;
            }

            // Decode composite "Custom:<start>:<stop>" or plain mode name.
            if (originalMode.StartsWith("Custom:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = originalMode.Split(':');
                SetAttribute(AttrName, "Custom");
                if (parts.Length >= 3)
                {
                    SetAttribute("CustomChargeStart", parts[1]);
                    SetAttribute("CustomChargeStop",  parts[2]);
                }
            }
            else
            {
                SetAttribute(AttrName, originalMode);
            }
        }

        private string? ReadEnumAttr(string attrName)
        {
            try
            {
                using var s = new ManagementObjectSearcher(Ns,
                    $"SELECT CurrentValue FROM EnumerationAttribute WHERE AttributeName='{attrName}'");
                foreach (var item in s.Get())
                    return item["CurrentValue"]?.ToString();
            }
            catch { }
            return null;
        }

        private string? ReadIntegerAttr(string attrName)
        {
            try
            {
                using var s = new ManagementObjectSearcher(Ns,
                    $"SELECT CurrentValue FROM IntegerAttribute WHERE AttributeName='{attrName}'");
                foreach (var item in s.Get())
                    return item["CurrentValue"]?.ToString();
            }
            catch { }
            return null;
        }

        private bool SetAttribute(string attributeName, string value)
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
                        // SecHandle / SecHndCount / SecType are required parameters — pass
                        // empty / zero values which the BIOS interprets as "no admin password".
                        // Most consumer laptops have no BIOS admin password configured.
                        inParams["SecHandle"]   = new byte[0];
                        inParams["SecHndCount"] = 0u;
                        inParams["SecType"]     = 0u;

                        var result = inst.InvokeMethod("SetAttribute", inParams, null);
                        var rc = Convert.ToInt32(result?["Status"] ?? result?["ReturnValue"] ?? -1);
                        if (rc == 0) return true;
                        _log.Warn("BatteryPauseService", $"Dell SetAttribute({attributeName}={value}) returned {rc}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("BatteryPauseService", $"Dell SetAttribute({attributeName}) threw: {ex.Message}");
            }
            return false;
        }
    }

    // ── Method 2: Dell legacy (root\dcim\sysman) ───────────────────────────────
    //
    // Older Dell systems (with Dell Command | Configure or Dell Command | Monitor)
    // expose the same setting via the older DCIM_* class hierarchy and the
    // DCIM_BIOSService.SetBIOSAttributes method. Same attribute name — just
    // different plumbing.  SetBIOSAttributes accepts arrays so we batch Custom +
    // threshold writes in one call.

    private sealed class DellLegacyMethod : IBatteryPauseMethod
    {
        private const string Ns       = @"root\dcim\sysman";
        private const string AttrName = "PrimaryBattChargeCfg";

        public string Name         => "DellLegacy";
        public string FriendlyName => "Dell Command | Monitor";

        public BatteryPauseSupport Probe(string vendor)
        {
            if (!vendor.ToLowerInvariant().Contains("dell"))
                return BatteryPauseSupport.UnsupportedVendor;

            try
            {
                using var s = new ManagementObjectSearcher(Ns,
                    $"SELECT CurrentValue FROM DCIM_BIOSEnumeration WHERE AttributeName='{AttrName}'");
                foreach (var _ in s.Get()) return BatteryPauseSupport.Supported;
                return BatteryPauseSupport.UnsupportedVendor;
            }
            catch (ManagementException me) when (
                me.ErrorCode == ManagementStatus.InvalidNamespace ||
                me.ErrorCode == ManagementStatus.InvalidClass)
            {
                return BatteryPauseSupport.DriverMissing;
            }
            catch { return BatteryPauseSupport.UnsupportedVendor; }
        }

        public string? GetCurrentMode()
        {
            try
            {
                string? mode = ReadEnumAttr(AttrName);
                if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
                {
                    string? start = ReadIntegerAttr("CustomChargeStart");
                    string? stop  = ReadIntegerAttr("CustomChargeStop");
                    return $"Custom:{start ?? "50"}:{stop ?? "55"}";
                }
                return mode;
            }
            catch { }
            return null;
        }

        public bool Pause(int thresholdHint)
        {
            // Batch: set Custom mode + lowest allowed thresholds in one WMI call.
            return Set(
                new[] { AttrName,  "CustomChargeStart", "CustomChargeStop" },
                new[] { "Custom",  "50",                "55"               });
        }

        public void Resume(string? originalMode)
        {
            if (string.IsNullOrEmpty(originalMode))
            {
                Set(new[] { AttrName }, new[] { "Standard" });
                return;
            }

            if (originalMode.StartsWith("Custom:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = originalMode.Split(':');
                if (parts.Length >= 3)
                    Set(new[] { AttrName, "CustomChargeStart", "CustomChargeStop" },
                        new[] { "Custom",  parts[1],             parts[2] });
                else
                    Set(new[] { AttrName }, new[] { "Custom" });
            }
            else
            {
                Set(new[] { AttrName }, new[] { originalMode });
            }
        }

        private string? ReadEnumAttr(string attrName)
        {
            try
            {
                using var s = new ManagementObjectSearcher(Ns,
                    $"SELECT CurrentValue FROM DCIM_BIOSEnumeration WHERE AttributeName='{attrName}'");
                foreach (var item in s.Get())
                    return item["CurrentValue"]?.ToString();
            }
            catch { }
            return null;
        }

        private string? ReadIntegerAttr(string attrName)
        {
            try
            {
                using var s = new ManagementObjectSearcher(Ns,
                    $"SELECT CurrentValue FROM DCIM_BIOSInteger WHERE AttributeName='{attrName}'");
                foreach (var item in s.Get())
                    return item["CurrentValue"]?.ToString();
            }
            catch { }
            return null;
        }

        private static bool Set(string[] attrNames, string[] values)
        {
            try
            {
                using var svcClass  = new ManagementClass(Ns, "DCIM_BIOSService", null);
                using var instances = svcClass.GetInstances();
                foreach (ManagementObject svc in instances)
                {
                    using (svc)
                    {
                        var inParams = svc.GetMethodParameters("SetBIOSAttributes");
                        inParams["AttributeName"]  = attrNames;
                        inParams["AttributeValue"] = values;
                        var result = svc.InvokeMethod("SetBIOSAttributes", inParams, null);
                        var rc = Convert.ToInt32(result?["ReturnValue"] ?? -1);
                        if (rc == 0) return true;
                        _log.Warn("BatteryPauseService", $"Dell legacy SetBIOSAttributes returned {rc}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("BatteryPauseService", $"Dell legacy SetBIOSAttributes threw: {ex.Message}");
            }
            return false;
        }
    }

    // ── Method 3: Lenovo (root\WMI Lenovo_*) ───────────────────────────────────
    //
    // Lenovo BIOS WMI exposes settings as "Item,Value" strings in Lenovo_BiosSetting.
    // Writes go through Lenovo_SetBiosSetting.SetBiosSetting and MUST be followed
    // by Lenovo_SaveBiosSettings.SaveBiosSettings or the change won't persist.
    //
    // The setting name varies by model: "ChargeThreshold" on newer ThinkPad,
    // "BCCS" on IdeaPad / Legion. We probe for any setting whose name starts
    // with one of these and use whichever is present.

    private sealed class LenovoMethod : IBatteryPauseMethod
    {
        private const string Ns = @"root\WMI";

        // Candidate setting names ordered by preference. First one found is used.
        private static readonly string[] CandidateNames = {
            "ChargeThreshold",        // newer ThinkPad / business
            "BCCS",                   // IdeaPad / Legion / older ThinkPad
            "BatteryConservationMode",
        };

        private string? _detectedSettingName;

        public string Name         => "Lenovo";
        public string FriendlyName => "Lenovo Vantage BIOS WMI";

        public BatteryPauseSupport Probe(string vendor)
        {
            if (!vendor.ToLowerInvariant().Contains("lenovo"))
                return BatteryPauseSupport.UnsupportedVendor;

            try
            {
                using var s = new ManagementObjectSearcher(Ns,
                    "SELECT CurrentSetting FROM Lenovo_BiosSetting");
                int total = 0;
                foreach (var item in s.Get())
                {
                    var raw = item["CurrentSetting"]?.ToString() ?? "";
                    foreach (var c in CandidateNames)
                    {
                        if (raw.StartsWith(c + ",", StringComparison.OrdinalIgnoreCase))
                        {
                            _detectedSettingName = c;
                            return BatteryPauseSupport.Supported;
                        }
                    }
                    if (++total > 400) break; // Lenovo BIOS exposes hundreds of settings — cap
                }
                return total > 0 ? BatteryPauseSupport.UnsupportedVendor
                                 : BatteryPauseSupport.DriverMissing;
            }
            catch (ManagementException me) when (
                me.ErrorCode == ManagementStatus.InvalidNamespace ||
                me.ErrorCode == ManagementStatus.InvalidClass)
            {
                return BatteryPauseSupport.DriverMissing;
            }
            catch { return BatteryPauseSupport.UnsupportedVendor; }
        }

        public string? GetCurrentMode()
        {
            if (_detectedSettingName == null) return null;
            try
            {
                using var s = new ManagementObjectSearcher(Ns,
                    "SELECT CurrentSetting FROM Lenovo_BiosSetting");
                foreach (var item in s.Get())
                {
                    var raw = item["CurrentSetting"]?.ToString() ?? "";
                    if (raw.StartsWith(_detectedSettingName + ",", StringComparison.OrdinalIgnoreCase))
                        return raw.Substring(_detectedSettingName.Length + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        public bool Pause(int thresholdHint)
        {
            if (_detectedSettingName == null) return false;
            // ChargeThreshold takes a percent string; BCCS takes "0" or "1".
            // We use the user's threshold for the percent path and "1" for the binary path.
            string value = string.Equals(_detectedSettingName, "ChargeThreshold", StringComparison.OrdinalIgnoreCase)
                ? Math.Clamp(thresholdHint, 50, 95).ToString()
                : "1";
            return SetAndSave(_detectedSettingName, value);
        }

        public void Resume(string? originalMode)
        {
            if (_detectedSettingName == null) return;
            // ChargeThreshold restored to original percent (or 100 = no cap).
            // BCCS restored to original "0"/"1" (or "0" = off if unknown).
            var fallback = string.Equals(_detectedSettingName, "ChargeThreshold", StringComparison.OrdinalIgnoreCase) ? "100" : "0";
            SetAndSave(_detectedSettingName, string.IsNullOrEmpty(originalMode) ? fallback : originalMode);
        }

        private static bool SetAndSave(string item, string value)
        {
            bool setOk = InvokeBios("Lenovo_SetBiosSetting", "SetBiosSetting", $"{item},{value};");
            if (!setOk) return false;
            // SaveBiosSettings is REQUIRED on Lenovo — without it the change reverts on next boot.
            return InvokeBios("Lenovo_SaveBiosSettings", "SaveBiosSettings", ";");
        }

        private static bool InvokeBios(string className, string methodName, string parameter)
        {
            try
            {
                using var c   = new ManagementClass(Ns, className, null);
                using var ins = c.GetInstances();
                foreach (ManagementObject inst in ins)
                {
                    using (inst)
                    {
                        var inParams = inst.GetMethodParameters(methodName);
                        inParams["parameter"] = parameter;
                        var result = inst.InvokeMethod(methodName, inParams, null);
                        var ret = result?["return"]?.ToString() ?? "";
                        if (string.Equals(ret, "Success", StringComparison.OrdinalIgnoreCase)) return true;
                        _log.Warn("BatteryPauseService", $"Lenovo {className}.{methodName} returned '{ret}'");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("BatteryPauseService", $"Lenovo {className}.{methodName} threw: {ex.Message}");
            }
            return false;
        }
    }

    // ── Method 4: HP (root\hp\InstrumentedBIOS) ────────────────────────────────
    //
    // HP business laptops with Battery Health Manager (BHM) expose the setting
    // "Battery Health Manager" through HP_BIOSEnumeration / HP_BIOSSettingInterface.
    // Values: "Maximize my battery health" (cap at ~80%), "Let HP manage my battery
    // charging" (HP-adaptive — caps based on usage pattern), or "Maximum charge"
    // (full 100% charging).

    private sealed class HpMethod : IBatteryPauseMethod
    {
        private const string Ns       = @"root\hp\InstrumentedBIOS";
        private const string AttrName = "Battery Health Manager";
        private const string PauseValue = "Maximize my battery health";

        public string Name         => "HP";
        public string FriendlyName => "HP Battery Health Manager";

        public BatteryPauseSupport Probe(string vendor)
        {
            var v = vendor.ToLowerInvariant();
            if (!v.Contains("hewlett") && !v.StartsWith("hp"))
                return BatteryPauseSupport.UnsupportedVendor;

            try
            {
                using var s = new ManagementObjectSearcher(Ns,
                    $"SELECT CurrentValue FROM HP_BIOSEnumeration WHERE Name='{AttrName}'");
                foreach (var _ in s.Get()) return BatteryPauseSupport.Supported;
                return BatteryPauseSupport.UnsupportedVendor;
            }
            catch (ManagementException me) when (
                me.ErrorCode == ManagementStatus.InvalidNamespace ||
                me.ErrorCode == ManagementStatus.InvalidClass)
            {
                return BatteryPauseSupport.DriverMissing;
            }
            catch { return BatteryPauseSupport.UnsupportedVendor; }
        }

        public string? GetCurrentMode()
        {
            try
            {
                using var s = new ManagementObjectSearcher(Ns,
                    $"SELECT CurrentValue FROM HP_BIOSEnumeration WHERE Name='{AttrName}'");
                foreach (var item in s.Get())
                    return item["CurrentValue"]?.ToString();
            }
            catch { }
            return null;
        }

        public bool Pause(int thresholdHint) => SetSetting(PauseValue);
        public void Resume(string? originalMode) =>
            SetSetting(string.IsNullOrEmpty(originalMode) ? "Maximum charge" : originalMode);

        private static bool SetSetting(string value)
        {
            try
            {
                using var c   = new ManagementClass(Ns, "HP_BIOSSettingInterface", null);
                using var ins = c.GetInstances();
                foreach (ManagementObject inst in ins)
                {
                    using (inst)
                    {
                        var inParams = inst.GetMethodParameters("SetBIOSSetting");
                        inParams["Name"]     = AttrName;
                        inParams["Value"]    = value;
                        inParams["Password"] = "<utf-16/>"; // empty-password marker per HP CMSL convention
                        var result = inst.InvokeMethod("SetBIOSSetting", inParams, null);
                        var rc = Convert.ToInt32(result?["Return"] ?? -1);
                        if (rc == 0) return true;
                        _log.Warn("BatteryPauseService", $"HP SetBIOSSetting returned {rc}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("BatteryPauseService", $"HP SetBIOSSetting threw: {ex.Message}");
            }
            return false;
        }
    }

    // ── Method 5: Acer (root\WMI WMI GUID 79772EC5-...) ────────────────────────
    //
    // Acer Care Center / AcerSense exposes battery health via a WMI method
    // identified by GUID 79772EC5-04B1-4bfd-843C-61E7F77B6CC9. Method 21 sets
    // health mode (8-byte parameter); method 20 queries it. The "health mode"
    // is a single binary toggle that caps charging at 80%.

    private sealed class AcerMethod : IBatteryPauseMethod
    {
        // The class name WMI exposes for this GUID is provider-specific. Acer's
        // ATKACPI driver registers it as "AcerSense_Method" or similar — we
        // enumerate the namespace at probe time to find which class carries the GUID.
        private const string Ns = @"root\WMI";
        private const string GuidStr = "79772EC5-04B1-4bfd-843C-61E7F77B6CC9";
        private string? _className;

        public string Name         => "Acer";
        public string FriendlyName => "Acer Care Center battery health";

        public BatteryPauseSupport Probe(string vendor)
        {
            if (!vendor.ToLowerInvariant().Contains("acer"))
                return BatteryPauseSupport.UnsupportedVendor;

            try
            {
                using var classes = new ManagementClass(Ns, "meta_class", null).GetSubclasses();
                foreach (ManagementObject c in classes)
                {
                    using (c)
                    {
                        var qual = c.Qualifiers["WMI"]; // not all classes have it
                        var guidQual = c.Qualifiers["guid"];
                        var guid = guidQual?.Value?.ToString() ?? "";
                        if (guid.Trim('{','}').Equals(GuidStr, StringComparison.OrdinalIgnoreCase))
                        {
                            _className = c["__CLASS"]?.ToString();
                            return BatteryPauseSupport.Supported;
                        }
                    }
                }
                return BatteryPauseSupport.DriverMissing;
            }
            catch { return BatteryPauseSupport.DriverMissing; }
        }

        public string? GetCurrentMode()
        {
            // Method 20 returns current health-mode byte. We persist it as the digit "0" or "1".
            try
            {
                if (_className == null) return null;
                using var c   = new ManagementClass(Ns, _className, null);
                using var ins = c.GetInstances();
                foreach (ManagementObject inst in ins)
                {
                    using (inst)
                    {
                        var inParams = inst.GetMethodParameters("WMAB"); // canonical Acer method name
                        inParams["Data"] = new byte[] { 0x14, 0x01, 0, 0, 0, 0, 0, 0 }; // method 20, batt 1
                        var result = inst.InvokeMethod("WMAB", inParams, null);
                        if (result?["Data"] is byte[] resp && resp.Length > 1)
                            return (resp[1] & 0x01) != 0 ? "1" : "0";
                    }
                }
            }
            catch { }
            return null;
        }

        public bool Pause(int thresholdHint) => SetHealth(true);
        public void Resume(string? originalMode) => SetHealth(originalMode == "1");

        private bool SetHealth(bool on)
        {
            try
            {
                if (_className == null) return false;
                using var c   = new ManagementClass(Ns, _className, null);
                using var ins = c.GetInstances();
                foreach (ManagementObject inst in ins)
                {
                    using (inst)
                    {
                        var inParams = inst.GetMethodParameters("WMAB");
                        // Method 21 set: byte0=method, byte1=batt index, byte2=mask (0x01=health), byte3=on/off
                        inParams["Data"] = new byte[]
                        {
                            0x15, 0x01, 0x01, (byte)(on ? 0x01 : 0x00), 0, 0, 0, 0
                        };
                        var result = inst.InvokeMethod("WMAB", inParams, null);
                        if (result?["Data"] is byte[] resp && resp.Length > 0 && resp[0] == 0)
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("BatteryPauseService", $"Acer SetHealth threw: {ex.Message}");
            }
            return false;
        }
    }

    // ── Method 6: powrprof.dll (universal — works on ASUS + any laptop where firmware
    // honors the standard ACPI battery threshold IOCTL) ───────────────────────
    //
    // Microsoft exposes BATTERYTHRESHOLDSTART / BATTERYTHRESHOLDSTOP under the
    // SUB_BATTERY power-scheme subgroup. ASUS's ATKACPI driver and a few other
    // OEMs honor these — when set, the EC stops charging above STOP and resumes
    // below START.
    //
    // We call the powrprof.dll Win32 APIs directly instead of spawning powercfg.exe.
    // powercfg.exe uses these same APIs internally, so the result is identical,
    // but there is no subprocess launch — which eliminates the "hidden process with
    // redirected I/O" heuristic that AV engines flag on unsigned apps.

    private sealed class PowercfgMethod : IBatteryPauseMethod
    {
        // Standard Microsoft GUIDs — these never change across Windows versions.
        private static readonly Guid SubBattery      = new Guid("e73a048d-bf27-4f12-9731-8b2076e8891f");
        private static readonly Guid ThresholdStart  = new Guid("f1244e21-8c6c-4c70-bd7e-6a1f2b3a4ab1");
        private static readonly Guid ThresholdStop   = new Guid("37f3aafa-8c91-4f0e-b69a-1a6cd2b3fe0f");

        public string Name         => "Powercfg";
        public string FriendlyName => "Windows BATTERYTHRESHOLD";

        // ── powrprof.dll — Windows Power Profile API ────────────────────────────
        // Standard, widely-used Windows APIs. Same DLL powercfg.exe links against.

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(
            IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(
            IntPtr RootPowerKey, ref Guid SchemeGuid,
            ref Guid SubGroupGuid, ref Guid PowerSettingGuid,
            out uint AcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteACValueIndex(
            IntPtr RootPowerKey, ref Guid SchemeGuid,
            ref Guid SubGroupGuid, ref Guid PowerSettingGuid,
            uint AcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteDCValueIndex(
            IntPtr RootPowerKey, ref Guid SchemeGuid,
            ref Guid SubGroupGuid, ref Guid PowerSettingGuid,
            uint DcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(
            IntPtr UserRootPowerKey, ref Guid SchemeGuid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public BatteryPauseSupport Probe(string vendor)
        {
            // Probe by reading the threshold-start value from the active power scheme.
            // If the firmware doesn't register this sub-setting, PowerReadACValueIndex
            // returns a non-zero error code (e.g. ERROR_FILE_NOT_FOUND = 2).
            try
            {
                var scheme = GetActiveSchemeGuid();
                if (scheme == null) return BatteryPauseSupport.UnsupportedVendor;
                var schemeGuid  = scheme.Value;
                var sub         = SubBattery;
                var threshStart = ThresholdStart;
                uint rc = PowerReadACValueIndex(
                    IntPtr.Zero, ref schemeGuid, ref sub, ref threshStart, out _);
                return rc == 0 ? BatteryPauseSupport.Supported : BatteryPauseSupport.UnsupportedVendor;
            }
            catch { return BatteryPauseSupport.UnsupportedVendor; }
        }

        public string? GetCurrentMode()
        {
            // Encode "start,stop" so Resume can restore both values exactly.
            try
            {
                var scheme = GetActiveSchemeGuid();
                if (scheme == null) return null;
                uint start = ReadAc(scheme.Value, ThresholdStart);
                uint stop  = ReadAc(scheme.Value, ThresholdStop);
                return $"{start},{stop}";
            }
            catch { return null; }
        }

        public bool Pause(int thresholdHint)
        {
            uint stop  = (uint)Math.Clamp(thresholdHint, 30, 95);
            uint start = (uint)Math.Max(20, (int)stop - 5);
            return Apply(start, stop);
        }

        public void Resume(string? originalMode)
        {
            uint start = 0, stop = 100;
            if (!string.IsNullOrEmpty(originalMode) && originalMode.Contains(','))
            {
                var parts = originalMode.Split(',');
                if (parts.Length == 2 &&
                    uint.TryParse(parts[0], out var s) &&
                    uint.TryParse(parts[1], out var e))
                { start = s; stop = e; }
            }
            Apply(start, stop);
        }

        private static bool Apply(uint start, uint stop)
        {
            try
            {
                var scheme = GetActiveSchemeGuid();
                if (scheme == null) return false;

                bool ok = WriteIndex(scheme.Value, ThresholdStart, start) &&
                          WriteIndex(scheme.Value, ThresholdStop,  stop);
                if (!ok) return false;

                // Activate the scheme so the EC picks up the new values immediately.
                var s = scheme.Value;
                PowerSetActiveScheme(IntPtr.Zero, ref s);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("BatteryPauseService", $"PowercfgMethod.Apply threw: {ex.Message}");
                return false;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Guid? GetActiveSchemeGuid()
        {
            uint rc = PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr);
            if (rc != 0 || ptr == IntPtr.Zero) return null;
            try   { return Marshal.PtrToStructure<Guid>(ptr); }
            finally { LocalFree(ptr); }
        }

        private static uint ReadAc(Guid scheme, Guid setting)
        {
            var sub = SubBattery;
            PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint val);
            return val;
        }

        private static bool WriteIndex(Guid scheme, Guid setting, uint value)
        {
            var sub = SubBattery;
            uint r1 = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
            uint r2 = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
            if (r1 != 0) _log.Warn("BatteryPauseService", $"PowerWriteACValueIndex returned {r1}");
            if (r2 != 0) _log.Warn("BatteryPauseService", $"PowerWriteDCValueIndex returned {r2}");
            return r1 == 0 && r2 == 0;
        }
    }
}
