using System.Collections.Concurrent;
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
/// A Live monitor row's app icon, the way Windows Settings lists show one: the icon of the
/// process's exe, read once per app name and cached for the session.
///
/// Never blocks the UI thread. An icon that isn't cached yet is read on one background worker
/// and the row shows Windows' generic app icon until then; the monitor rebuilds its rows every
/// 2 s, so the real icon appears on the next refresh. (Opening "All processes" the first time
/// would otherwise read ~150 icons on the UI thread in one go.)
/// </summary>
public sealed class ProcessIconConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<(string Name, int Pid)> Queue = new();
    private static readonly ConcurrentDictionary<string, byte> Queued = new(StringComparer.OrdinalIgnoreCase);
    private static int _draining;

    private static readonly Lazy<ImageSource> Generic = new(() =>
        FromIcon(System.Drawing.SystemIcons.Application) ?? new DrawingImage());

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ProcessSnapshot p || string.IsNullOrEmpty(p.Name)) return Generic.Value;
        if (Cache.TryGetValue(p.Name, out var icon)) return icon;

        if (Queued.TryAdd(p.Name, 0))
        {
            Queue.Enqueue((p.Name, p.Pid));
            if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
                Task.Run(Drain);
        }
        return Generic.Value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;

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
            src.Freeze();   // built off the UI thread; frozen so the UI thread can use it
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
