using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Systema.ViewModels;

namespace Systema.Views;

public partial class MainWindow : Window
{
    // ── DWM rounded corners (Windows 11) ─────────────────────────────────────
    // AllowsTransparency was removed to stop layered-window mode from disabling
    // MPO / Independent Flip and breaking driver-level VSync on NVIDIA. On Win11
    // we restore the rounded-corner look via DWMWA_WINDOW_CORNER_PREFERENCE, which
    // is a system-drawn cosmetic hint — it does NOT change the window's HWND
    // style, never triggers layered-window composition, and is a no-op on Win10.
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private enum DWM_WINDOW_CORNER_PREFERENCE
    {
        DWMWCP_DEFAULT    = 0,
        DWMWCP_DONOTROUND = 1,
        DWMWCP_ROUND      = 2,
        DWMWCP_ROUNDSMALL = 3,
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // ── Maximize work-area clamp ─────────────────────────────────────────────
    // A WindowStyle=None window maximizes to the full monitor by default, which
    // covers the taskbar and clips ~7px off each edge. Handling WM_GETMINMAXINFO
    // pins the maximized rect to the monitor's WORK AREA (excludes the taskbar)
    // so nothing is hidden or cut off.
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ClampToWorkArea();
    }

    /// <summary>
    /// Width/Height (in XAML) are device-independent units, so the effective logical
    /// screen SHRINKS as display scaling rises — a 1920x1200 panel at 150% is only
    /// 1280x800 DIPs. The fixed window (1280x820) would then be larger than the whole
    /// usable screen and cover it. Clamp the size to the work area (minus a small margin)
    /// so the non-resizable window always fits, at any scaling. WindowStartupLocation
    /// =CenterScreen then re-centers it using the clamped size.
    /// </summary>
    private void ClampToWorkArea()
    {
        try
        {
            var work = SystemParameters.WorkArea;   // DIPs, already excludes the taskbar
            // On a small logical screen (e.g. a high-DPI laptop at 150%, where the work
            // area is only ~1280x744 DIPs) the fixed size fills the whole screen. Cap the
            // window to 85% of the work area so it stays comfortably windowed with margins.
            // Big screens keep the full intended size because 85% of their work area
            // exceeds it (the Min() picks the smaller value).
            const double fit = 0.85;
            Width  = Math.Min(Width,  work.Width  * fit);
            Height = Math.Min(Height, work.Height * fit);
        }
        catch { /* fall back to the XAML size */ }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Best-effort rounded corners on Win11. Silently ignored on Win10 / older DWM.
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int pref = (int)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc);
        }
        catch { /* not supported on this Windows build — leave square */ }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            try
            {
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref mi))
                    {
                        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                        RECT work = mi.rcWork, mon = mi.rcMonitor;
                        mmi.ptMaxPosition.x = work.left - mon.left;
                        mmi.ptMaxPosition.y = work.top - mon.top;
                        mmi.ptMaxSize.x     = work.right - work.left;
                        mmi.ptMaxSize.y     = work.bottom - work.top;
                        Marshal.StructureToPtr(mmi, lParam, true);
                        handled = true;
                    }
                }
            }
            catch { /* fall back to default maximize behaviour */ }
        }
        return IntPtr.Zero;
    }

    // Title bar drag. The window is fixed-size (ResizeMode=NoResize), so there is no
    // double-click-to-maximize — a click-drag just moves the window.
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    // Minimize → hide to tray (Ghost Mode is handled by App.xaml.cs via Closed event)
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        Hide(); // collapses to tray; App.xaml.cs re-shows on tray icon double-click
        (DataContext as MainViewModel)?.SetTrayOnly(true);
        (Application.Current as App)?.NotifyWindowHidden();
    }

    // Close button → hide to tray, not exit (use tray "Exit" to fully quit)
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        (DataContext as MainViewModel)?.SetTrayOnly(true);
        (Application.Current as App)?.NotifyWindowHidden();
    }

    private void Window_Activated(object sender, EventArgs e)
    {
        (DataContext as MainViewModel)?.SetTrayOnly(false);
        (DataContext as MainViewModel)?.SetFocused(true);
    }

    private void Window_Deactivated(object sender, EventArgs e)
        => (DataContext as MainViewModel)?.SetFocused(false);

    // ── Title-bar action buttons ─────────────────────────────────────────────
    // Both open in the user's default browser. UseShellExecute=true is required
    // for http(s) URLs; without it Process.Start interprets the URL as a literal
    // file path and throws Win32 error 2 ("file not found").
    private void DownloadButton_Click(object sender, RoutedEventArgs e)
        => OpenExternal("https://github.com/Coolbright27/Systema/releases");

    private void DiscordButton_Click(object sender, RoutedEventArgs e)
        => OpenExternal("https://discord.gg/yYhM7mdupH");

    private static void OpenExternal(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Services.LoggerService.Instance.Warn("MainWindow",
                $"Could not open external URL '{url}': {ex.Message}");
        }
    }
}
