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
        _methods = new List<IBatteryPauseMethod>
        {
            new DellModernMethod(),
            new DellLegacyMethod(),
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
                using var iface = new ManagementClass(Ns, "BIOSAttributeInterface", null);
                _ = iface.GetMethodParameters("SetAttribute"); // throws if class/namespace missing

                // Read PrimaryBattChargeCfg — log its current value and all PossibleValues
                // so we can see if "Custom" is a valid option on this specific BIOS version.
                // Diagnostic-only: log current value and PossibleValues if available.
                // We do NOT gate on this — some Dell BIOS versions don't expose
                // EnumerationAttribute but SetAttribute still works fine.
                try
                {
                    using var s = new ManagementObjectSearcher(Ns,
                        $"SELECT CurrentValue, PossibleValues FROM EnumerationAttribute WHERE AttributeName='{AttrName}'");
                    foreach (var item in s.Get())
                    {
                        var cur = item["CurrentValue"]?.ToString() ?? "(null)";
                        var pv  = item["PossibleValues"] as string[] ?? Array.Empty<string>();
                        _log.Info("BatteryPauseService",
                            $"Dell Probe: {AttrName} current='{cur}', PossibleValues=[{string.Join(", ", pv)}]");
                    }
                }
                catch (Exception ex)
                {
                    _log.Info("BatteryPauseService",
                        $"Dell Probe: EnumerationAttribute query skipped ({ex.Message})");
                }

                // Log all IntegerAttributes that look battery/charge-related so we know
                // what attribute names the BIOS actually exposes for custom thresholds.
                try
                {
                    using var si = new ManagementObjectSearcher(Ns,
                        "SELECT AttributeName, CurrentValue, MinValue, MaxValue FROM IntegerAttribute");
                    foreach (var item in si.Get())
                    {
                        var n = item["AttributeName"]?.ToString() ?? "";
                        if (n.IndexOf("Charge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Batt",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Custom", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _log.Info("BatteryPauseService",
                                $"Dell Probe: IntegerAttribute '{n}' " +
                                $"current={item["CurrentValue"]} " +
                                $"min={item["MinValue"]} max={item["MaxValue"]}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("BatteryPauseService",
                        $"Dell Probe: IntegerAttribute enum failed: {ex.Message}");
                }

                return BatteryPauseSupport.Supported;
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
            _log.Info("BatteryPauseService", "Dell Pause: attempting Custom mode (start=50, stop=55)");

            // ── Strategy A: combined "Custom:50:55" ──────────────────────────────
            // Dell CCTK sets custom charging as --PrimaryBattChargeCfg=Custom:50:55.
            // Some BIOS versions accept this same format via WMI SetAttribute,
            // atomically setting mode + thresholds in one call.
            if (SetAttr(AttrName, "Custom:50:55"))
            {
                _log.Info("BatteryPauseService", "Dell Pause: Strategy A (combined) succeeded");
                return true;
            }
            _log.Info("BatteryPauseService", "Dell Pause: Strategy A (combined) rejected — trying separate attributes");

            // ── Strategy B: thresholds first, then mode ───────────────────────────
            // BIOS validates CustomChargeStart/Stop at the moment Custom mode is
            // activated, so the integers must be written before the enum switch.
            bool startOk = SetAttr("CustomChargeStart", "50");
            bool stopOk  = SetAttr("CustomChargeStop",  "55");
            _log.Info("BatteryPauseService",
                $"Dell Pause: Strategy B thresholds — start={startOk}, stop={stopOk}");

            if (!startOk || !stopOk)
            {
                _log.Warn("BatteryPauseService",
                    "Dell Pause: CustomChargeStart/Stop attributes unavailable on this BIOS. " +
                    "Check log for PossibleValues and IntegerAttribute names from Probe.");
                return false;
            }

            bool modeOk = SetAttr(AttrName, "Custom");
            _log.Info("BatteryPauseService",
                $"Dell Pause: Strategy B mode switch — PrimaryBattChargeCfg=Custom → {(modeOk ? "OK" : "FAILED")}");

            // Verify what the BIOS reports after the write.
            var verify = ReadEnumAttr(AttrName);
            _log.Info("BatteryPauseService",
                $"Dell Pause: post-write read → PrimaryBattChargeCfg='{verify ?? "(null)"}'");

            return modeOk;
        }

        public void Resume(string? originalMode)
        {
            if (string.IsNullOrEmpty(originalMode))
            {
                SetAttr(AttrName, "Standard");
                return;
            }

            if (originalMode.StartsWith("Custom:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = originalMode.Split(':');
                if (parts.Length >= 3)
                {
                    SetAttr("CustomChargeStart", parts[1]);
                    SetAttr("CustomChargeStop",  parts[2]);
                }
                SetAttr(AttrName, "Custom");
            }
            else
            {
                SetAttr(AttrName, originalMode);
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

        private bool SetAttr(string attributeName, string value)
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
                        if (rc == 0)
                        {
                            _log.Info("BatteryPauseService",
                                $"Dell SetAttr({attributeName}={value}) → OK");
                            return true;
                        }
                        _log.Warn("BatteryPauseService",
                            $"Dell SetAttr({attributeName}={value}) → rc={rc}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("BatteryPauseService",
                    $"Dell SetAttr({attributeName}={value}) threw: {ex.Message}");
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
            // Thresholds first in the array — BIOS validates them at the moment
            // Custom mode is activated, so they must already be present.
            return Set(
                new[] { "CustomChargeStart", "CustomChargeStop", AttrName },
                new[] { "50",               "55",               "Custom"  });
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
}
