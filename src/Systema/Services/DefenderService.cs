// ════════════════════════════════════════════════════════════════════════════
// DefenderService.cs  ·  Toggles Windows Defender CFA and RTP via PowerShell
// ════════════════════════════════════════════════════════════════════════════
//
// Enables or disables Controlled Folder Access (CFA) and Real-Time Protection
// (RTP) by invoking Set-MpPreference via a hidden PowerShell process. Current
// state is read directly from the Defender registry keys to avoid WMI timeouts.
//
// RELATED FILES
//   NetworkViewModel.cs  — exposes CFA toggle to the user via the Network tab
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

public class DefenderService
{
    private static readonly LoggerService _log = LoggerService.Instance;

    // Windows Defender stores Controlled Folder Access state directly in the registry.
    // Reading it here avoids spawning powershell.exe on every auto-refresh tick.
    private const string CfaKeyPath =
        @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access";

    public async Task<TweakResult> EnableControlledFolderAccessAsync()
    {
        _log.Info("DefenderService", "Enabling Controlled Folder Access");
        var result = await RunPowerShellAsync("Set-MpPreference -EnableControlledFolderAccess Enabled");
        if (result.Success) _log.Info("DefenderService", "Controlled Folder Access enabled");
        else _log.Warn("DefenderService", $"CFA enable failed: {result.Message}");
        return result;
    }

    public async Task<TweakResult> DisableControlledFolderAccessAsync()
    {
        _log.Info("DefenderService", "Disabling Controlled Folder Access");
        var result = await RunPowerShellAsync("Set-MpPreference -EnableControlledFolderAccess Disabled");
        if (result.Success) _log.Info("DefenderService", "Controlled Folder Access disabled");
        else _log.Warn("DefenderService", $"CFA disable failed: {result.Message}");
        return result;
    }

    public async Task<TweakResult> EnableRealTimeProtectionAsync()
    {
        _log.Info("DefenderService", "Enabling Real-Time Protection");
        var result = await RunPowerShellAsync("Set-MpPreference -DisableRealtimeMonitoring $false");
        if (result.Success) _log.Info("DefenderService", "Real-Time Protection enabled");
        else _log.Warn("DefenderService", $"RTP enable failed: {result.Message}");
        return result;
    }

    public async Task<TweakResult> EnableNetworkProtectionAsync()
    {
        _log.Info("DefenderService", "Enabling Network Protection");
        var result = await RunPowerShellAsync("Set-MpPreference -EnableNetworkProtection Enabled");
        if (result.Success) _log.Info("DefenderService", "Network Protection enabled");
        else _log.Warn("DefenderService", $"Network Protection enable failed: {result.Message}");
        return result;
    }

    /// <summary>
    /// Reads CFA state directly from the Windows Defender registry key — no process spawn,
    /// no PowerShell overhead, safe to call on any thread including small-stack threadpool threads.
    /// </summary>
    public bool IsControlledFolderAccessEnabled()
    {
        try
        {
            // Primary location: Defender's own key
            using var key = Registry.LocalMachine.OpenSubKey(CfaKeyPath, writable: false);
            if (key != null)
            {
                var val = key.GetValue("EnableControlledFolderAccess");
                if (val is int i) return i == 1;
            }

            // Fallback: Group Policy override path (set by MDM / SCCM / Intune)
            using var polKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access",
                writable: false);
            if (polKey != null)
            {
                var val = polKey.GetValue("EnableControlledFolderAccess");
                if (val is int ip) return ip == 1;
            }

            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Runs a PowerShell command that modifies Defender settings.
    /// Uses a large-stack thread (8 MB) so that Process.Start() — which can trigger
    /// AV/EDR CreateProcess hooks on the calling thread — doesn't overflow the stack.
    /// Only redirects stderr (not stdout) to avoid the classic pipe-buffer deadlock.
    /// </summary>
    private static Task<TweakResult> RunPowerShellAsync(string command)
    {
        return ThreadHelper.RunOnLargeStackAsync<TweakResult>(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -NonInteractive -Command \"{command}\"")
                {
                    UseShellExecute        = false,
                    // Only redirect stderr — we don't use stdout for these Set-MpPreference commands.
                    // Redirecting stdout without reading it fills the pipe buffer and deadlocks.
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                    return TweakResult.Fail("Failed to start powershell.exe — process could not be created.");
                string error = proc.StandardError.ReadToEnd();
                proc.WaitForExit(30_000); // 30-second timeout for Defender commands
                return proc.ExitCode == 0
                    ? TweakResult.Ok("Defender setting applied.")
                    : TweakResult.Fail(error.Length > 0 ? error : $"PowerShell exited with code {proc.ExitCode}.");
            }
            catch (Exception ex)
            {
                return TweakResult.FromException(ex);
            }
        });
    }
}
