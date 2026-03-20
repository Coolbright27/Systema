// ════════════════════════════════════════════════════════════════════════════
// MemoryService.cs  ·  Physical RAM and page-file stats via P/Invoke
// ════════════════════════════════════════════════════════════════════════════
//
// Reads total/available physical RAM using GlobalMemoryStatusEx P/Invoke to
// avoid WMI hangs that affect some machines. Also reads page-file usage from
// the registry. Returns plain numeric values; no WMI dependency.
//
// RELATED FILES
//   MemoryViewModel.cs    — displays RAM stats and calls GetMemoryInfo()
// ════════════════════════════════════════════════════════════════════════════

using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

public class MemoryService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    // ── P/Invoke: GlobalMemoryStatusEx (instant, no WMI overhead) ─────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Returns total physical RAM in MB. Uses P/Invoke — never hangs.</summary>
    public long GetTotalRamMb()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms))
            return (long)(ms.ullTotalPhys / 1024 / 1024);
        return 0;
    }

    /// <summary>Returns available (free) physical RAM in MB. Uses P/Invoke — never hangs.</summary>
    public long GetAvailableRamMb()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms))
            return (long)(ms.ullAvailPhys / 1024 / 1024);
        return 0;
    }

    /// <summary>Returns both total and available RAM in a single P/Invoke call.</summary>
    public (long totalMb, long availMb) GetRamStats()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms))
            return ((long)(ms.ullTotalPhys / 1024 / 1024), (long)(ms.ullAvailPhys / 1024 / 1024));
        return (0, 0);
    }

    // ── Pagefile ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the configured pagefile sizes from the registry (fast, no WMI).
    /// Returns isSystemManaged=true when sizes are 0/0 (Windows auto-sizing) or when
    /// the registry key cannot be read.
    /// </summary>
    public (int initialMb, int maximumMb, bool isSystemManaged) GetPagefileSettings()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            if (key != null)
            {
                var pagingFiles = key.GetValue("PagingFiles") as string[];
                if (pagingFiles != null && pagingFiles.Length > 0)
                {
                    // Format: "C:\pagefile.sys <initial> <max>"
                    var parts = pagingFiles[0].Split(' ');
                    if (parts.Length >= 3
                        && int.TryParse(parts[1], out int initial)
                        && int.TryParse(parts[2], out int max)
                        && (initial > 0 || max > 0))
                    {
                        return (initial, max, false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("MemoryService", "GetPagefileSettings registry read failed", ex);
        }
        return (0, 0, true); // system managed / unknown
    }

    /// <summary>
    /// Returns the actual running pagefile size via WMI Win32_PageFileUsage.
    /// This reflects what Windows is currently using, regardless of configured sizes.
    /// Returns (0, 0) if no pagefile exists or WMI is unavailable.
    /// Uses a 5-second timeout to prevent hanging on machines with WMI issues.
    /// </summary>
    public (long allocatedMb, long usedMb) GetCurrentPagefileUsageMb()
    {
        try
        {
            var task = Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage");
                long totalAllocated = 0, totalUsed = 0;
                foreach (ManagementObject obj in searcher.Get())
                {
                    totalAllocated += Convert.ToInt64(obj["AllocatedBaseSize"]);
                    totalUsed      += Convert.ToInt64(obj["CurrentUsage"]);
                }
                return (totalAllocated, totalUsed);
            });

            // 5-second guard — WMI can hang indefinitely on some machines
            if (task.Wait(TimeSpan.FromSeconds(5)))
                return task.Result;

            Log.Warn("MemoryService", "GetCurrentPagefileUsageMb WMI query timed out after 5 s — returning (0,0)");
            return (0, 0);
        }
        catch (Exception ex)
        {
            Log.Error("MemoryService", "GetCurrentPagefileUsageMb failed", ex);
        }
        return (0, 0);
    }

    /// <summary>Returns the recommended pagefile size in MB based on installed RAM.</summary>
    public int GetRecommendedPagefileMb()
    {
        long ramMb = GetTotalRamMb();
        // 13-16 GB RAM → 32 GB pagefile, 27-32 GB RAM → 16 GB pagefile
        if (ramMb >= 13000 && ramMb <= 16384)
            return 32768;
        if (ramMb >= 27000 && ramMb <= 32768)
            return 16384;
        // Default: 1.5× RAM, capped at 32 GB, floor 4 GB
        int recommended = (int)Math.Min(ramMb * 1.5, 32768);
        return Math.Max(recommended, 4096);
    }

    // ── Free RAM (EmptyWorkingSet + purge standby list) ───────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    private static extern uint NtSetSystemInformation(int SystemInformationClass, ref uint SystemInformation, int SystemInformationLength);

    private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    private const int  SystemMemoryListInformation = 0x50;
    private const uint MemoryPurgeStandbyList = 4;
    private const uint MemoryFlushModifiedList  = 3;

    /// <summary>
    /// Flushes process working sets and purges the standby memory list.
    /// Returns (freedMb, message). Runs on caller's thread — wrap in Task.Run.
    /// </summary>
    public (long freedMb, string message) FreeRam()
    {
        var (_, beforeMb) = GetRamStats();

        // 1. EmptyWorkingSet on every accessible process
        int trimmed = 0;
        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var handle = OpenProcess(PROCESS_ALL_ACCESS, false, proc.Id);
                if (handle == IntPtr.Zero) continue;
                EmptyWorkingSet(handle);
                CloseHandle(handle);
                trimmed++;
            }
            catch { /* skip inaccessible processes */ }
        }

        // 2. Purge standby list (requires SeProfileSingleProcessPrivilege — present when admin)
        try
        {
            uint cmd = MemoryFlushModifiedList;
            NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(uint));
            cmd = MemoryPurgeStandbyList;
            NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(uint));
        }
        catch (Exception ex) { Log.Warn("MemoryService", $"Standby purge skipped: {ex.Message}"); }

        System.Threading.Thread.Sleep(500); // let the OS reclaim before re-sampling
        var (_, afterMb) = GetRamStats();
        long freed = Math.Max(0, afterMb - beforeMb);

        Log.Info("MemoryService", $"FreeRam: trimmed {trimmed} processes, freed ~{freed} MB");
        return (freed, $"Freed ~{freed:N0} MB from {trimmed} processes.");
    }

    /// <summary>Returns available disk space on C: in MB.</summary>
    public long GetSystemDriveFreeMb()
    {
        try
        {
            var drive = new System.IO.DriveInfo("C");
            return drive.AvailableFreeSpace / 1024 / 1024;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Sets a custom pagefile size. Writes to the registry (reliable) and also
    /// tries to disable Windows automatic management via WMI.
    /// Requires a restart to take effect.
    /// </summary>
    public async Task<TweakResult> ConfigurePagefileAsync(int initialMb = 4096, int maximumMb = 4096)
    {
        return await Task.Run(() =>
        {
            try
            {
                Log.Info("MemoryService", $"ConfigurePagefile: {initialMb} MB / {maximumMb} MB");

                // Check available disk space
                long freeMb = GetSystemDriveFreeMb();
                if (freeMb > 0 && freeMb < maximumMb + 2048)
                    return TweakResult.Fail(
                        $"Not enough space on C:. Need {maximumMb + 2048:N0} MB free, only {freeMb:N0} MB available.");

                // 1. Try to disable automatic management via WMI (best-effort)
                try
                {
                    using var csSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
                    foreach (ManagementObject cs in csSearcher.Get())
                    {
                        cs["AutomaticManagedPagefile"] = false;
                        cs.Put();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("MemoryService", "WMI AutomaticManagedPagefile=false failed (non-fatal)", ex);
                }

                // 2. Write custom sizes directly to the registry (primary path)
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                    writable: true);

                if (key == null)
                    return TweakResult.Fail(
                        "Cannot write to registry. Make sure Systema is running as Administrator.");

                key.SetValue(
                    "PagingFiles",
                    new[] { $@"C:\pagefile.sys {initialMb} {maximumMb}" },
                    RegistryValueKind.MultiString);

                Log.Info("MemoryService", $"Pagefile registry entry written: {initialMb}/{maximumMb} MB");
                return TweakResult.Ok(
                    $"Overflow memory set to {initialMb:N0} MB initial / {maximumMb:N0} MB max.\nA restart is required to apply the new size.");
            }
            catch (Exception ex)
            {
                Log.Error("MemoryService", "ConfigurePagefile exception", ex);
                return TweakResult.FromException(ex);
            }
        });
    }

    /// <summary>Reverts to Windows automatic pagefile management. Requires a restart.</summary>
    public async Task<TweakResult> RevertToManagedPagefileAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                // 1. Re-enable automatic management via WMI (best-effort)
                try
                {
                    using var csSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
                    foreach (ManagementObject cs in csSearcher.Get())
                    {
                        cs["AutomaticManagedPagefile"] = true;
                        cs.Put();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("MemoryService", "WMI AutomaticManagedPagefile=true failed (non-fatal)", ex);
                }

                // 2. Reset registry entry to "0 0" (system-managed marker)
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                    writable: true);

                if (key != null)
                    key.SetValue(
                        "PagingFiles",
                        new[] { @"C:\pagefile.sys 0 0" },
                        RegistryValueKind.MultiString);

                Log.Info("MemoryService", "Pagefile reverted to Windows-managed");
                return TweakResult.Ok("Overflow memory returned to Windows default.\nA restart is required to apply the change.");
            }
            catch (Exception ex)
            {
                Log.Error("MemoryService", "RevertToManagedPagefile exception", ex);
                return TweakResult.FromException(ex);
            }
        });
    }
}
