using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Systema.Models;

namespace Systema.Core.Converters;

/// <summary>
/// An app's icon for a Systema Engine list row, the way Windows Settings lists show one: the icon
/// of the program's exe, read once per app name and cached for the session. Rows it serves:
/// <list type="bullet">
/// <item>Live monitor (<see cref="ProcessSnapshot"/>) and the never-nap picker
/// (<see cref="RunningApp"/>): read on one background worker so the UI thread never waits. The row
/// shows Windows' generic app icon until then; the Live monitor rebuilds its rows every 2 s and the
/// picker rebuilds on <see cref="IconsLoaded"/>, so the real icon appears moments later.</item>
/// <item>Never-nap list (a plain name string): read right away if the app is running. The list is a
/// handful of names and its rows aren't rebuilt, so a background read would never be seen.</item>
/// </list>
/// </summary>
public sealed class ProcessIconConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<(string Name, int Pid)> Queue = new();
    private static readonly ConcurrentDictionary<string, byte> Queued = new(StringComparer.OrdinalIgnoreCase);
    private static int _draining;

    private static readonly Lazy<ImageSource> Generic = new(() =>
        FromIcon(System.Drawing.SystemIcons.Application) ?? new DrawingImage());

    /// <summary>Raised (on a worker thread) when a batch of background icon reads finishes.</summary>
    public static event Action? IconsLoaded;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ProcessSnapshot p => Async(p.Name, p.Pid),
        RunningApp a      => Async(a.Name, a.Pid),
        string name       => ByName(name),
        _                 => Generic.Value,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static ImageSource Async(string name, int pid)
    {
        if (string.IsNullOrEmpty(name)) return Generic.Value;
        if (Cache.TryGetValue(name, out var icon)) return icon;

        if (Queued.TryAdd(name, 0))
        {
            Queue.Enqueue((name, pid));
            if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
                Task.Run(Drain);
        }
        return Generic.Value;
    }

    private static ImageSource ByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Generic.Value;
        if (Cache.TryGetValue(name, out var icon)) return icon;

        // Only a success is cached: an app that isn't running yet can get its icon next time.
        int pid = PidFor(name);
        if (pid > 0 && Load(pid) is { } loaded)
            return Cache[name] = loaded;
        return Generic.Value;
    }

    private static void Drain()
    {
        try
        {
            while (Queue.TryDequeue(out var job))
                Cache[job.Name] = Load(job.Pid) ?? Generic.Value;   // a failure caches the generic icon too
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
            // Something queued between the last dequeue and the flag reset: pick it up.
            if (!Queue.IsEmpty && Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
                Task.Run(Drain);
            else
                try { IconsLoaded?.Invoke(); } catch { }
        }
    }

    // Name → pid, rebuilt at most every 5 s (one process list for a whole never-nap list, not one per row).
    private static readonly object PidLock = new();
    private static Dictionary<string, int> _pids = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _pidsAt;

    private static int PidFor(string name)
    {
        lock (PidLock)
        {
            if ((DateTime.UtcNow - _pidsAt).TotalSeconds > 5)
            {
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    try { map.TryAdd(p.ProcessName, p.Id); } catch { }
                    finally { p.Dispose(); }
                }
                _pids = map;
                _pidsAt = DateTime.UtcNow;
            }
            return _pids.TryGetValue(name, out int pid) ? pid : 0;
        }
    }

    private static ImageSource? Load(int pid)
    {
        try
        {
            string? path = ImagePath(pid);
            if (string.IsNullOrEmpty(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            return icon == null ? null : FromIcon(icon);
        }
        catch { return null; }
    }

    private static ImageSource? FromIcon(System.Drawing.Icon icon)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                                                          BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();   // may be built off the UI thread; frozen so the UI thread can use it
            return src;
        }
        catch { return null; }
    }

    private static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
