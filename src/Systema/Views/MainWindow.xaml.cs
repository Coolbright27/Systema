using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Systema.Core;
using Systema.ViewModels;
using WpfControls = System.Windows.Controls;
using Media = System.Windows.Media;

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

    // ── Mica (Windows 11 22H2+) ──────────────────────────────────────────────
    // The wallpaper-tinted backdrop the Settings app uses. DWM draws it, and only while the window
    // is active: unfocused, or with Transparency effects / Energy saver on, DWM paints the plain
    // #202020 base instead, which is the colour this window used before.
    //
    // The user asked for real Mica on 2026-09-26 and waived the VSync rule for it. It is a DWM
    // system backdrop, NOT a layered window: AllowsTransparency stays banned.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;   // dark Mica, not the light one
    private const int DWMWA_SYSTEMBACKDROP_TYPE     = 38;   // Windows 11 22H2 (build 22621) and later
    private const int DWMSBT_MAINWINDOW             = 2;    // Mica
    private static readonly bool MicaCapable = Environment.OSVersion.Version.Build >= 22621;

    // With the glass extended over the whole window, DWM paints Windows' own caption buttons in
    // it, and its close X showed through under ours as a doubled X (0.7.351). WS_SYSMENU is what
    // makes DWM draw them, so it's removed; our own X, Alt+F4 and the taskbar's Close still work.
    private const int  GWL_STYLE        = -16;
    private const int  WS_SYSMENU       = 0x00080000;
    private const uint SWP_NOSIZE       = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
                       SWP_NOACTIVATE   = 0x0010, SWP_FRAMECHANGED = 0x0020;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);


    public bool IsMicaOn { get; private set; }

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
        PrepareMica();
        ThemeManager.Changed += OnThemeChanged;   // both live as long as the app
        ApplyWindowIcon();
        // Pages are built once and reused; the cache needs to see this window's DataTemplates.
        ((PageViewCache)Resources["PageViews"]).Owner = this;
        DataContext = viewModel;
        ClampToWorkArea();

        // Nav selection indicator. Re-placed without animation whenever the list itself
        // changes size (the Intel, NVIDIA and Dell entries appear after detection finishes),
        // and slid into place when the user moves between pages.
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => UpdateNavIndicator(animate: false);
        NavList.SizeChanged += (_, _) => UpdateNavIndicator(animate: false);

        // Page motion (Controls/Motion.cs): cards cascade in when a page opens, and the page
        // scrolls with an eased glide instead of WPF's 48 px jumps. The first page after launch
        // skips the cascade: that's when the app is busiest, and it only adds work to startup.
        Systema.Controls.Motion.PageOpened(animate: false);
        PageHost.TargetUpdated += OnPageChanged;
        PageHost.PreviewMouseWheel += PageHost_PreviewMouseWheel;

        // The hitch monitor only measures while the window is actually on screen.
        IsVisibleChanged += (_, _) => UpdateLagMonitorVisibility();
        StateChanged     += (_, _) => UpdateLagMonitorVisibility();
        ContentRendered  += (_, _) =>
        {
            try
            {
                double sinceStart = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                Systema.Services.LoggerService.Instance.Info("MainWindow", $"First frame on screen {sinceStart:F0} ms after launch");
            }
            catch { }
        };
    }

    private void UpdateLagMonitorVisibility() =>
        Systema.Core.UiLagMonitor.WindowVisible = IsVisible && WindowState != WindowState.Minimized;

    private bool _firstPageShown;

    private void OnPageChanged(object? sender, System.Windows.Data.DataTransferEventArgs e)
    {
        Systema.Controls.Motion.PageOpened(animate: _firstPageShown);
        _firstPageShown = true;
        Systema.Core.UiLagMonitor.CurrentPage = (DataContext as MainViewModel)?.ActiveSection;

        // Pages are reused now (PageViewCache), so one you scrolled down on last time would
        // otherwise reopen halfway down. Start every visit at the top, as before.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                var page = FindPageScroller(PageHost);
                if (page != null) Systema.Controls.SmoothScroll.ResetToTop(page);
            }
            catch { /* cosmetic */ }
        }));
    }

    // ── Smooth page scrolling ────────────────────────────────────────────────
    private void PageHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        try
        {
            var page = FindPageScroller(PageHost);
            if (page != null && Systema.Controls.SmoothScroll.TryHandle(page, e)) e.Handled = true;
        }
        catch { /* fall back to WPF's own scrolling */ }
    }

    /// <summary>The page's own ScrollViewer: the first one under the page host, breadth-first.</summary>
    private static WpfControls.ScrollViewer? FindPageScroller(DependencyObject root)
    {
        var queue = new System.Collections.Generic.Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (d is WpfControls.ScrollViewer sv && !ReferenceEquals(d, root)) return sv;
            int n = Media.VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) queue.Enqueue(Media.VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    // ── Nav selection indicator ──────────────────────────────────────────────
    // Windows 11's NavigationView marks the current page with one accent pill that slides
    // between items, rather than a bar drawn inside each button. This is that pill. It is
    // purely cosmetic: if anything goes wrong it hides itself, and the active item's subtle
    // background (set by the NavButton style) still shows which page is open.
    private bool _navIndicatorPlaced;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ActiveSection)) return;
        // Wait for layout so the target button's position is final before measuring it.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => UpdateNavIndicator(animate: true)));
    }

    private void UpdateNavIndicator(bool animate)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;

            System.Windows.Controls.Button? active = null;
            foreach (var child in NavList.Children)
            {
                if (child is System.Windows.Controls.Button b && b.Visibility == Visibility.Visible &&
                    b.Tag is string tag && string.Equals(tag, vm.ActiveSection, StringComparison.Ordinal))
                {
                    active = b;
                    break;
                }
            }

            if (active == null || active.ActualHeight <= 0)
            {
                NavIndicator.Opacity = 0;
                _navIndicatorPlaced = false;
                return;
            }

            double y = active.TranslatePoint(new System.Windows.Point(0, 0), NavCanvas).Y
                     + (active.ActualHeight - NavIndicator.Height) / 2;

            // First placement, a layout change, or Windows animations turned off: jump there.
            if (!animate || !_navIndicatorPlaced || !SystemParameters.ClientAreaAnimation)
            {
                NavIndicatorShift.BeginAnimation(TranslateTransform.YProperty, null);
                NavIndicatorShift.Y = y;
                NavIndicator.Opacity = 1;
                _navIndicatorPlaced = true;
                return;
            }

            // 250 ms decelerate, WinUI's ControlNormalAnimationDuration.
            var slide = new DoubleAnimation(y, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
            };
            NavIndicatorShift.BeginAnimation(TranslateTransform.YProperty, slide);
        }
        catch
        {
            NavIndicator.Opacity = 0;
            _navIndicatorPlaced = false;
        }
    }

    /// <summary>
    /// Sets the taskbar / Alt-Tab icon explicitly from the logo embedded in this assembly.
    /// Without this, WPF leaves the window iconless and the shell supplies one from its
    /// per-path icon CACHE. An in-place update keeps the same exe path, so the shell never
    /// invalidates that cache and the window keeps showing the PREVIOUS logo even though the
    /// new icon is correctly compiled into the exe. Loading the bytes we actually ship takes
    /// the shell cache out of the picture. Purely cosmetic — failure just falls back.
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string? res = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("logo.ico", StringComparison.OrdinalIgnoreCase));
            if (res == null) return;

            using var stream = asm.GetManifestResourceStream(res);
            if (stream == null) return;

            // OnLoad so the frames survive the stream being disposed.
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

            // Largest frame: WPF downscales it cleanly for whatever the shell asks for,
            // which keeps it crisp on high-DPI displays.
            System.Windows.Media.Imaging.BitmapFrame? best = null;
            foreach (var frame in decoder.Frames)
                if (best == null || frame.PixelWidth > best.PixelWidth) best = frame;

            if (best != null) Icon = best;

            // Title bar logo: the smallest frame that still covers 16 px at this display's
            // scale (16 at 100%, 24 at 150%, 32 at 200%...). Scaling a 256 px frame all the way
            // down to 16 blurs it; starting from the nearest size keeps it crisp.
            double scale = Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            int needed = (int)Math.Ceiling(16 * scale);
            System.Windows.Media.Imaging.BitmapFrame? small = null;
            foreach (var frame in decoder.Frames)
                if (frame.PixelWidth >= needed && (small == null || frame.PixelWidth < small.PixelWidth))
                    small = frame;
            small ??= best;
            if (small != null)
            {
                TitleLogo.Source = small;
                TitleLogoFallback.Visibility = Visibility.Collapsed;
            }
        }
        catch { /* cosmetic only — leave the default icon (and the accent square) in place */ }
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
            // If Systema started in the tray, this window didn't exist to hear a theme change.
            ThemeManager.Refresh();
            ApplyMica(hwnd);
        }
        catch { /* not supported on this Windows build — leave square */ }
    }

    /// <summary>
    /// Before the window exists: give it a frame (DWM only draws a system backdrop behind a
    /// framed window; WindowChrome still hides that frame) and extend the glass over the whole
    /// window so the backdrop can reach every pixel.
    /// </summary>
    private void PrepareMica()
    {
        if (!MicaCapable) return;
        WindowStyle = WindowStyle.SingleBorderWindow;
        SetGlassFrame(-1);
    }

    private void SetGlassFrame(double thickness)
    {
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is not { } current) return;
        var chrome = (System.Windows.Shell.WindowChrome)current.Clone();
        chrome.GlassFrameThickness = new Thickness(thickness);
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);
    }

    /// <summary>
    /// Turns Mica on, then clears the window's own base colour so it shows through. Nothing is
    /// made see-through unless DWM accepted the backdrop, so a build without Mica keeps the
    /// solid look instead of turning black.
    /// </summary>
    private void ApplyMica(IntPtr hwnd)
    {
        if (!MicaCapable) return;
        var log = Services.LoggerService.Instance;
        try
        {
            // Dark or light Mica, to match the theme ThemeManager picked (it follows Windows).
            int dark = ThemeManager.IsLight ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int backdrop = DWMSBT_MAINWINDOW;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            if (hr != 0)
            {
                SetGlassFrame(0);
                log.Warn("MainWindow", $"Mica not available (DWM returned 0x{hr:X8}), keeping the solid background");
                return;
            }

            HideNativeCaptionButtons(hwnd);

            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                target.BackgroundColor = Media.Colors.Transparent;
            Background                = Media.Brushes.Transparent;
            RootSurface.Background    = Media.Brushes.Transparent;
            TitleBarSurface.Background = Media.Brushes.Transparent;
            SidebarSurface.Background = Media.Brushes.Transparent;
            // The Settings app's card layer for this theme (5% white on dark, 70% white on light);
            // cards pick it up through DynamicResource. OnThemeChanged swaps it with the theme.
            Resources["CardLayerBrush"] = TryFindResource("CardOverMicaBrush");
            IsMicaOn = true;
            log.Info("MainWindow", "Mica backdrop on");
        }
        catch (Exception ex)
        {
            log.Warn("MainWindow", $"Mica setup failed, keeping the solid background: {ex.Message}");
        }
    }

    private static void HideNativeCaptionButtons(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_STYLE);
        if ((style & WS_SYSMENU) == 0) return;
        SetWindowLong(hwnd, GWL_STYLE, style & ~WS_SYSMENU);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private const int WM_STYLECHANGING = 0x007C;

    // Windows broadcasts these when the user changes light/dark mode or the accent colour.
    // Top-level windows get them even while hidden, so the theme follows along in the tray too.
    private const int WM_SETTINGCHANGE              = 0x001A;
    private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    /// <summary>After a live theme change: Mica's light/dark variant and the card layer over it.</summary>
    private void OnThemeChanged()
    {
        if (!IsMicaOn) return;   // without Mica every surface is a DynamicResource already
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = ThemeManager.IsLight ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            Resources["CardLayerBrush"] = TryFindResource("CardOverMicaBrush");
        }
        catch { /* cosmetic */ }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            || msg == WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            // Let Windows finish writing the new values before reading them.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ThemeManager.Refresh));
        }

        // Keep the native caption buttons away if anything (WPF included) later rewrites the
        // window style. STYLESTRUCT is { styleOld, styleNew }.
        if (msg == WM_STYLECHANGING && IsMicaOn && wParam.ToInt64() == GWL_STYLE)
        {
            int styleNew = Marshal.ReadInt32(lParam, 4);
            if ((styleNew & WS_SYSMENU) != 0)
                Marshal.WriteInt32(lParam, 4, styleNew & ~WS_SYSMENU);
        }

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

    /// <summary>
    /// Set by <see cref="App.ExplicitShutdown"/> so the one genuine close (tray → Exit) is let
    /// through. Every other close request is turned into a hide.
    /// </summary>
    public bool AllowClose { get; set; }

    // The separate Minimize button was removed: it called Hide() + SetTrayOnly(true), i.e.
    // exactly what Close does, so the title bar offered two controls that did the same thing.
    // Close button → hide to tray, not exit (use tray "Exit" to fully quit).
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Turns every close into a hide-to-tray, so the window object survives.
    ///
    /// The title bar's X used to call Hide() directly, which meant it was the ONLY close path
    /// that behaved. Alt+F4, the taskbar's "Close window", and the system menu all go straight
    /// to Close() and really did destroy the window — and a closed WPF Window can never be shown
    /// again. App kept its reference, so the next "open Systema" (tray double-click, Windows
    /// Search, a second launch) called Show() on a dead window and threw
    /// "Cannot set Visibility or call Show ... after a Window has closed". A user hit exactly
    /// that on 0.7.279. Routing every close through here means there is one behaviour, not two.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            (DataContext as MainViewModel)?.SetTrayOnly(true);
            (Application.Current as App)?.NotifyWindowHidden();
            return;
        }
        base.OnClosing(e);
    }

    private void Window_Activated(object sender, EventArgs e)
    {
        (DataContext as MainViewModel)?.SetTrayOnly(false);
        (DataContext as MainViewModel)?.SetFocused(true);
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        (DataContext as MainViewModel)?.SetFocused(false);
        // Clicking away from Systema dismisses the search palette, as a flyout would.
        if (IsSearchOpen) CloseSearch();
    }

    // ── Search (Ctrl+K) ──────────────────────────────────────────────────────
    // The palette searches Core/SettingsSearchIndex. Picking a page just navigates; picking a
    // setting navigates and then outlines that setting's row for a moment so the eye lands on
    // it. Everything past navigation is cosmetic and wrapped so it can never throw into the UI.

    private bool IsSearchOpen => SearchOverlay.Visibility == Visibility.Visible;

    private void SearchBox_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (IsSearchOpen) CloseSearch(); else OpenSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && IsSearchOpen)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    private void OpenSearch()
    {
        SearchInput.Text = "";
        RunSearch();
        SearchOverlay.Visibility = Visibility.Visible;

        if (SystemParameters.ClientAreaAnimation)
        {
            SearchOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(167)));
            SearchPanelShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
                });
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SearchInput.Focus();
            Keyboard.Focus(SearchInput);
        }));
    }

    private void CloseSearch()
    {
        SearchOverlay.BeginAnimation(OpacityProperty, null);
        SearchOverlay.Visibility = Visibility.Collapsed;
    }

    private static bool IsSectionVisible(MainViewModel vm, string section) => section switch
    {
        "Intel"  => vm.IsIntelGpuPresent,
        "Nvidia" => vm.IsNvidiaGpuPresent,
        "Dell"   => vm.IsDellPresent,
        _        => true,
    };

    private void RunSearch()
    {
        if (DataContext is not MainViewModel vm) return;
        var hits = SettingsSearchIndex.Search(SearchInput.Text, s => IsSectionVisible(vm, s));
        SearchResults.ItemsSource = hits.Select(h => new SearchResultItem(
            h.Title, h.IsPage ? "Page" : SettingsSearchIndex.SectionNames[h.Section], h)).ToList();
        SearchResults.SelectedIndex = hits.Count > 0 ? 0 : -1;
        SearchEmpty.Visibility       = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Visibility = SearchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchInput_TextChanged(object sender, WpfControls.TextChangedEventArgs e) => RunSearch();

    private void SearchInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        int count = SearchResults.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                SearchResults.SelectedIndex = (SearchResults.SelectedIndex + 1) % count;
                SearchResults.ScrollIntoView(SearchResults.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                SearchResults.SelectedIndex = (SearchResults.SelectedIndex - 1 + count) % count;
                SearchResults.ScrollIntoView(SearchResults.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                ChooseResult(SearchResults.SelectedItem as SearchResultItem);
                e.Handled = true;
                break;
            case Key.Tab:
                e.Handled = true;   // keep focus in the palette
                break;
        }
    }

    private void SearchResults_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Only a click on a result row opens it, not a click on the list's scrollbar.
        for (var d = e.OriginalSource as DependencyObject; d != null && d != SearchResults; d = Media.VisualTreeHelper.GetParent(d))
        {
            if (d is WpfControls.ListBoxItem item)
            {
                ChooseResult(item.DataContext as SearchResultItem);
                return;
            }
        }
    }

    private void SearchOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only the dimmed area closes the palette; clicks inside the panel bubble here too.
        if (ReferenceEquals(e.OriginalSource, SearchOverlay)) CloseSearch();
    }

    private void ChooseResult(SearchResultItem? item)
    {
        if (item == null || DataContext is not MainViewModel vm) return;
        CloseSearch();
        vm.NavigateCommand.Execute(item.Entry.Section);
        if (!item.Entry.IsPage)
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => HighlightSetting(item.Entry.Title, attempt: 0)));
    }

    /// <summary>
    /// Finds the setting's title on the page that just opened, scrolls it into view and outlines
    /// its row for a moment. Heavy pages can take a beat to build, so it retries a few times.
    /// A setting inside a collapsed section simply isn't outlined; the page still opened.
    /// </summary>
    private void HighlightSetting(string title, int attempt)
    {
        try
        {
            var text = FindVisibleText(PageHost, title);
            if (text == null)
            {
                if (attempt >= 4) return;
                var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                retry.Tick += (_, _) => { retry.Stop(); HighlightSetting(title, attempt + 1); };
                retry.Start();
                return;
            }

            var row = RowFor(text);
            row.BringIntoView(new Rect(0, -60, row.ActualWidth, row.ActualHeight + 120));

            var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(row);
            if (layer == null) return;
            var accent = TryFindResource("AccentBlueBrush") as Media.SolidColorBrush;
            var outline = new SearchHighlightAdorner(row, accent?.Color ?? Media.Color.FromRgb(0x60, 0xCD, 0xFF));
            layer.Add(outline);

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700))
            {
                BeginTime = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 900 : 1400)
            };
            fade.Completed += (_, _) => { try { layer.Remove(outline); } catch { } };
            outline.BeginAnimation(OpacityProperty, fade);
        }
        catch { /* cosmetic: the page has already opened */ }
    }

    private static WpfControls.TextBlock? FindVisibleText(DependencyObject root, string text)
    {
        int n = Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = Media.VisualTreeHelper.GetChild(root, i);
            // Tag="NotSearchable" marks a label that repeats a setting's name for display only
            // (Home's header shows "Auto Pilot" above the Auto Pilot card), so a jump skips it.
            if (child is WpfControls.TextBlock tb && tb.IsVisible &&
                string.Equals(tb.Text, text, StringComparison.Ordinal) &&
                !Equals(tb.Tag, "NotSearchable"))
                return tb;
            var deeper = FindVisibleText(child, text);
            if (deeper != null) return deeper;
        }
        return null;
    }

    /// <summary>
    /// The setting's row: settings are laid out as a Grid with the text in one column and the
    /// control in another, so the nearest multi-column Grid is the row. Never climbs past the
    /// card, which would outline a whole group instead of one setting.
    /// </summary>
    private FrameworkElement RowFor(WpfControls.TextBlock text)
    {
        var card = TryFindResource("Card");
        DependencyObject? cur = text;
        for (int i = 0; i < 6 && cur != null; i++)
        {
            cur = Media.VisualTreeHelper.GetParent(cur);
            // Reached the page itself: the title is a heading above a group ("I want…",
            // "All startup apps"), not a row. Outline just the heading, never the ScrollViewer's
            // own two-column template Grid, which is the whole page.
            if (cur is WpfControls.ScrollContentPresenter) return text;
            if (cur is WpfControls.Grid g && g.ColumnDefinitions.Count >= 2) return g;
            // Reached the card the title sits in (Card, or a style built on it like Home's
            // cards): outline that card, never climb past it into the page's column layout.
            if (cur is WpfControls.Border b && card != null && IsCardStyle(b.Style, card)) return b;
        }
        return (Media.VisualTreeHelper.GetParent(text) as FrameworkElement) ?? text;
    }

    private static bool IsCardStyle(Style? style, object card)
    {
        for (var s = style; s != null; s = s.BasedOn)
            if (ReferenceEquals(s, card)) return true;
        return false;
    }

    /// <summary>Accent outline drawn over a row, never taking input or changing layout.</summary>
    private sealed class SearchHighlightAdorner : System.Windows.Documents.Adorner
    {
        private readonly Media.Pen _pen;
        private readonly Media.Brush _fill;

        public SearchHighlightAdorner(UIElement adorned, Media.Color accent) : base(adorned)
        {
            IsHitTestVisible = false;
            var stroke = new Media.SolidColorBrush(accent);
            stroke.Freeze();
            _pen = new Media.Pen(stroke, 2);
            _pen.Freeze();
            var fill = new Media.SolidColorBrush(Media.Color.FromArgb(0x1F, accent.R, accent.G, accent.B));
            fill.Freeze();
            _fill = fill;
        }

        protected override void OnRender(Media.DrawingContext dc)
        {
            var r = new Rect(AdornedElement.RenderSize);
            r.Inflate(8, 5);
            dc.DrawRoundedRectangle(_fill, _pen, r, 6, 6);
        }
    }

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

/// <summary>One row in the Ctrl+K palette. Public so WPF bindings can read it.</summary>
public sealed record SearchResultItem(string Title, string Where, Systema.Core.SearchEntry Entry);
