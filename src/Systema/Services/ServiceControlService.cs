// ════════════════════════════════════════════════════════════════════════════
// ServiceControlService.cs  ·  Windows service enumeration and state management
// ════════════════════════════════════════════════════════════════════════════
//
// Lists all Windows services enriched with a Recommended/Expert categorization
// and a safety-level badge. Provides Enable, Disable, and Restart operations.
// Also used by GameBoosterService to kill and restore a configured kill list of
// services during active boost sessions.
//
// RELATED FILES
//   Models/ServiceInfo.cs      — service row data shape with Recommendation field
//   ServicesViewModel.cs       — lists services and drives enable/disable commands
//   GameBoosterService.cs      — uses this to kill/restore services during boost
// ════════════════════════════════════════════════════════════════════════════

using System.ServiceProcess;
using Microsoft.Win32;
using Systema.Core;
using Systema.Models;
using static Systema.Core.ThreadHelper;

namespace Systema.Services;

public class ServiceControlService
{
    private static readonly LoggerService Log = LoggerService.Instance;
    // Services that should NEVER get the "Recommended" tag
    private static readonly HashSet<string> _noRecommendedTag = new(StringComparer.OrdinalIgnoreCase)
    {
        "Spooler",        // Print Spooler — needed if user has a printer
        "XboxGipSvc",     // Xbox Accessory Management
        "xbgm",           // Xbox Game Monitoring
        "XblAuthManager", // Xbox Live Auth Manager
        "XblGameSave",    // Xbox Live Game Save
        "XboxNetApiSvc",  // Xbox Live Networking
        "bthserv",        // Bluetooth Support — needed if user has Bluetooth devices
        "Fax",            // Fax — needed for fax functionality
        "BITS",           // Background Intelligent Transfer — Windows Update uses this
        "wbengine",       // Block Level Backup Engine — needed if using Windows Backup
        "SCardSvr",       // Smart Card — needed if user has a smart card reader
        "SCPolicySvc",    // Smart Card Removal Policy — needed with smart cards
        "SysMain",        // SuperFetch — useful on systems with less RAM
        "WSearch",        // Windows Search — disabling slows File Explorer search
    };

    public static readonly List<(string ServiceName, string DisplayName, string Description, string Tooltip)> OptimizableServices = new()
    {
        // ── Telemetry & diagnostics ───────────────────────────────────────────
        ("DiagTrack",         "Connected User Experiences & Telemetry",
            "Silently uploads usage statistics, diagnostics, and behavioral data to Microsoft servers around the clock.",
            "Safe to disable. Does not affect Windows Update, security, or performance."),
        ("dmwappushservice",  "Device Management WAP Push",
            "Routes WAP Push messages for Mobile Device Management (MDM) enrollment — used by corporate IT to manage devices remotely.",
            "Safe to disable on personal PCs not managed by a company IT department."),
        ("WerSvc",            "Windows Error Reporting",
            "Automatically captures crash dumps and sends error reports to Microsoft when apps or Windows itself crashes.",
            "Safe to disable. Crash reports help Microsoft but are not needed for your PC to run."),

        // ── Search & indexing ─────────────────────────────────────────────────
        ("WSearch",           "Windows Search",
            "Continuously indexes files, emails, and documents in the background so searches return results instantly.",
            "Disabling frees disk I/O and memory. Search still works, but File Explorer searches will be slower."),

        // ── Memory prefetching ────────────────────────────────────────────────
        ("SysMain",           "SysMain (SuperFetch)",
            "Pre-loads your most-used apps into RAM in the background to make them open faster.",
            "Safe to disable on gaming PCs or systems with 16GB+ RAM where games need all available memory."),

        // ── Maps ──────────────────────────────────────────────────────────────
        ("MapsBroker",        "Downloaded Maps Manager",
            "Manages offline map downloads and auto-updates cached map data for the Windows Maps app.",
            "Safe to disable if you don't use the Windows Maps app."),

        // ── Security / remote access ──────────────────────────────────────────
        ("RemoteRegistry",    "Remote Registry",
            "Allows other computers on the network to remotely read and modify your Windows registry.",
            "Disable this — it is a security risk. IT admins should use Group Policy instead."),

        // ── Printing ──────────────────────────────────────────────────────────
        ("Spooler",           "Print Spooler",
            "Manages print jobs sent to local and network printers. Required for any printing to work.",
            "Disable only if you have no printer. This will break all printing if disabled."),

        // ── Input devices ─────────────────────────────────────────────────────
        ("TabletInputService","Touch Keyboard and Handwriting",
            "Provides the on-screen touch keyboard, handwriting panel, and stylus input on touchscreen devices.",
            "Safe to disable on desktops and laptops without a touchscreen."),

        // ── Xbox ──────────────────────────────────────────────────────────────
        ("XboxGipSvc",        "Xbox Accessory Management",
            "Manages Xbox controllers, headsets, and accessories connected via USB or Bluetooth.",
            "Safe to disable if you don't own Xbox peripherals. Re-enable if controllers stop responding."),
        ("xbgm",              "Xbox Game Monitoring",
            "Tracks game sessions and activity for Xbox achievement recording and Game Bar statistics.",
            "Safe to disable if you don't use Xbox Game Bar or care about Xbox achievements."),
        ("XblAuthManager",    "Xbox Live Auth Manager",
            "Handles sign-in and authentication for Xbox Live accounts and Xbox Game Pass.",
            "Safe to disable if you don't use Xbox Game Pass, Game Bar, or Xbox-linked games."),
        ("XblGameSave",       "Xbox Live Game Save",
            "Syncs game save files to Xbox Live cloud storage for compatible PC games.",
            "Safe to disable if you don't use Xbox Game Pass or Xbox cloud saves."),
        ("XboxNetApiSvc",     "Xbox Live Networking",
            "Provides Xbox Live multiplayer networking APIs to games that use Xbox services.",
            "Safe to disable if you don't play Xbox Game Pass or Xbox-integrated titles."),

        // ── Connectivity & location ───────────────────────────────────────────
        ("icssvc",            "Mobile Hotspot",
            "Enables the Windows Mobile Hotspot feature that shares your internet connection via Wi-Fi.",
            "Safe to disable if you never use your PC as a Wi-Fi hotspot."),
        ("lfsvc",             "Geolocation",
            "Provides your device's physical location to apps and websites that request it.",
            "Disable for better privacy. Location-based apps will stop working. Re-enable anytime in Settings."),
        ("PhoneSvc",          "Phone Service",
            "Manages the Phone Link app connection that mirrors your Android or iPhone on your PC.",
            "Safe to disable if you don't use the Phone Link / Your Phone app."),

        // ── Hardware ──────────────────────────────────────────────────────────
        ("bthserv",           "Bluetooth Support",
            "Core Windows Bluetooth stack. Required for all Bluetooth devices — mice, keyboards, headphones, speakers.",
            "Only disable if you have no Bluetooth devices. This will break all Bluetooth connectivity."),
        ("Fax",               "Fax",
            "Enables sending and receiving of faxes through a connected fax modem.",
            "Safe to disable — virtually no modern PCs send faxes."),
        ("RetailDemo",        "Retail Demo",
            "Runs the Windows retail store demo experience that loops marketing content on store display PCs.",
            "Always safe to disable. Should never be running on a personal computer."),

        // ── Network / sharing ─────────────────────────────────────────────────
        ("ssdpsrv",           "SSDP Discovery",
            "Discovers UPnP devices on your local network such as smart TVs, network printers, and routers.",
            "Safe to disable if you don't use network-discoverable or smart home devices."),
        ("upnphost",          "UPnP Device Host",
            "Allows this PC to act as a UPnP device that other computers on the network can connect to.",
            "Safe to disable on most home PCs."),
        ("lmhosts",           "TCP/IP NetBIOS Helper",
            "Resolves NetBIOS computer names for legacy Windows network file sharing over older protocols.",
            "Safe to disable. Modern networks use DNS and don't need NetBIOS name resolution."),
        ("NcaSvc",            "Network Connectivity Assistant",
            "Provides network connectivity status for DirectAccess enterprise VPN connections.",
            "Safe to disable — only relevant in corporate environments using DirectAccess VPN."),

        // ── Background downloads ──────────────────────────────────────────────
        ("DoSvc",             "Delivery Optimization",
            "Downloads Windows updates using a P2P system — pulling from other PCs on your network and uploading your bandwidth to Microsoft's network.",
            "Safe to disable. Updates will still download normally, just from Microsoft's servers only."),
        ("BITS",              "Background Intelligent Transfer",
            "Queues and manages background file transfers for Windows Update and Microsoft apps.",
            "Set to Manual rather than Disabled. Windows Update relies on this — fully disabling it can break update downloads."),
        ("WMPNetworkSvc",     "Windows Media Player Network Sharing",
            "Shares your Windows Media Player music and video library with other devices on the local network via DLNA.",
            "Safe to disable if you don't stream media from this PC to other devices."),

        // ── Compatibility / maintenance ───────────────────────────────────────
        ("PcaSvc",            "Program Compatibility Assistant",
            "Monitors apps as they run and automatically applies compatibility fixes for programs that have known issues.",
            "Safe to disable on modern PCs running current software."),
        ("TrkWks",            "Distributed Link Tracking Client",
            "Maintains NTFS shortcuts and links when files are moved between NTFS volumes on the network.",
            "Safe to disable on personal PCs."),
        ("wbengine",          "Block Level Backup Engine",
            "Powers the Windows built-in backup and restore feature for creating system image backups.",
            "Disable only if you use third-party backup software. Disabling breaks Windows Backup."),

        // ── Smart cards ───────────────────────────────────────────────────────
        ("SCardSvr",          "Smart Card",
            "Enables access to smart card readers used for hardware-based authentication.",
            "Safe to disable if you have no smart card reader or don't use smart card login."),
        ("SCPolicySvc",       "Smart Card Removal Policy",
            "Automatically locks the PC screen when a smart card is removed from its reader.",
            "Safe to disable if you don't use smart card authentication."),

        // ── Voice / telephony ─────────────────────────────────────────────────
        ("TapiSrv",           "Telephony",
            "Provides legacy telephony APIs used by VoIP softphone applications and fax software.",
            "Safe to disable if you don't use VoIP apps or software fax programs."),

        // ── Mixed reality ─────────────────────────────────────────────────────
        ("spectrum",          "Windows Perception Service",
            "Provides spatial tracking and perception features for Windows Mixed Reality and HoloLens VR headsets.",
            "Safe to disable on any PC without a Windows Mixed Reality headset attached."),

        // ── Insider ───────────────────────────────────────────────────────────
        ("wisvc",             "Windows Insider Service",
            "Connects this PC to the Windows Insider Program to receive pre-release preview builds from Microsoft.",
            "Safe to disable if you're not enrolled in the Insider Program."),

        // ── Parental Controls ────────────────────────────────────────────────
        ("WpcMonSvc",         "Parental Controls",
            "Monitors and logs all web browsing, app usage, and screen time — even when no child account is configured.",
            "Safe to disable if you don't use Windows Family Safety / parental controls."),

        // ── Diagnostics / privacy expansion (added v1.7.76) ──────────────────
        // These send diagnostic / personal data to Microsoft or to other apps;
        // disabling them doesn't break Windows core or networking. We do NOT
        // touch the indexer (WSearch is intentionally left non-Recommended).
        ("DPS",               "Diagnostic Policy Service",
            "Drives Windows' built-in troubleshooters and bundles per-component diagnostic data uploaded with telemetry.",
            "Safe to disable. Troubleshooters still launch but skip the data-collection phase."),
        ("WdiServiceHost",    "Diagnostic Service Host",
            "Hosts user-mode diagnostic providers (network, power, performance) that report back to Microsoft.",
            "Safe to disable. Companion to DPS — neither is needed for a healthy PC."),
        ("MessagingService",  "Messaging Service",
            "Legacy SMS-style messaging interop, used to relay text messages from a phone to Windows.",
            "Safe to disable. Almost no consumer apps still use it; modern messaging uses the Phone Link app instead."),
        ("PimIndexMaintenanceSvc", "Contact Data",
            "Builds a searchable index of your Contacts, Calendar, and address-book data for the People app.",
            "Safe to disable if you don't use the People app. Outlook/Mail keep their own contact stores."),
        ("stisvc",            "Windows Image Acquisition (Scanners)",
            "Provides scanner support for Windows. Webcams use a different service (FrameServer) and are unaffected.",
            "Safe to disable if you don't have a flatbed/document scanner. Re-enable if a scanner stops working."),
    };

    // ── Telemetry service list (for master toggle) ──
    public static readonly string[] TelemetryServices =
    {
        "DiagTrack",
        "dmwappushservice",
    };

    // ── Service status enumeration ────────────────────────────────────────────

    // Cache: avoids re-querying all services on every 1-second refresh tick.
    private (DateTime Time, List<ServiceInfo> Data) _statusCache;
    private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>Forces the next GetServiceStatuses call to re-query all services.</summary>
    public void InvalidateCache() => _statusCache = default;

    public List<ServiceInfo> GetServiceStatuses(bool gamesInstalled = false)
    {
        // Return cached data if it is still fresh enough (avoids SCM round-trips on every tick)
        if (_statusCache.Data != null
            && (DateTime.UtcNow - _statusCache.Time) < StatusCacheTtl)
        {
            return _statusCache.Data;
        }

        var result = new List<ServiceInfo>();
        foreach (var (name, display, desc, tooltip) in OptimizableServices)
        {
            try
            {
                using var svc = new ServiceController(name);
                var startType = GetStartType(name);
                result.Add(new ServiceInfo
                {
                    ServiceName   = name,
                    DisplayName   = display,
                    Description   = desc,
                    Tooltip       = tooltip,
                    Status        = svc.Status.ToString(),
                    StartType     = startType,
                    IsOptimized   = svc.Status == ServiceControllerStatus.Stopped,
                    IsRecommended = ComputeRecommended(name, gamesInstalled)
                });
            }
            catch (Exception ex)
            {
                // Service not installed on this machine — expected for optional Windows features, not a warning
                if (!ex.Message.Contains("was not found"))
                    Log.Warn("ServiceControl", $"Could not query service '{name}': {ex.Message}");
                result.Add(new ServiceInfo
                {
                    ServiceName   = name,
                    DisplayName   = display,
                    Description   = desc,
                    Tooltip       = tooltip,
                    Status        = "Not Installed",
                    StartType     = "N/A",
                    IsOptimized   = false,
                    IsRecommended = false
                });
            }
        }

        // Cache for StatusCacheTtl to avoid hammering the SCM on every refresh tick
        _statusCache = (DateTime.UtcNow, result);
        return result;
    }

    private static bool ComputeRecommended(string serviceName, bool gamesInstalled)
    {
        // Xbox services: recommended only when no games installed
        bool isXboxService = serviceName is "XboxGipSvc" or "xbgm" or "XblAuthManager"
                                         or "XblGameSave" or "XboxNetApiSvc";
        if (isXboxService)
            return !gamesInstalled;

        return !_noRecommendedTag.Contains(serviceName);
    }

    private static string GetStartType(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            int startType = (int)(key?.GetValue("Start") ?? 2);
            return startType switch
            {
                0 => "Boot",
                1 => "System",
                2 => "Auto",
                3 => "Manual",
                4 => "Disabled",
                _ => "Unknown"
            };
        }
        catch (Exception ex) { Log.Warn("ServiceControl", $"GetStartType({serviceName}) failed: {ex.Message}"); return "Unknown"; }
    }

    // ── Service state changes ─────────────────────────────────────────────────

    /// <summary>
    /// Polls a service until it reaches the target status or the timeout expires.
    /// Avoids WaitForStatus() which uses kernel waits that can exhaust stack space
    /// on threadpool threads (small default stack).
    /// </summary>
    private static void PollForStatus(
        ServiceController svc, ServiceControllerStatus target, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            svc.Refresh();
            if (svc.Status == target) return;
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// Changes a service's Start value via <c>sc.exe config</c>. Used as a fallback
    /// when direct registry writes fail with "Requested registry access is not allowed"
    /// — some service keys (TrkWks, DPS, WdiServiceHost, etc.) are owned by
    /// TrustedInstaller, and even an admin token can't write them directly.
    /// The Service Control Manager handles the elevation internally, so going through
    /// sc.exe succeeds where registry access fails.
    /// </summary>
    /// <param name="serviceName">Internal service name (e.g. "DPS").</param>
    /// <param name="startMode">"disabled" | "demand" (Manual) | "auto".</param>
    /// <returns>True on success (sc.exe exit 0), false otherwise.</returns>
    private static bool SetStartTypeViaSc(string serviceName, string startMode)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "sc.exe",
                Arguments              = $"config \"{serviceName}\" start= {startMode}",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            if (proc.ExitCode != 0)
            {
                Log.Warn("ServiceControl",
                    $"sc.exe config {serviceName} start={startMode} exited {proc.ExitCode}: {proc.StandardError.ReadToEnd().Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("ServiceControl", $"sc.exe config {serviceName} failed: {ex.Message}");
            return false;
        }
    }

    public async Task<TweakResult> DisableServiceAsync(string serviceName)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var svc = new ServiceController(serviceName);
                if (svc.Status == ServiceControllerStatus.Running)
                {
                    svc.Stop();
                    PollForStatus(svc, ServiceControllerStatus.Stopped, timeoutSeconds: 10);
                }
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}", true);
                if (key == null)
                    return TweakResult.Fail($"Cannot open registry key for {serviceName} — access denied or service not found.");
                key.SetValue("Start", 4, RegistryValueKind.DWord);
                InvalidateCache();
                Log.Info("ServiceControl", $"Service disabled: {serviceName}");
                Log.LogChange("Service Disabled", serviceName);
                return TweakResult.Ok($"{serviceName} disabled.");
            }
            catch (Exception ex) { return TweakResult.FromException(ex); }
        });
    }

    public async Task<TweakResult> SetManualAsync(string serviceName)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var svc = new ServiceController(serviceName);
                if (svc.Status == ServiceControllerStatus.Running)
                {
                    svc.Stop();
                    PollForStatus(svc, ServiceControllerStatus.Stopped, timeoutSeconds: 10);
                }
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}", true);
                if (key == null)
                    return TweakResult.Fail($"Cannot open registry key for {serviceName} — access denied or service not found.");
                key.SetValue("Start", 3, RegistryValueKind.DWord);
                InvalidateCache();
                Log.LogChange("Service Set Manual", serviceName);
                return TweakResult.Ok($"{serviceName} set to Manual.");
            }
            catch (Exception ex) { return TweakResult.FromException(ex); }
        });
    }

    public async Task<TweakResult> EnableServiceAsync(string serviceName)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}", true);
                if (key == null)
                    return TweakResult.Fail($"Cannot open registry key for {serviceName} — access denied or service not found.");
                key.SetValue("Start", 2, RegistryValueKind.DWord);
                InvalidateCache();
                Log.Info("ServiceControl", $"Service re-enabled: {serviceName}");
                Log.LogChange("Service Re-enabled", serviceName);
                return TweakResult.Ok($"{serviceName} re-enabled.");
            }
            catch (Exception ex) { return TweakResult.FromException(ex); }
        });
    }

    // ── Telemetry master toggle ───────────────────────────────────────────────

    /// <summary>
    /// Disables all DiagTrack / dmwappushservice / WAP-related telemetry services and tasks.
    /// </summary>
    public Task<TweakResult> DisableAllTelemetryServicesAsync()
    {
        // Uses a large-stack thread: DisableTelemetryTasks() spawns schtasks.exe via Process.Start(),
        // which can trigger AV/EDR hooks that overflow a 1 MB threadpool stack.
        return RunOnLargeStackAsync<TweakResult>(() =>
        {
            var failed = new List<string>();
            foreach (var svcName in TelemetryServices)
            {
                try
                {
                    using var svc = new ServiceController(svcName);
                    try
                    {
                        if (svc.Status == ServiceControllerStatus.Running)
                        {
                            svc.Stop();
                            PollForStatus(svc, ServiceControllerStatus.Stopped, timeoutSeconds: 8);
                        }
                    }
                    catch (Exception ex) { Log.Warn("ServiceControl", $"Could not stop telemetry service '{svcName}': {ex.Message}"); }

                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{svcName}", true);
                    if (key != null)
                        key.SetValue("Start", 4, RegistryValueKind.DWord);
                    else
                    {
                        Log.Warn("ServiceControl", $"Cannot open registry key for telemetry service '{svcName}' — Start value not written");
                        failed.Add(svcName);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("ServiceControl", $"Failed to disable telemetry service '{svcName}'", ex);
                    failed.Add(svcName);
                }
            }

            // Also disable scheduled telemetry tasks
            DisableTelemetryTasks();

            return failed.Count == 0
                ? TweakResult.Ok("All telemetry services disabled.")
                : TweakResult.Ok($"Telemetry mostly disabled. Some services not found: {string.Join(", ", failed)}");
        });
    }

    /// <summary>
    /// Combined privacy cleanup: disables ALL telemetry services + tasks AND every
    /// service that ComputeRecommended() flags as safe-to-disable for the current PC
    /// (so e.g. Xbox services are skipped on machines with games installed).
    ///
    /// One-shot action: does not persist any "always enforce" flag — that
    /// behaviour is owned by Auto-Pilot Mode (DashboardViewModel). Called from
    /// both the merged ServicesView "Disable Privacy &amp; Background Services"
    /// button and from Auto-Pilot's RunAutoPilotAsync flow.
    /// </summary>
    /// <param name="gamesInstalled">If true, Xbox/Game services are kept enabled.</param>
    /// <returns>Summary of what was disabled, suitable for the status bar.</returns>
    public async Task<TweakResult> DisablePrivacyAndRecommendedAsync(bool gamesInstalled)
    {
        // 1. Telemetry services + scheduled tasks
        var telemetryResult = await DisableAllTelemetryServicesAsync();

        // 2. Every "Recommended" optional service that isn't already disabled.
        //    BITS is special — it's intentionally NOT marked Recommended (Windows
        //    Update needs it), so the loop below already skips it.
        int disabled = 0, alreadyOff = 0, failed = 0;
        var skipped = new List<string>();

        await Task.Run(() =>
        {
            foreach (var (name, _, _, _) in OptimizableServices)
            {
                if (!ComputeRecommended(name, gamesInstalled)) continue;

                // Skip if already disabled
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{name}");
                    if (key != null && (int?)key.GetValue("Start") == 4)
                    {
                        alreadyOff++;
                        continue;
                    }
                }
                catch
                {
                    skipped.Add(name);
                    continue;
                }

                try
                {
                    using var svc = new ServiceController(name);
                    if (svc.Status == ServiceControllerStatus.Running)
                    {
                        try { svc.Stop(); PollForStatus(svc, ServiceControllerStatus.Stopped, 5); }
                        catch (Exception ex) { Log.Warn("ServiceControl", $"Stop({name}) failed: {ex.Message}"); }
                    }

                    // Try direct registry write first (fastest path), then fall back to
                    // sc.exe for TrustedInstaller-protected service keys (DPS, TrkWks,
                    // WdiServiceHost, etc.) where registry writes return "access denied"
                    // even with admin token — the SCM handles those internally.
                    bool ok = false;
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(
                            $@"SYSTEM\CurrentControlSet\Services\{name}", writable: true);
                        if (key != null)
                        {
                            key.SetValue("Start", 4, RegistryValueKind.DWord);
                            ok = true;
                        }
                    }
                    catch (UnauthorizedAccessException) { /* fall through to sc.exe */ }
                    catch (System.Security.SecurityException) { /* fall through to sc.exe */ }

                    if (!ok)
                        ok = SetStartTypeViaSc(name, "disabled");

                    if (ok) disabled++; else failed++;
                }
                catch (InvalidOperationException)
                {
                    // Service not installed on this PC — silent skip.
                    skipped.Add(name);
                }
                catch (Exception ex)
                {
                    Log.Warn("ServiceControl", $"DisablePrivacy: {name} failed: {ex.Message}");
                    // Last-resort: try sc.exe even when something else threw.
                    if (SetStartTypeViaSc(name, "disabled")) disabled++; else failed++;
                }
            }
        });

        InvalidateCache();

        var msg = $"{telemetryResult.Message} Background services: {disabled} disabled" +
                  (alreadyOff > 0 ? $", {alreadyOff} already off" : string.Empty) +
                  (failed > 0    ? $", {failed} failed"           : string.Empty) + ".";
        return TweakResult.Ok(msg);
    }

    /// <summary>
    /// Checks whether every "Recommended" service for this PC is currently disabled.
    /// Used by the Dashboard checklist to show the merged Privacy &amp; Background
    /// Services item as Done / Pending. Counts already-not-installed services as
    /// disabled (nothing to do) so a cloud / Pro / N edition PC doesn't get stuck
    /// in Pending forever.
    /// </summary>
    public bool AreAllRecommendedDisabled(bool gamesInstalled)
    {
        foreach (var (name, _, _, _) in OptimizableServices)
        {
            if (!ComputeRecommended(name, gamesInstalled)) continue;

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{name}");
                if (key == null) continue; // service not installed — treat as done
                int start = (int?)key.GetValue("Start") ?? 2;
                if (start != 4) return false;
            }
            catch (Exception ex)
            {
                Log.Warn("ServiceControl", $"AreAllRecommendedDisabled({name}): {ex.Message}");
                // Conservative: treat as not-disabled so user knows to re-run the cleanup
                return false;
            }
        }
        return true;
    }

    public async Task<TweakResult> RestoreTelemetryServicesAsync()
    {
        return await Task.Run(() =>
        {
            foreach (var svcName in TelemetryServices)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{svcName}", true);
                    key?.SetValue("Start", 2, RegistryValueKind.DWord);
                }
                catch (Exception ex) { Log.Warn("ServiceControl", $"Could not restore telemetry service '{svcName}': {ex.Message}"); }
            }
            return TweakResult.Ok("Telemetry services restored.");
        });
    }

    /// <summary>
    /// Reverses <see cref="DisablePrivacyAndRecommendedAsync"/>. Restores telemetry
    /// services to Auto (Windows default) and sets every previously-disabled
    /// Recommended service back to Manual start (3) — services start on demand
    /// without auto-loading at boot, so memory impact is minimal but the user
    /// gets full functionality back.
    ///
    /// Called when the user flips the Privacy &amp; Background Services toggle OFF.
    /// </summary>
    public async Task<TweakResult> RestorePrivacyAndRecommendedAsync(bool gamesInstalled)
    {
        // 1. Telemetry first
        var telemetryResult = await RestoreTelemetryServicesAsync();

        // 2. Every Recommended service that's currently disabled → Manual.
        int restored = 0, skipped = 0;

        await Task.Run(() =>
        {
            foreach (var (name, _, _, _) in OptimizableServices)
            {
                if (!ComputeRecommended(name, gamesInstalled)) continue;

                // Read current state first (read-only access, never throws on locked keys).
                int currentStart;
                try
                {
                    using var readKey = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{name}");
                    if (readKey == null) { skipped++; continue; }
                    currentStart = (int?)readKey.GetValue("Start") ?? 3;
                }
                catch { skipped++; continue; }

                if (currentStart != 4) { skipped++; continue; } // already not disabled

                // Try direct registry write first, fall back to sc.exe for
                // TrustedInstaller-protected keys (same pattern as the disable path).
                bool ok = false;
                try
                {
                    using var writeKey = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{name}", writable: true);
                    if (writeKey != null)
                    {
                        writeKey.SetValue("Start", 3, RegistryValueKind.DWord); // Manual
                        ok = true;
                    }
                }
                catch (UnauthorizedAccessException)   { /* fall through to sc.exe */ }
                catch (System.Security.SecurityException) { /* fall through to sc.exe */ }

                if (!ok) ok = SetStartTypeViaSc(name, "demand"); // "demand" == Manual in sc.exe
                if (ok) restored++; else skipped++;
            }
        });

        InvalidateCache();

        return TweakResult.Ok(
            $"{telemetryResult.Message} Background services: {restored} restored to Manual" +
            (skipped > 0 ? $", {skipped} left unchanged" : string.Empty) + ".");
    }

    private static void DisableTelemetryTasks()
    {
        string[] tasks =
        {
            @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
            @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
            @"\Microsoft\Windows\Autochk\Proxy",
            @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
            @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
            @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
        };

        foreach (var task in tasks)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName  = "schtasks.exe",
                    Arguments = $"/Change /TN \"{task}\" /Disable",
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null)
                {
                    Log.Warn("ServiceControl", $"schtasks.exe failed to start for task: {task}");
                    continue;
                }
                bool exited = proc.WaitForExit(5000);
                if (!exited)
                    Log.Warn("ServiceControl", $"schtasks.exe timed out disabling task: {task}");
                else if (proc.ExitCode != 0)
                    Log.Warn("ServiceControl", $"schtasks.exe exited {proc.ExitCode} disabling task: {task}");
            }
            catch (Exception ex) { Log.Warn("ServiceControl", $"Failed to disable telemetry task '{task}': {ex.Message}"); }
        }
    }

    /// <summary>Check whether all known telemetry services are currently disabled.</summary>
    public bool AreTelemetryServicesDisabled()
    {
        int disabledCount = 0;
        foreach (var svcName in TelemetryServices)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{svcName}");
                if ((int)(key?.GetValue("Start") ?? 2) == 4)
                    disabledCount++;
            }
            catch (Exception ex) { Log.Warn("ServiceControl", $"AreTelemetryServicesDisabled check failed for '{svcName}': {ex.Message}"); }
        }
        return disabledCount >= TelemetryServices.Length;
    }
}
