// ════════════════════════════════════════════════════════════════════════════
// NtProcessSampler.cs  ·  Single-call kernel CPU sampler for all processes
// ════════════════════════════════════════════════════════════════════════════
//
// Replaces the Parallel.ForEach + per-process Process.TotalProcessorTime loop
// used in earlier Task Sleep builds. One NtQuerySystemInformation call returns
// CreateTime / KernelTime / UserTime for every process in the system — typically
// under ~5 ms vs the 200-900 ms the old path took on a busy box.
//
// Scope:
//   • Windows 11 (x64) only — matches the app's supported runtime
//   • Stable SYSTEM_PROCESS_INFORMATION offsets (Win10 1809+ / Win11)
//   • Auto-growing unmanaged buffer (initial 512 KB, retries on LENGTH_MISMATCH)
//   • Per-PID prev sample is keyed by CreationTime100ns so PID reuse is rejected
//
// Output: List<Sample> with Pid, CreationTime100ns, CpuPercent, ImageName,
//          ThreadCount. CpuPercent is already normalized over logical CPU count.
//
// Related: TaskSleepService consumes this via SampleAllProcessCpu.
// ════════════════════════════════════════════════════════════════════════════

using System.Runtime.InteropServices;
using Systema.Core;

namespace Systema.Services.TaskSleep;

internal sealed class NtProcessSampler : IDisposable
{
    private static readonly LoggerService _log = LoggerService.Instance;

    // ── ntdll.dll P/Invoke ────────────────────────────────────────────────────
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int SystemInformationClass,
        IntPtr SystemInformation,
        uint SystemInformationLength,
        out uint ReturnLength);

    private const int SystemProcessInformation    = 5;
    private const int STATUS_SUCCESS              = 0;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    // ── SYSTEM_PROCESS_INFORMATION stable x64 offsets ─────────────────────────
    //   These have been stable since Windows 10 1809 and remain correct on Win11.
    //   Documented via Geoff Chappell's NT reference and ReactOS ntoskrnl headers.
    private const int OFF_NextEntry   = 0;    // ULONG NextEntryOffset
    private const int OFF_Threads     = 4;    // ULONG NumberOfThreads
    private const int OFF_CreateTime  = 32;   // LARGE_INTEGER CreateTime (100ns ticks since 1601)
    private const int OFF_UserTime    = 40;   // LARGE_INTEGER UserTime
    private const int OFF_KernelTime  = 48;   // LARGE_INTEGER KernelTime
    private const int OFF_NameLength  = 56;   // USHORT ImageName.Length
    private const int OFF_NameBuffer  = 64;   // PWSTR  ImageName.Buffer
    private const int OFF_UniquePid   = 80;   // HANDLE UniqueProcessId

    // ── State ─────────────────────────────────────────────────────────────────
    private IntPtr _buf     = IntPtr.Zero;
    private uint   _bufSize = 0;

    private struct Prev
    {
        public long CreationTime;  // 100ns ticks — PID reuse guard
        public long TotalCpuTime;  // user + kernel in 100ns ticks
        public long WallTicks;     // DateTime.UtcNow.Ticks at sample time
    }
    private readonly Dictionary<int, Prev> _prev = new();
    private readonly int _cpuCount = Math.Max(1, Environment.ProcessorCount);

    // ── Public surface ────────────────────────────────────────────────────────

    /// <summary>One per-process entry returned by <see cref="Sample"/>.</summary>
    public readonly struct ProcessSample
    {
        public readonly int     Pid;
        public readonly long    CreationTime100ns;
        public readonly double  CpuPercent;
        public readonly string? ImageName;       // file name only (no path), may be null for System (4)
        public readonly int     ThreadCount;

        public ProcessSample(int pid, long creation, double cpu, string? name, int threads)
        {
            Pid = pid;
            CreationTime100ns = creation;
            CpuPercent = cpu;
            ImageName = name;
            ThreadCount = threads;
        }
    }

    /// <summary>
    /// Sample every process currently in the system. First call returns 0 %
    /// across the board (no baseline yet); subsequent calls return the %CPU since
    /// the previous sample. Resilient to PID reuse via CreateTime comparison.
    /// </summary>
    public List<ProcessSample> Sample()
    {
        var results = new List<ProcessSample>(512);
        if (!EnsureBufferAndQuery()) return results;

        long nowTicks = DateTime.UtcNow.Ticks;
        IntPtr p = _buf;
        int safety = 0;

        while (true)
        {
            if (safety++ > 20000)
            {
                _log.Warn("NtProcessSampler", "Safety cap hit while walking SYSTEM_PROCESS_INFORMATION chain");
                break;
            }

            int  nextOff    = Marshal.ReadInt32(p, OFF_NextEntry);
            int  threads    = Marshal.ReadInt32(p, OFF_Threads);
            long createTime = Marshal.ReadInt64(p, OFF_CreateTime);
            long userTime   = Marshal.ReadInt64(p, OFF_UserTime);
            long kernelTime = Marshal.ReadInt64(p, OFF_KernelTime);
            IntPtr pidPtr   = Marshal.ReadIntPtr(p, OFF_UniquePid);
            int  pid        = pidPtr.ToInt32();

            // Read UNICODE_STRING image name (Length is byte count, not char count)
            short   nameLen = Marshal.ReadInt16(p, OFF_NameLength);
            IntPtr  nameBuf = Marshal.ReadIntPtr(p, OFF_NameBuffer);
            string? name    = null;
            if (nameBuf != IntPtr.Zero && nameLen > 0)
            {
                try { name = Marshal.PtrToStringUni(nameBuf, nameLen / 2); }
                catch { name = null; }
            }

            long totalCpu = userTime + kernelTime;
            double pct = 0.0;

            if (pid > 0)
            {
                if (_prev.TryGetValue(pid, out var prev) && prev.CreationTime == createTime)
                {
                    long dCpu  = totalCpu - prev.TotalCpuTime;
                    long dWall = nowTicks - prev.WallTicks;
                    if (dWall > 0 && dCpu >= 0)
                    {
                        pct = (dCpu * 100.0) / (dWall * (double)_cpuCount);
                        if (pct < 0)   pct = 0;
                        if (pct > 100) pct = 100;
                    }
                }
                _prev[pid] = new Prev
                {
                    CreationTime = createTime,
                    TotalCpuTime = totalCpu,
                    WallTicks    = nowTicks
                };

                results.Add(new ProcessSample(pid, createTime, pct, name, threads));
            }

            if (nextOff == 0) break;
            p = IntPtr.Add(p, nextOff);
        }

        return results;
    }

    /// <summary>Drop cached baseline for a single PID (e.g. after process exit).</summary>
    public void Forget(int pid) => _prev.Remove(pid);

    /// <summary>Drop baselines for any PID not in the provided live set.</summary>
    public void Prune(HashSet<int> livePids)
    {
        if (_prev.Count == 0) return;
        List<int>? toRemove = null;
        foreach (var kv in _prev)
        {
            if (!livePids.Contains(kv.Key))
            {
                toRemove ??= new List<int>();
                toRemove.Add(kv.Key);
            }
        }
        if (toRemove != null)
            foreach (int k in toRemove) _prev.Remove(k);
    }

    // ── Buffer management ─────────────────────────────────────────────────────

    private bool EnsureBufferAndQuery()
    {
        if (_buf == IntPtr.Zero)
        {
            _bufSize = 512 * 1024;
            _buf     = Marshal.AllocHGlobal((int)_bufSize);
        }

        // Up to 6 retries — on a 2000-process machine the buffer can balloon fast
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int status = NtQuerySystemInformation(
                SystemProcessInformation, _buf, _bufSize, out uint ret);

            if (status == STATUS_SUCCESS) return true;

            if (status == STATUS_INFO_LENGTH_MISMATCH)
            {
                uint newSize = ret > 0 ? ret + 64 * 1024 : _bufSize * 2;
                try
                {
                    Marshal.FreeHGlobal(_buf);
                    _buf     = Marshal.AllocHGlobal((int)newSize);
                    _bufSize = newSize;
                }
                catch (Exception ex)
                {
                    _log.Warn("NtProcessSampler", $"Buffer grow failed at {newSize} bytes: {ex.Message}");
                    _buf = IntPtr.Zero;
                    _bufSize = 0;
                    return false;
                }
                continue;
            }

            _log.Warn("NtProcessSampler", $"NtQuerySystemInformation returned 0x{status:X8}");
            return false;
        }

        _log.Warn("NtProcessSampler", "Buffer kept under-sized after 6 retries");
        return false;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_buf != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buf);
            _buf = IntPtr.Zero;
            _bufSize = 0;
        }
        _prev.Clear();
    }
}
