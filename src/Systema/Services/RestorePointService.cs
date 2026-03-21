// ════════════════════════════════════════════════════════════════════════════
// RestorePointService.cs  ·  Creates, lists, and deletes Windows restore points
// ════════════════════════════════════════════════════════════════════════════
//
// Uses WMI SystemRestore for create and list, PowerShell
// Remove-ComputerRestorePoint for targeted deletion, and rstrui.exe for the
// user-facing restore wizard (Windows can't restore while running).
//
// RELATED FILES
//   Models/RestorePointInfo.cs           — data shape for a listed restore point
//   ServicesViewModel.cs                 — restore point button on the Services tab
//   ToolsViewModel.cs                    — restore point button on the Tools tab
//   Views/RestorePointManagerWindow.xaml — Manage Restore Points window
//   ViewModels/SettingsViewModel.cs      — opens the manager from Settings tab
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Management;
using Systema.Core;
using Systema.Models;

namespace Systema.Services;

public class RestorePointService
{
    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<TweakResult> CreateAsync(string description)
    {
        return await Task.Run(() =>
        {
            try
            {
                var scope  = new ManagementScope(@"\\.\root\default");
                var path   = new ManagementPath("SystemRestore");
                using var cls = new ManagementClass(scope, path, null);
                var inParams  = cls.GetMethodParameters("CreateRestorePoint");
                inParams["Description"]    = description;
                inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
                inParams["EventType"]      = 100;  // BEGIN_SYSTEM_CHANGE
                var result     = cls.InvokeMethod("CreateRestorePoint", inParams, null);
                int returnValue = System.Convert.ToInt32(result["ReturnValue"]);
                if (returnValue == 0)
                    return TweakResult.Ok("Restore point created successfully.");

                // Translate well-known WMI SystemRestore return codes to friendly text
                string friendly = returnValue switch
                {
                    // Windows throttles restore point creation to one per 24 hours
                    // (unless the machine is joined to a domain with override policy).
                    // 0x80042306 = -2147213562 — SR_ALREADY_CREATED_TODAY
                    var c when c == unchecked((int)0x80042306) || c == -2147213562
                        => "Windows only allows one restore point to be created per 24 hours. " +
                           "A restore point already exists from today. Try again tomorrow, or " +
                           "open Restore Point Manager to see existing points.",
                    // 0x80042302 — SR disabled by policy or System Protection is off
                    var c when c == unchecked((int)0x80042302) || c == -2147213566
                        => "System Protection is disabled for drive C:. " +
                           "Enable it in Control Panel → System → System Protection before creating restore points.",
                    // 0x80042301 — insufficient disk space for System Protection
                    var c when c == unchecked((int)0x80042301) || c == -2147213567
                        => "Not enough disk space to create a restore point. " +
                           "Free up space on C: or reduce the System Protection disk usage limit.",
                    _ => $"Restore point creation returned code {returnValue} (0x{returnValue:X8})."
                };
                return TweakResult.Fail(friendly);
            }
            catch (Exception ex)
            {
                return TweakResult.FromException(ex);
            }
        });
    }

    // ── List ──────────────────────────────────────────────────────────────────

    /// <summary>Returns all restore points on this machine, newest first.</summary>
    public async Task<List<RestorePointInfo>> GetRestorePointsAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<RestorePointInfo>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\default");
                var query = new ObjectQuery("SELECT * FROM SystemRestore");
                using var searcher = new ManagementObjectSearcher(scope, query);

                foreach (ManagementObject obj in searcher.Get())
                {
                    var creationRaw = obj["CreationTime"]?.ToString() ?? "";
                    DateTime dt = DateTime.MinValue;
                    if (!string.IsNullOrEmpty(creationRaw))
                    {
                        try { dt = ManagementDateTimeConverter.ToDateTime(creationRaw); }
                        catch { }
                    }

                    int rpType   = System.Convert.ToInt32(obj["RestorePointType"] ?? 12);
                    string label = rpType switch
                    {
                        0  => "App Install",
                        1  => "App Uninstall",
                        6  => "Manual Backup",
                        10 => "Driver Install",
                        12 => "Settings Change",
                        13 => "Cancelled",
                        _  => "System"
                    };

                    list.Add(new RestorePointInfo
                    {
                        SequenceNumber = System.Convert.ToInt32(obj["SequenceNumber"] ?? 0),
                        Description    = obj["Description"]?.ToString() ?? "Unnamed",
                        CreatedAt      = dt,
                        TypeLabel      = label,
                    });
                }
            }
            catch { }

            // Newest first
            list.Sort((a, b) => b.SequenceNumber.CompareTo(a.SequenceNumber));
            return list;
        });
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes a specific restore point by sequence number using
    /// PowerShell Remove-ComputerRestorePoint.
    /// </summary>
    public async Task<TweakResult> DeleteRestorePointAsync(int sequenceNumber)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var ps = new Process();
                ps.StartInfo = new ProcessStartInfo
                {
                    FileName               = "powershell.exe",
                    Arguments              = $"-NonInteractive -NoProfile -Command \"Remove-ComputerRestorePoint -RestorePoint {sequenceNumber}\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                ps.Start();
                // Read both streams concurrently before WaitForExit — sequential reads
                // deadlock if either pipe buffer fills while the other is being drained.
                var errTask  = Task.Run(() => ps.StandardError.ReadToEnd());
                var outTask  = Task.Run(() => ps.StandardOutput.ReadToEnd());
                ps.WaitForExit();
                string err  = errTask.Result;
                string out_ = outTask.Result;

                return ps.ExitCode == 0
                    ? TweakResult.Ok("Restore point deleted.")
                    : TweakResult.Fail($"Delete failed: {(string.IsNullOrWhiteSpace(err) ? out_ : err).Trim()}");
            }
            catch (Exception ex)
            {
                return TweakResult.FromException(ex);
            }
        });
    }

    // ── Restore ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the Windows System Restore wizard (rstrui.exe).
    /// Windows cannot restore itself while running, so we hand off to the wizard.
    /// </summary>
    public void OpenSystemRestoreWizard()
    {
        try
        {
            Process.Start(new ProcessStartInfo("rstrui.exe") { UseShellExecute = true });
        }
        catch { /* rstrui not found — unusual but non-fatal */ }
    }
}
