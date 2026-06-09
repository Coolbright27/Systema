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

            // 3-second guard — WMI can hang indefinitely on some machines
            if (task.Wait(TimeSpan.FromSeconds(3)))
                return task.Result;

            Log.Warn("MemoryService", "GetCurrentPagefileUsageMb WMI query timed out after 3 s — returning (0,0)");
            return (0, 0);
        }
        catch (Exception ex)
        {
            Log.Error("MemoryService", "GetCurrentPagefileUsageMb failed", ex);
        }
        return (0, 0);
    }

    /// <summary>Returns the recommended pagefile size in MB based on installed RAM.</summary>
    public int GetRecommendedPagefileMb() => GetRecommendedPagefileMb(GetTotalRamMb());

    /// <summary>
    /// Returns the recommended pagefile size in MB and the detected RAM in MB so
    /// callers can display both values together without a second P/Invoke call.
    /// </summary>
    public (int recommendedMb, long ramMb) GetRecommendedPagefileWithRam()
    {
        long ramMb = GetTotalRamMb();
        return (GetRecommendedPagefileMb(ramMb), ramMb);
    }

    private static int GetRecommendedPagefileMb(long ramMb)
    {
        // Tiered pagefile recommendations based on installed RAM.
        // Upper bounds are generous (+5 %) to absorb BIOS/reporting variance so a
        // nominally-16 GB system (which may report anywhere from 15.5–16.4 GB) always
        // lands in the correct tier.
        if (ramMb < 9000)                        // < 8 GB  → 1.5× RAM, floor 4 GB
        {
            int fallback = (int)Math.Min(ramMb * 1.5, 32768);
            return Math.Max(fallback, 4096);
        }
        if (ramMb < 17500)   return 32768;       //  8–16 GB  → 32 GB pagefile
        if (ramMb < 27500)   return 24576;       // 16–24 GB  → 24 GB pagefile
        return 16384;                            // 24 GB+    → 16 GB pagefile
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

    // ── VSync-critical processes (NEVER EmptyWorkingSet these) ────────────────
    // Trimming these processes' working sets forces them to page memory from disk
    // the next time they run, which causes multi-second latency spikes. For
    // dwm.exe specifically, this means the compositor thread misses its 60/144 Hz
    // presentation deadlines and NVIDIA MPO / Independent Flip falls back to
    // composed mode — hard tearing on every window including the foreground game.
    // svchost is excluded because Windows hosts DWM helpers, Themes, UxSms,
    // AudioSrv, and other presentation-critical services inside svchost.exe.
    // nvcontainer / nvdisplay.container host NVIDIA's user-mode GPU scheduler.
    private static readonly HashSet<string> VsyncCriticalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm",                    // Desktop Window Manager — THE VSync killer when trimmed
        "audiodg",                // Audio Device Graph Isolation — feeds MMCSS boost to DWM
        "svchost",                // hosts DWM helpers, Themes, UxSms, AudioSrv, etc.
        "nvcontainer",            // NVIDIA user-mode GPU scheduler / telemetry
        "nvdisplay.container",    // NVIDIA display container
        "nvwmi64",                // NVIDIA WMI
        "RadeonSoftware",         // AMD driver user-mode
        "amdow",                  // AMD overlay
        "atieclxx", "atiesrxx",   // AMD kernel-mode helpers
        "igfxEM", "igfxCUIService", // Intel GPU helpers
        "csrss",                  // Client/Server Runtime — critical session manager
        "services",               // SCM
        "lsass",                  // credential manager — memory corruption risk if trimmed
        "winlogon",               // session / DWM parent
        "wininit", "smss",        // early-boot session managers
        "System", "Registry", "Idle", "Secure System", "Memory Compression",
    };

    /// <summary>
    /// Flushes process working sets and purges the standby memory list.
    /// Returns (freedMb, message). Runs on caller's thread — wrap in Task.Run.
    ///
    /// VSYNC WARNING: dwm.exe, audiodg.exe, svchost.exe, and GPU vendor user-mode
    /// processes are EXCLUDED. Trimming dwm's working set forces the compositor to
    /// page from disk and breaks NVIDIA MPO / Independent Flip for every window on
    /// the desktop — causing hard tearing in foreground games until DWM's pages
    /// fault back in. Never remove the exclusion list below without understanding
    /// the consequences; see VsyncCriticalProcessNames for the full list.
    /// </summary>
    public (long freedMb, string message) FreeRam()
    {
        var (_, beforeMb) = GetRamStats();

        // 1. EmptyWorkingSet on every accessible process EXCEPT VSync-critical ones
        int trimmed = 0, skipped = 0;
        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                // Skip DWM, audiodg, svchost, GPU driver user-mode, session-critical
                if (VsyncCriticalProcessNames.Contains(proc.ProcessName))
                {
                    skipped++;
                    continue;
                }

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

        Log.Info("MemoryService", $"FreeRam: trimmed {trimmed} processes (skipped {skipped} VSync-critical), freed ~{freed} MB");
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

                // Registry-only path. The earlier WMI approach
                // (Win32_ComputerSystem.AutomaticManagedPagefile = false followed
                // by cs.Put()) regularly returned "Generic failure" on Win11 23H2+
                // and could block the Put() call for tens of seconds while DCOM
                // negotiated with the WMI service — long enough to look like the
                // UI had frozen. Both the system-managed flag AND the static
                // sizes are mirrored as plain registry values under Memory
                // Management, so we just write those directly and skip WMI.
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                    writable: true);

                if (key == null)
                    return TweakResult.Fail(
                        "Cannot write to registry. Make sure Systema is running as Administrator.");

                // 1. Turn off "automatically manage paging file size for all drives".
                //    Registry-backed twin of the WMI AutomaticManagedPagefile flag.
                key.SetValue("AutomaticManagedPagefile", 0, RegistryValueKind.DWord);

                // 2. Write the static size.
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
                // Registry-only path (same rationale as ConfigurePagefileAsync —
                // skip WMI to avoid the "Generic failure" stalls).
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                    writable: true);

                if (key != null)
                {
                    // Turn automatic management back on, and reset the size marker.
                    key.SetValue("AutomaticManagedPagefile", 1, RegistryValueKind.DWord);
                    key.SetValue(
                        "PagingFiles",
                        new[] { @"?:\pagefile.sys" },
                        RegistryValueKind.MultiString);
                }

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
