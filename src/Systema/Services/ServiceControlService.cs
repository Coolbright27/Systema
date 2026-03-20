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
    };

    // ── Telemetry service list (for master toggle) ──
    public static readonly string[] TelemetryServices =
    {
        "DiagTrack",
        "dmwappushservice",
    };

    // ── Service status enumeration ────────────────────────────────────────────

    public List<ServiceInfo> GetServiceStatuses(bool gamesInstalled = false)
    {
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
        catch { return "Unknown"; }
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
                    catch { /* service may not be stoppable */ }

                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{svcName}", true);
                    key?.SetValue("Start", 4, RegistryValueKind.DWord);
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
                catch { /* ignore */ }
            }
            return TweakResult.Ok("Telemetry services restored.");
        });
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
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(5000);
            }
            catch { /* skip */ }
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
            catch { }
        }
        return disabledCount >= TelemetryServices.Length;
    }
}
