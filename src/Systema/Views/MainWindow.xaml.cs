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

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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
        }
        catch { /* not supported on this Windows build — leave square */ }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    // Minimize → hide to tray (Ghost Mode is handled by App.xaml.cs via Closed event)
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        Hide(); // collapses to tray; App.xaml.cs re-shows on tray icon double-click
        (DataContext as MainViewModel)?.SetTrayOnly(true);
        (Application.Current as App)?.NotifyWindowHidden();
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

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
}
