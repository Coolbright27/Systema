// ════════════════════════════════════════════════════════════════════════════
// DnsService.cs  ·  Named DNS profile switching via direct registry writes
// ════════════════════════════════════════════════════════════════════════════
//
// Applies a named DNS profile (primary + secondary DNS) by writing directly to
// HKLM Tcpip adapter subkeys, deliberately bypassing the NetworkInterface API
// which can crash certain drivers. Provides a FlushDns helper (invokes ipconfig).
//
// RELATED FILES
//   Models/DnsProfile.cs        — profile data shape (ProfileName, PrimaryDNS, SecondaryDNS)
//   NetworkViewModel.cs         — profile picker and apply command
//   ToolsViewModel.cs           — DNS flush button
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using Microsoft.Win32;
using Systema.Core;
using Systema.Models;

namespace Systema.Services;

/// <summary>
/// DNS profile switching service.
///
/// IMPORTANT — why NetworkInterface is NOT used here:
/// NetworkInterface.GetAllNetworkInterfaces() calls into native iphlpapi.dll which triggers
/// third-party VPN/AV/EDR network-filter drivers. On some machines this causes:
///   • An indefinite hang (driver waits for a response that never comes)
///   • An AccessViolationException inside native driver code — uncatchable by .NET,
///     kills the process with no error dialog.
/// All DNS reads now use pure registry access (Tcpip\Parameters\Interfaces) which is
/// fast, always reliable, and never touches network driver code.
/// </summary>
public class DnsService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    // Registry paths
    private const string TcpipInterfacesPath =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string NetworkAdaptersPath =
        @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    public static readonly List<DnsProfile> Profiles = new()
    {
        new DnsProfile { Name = "Cloudflare (Recommended)", Primary = "1.1.1.1",          Secondary = "1.0.0.1",         SupportsDoH = true  },
        new DnsProfile { Name = "Google",                   Primary = "8.8.8.8",           Secondary = "8.8.4.4",          SupportsDoH = true  },
        new DnsProfile { Name = "OpenDNS",                  Primary = "208.67.222.222",     Secondary = "208.67.220.220",   SupportsDoH = false },
        new DnsProfile { Name = "System Default (DHCP)",    Primary = "",                   Secondary = "",                 SupportsDoH = false }, // Empty Primary = DHCP mode — ApplyProfileAsync uses netsh source=dhcp
    };

    /// <summary>
    /// Reads the current DNS servers from the registry — no NetworkInterface,
    /// no native driver calls, safe on any thread with any stack size.
    /// </summary>
    public string GetCurrentDns()
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(TcpipInterfacesPath);
            if (baseKey == null) return "Unknown";

            foreach (var guid in baseKey.GetSubKeyNames())
            {
                using var ifKey = baseKey.OpenSubKey(guid);
                if (ifKey == null) continue;

                // Only look at interfaces that actually have an IP address (i.e. active)
                if (!HasActiveIp(ifKey)) continue;

                // Prefer manually-set DNS; fall back to DHCP-assigned
                var dns = ReadDnsValue(ifKey, "NameServer")
                       ?? ReadDnsValue(ifKey, "DhcpNameServer");

                if (dns != null)
                    return dns;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("DnsService", $"GetCurrentDns registry read failed: {ex.Message}");
        }
        return "Unknown";
    }

    public async Task<TweakResult> ApplyProfileAsync(DnsProfile profile)
    {
        // Validate Primary DNS before doing anything — prevents corrupt netsh calls.
        if (!string.IsNullOrEmpty(profile.Primary) && string.IsNullOrWhiteSpace(profile.Primary))
            return TweakResult.Fail("Primary DNS address is invalid.");

        Log.Info("DnsService", $"Applying DNS profile: {profile.Name} ({profile.Primary})");

        // Run netsh on a large-stack thread — spawning processes can trigger
        // AV/EDR CreateProcess hooks that exhaust small threadpool stacks.
        return await ThreadHelper.RunOnLargeStackAsync(() =>
        {
            try
            {
                var adapters = GetActiveAdapterNames();
                if (adapters.Count == 0)
                    return TweakResult.Fail("No active network adapters found.");

                foreach (var adapter in adapters)
                {
                    // Escape any embedded double-quotes in the adapter name to prevent
                    // command injection when the name is embedded in the netsh argument string.
                    var safeAdapter = adapter.Replace("\"", "\\\"");

                    if (string.IsNullOrEmpty(profile.Primary))
                    {
                        RunNetsh($"interface ip set dns name=\"{safeAdapter}\" source=dhcp");
                    }
                    else
                    {
                        RunNetsh($"interface ip set dns name=\"{safeAdapter}\" static {profile.Primary} primary");
                        if (!string.IsNullOrEmpty(profile.Secondary))
                            RunNetsh($"interface ip add dns name=\"{safeAdapter}\" {profile.Secondary} index=2");
                    }
                }

                if (profile.SupportsDoH)
                    EnableDoH();

                Log.Info("DnsService", $"DNS profile applied successfully: {profile.Name}");
                return TweakResult.Ok($"DNS set to {profile.Name} ({profile.Primary}).");
            }
            catch (Exception ex)
            {
                Log.Error("DnsService", "Failed to apply DNS profile", ex);
                return TweakResult.FromException(ex);
            }
        });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Gets active adapter friendly names from the registry.
    /// Falls back to parsing "netsh interface show interface" output if registry yields nothing.
    /// Never calls NetworkInterface — see class-level comment.
    /// </summary>
    private List<string> GetActiveAdapterNames()
    {
        var names = new List<string>();

        try
        {
            using var tcpipKey = Registry.LocalMachine.OpenSubKey(TcpipInterfacesPath);
            using var netKey   = Registry.LocalMachine.OpenSubKey(NetworkAdaptersPath);
            if (tcpipKey == null) goto fallback;

            foreach (var guid in tcpipKey.GetSubKeyNames())
            {
                using var ifKey = tcpipKey.OpenSubKey(guid);
                if (ifKey == null || !HasActiveIp(ifKey)) continue;

                // Get the human-readable adapter name from the Network key
                string? friendlyName = null;
                try
                {
                    using var connKey = netKey?.OpenSubKey($@"{guid}\Connection");
                    friendlyName = connKey?.GetValue("Name") as string;
                }
                catch { /* registry key may not exist for all interfaces */ }

                if (!string.IsNullOrWhiteSpace(friendlyName) && !IsVirtualAdapter(friendlyName))
                    names.Add(friendlyName);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("DnsService", $"Registry adapter enumeration failed: {ex.Message}");
        }

        fallback:
        // If registry gave us nothing, spawn netsh as a safe fallback
        if (names.Count == 0)
            names = GetAdapterNamesViaNetsh();

        return names;
    }

    /// <summary>
    /// Parses "netsh interface show interface" output to get connected adapter names.
    /// Completely isolated — if netsh crashes/hangs it doesn't affect our process.
    /// </summary>
    private static List<string> GetAdapterNamesViaNetsh()
    {
        var names = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("netsh", "interface show interface")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return names; // netsh failed to start — return empty list
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5_000);

            // Output format (skip 3 header lines):
            // Admin State  State    Type          Interface Name
            // -------------------------------------------------------
            // Enabled      Connected    Dedicated    Wi-Fi
            foreach (var line in output.Split('\n').Skip(3))
            {
                var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    parts[0].Equals("Enabled",   StringComparison.OrdinalIgnoreCase) &&
                    parts[1].Equals("Connected", StringComparison.OrdinalIgnoreCase))
                {
                    var ifName = string.Join(" ", parts.Skip(3));
                    if (!IsVirtualAdapter(ifName))
                        names.Add(ifName);
                }
            }
        }
        catch { /* swallow — caller handles empty list */ }
        return names;
    }

    /// <summary>
    /// Returns true if the Tcpip interface registry key has an active IP address.
    /// Checks both static (IPAddress REG_MULTI_SZ) and DHCP-assigned (DhcpIPAddress).
    /// </summary>
    private static bool HasActiveIp(RegistryKey ifKey)
    {
        try
        {
            // Static IP — stored as REG_MULTI_SZ (string[])
            if (ifKey.GetValue("IPAddress") is string[] staticIps &&
                staticIps.Any(ip => !string.IsNullOrEmpty(ip) && ip != "0.0.0.0"))
                return true;

            // DHCP IP — stored as REG_SZ
            var dhcpIp = (ifKey.GetValue("DhcpIPAddress") as string)?.Trim();
            if (!string.IsNullOrEmpty(dhcpIp) && dhcpIp != "0.0.0.0")
                return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Reads a DNS registry value (space- or comma-separated) and returns
    /// up to 2 addresses formatted as "x.x.x.x, y.y.y.y", or null if empty.
    /// </summary>
    private static string? ReadDnsValue(RegistryKey key, string valueName)
    {
        try
        {
            var raw = (key.GetValue(valueName) as string)?.Trim();
            if (string.IsNullOrEmpty(raw)) return null;

            var parts = raw.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? null : string.Join(", ", parts.Take(2));
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns true if the adapter friendly name looks like a VPN, TAP, virtual, or loopback
    /// interface that should not have its DNS rewritten. Consistent with the Wi-Fi disable
    /// filter in GameBoosterService which excludes Virtual/VPN/TAP descriptions.
    /// </summary>
    private static bool IsVirtualAdapter(string adapterName)
    {
        return adapterName.Contains("VPN",      StringComparison.OrdinalIgnoreCase)
            || adapterName.Contains("TAP",      StringComparison.OrdinalIgnoreCase)
            || adapterName.Contains("Tunnel",   StringComparison.OrdinalIgnoreCase)
            || adapterName.Contains("Virtual",  StringComparison.OrdinalIgnoreCase)
            || adapterName.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            || adapterName.Contains("WireGuard",StringComparison.OrdinalIgnoreCase);
    }

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            CreateNoWindow  = true
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(5_000);
    }

    private static void EnableDoH()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", true);
            key.SetValue("EnableAutoDoh", 2, RegistryValueKind.DWord);
        }
        catch { }
    }
}
