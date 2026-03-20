// ════════════════════════════════════════════════════════════════════════════
// ProcessLassoService.cs  ·  Detects Process Lasso and reads/writes ProBalance settings
// ════════════════════════════════════════════════════════════════════════════
//
// Checks for a Process Lasso installation by scanning common install paths and
// registry keys. If found, reads and writes ProBalance configuration settings
// directly to the Process Lasso registry hive without requiring the UI to be open.
//
// RELATED FILES
//   Models/ProcessLassoSettings.cs  — settings data shape
//   ToolsViewModel.cs               — Process Lasso settings panel on the Tools tab
// ════════════════════════════════════════════════════════════════════════════

using System.IO;
using System.Management;
using System.Threading.Tasks;
using Microsoft.Win32;
using Systema.Core;
using Systema.Models;

namespace Systema.Services;

/// <summary>
/// Detects Process Lasso installation and reads/writes its ProBalance settings
/// directly into the HKCU registry hive that Process Lasso uses at runtime.
///
/// Registry root: HKCU\SOFTWARE\Bitsum\ProcessLasso\
/// </summary>
public class ProcessLassoService
{
    private const string LassoRoot = @"SOFTWARE\Bitsum\ProcessLasso";

    private static readonly LoggerService _log = LoggerService.Instance;

    // ── Detection ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when Process Lasso appears to be installed on this machine.
    /// Checks the Bitsum registry key and common Program Files install paths.
    /// </summary>
    public bool IsInstalled()
    {
        try
        {
            // Primary check: registry key created by the installer
            using var key = Registry.LocalMachine.OpenSubKey(LassoRoot, writable: false)
                         ?? Registry.CurrentUser.OpenSubKey(LassoRoot, writable: false);

            if (key != null) return true;

            // Secondary check: executable on disk
            string installPath = GetInstallPath();
            if (!string.IsNullOrEmpty(installPath))
            {
                string exe = Path.Combine(installPath, "ProcessLasso.exe");
                if (File.Exists(exe)) return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.Warn("ProcessLassoService", $"IsInstalled check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns the installation directory of Process Lasso, or an empty string when not found.
    /// </summary>
    public string GetInstallPath()
    {
        try
        {
            // Check HKLM first, then HKCU (per-user install)
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(LassoRoot, writable: false);
                if (key == null) continue;

                var path = key.GetValue("InstallPath") as string
                        ?? key.GetValue("") as string;
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }

            // Fall back to typical installation locations
            foreach (string candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             "Process Lasso"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             "Process Lasso")
            })
            {
                if (Directory.Exists(candidate)) return candidate;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            _log.Warn("ProcessLassoService", $"GetInstallPath failed: {ex.Message}");
            return string.Empty;
        }
    }

    // ── Settings read / write ─────────────────────────────────────────────────

    /// <summary>
    /// Reads the current ProBalance settings from the Process Lasso registry hive.
    /// Returns default values when the keys do not exist yet.
    /// </summary>
    public ProcessLassoSettings GetSettings()
    {
        var s = new ProcessLassoSettings();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LassoRoot, writable: false);
            if (key == null) return s;

            s.SystemwideCpuThreshold    = ReadInt(key, "ProBalance.SystemwideAllCPUThreshold", s.SystemwideCpuThreshold);
            s.PerProcessCpuThreshold    = ReadInt(key, "ProBalance.PerProcessAllCPUThreshold",  s.PerProcessCpuThreshold);
            s.PerProcessRelaxThreshold  = ReadInt(key, "ProBalance.PerProcessRelaxThreshold",   s.PerProcessRelaxThreshold);
            s.RestraintPeriodMs         = ReadInt(key, "ProBalance.RestraintPeriod",            s.RestraintPeriodMs);
            s.MaxRestraintDurationMs    = ReadInt(key, "ProBalance.MaxRestraintDuration",        s.MaxRestraintDurationMs);
            s.MinRestraintDurationMs    = ReadInt(key, "ProBalance.MinRestraintDuration",        s.MinRestraintDurationMs);
            s.LowerToIdle               = ReadBool(key, "ProBalance.bLowerToIdle",              s.LowerToIdle);
            s.IgnoreForeground          = ReadBool(key, "ProBalance.bIgnoreForeground",         s.IgnoreForeground);
            s.ExcludeForegroundChildren = ReadBool(key, "ProBalance.bExcludeForegroundChildren", s.ExcludeForegroundChildren);
            s.ExcludeNonNormalPriority  = ReadBool(key, "ProBalance.bExcludeNonNormalPriority", s.ExcludeNonNormalPriority);
            s.ExcludeSystemServices     = ReadBool(key, "ProBalance.bExcludeSystemServices",    s.ExcludeSystemServices);
            s.UseEfficiencyMode         = ReadBool(key, "ProBalance.bUseEfficiencyMode",        s.UseEfficiencyMode);
            s.LowerIoPriority           = ReadBool(key, "ProBalance.bLowerIOPriority",          s.LowerIoPriority);
        }
        catch (Exception ex)
        {
            _log.Warn("ProcessLassoService", $"GetSettings failed: {ex.Message}");
        }
        return s;
    }

    /// <summary>
    /// Writes ProBalance settings to the Process Lasso registry hive.
    /// If E-cores are detected the service also writes the E-core affinity values
    /// so Process Lasso can prefer scheduling background work on efficiency cores.
    /// </summary>
    public Task<TweakResult> ApplySettingsAsync(ProcessLassoSettings settings) => Task.Run(() =>
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LassoRoot, writable: true);
            if (key == null)
                return TweakResult.Fail("Could not open/create Process Lasso registry key.");

            key.SetValue("ProBalance.SystemwideAllCPUThreshold", settings.SystemwideCpuThreshold,  RegistryValueKind.DWord);
            key.SetValue("ProBalance.PerProcessAllCPUThreshold",  settings.PerProcessCpuThreshold, RegistryValueKind.DWord);
            key.SetValue("ProBalance.PerProcessRelaxThreshold",   settings.PerProcessRelaxThreshold, RegistryValueKind.DWord);
            key.SetValue("ProBalance.RestraintPeriod",            settings.RestraintPeriodMs,        RegistryValueKind.DWord);
            key.SetValue("ProBalance.MaxRestraintDuration",        settings.MaxRestraintDurationMs,  RegistryValueKind.DWord);
            key.SetValue("ProBalance.MinRestraintDuration",        settings.MinRestraintDurationMs,  RegistryValueKind.DWord);
            key.SetValue("ProBalance.bLowerToIdle",               settings.LowerToIdle               ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ProBalance.bIgnoreForeground",          settings.IgnoreForeground           ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ProBalance.bExcludeForegroundChildren", settings.ExcludeForegroundChildren  ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ProBalance.bExcludeNonNormalPriority",  settings.ExcludeNonNormalPriority   ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ProBalance.bExcludeSystemServices",     settings.ExcludeSystemServices      ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ProBalance.bUseEfficiencyMode",         settings.UseEfficiencyMode          ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("ProBalance.bLowerIOPriority",           settings.LowerIoPriority            ? 1 : 0, RegistryValueKind.DWord);

            // E-core affinity — if the CPU has efficiency cores, configure ProBalance
            // to prefer scheduling restrained work on those cores.
            TryApplyECoreAffinity(key);

            _log.Info("ProcessLassoService", "ProBalance settings written.");
            return TweakResult.Ok("Process Lasso ProBalance settings applied.");
        }
        catch (Exception ex)
        {
            _log.Error("ProcessLassoService", "ApplySettingsAsync failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    // ── E-core detection ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when at least one processor in the system reports more than one
    /// efficiency class (i.e. the CPU has both P-cores and E-cores / Atoms).
    /// </summary>
    public bool HasECores()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT NumberOfEfficiencyClasses FROM Win32_Processor");

            foreach (ManagementObject obj in searcher.Get())
            {
                var val = obj["NumberOfEfficiencyClasses"];
                if (val is uint u && u > 1) return true;
                if (val is int  i && i > 1) return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn("ProcessLassoService", $"HasECores WMI query failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to detect E-core logical processor indices and write the ProBalance
    /// E-core affinity mask to the registry. Silently skips when detection fails.
    /// </summary>
    private void TryApplyECoreAffinity(RegistryKey lassoKey)
    {
        try
        {
            if (!HasECores()) return;

            long eCoreMask = BuildECoreMask();
            if (eCoreMask == 0) return;

            lassoKey.SetValue("ProBalance.bRestrictToECores", 1, RegistryValueKind.DWord);
            // Store as QWord to accommodate systems with >32 logical processors
            lassoKey.SetValue("ProBalance.ECoreMask", eCoreMask, RegistryValueKind.QWord);

            _log.Info("ProcessLassoService",
                $"E-core affinity mask 0x{eCoreMask:X} written to Process Lasso.");
        }
        catch (Exception ex)
        {
            _log.Warn("ProcessLassoService", $"TryApplyECoreAffinity failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a bitmask of logical processor indices that belong to the lowest
    /// efficiency class (E-cores / Atoms). Returns 0 when detection is inconclusive.
    ///
    /// Strategy: Query Win32_PerfFormattedData_Counters_ProcessorInformation for each
    /// logical processor and correlate with processor efficiency class data from WMI.
    /// Because WMI does not directly expose per-logical-processor efficiency classes,
    /// we use the heuristic that E-cores on Intel hybrid CPUs have higher logical
    /// processor indices than P-cores (they are numbered after all P-core logical
    /// processors including HT siblings).
    /// </summary>
    private long BuildECoreMask()
    {
        try
        {
            // Determine total logical processor count
            int totalLogical = Environment.ProcessorCount;
            if (totalLogical <= 1) return 0;

            // Query physical-core and efficiency-class counts per processor socket
            int pCoreLogicalCount = 0;
            int eCoreLogicalCount = 0;

            using (var cpuSearcher = new ManagementObjectSearcher(
                       "SELECT NumberOfCores, NumberOfLogicalProcessors, NumberOfEfficiencyClasses " +
                       "FROM Win32_Processor"))
            {
                foreach (ManagementObject cpu in cpuSearcher.Get())
                {
                    uint effClasses = Convert.ToUInt32(cpu["NumberOfEfficiencyClasses"] ?? 1u);
                    uint logicals   = Convert.ToUInt32(cpu["NumberOfLogicalProcessors"] ?? (uint)totalLogical);
                    uint cores      = Convert.ToUInt32(cpu["NumberOfCores"] ?? logicals);

                    if (effClasses < 2)
                    {
                        // Homogeneous — no E-cores
                        pCoreLogicalCount += (int)logicals;
                        continue;
                    }

                    // Hybrid CPU: Intel 12th-gen+ places P-core logical processors first.
                    // A common pattern: 8 P-cores (with HT = 16 logical) + 16 E-cores (no HT = 16 logical).
                    // We cannot determine the split precisely from Win32_Processor alone,
                    // so we fall back to assuming half of the logical processors are P-cores
                    // (rounded up) when HT is enabled.
                    bool hyperthreading = logicals > cores;
                    int  pLogicals      = hyperthreading ? (int)(cores * 2) : (int)cores;
                    int  eLogicals      = (int)logicals - pLogicals;

                    pCoreLogicalCount += pLogicals;
                    eCoreLogicalCount += eLogicals;
                }
            }

            if (eCoreLogicalCount <= 0) return 0;

            // Build mask: bits for logical processors starting at pCoreLogicalCount
            long mask = 0;
            for (int i = pCoreLogicalCount; i < pCoreLogicalCount + eCoreLogicalCount; i++)
            {
                if (i >= 64) break; // 64-bit mask limit
                mask |= (1L << i);
            }

            return mask;
        }
        catch (Exception ex)
        {
            _log.Warn("ProcessLassoService", $"BuildECoreMask failed: {ex.Message}");
            return 0;
        }
    }

    // ── ProBalance per-process exclusion ─────────────────────────────────────

    /// <summary>
    /// Adds the specified executable to Process Lasso's ProBalance exclusion list
    /// so it is never throttled while the game boost is active.
    /// </summary>
    public TweakResult ExcludeFromProBalance(string executableName)
    {
        try
        {
            string subKey = $@"{LassoRoot}\Proc\{executableName}";
            using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
            if (key == null)
                return TweakResult.Fail($"Could not create exclusion key for {executableName}.");
            key.SetValue("ProBalance.bExclude", 1, RegistryValueKind.DWord);
            _log.Info("ProcessLassoService", $"ProBalance exclusion added: {executableName}");
            return TweakResult.Ok($"{executableName} excluded from ProBalance.");
        }
        catch (Exception ex)
        {
            _log.Warn("ProcessLassoService", $"ExcludeFromProBalance failed: {ex.Message}");
            return TweakResult.FromException(ex);
        }
    }

    /// <summary>Removes the ProBalance exclusion written by ExcludeFromProBalance.</summary>
    public void RemoveProBalanceExclusion(string executableName)
    {
        try
        {
            string subKey = $@"{LassoRoot}\Proc\{executableName}";
            using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
            key?.DeleteValue("ProBalance.bExclude", throwOnMissingValue: false);
            _log.Info("ProcessLassoService", $"ProBalance exclusion removed: {executableName}");
        }
        catch (Exception ex)
        {
            _log.Warn("ProcessLassoService", $"RemoveProBalanceExclusion failed: {ex.Message}");
        }
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    private static int ReadInt(RegistryKey key, string name, int defaultValue)
    {
        var val = key.GetValue(name);
        return val is int i ? i : defaultValue;
    }

    private static bool ReadBool(RegistryKey key, string name, bool defaultValue)
    {
        var val = key.GetValue(name);
        return val is int i ? i != 0 : defaultValue;
    }
}
