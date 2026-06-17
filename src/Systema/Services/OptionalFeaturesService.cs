// ════════════════════════════════════════════════════════════════════════════
// OptionalFeaturesService.cs  ·  Enumerate and toggle Windows optional features via DISM
// ════════════════════════════════════════════════════════════════════════════
//
// Shells out to dism.exe to list all optional Windows features and their
// enabled/disabled state. Provides Enable and Disable operations by invoking
// DISM with the appropriate /Enable-Feature or /Disable-Feature flags.
//
// RELATED FILES
//   Models/OptionalFeatureInfo.cs  — feature row data shape (Name, State)
//   ServicesViewModel.cs           — optional features list on the Services tab
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using Systema.Core;
using Systema.Models;

namespace Systema.Services;

public class OptionalFeaturesService
{
    private static readonly LoggerService _log = LoggerService.Instance;

    // Features to hide from the list (not useful to toggle)
    private static readonly HashSet<string> HiddenFeatures = new(StringComparer.OrdinalIgnoreCase)
    {
        "Internet-Explorer-Optional-amd64"
    };

    // Features flagged as unsafe/obsolete — surfaces a "REMOVE RECOMMENDED" badge in the UI
    private static readonly HashSet<string> RecommendedToRemove = new(StringComparer.OrdinalIgnoreCase)
    {
        "SMB1Protocol",
        "SMB1Protocol-Server",
        "SMB1Protocol-Client",
    };

    /// <summary>Human-readable descriptions for known Windows optional features.</summary>
    private static readonly Dictionary<string, string> FeatureDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Virtualization ────────────────────────────────────────────────────
        ["Microsoft-Hyper-V"]                          = "Microsoft's built-in hypervisor for running virtual machines. Required for WSL 2, Docker Desktop, and Android Subsystem. Disable only if you use another hypervisor like VMware.",
        ["Microsoft-Hyper-V-All"]                      = "Full Hyper-V stack including management tools and the hypervisor platform.",
        ["Microsoft-Hyper-V-Management-Clients"]       = "Hyper-V Manager and the GUI tools for creating and controlling virtual machines.",
        ["Microsoft-Hyper-V-Management-PowerShell"]    = "PowerShell cmdlets for managing Hyper-V virtual machines from the command line.",
        ["Microsoft-Hyper-V-Hypervisor"]               = "Core Hyper-V hypervisor component. Required for WSL 2, Windows Sandbox, and Docker Desktop.",
        ["Microsoft-Hyper-V-Services"]                 = "Hyper-V background services for VM management and guest communication.",
        ["Microsoft-Hyper-V-Tools-All"]                = "Hyper-V management GUI tools including Hyper-V Manager.",
        ["HypervisorPlatform"]                         = "Low-level hypervisor APIs used by VirtualBox, VMware Workstation, and Android Emulator alongside Hyper-V.",
        ["VirtualMachinePlatform"]                     = "Required for WSL 2 and the Windows Subsystem for Android. Provides the VM host platform layer.",

        // ── Windows Subsystem for Linux ───────────────────────────────────────
        ["Microsoft-Windows-Subsystem-Linux"]          = "Windows Subsystem for Linux (WSL) — runs a real Linux kernel and distros like Ubuntu directly on Windows. Remove if you don't use Linux on this PC.",

        // ── Windows Sandbox ───────────────────────────────────────────────────
        ["Containers-DisposableClientVM"]              = "Windows Sandbox — a lightweight isolated VM for safely running untrusted apps. Each session starts fresh with no traces left behind. Requires Hyper-V.",

        // ── Remote desktop ────────────────────────────────────────────────────
        ["Microsoft-RemoteDesktopConnection"]          = "Remote Desktop Connection client for connecting to other PCs and servers remotely.",

        // ── Legacy protocols ──────────────────────────────────────────────────
        ["TelnetClient"]                               = "Command-line Telnet client for connecting to legacy servers. Insecure (transmits data in plain text) — use SSH instead.",
        ["TFTP"]                                       = "Trivial File Transfer Protocol client for transferring files to network devices. Used mainly in enterprise/networking contexts.",
        ["SimpleTCP"]                                  = "Legacy TCP/IP services including Echo, Daytime, and Character Generator. Not needed on modern PCs.",

        // ── Legacy Windows features ───────────────────────────────────────────
        ["MicrosoftWindowsPowerShellV2Root"]           = "PowerShell 2.0 engine. Required only by very old scripts or tools that can't run on PowerShell 5+. Safe to remove on modern setups.",
        ["MicrosoftWindowsPowerShellV2"]               = "PowerShell 2.0 core components. Only needed for legacy automation scripts that can't be updated.",
        ["WorkFolders-Client"]                         = "Work Folders client for syncing files with a corporate Work Folders server. Not needed on home PCs.",
        ["Printing-Foundation-Features"]               = "Core printing subsystem components. Remove only if you have no printers and never print.",
        ["Printing-PrintToPDFServices-Features"]       = "The built-in 'Microsoft Print to PDF' printer. Remove if you don't need to save documents as PDFs.",
        ["Printing-XPSServices-Features"]              = "XPS Document Writer printer driver for the legacy XML Paper Specification format. Safe to remove on most PCs.",
        ["FaxServicesClientPackage"]                   = "Windows Fax and Scan feature for sending and receiving faxes. Safe to remove if you don't have a fax modem.",
        ["MediaPlayback"]                              = "Windows Media Player and related media playback components. Safe to remove if you use a third-party media player exclusively.",
        ["WindowsMediaPlayer"]                         = "Classic Windows Media Player app. Safe to remove — use VLC or another modern player instead.",

        // ── .NET ──────────────────────────────────────────────────────────────
        ["NetFx3"]                                     = ".NET Framework 3.5 (includes .NET 2.0 and 3.0). Required by older apps and some games. Remove only if you're certain no installed software needs it.",
        ["NetFx4-AdvSrvs"]                             = ".NET Framework 4 Advanced Services including WCF and HTTP activation. Required by some enterprise server apps.",

        // ── Internet Information Services ─────────────────────────────────────
        ["IIS-WebServerRole"]                          = "Internet Information Services (IIS) web server. Lets this PC host websites and web apps locally. Remove unless you're a web developer.",
        ["IIS-WebServer"]                              = "IIS core web server components.",

        // ── DirectPlay ────────────────────────────────────────────────────────
        ["DirectPlay"]                                 = "Legacy DirectPlay networking API used by very old games (pre-2000s). Only needed if you play classic LAN games that require it.",

        // ── Containers ────────────────────────────────────────────────────────
        ["Containers"]                                 = "Windows Containers support for running Docker Windows containers. Remove if you don't use Docker with Windows containers.",

        // ── Data Center Bridging ──────────────────────────────────────────────
        ["DataCenterBridging"]                         = "Data Center Bridging (DCB) network quality-of-service feature for enterprise data center networking. Not needed on home PCs.",

        // ── SNMP ──────────────────────────────────────────────────────────────
        ["SNMP"]                                       = "Simple Network Management Protocol for monitoring network devices. Used in enterprise environments. Not needed on home PCs.",

        // ── SMB / file sharing ────────────────────────────────────────────────
        ["SMB1Protocol"]                               = "SMB 1.0 — an old and insecure file sharing protocol with known critical vulnerabilities (used by WannaCry ransomware). Remove this.",
        ["SMB1Protocol-Server"]                        = "SMB 1.0 server — allows other devices to connect to this PC using the insecure legacy SMB 1.0 protocol. Remove this.",
        ["SMB1Protocol-Client"]                        = "SMB 1.0 client — allows this PC to connect to old file servers using the insecure SMB 1.0 protocol. Remove this.",

        // ── Remote Differential Compression ──────────────────────────────────
        ["MSRDC-Infrastructure"]                       = "Remote Differential Compression API for efficient data sync over networks. Used by some sync and backup tools.",

        // ── Windows Search ────────────────────────────────────────────────────
        ["SearchEngine-Client-Package"]                = "Windows Search indexer and search UI. Disable to stop background file indexing and free up disk I/O.",

        // ── Tablet / touch ────────────────────────────────────────────────────
        ["TabletPCOpt-Embedded-BMP"]                   = "Tablet PC optional components including handwriting recognition and math input. Safe to remove on non-touchscreen devices.",

        // ── Games ─────────────────────────────────────────────────────────────
        ["Games"]                                      = "Classic Windows Games package (Solitaire, Minesweeper, etc.) included in older Windows versions.",

        // ── .NET Framework 4.x advanced (WCF) ─────────────────────────────────
        ["NetFx4Extended-ASPNET45"]                    = "ASP.NET 4.x web components for the .NET Framework. Only needed if you host .NET web apps locally.",
        ["WCF-Services45"]                             = "Windows Communication Foundation — the .NET system for networked services. Required by some desktop business apps.",
        ["WCF-HTTP-Activation45"]                      = "Lets WCF apps start automatically over HTTP. Needed by some enterprise/server software.",
        ["WCF-TCP-Activation45"]                       = "Lets WCF apps start automatically over TCP. Needed by some enterprise/server software.",
        ["WCF-Pipe-Activation45"]                      = "Lets WCF apps start automatically over named pipes (local app-to-app messaging).",
        ["WCF-MSMQ-Activation45"]                      = "Lets WCF apps start automatically via Microsoft Message Queue. Enterprise messaging only.",
        ["WCF-TCP-PortSharing45"]                      = "Allows several WCF services to share one TCP port. Used by some server apps.",
        ["WCF-HTTP-Activation"]                        = "Classic HTTP activation for WCF services. Legacy server feature.",
        ["WCF-NonHTTP-Activation"]                     = "Classic non-HTTP activation for WCF services. Legacy server feature.",

        // ── Windows Process Activation Service ────────────────────────────────
        ["WAS-WindowsActivationService"]               = "Hosts and starts web/WCF apps without a full web server. Used by IIS and some business apps.",
        ["WAS-ProcessModel"]                           = "Process model for the Windows Activation Service. Part of the IIS/WAS hosting stack.",
        ["WAS-NetFxEnvironment"]                       = "The .NET environment for the Windows Activation Service. Part of the IIS/WAS hosting stack.",
        ["WAS-ConfigurationAPI"]                       = "Configuration API for the Windows Activation Service. Part of the IIS/WAS hosting stack.",

        // ── Internet Information Services (web server) ────────────────────────
        ["IIS-CommonHttpFeatures"]                     = "Core web-server features (static files, default pages, errors). Part of IIS — web developers only.",
        ["IIS-HttpErrors"]                             = "Custom error pages for the IIS web server.",
        ["IIS-HttpRedirect"]                           = "URL redirection for the IIS web server.",
        ["IIS-ApplicationDevelopment"]                 = "Web-app development components for IIS (ASP.NET, CGI, etc.). Web developers only.",
        ["IIS-Security"]                               = "Security and sign-in components for the IIS web server.",
        ["IIS-RequestFiltering"]                       = "Filters and blocks unwanted web requests in IIS.",
        ["IIS-HealthAndDiagnostics"]                   = "Logging and monitoring components for the IIS web server.",
        ["IIS-HttpLogging"]                            = "Records web-request logs for the IIS server.",
        ["IIS-Performance"]                            = "Compression and caching to speed up the IIS web server.",
        ["IIS-WebServerManagementTools"]               = "Console and tools for configuring IIS.",
        ["IIS-ManagementConsole"]                      = "The IIS Manager app for configuring the web server.",
        ["IIS-ManagementScriptingTools"]               = "Command-line and script management for IIS.",
        ["IIS-ManagementService"]                      = "Remote management of IIS from another PC.",
        ["IIS-StaticContent"]                          = "Serves static files (HTML, images, CSS) from the IIS web server.",
        ["IIS-DefaultDocument"]                        = "Serves a default page (like index.html) for IIS websites.",
        ["IIS-DirectoryBrowsing"]                      = "Lets IIS list a folder's contents when there's no default page.",
        ["IIS-ASPNET45"]                               = "ASP.NET 4.x support for hosting .NET web apps in IIS.",
        ["IIS-ASPNET"]                                 = "ASP.NET support for hosting .NET web apps in IIS.",
        ["IIS-ASP"]                                    = "Classic ASP support in IIS (older web tech).",
        ["IIS-NetFxExtensibility45"]                   = "Lets .NET modules extend the IIS web server (v4.5).",
        ["IIS-NetFxExtensibility"]                     = "Lets .NET modules extend the IIS web server.",
        ["IIS-ISAPIExtensions"]                        = "Runs ISAPI web extensions in IIS (older web tech).",
        ["IIS-ISAPIFilter"]                            = "Runs ISAPI filters in IIS (older web tech).",
        ["IIS-CGI"]                                    = "Runs CGI programs (e.g. PHP) in the IIS web server.",
        ["IIS-WebSockets"]                             = "WebSocket support for real-time web apps in IIS.",
        ["IIS-ApplicationInit"]                        = "Pre-loads IIS web apps so the first visit is faster.",
        ["IIS-WebDAV"]                                 = "WebDAV publishing — edit files on the IIS server over HTTP.",
        ["IIS-BasicAuthentication"]                    = "Username/password sign-in for IIS sites (plain text — use with HTTPS).",
        ["IIS-WindowsAuthentication"]                  = "Windows-account sign-in for IIS sites (intranet use).",
        ["IIS-DigestAuthentication"]                   = "Digest sign-in for IIS websites.",
        ["IIS-HttpCompressionStatic"]                  = "Compresses static files to speed up IIS websites.",
        ["IIS-HttpCompressionDynamic"]                 = "Compresses dynamic responses to speed up IIS websites.",
        ["IIS-IIS6ManagementCompatibility"]            = "Lets old IIS 6 tools and scripts manage modern IIS.",
        ["IIS-Metabase"]                               = "Legacy IIS 6 configuration store, kept for backward compatibility.",
        ["IIS-FTPServer"]                              = "FTP file-transfer server hosted by IIS.",
        ["IIS-FTPSvc"]                                 = "The FTP service for the IIS web server.",
        ["IIS-FTPExtensibility"]                       = "Extensibility components for the IIS FTP server.",

        // ── Printing (extra) ──────────────────────────────────────────────────
        ["Printing-Foundation-InternetPrinting-Client"] = "Lets this PC print to printers shared over the internet (IPP). Safe to remove if you only use local/network printers.",
        ["Printing-Foundation-LPDPrintService"]        = "LPD print server — lets Unix/Linux devices print to printers shared by this PC. Enterprise/legacy only.",
        ["Printing-Foundation-LPRPortMonitor"]         = "LPR printing — lets this PC print to Unix/Linux print servers. Enterprise/legacy only.",

        // ── Microsoft Message Queuing ─────────────────────────────────────────
        ["MSMQ-Container"]                             = "Microsoft Message Queuing — store-and-forward messaging used by some business apps. Remove if nothing needs it.",
        ["MSMQ-Server"]                                = "Core Microsoft Message Queuing service. Enterprise messaging only.",
        ["MSMQ-Triggers"]                              = "Runs actions automatically when MSMQ messages arrive. Enterprise messaging only.",
        ["MSMQ-HTTP"]                                  = "Sends Microsoft Message Queue messages over HTTP. Enterprise only.",
        ["MSMQ-Multicast"]                             = "Multicast support for MSMQ messaging. Enterprise only.",
        ["MSMQ-DCOMProxy"]                             = "DCOM proxy for MSMQ messaging. Enterprise only.",
        ["MSMQ-ADIntegration"]                         = "Active Directory integration for MSMQ. Enterprise only.",

        // ── Network File System (Unix/Linux shares) ──────────────────────────
        ["ServicesForNFS-ClientOnly"]                  = "Network File System (NFS) client — connect to Unix/Linux file shares. Remove if you don't use NFS.",
        ["ClientForNFS-Infrastructure"]                = "Core components for connecting to NFS (Unix/Linux) file shares.",
        ["NFS-Administration"]                         = "Tools for managing NFS file-share connections.",

        // ── Containers (extra) ────────────────────────────────────────────────
        ["Containers-HNS"]                             = "Host Network Service for Windows/Docker containers. Part of the container stack.",
        ["Containers-SDN"]                             = "Software-defined networking for Windows containers. Container/enterprise use.",

        // ── Virtual file systems / directory ──────────────────────────────────
        ["Client-ProjFS"]                              = "Windows Projected File System — used by Git VFS and similar virtual-folder tools. Safe to remove if you don't use them.",
        ["DirectoryServices-ADAM-Client"]              = "Active Directory Lightweight Directory Services (AD LDS) client. Enterprise directory tooling only.",

        // ── Legacy / niche ────────────────────────────────────────────────────
        ["LegacyComponents"]                           = "Holder for very old Windows components like DirectPlay. Only needed for some classic games and apps.",
        ["TIFFIFilter"]                                = "Lets Windows Search read text inside scanned TIFF images (OCR). Safe to remove if you don't search scanned documents.",
        ["WMI-SNMP-Provider"]                          = "Lets management tools read SNMP data through Windows WMI. Enterprise monitoring only.",
        ["RasCMAK"]                                    = "Connection Manager Administration Kit — builds custom VPN/dial-up profiles. Admin/enterprise only.",
        ["RasRip"]                                     = "RIP listener for receiving network routes over dial-up/VPN. Legacy networking only.",
        ["SmbDirect"]                                  = "SMB Direct (RDMA) — high-speed file sharing on enterprise networks. Harmless on home PCs.",
        ["SMB1Protocol-Deprecation"]                   = "Automatically removes the insecure SMB 1.0 protocol once it goes unused. Leave this on.",
        ["MultiPointConnector"]                        = "MultiPoint Services connector for shared classroom/lab PCs. Remove on a personal PC.",
        ["MultiPointConnector-Services"]               = "Background services for MultiPoint shared-PC management. Remove on a personal PC.",
        ["MultiPointConnector-Tools"]                  = "Management tools for MultiPoint shared PCs. Remove on a personal PC.",
        ["HostGuardian"]                               = "Host Guardian support for running shielded VMs in enterprise data centers. Not needed on home PCs.",
        ["Windows-Defender-ApplicationGuard"]          = "Microsoft Defender Application Guard — opens risky sites/files in an isolated container. Safe to remove if you don't use it.",
        ["Windows-Identity-Foundation"]                = "Windows Identity Foundation — an older .NET single-sign-on framework. Needed only by some legacy business apps.",
    };

    public async Task<List<OptionalFeatureInfo>> GetAllFeaturesAsync()
    {
        return await Task.Run(() =>
        {
            var features = new List<OptionalFeatureInfo>();
            try
            {
                var psi = new ProcessStartInfo("dism.exe", "/Online /Get-Features /Format:List")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow         = true
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    _log.Error("OptionalFeaturesService", "dism.exe failed to start");
                    return features;
                }
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                var lines = output.Split('\n');
                string? currentFeature = null;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    // Parse "Feature Name : xyz" or localized equivalents.
                    // DISM always uses " : " as the key-value separator, so we
                    // split on the first " : " and match the key by checking
                    // known English AND checking whether this is the first or
                    // second field in each feature block.
                    int sepIdx = trimmed.IndexOf(" : ", StringComparison.Ordinal);
                    if (sepIdx < 0) continue;

                    string key   = trimmed[..sepIdx].Trim();
                    string value = trimmed[(sepIdx + 3)..].Trim();

                    // DISM outputs two fields per feature: name first, state second.
                    // On non-English Windows the key text differs, but the order is
                    // always name then state. We detect "Feature Name" by checking
                    // the English string OR by accepting any key when we don't have
                    // a current feature yet (first field of a block).
                    bool isNameField  = key.Equals("Feature Name", StringComparison.OrdinalIgnoreCase)
                                     || key.Contains("Name", StringComparison.OrdinalIgnoreCase);
                    bool isStateField = key.Equals("State", StringComparison.OrdinalIgnoreCase)
                                     || key.Contains("State", StringComparison.OrdinalIgnoreCase)
                                     || key.Contains("Staat", StringComparison.OrdinalIgnoreCase)   // German
                                     || key.Contains("Estado", StringComparison.OrdinalIgnoreCase)  // Spanish/Portuguese
                                     || key.Contains("État", StringComparison.OrdinalIgnoreCase)    // French
                                     || key.Contains("Stato", StringComparison.OrdinalIgnoreCase)   // Italian
                                     || key.Contains("状態", StringComparison.Ordinal)               // Japanese
                                     || key.Contains("状态", StringComparison.Ordinal)               // Chinese
                                     || key.Contains("상태", StringComparison.Ordinal);              // Korean

                    if (isNameField && currentFeature == null)
                    {
                        currentFeature = value;
                    }
                    else if ((isStateField || currentFeature != null) && currentFeature != null)
                    {
                        string state = value;

                        if (!HiddenFeatures.Contains(currentFeature))
                        {
                            FeatureDescriptions.TryGetValue(currentFeature, out string? desc);
                            features.Add(new OptionalFeatureInfo
                            {
                                Name                  = currentFeature,
                                State                 = state,
                                Description           = desc ?? string.Empty,
                                IsRecommendedToRemove = RecommendedToRemove.Contains(currentFeature),
                            });
                        }
                        currentFeature = null;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("OptionalFeaturesService", "GetAllFeaturesAsync failed", ex);
            }

            // Sort: enabled first, then alphabetical
            features.Sort((a, b) =>
            {
                int cmp = b.IsEnabled.CompareTo(a.IsEnabled);
                return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return features;
        });
    }

    /// <summary>
    /// Fast (no DISM) check for whether the SMBv1 feature is still installed.
    /// Uses mrxsmb10.sys presence as the indicator — DISM removes this driver file
    /// when the SMB1Protocol feature is uninstalled.
    /// </summary>
    public bool IsSMBv1Present()
    {
        string driversDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers");
        return File.Exists(Path.Combine(driversDir, "mrxsmb10.sys"));
    }

    /// <summary>Removes the SMBv1 protocol feature via DISM.</summary>
    public Task<TweakResult> RemoveSMBv1Async() => DisableFeatureAsync("SMB1Protocol");

    public async Task<TweakResult> DisableFeatureAsync(string featureName)
    {
        return await RunDismAsync($"/Online /Disable-Feature /FeatureName:{featureName} /NoRestart");
    }

    public async Task<TweakResult> EnableFeatureAsync(string featureName)
    {
        return await RunDismAsync($"/Online /Enable-Feature /FeatureName:{featureName} /NoRestart /All");
    }

    private static async Task<TweakResult> RunDismAsync(string args)
    {
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("dism.exe", args)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                    return TweakResult.Fail("dism.exe failed to start — process could not be created.");

                // Drain both streams concurrently before calling WaitForExit to prevent
                // pipe-buffer deadlocks. DISM can produce significant output.
                var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

                // 10-minute timeout — DISM feature changes can take several minutes
                bool finished = proc.WaitForExit(600_000);
                string output = stdoutTask.GetAwaiter().GetResult();
                string errOut = stderrTask.GetAwaiter().GetResult();

                if (!finished)
                    return TweakResult.Fail("DISM timed out after 10 minutes. The feature change may still be running in the background.");

                if (proc.ExitCode == 0 || proc.ExitCode == 3010)
                {
                    // Return a clean status rather than raw DISM output (which is mostly
                    // header noise and not useful in a status message).
                    string op       = args.Contains("/Enable-Feature", StringComparison.OrdinalIgnoreCase) ? "enabled" : "disabled";
                    string reboot   = proc.ExitCode == 3010 ? " A restart is required to apply the change." : "";
                    return TweakResult.Ok($"Feature {op} successfully.{reboot}");
                }

                if (proc.ExitCode == 2)
                    return TweakResult.Ok("Feature already removed or not present.");

                // Log the full error for diagnostics; surface a truncated version in the UI.
                string fullErr = string.IsNullOrWhiteSpace(errOut) ? output : errOut;
                LoggerService.Instance.Warn("OptionalFeaturesService",
                    $"DISM exited {proc.ExitCode} for args [{args}]: {fullErr}");
                return TweakResult.Fail($"DISM exited with code {proc.ExitCode}. {fullErr[..Math.Min(300, fullErr.Length)]}");
            }
            catch (Exception ex)
            {
                return TweakResult.FromException(ex);
            }
        });
    }
}
