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
                    if (trimmed.StartsWith("Feature Name :"))
                    {
                        currentFeature = trimmed.Replace("Feature Name :", "").Trim();
                    }
                    else if (trimmed.StartsWith("State :") && currentFeature != null)
                    {
                        string state = trimmed.Replace("State :", "").Trim();

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
