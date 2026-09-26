// ════════════════════════════════════════════════════════════════════════════
// OverlayScroll.cs  ·  Scroll bars that get out of the way, like Windows 11
// ════════════════════════════════════════════════════════════════════════════
//
// Windows 11 Settings only shows a scroll bar while it's useful: it appears when you scroll or
// move the mouse over the scrollable area, and fades out a moment after you stop. Pointing at the
// bar keeps it (and widens it, see the ScrollBar style in Dark.xaml).
//
// Turned on for every ScrollViewer by the implicit style in Dark.xaml. A ScrollViewer with nothing
// to scroll has no visible bar, so this costs it nothing but a couple of cheap event checks.
//
// Respects Windows' own choice: Accessibility > Visual effects > "Always show scrollbars" writes
// DynamicScrollbars = 0, and then the bars stay put. With animations off (ClientAreaAnimation),
// they still hide, just without the fade.
// ════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Systema.Controls;

public static class OverlayScroll
{
    public static readonly DependencyProperty AutoHideProperty = DependencyProperty.RegisterAttached(
        "AutoHide", typeof(bool), typeof(OverlayScroll), new PropertyMetadata(false, OnAutoHideChanged));

    public static bool GetAutoHide(DependencyObject o) => (bool)o.GetValue(AutoHideProperty);
    public static void SetAutoHide(DependencyObject o, bool value) => o.SetValue(AutoHideProperty, value);

    private static readonly DependencyProperty FaderProperty = DependencyProperty.RegisterAttached(
        "Fader", typeof(Fader), typeof(OverlayScroll), new PropertyMetadata(null));

    /// <summary>False when the user turned on "Always show scrollbars" in Windows.</summary>
    internal static readonly bool WindowsHidesScrollBars = ReadWindowsHidesScrollBars();

    internal static bool ReadWindowsHidesScrollBars()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Accessibility");
            return key?.GetValue("DynamicScrollbars") is not int v || v != 0;   // absent = Windows default = hide
        }
        catch { return true; }
    }

    private static void OnAutoHideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv || e.NewValue is not true || !WindowsHidesScrollBars) return;
        if (sv.GetValue(FaderProperty) == null) sv.SetValue(FaderProperty, new Fader(sv));
    }

    private sealed class Fader
    {
        private static readonly TimeSpan Linger = TimeSpan.FromSeconds(1.5);

        private readonly ScrollViewer _sv;
        private DispatcherTimer? _timer;
        private DateTime _lastActivity;
        private bool _shown;

        public Fader(ScrollViewer sv)
        {
            _sv = sv;
            sv.Loaded += (_, _) => { _shown = false; Apply(0, animate: false); };
            sv.Unloaded += (_, _) => _timer?.Stop();
            sv.MouseMove += (_, _) => Show();
            sv.ScrollChanged += (_, e) =>
            {
                // Only real scrolling, and only this viewer's own (ScrollChanged bubbles up from
                // text boxes and lists inside it).
                if (ReferenceEquals(e.OriginalSource, sv) && (e.VerticalChange != 0 || e.HorizontalChange != 0))
                    Show();
            };
        }

        private WpfScrollBar? Bar(string part) => _sv.Template?.FindName(part, _sv) as WpfScrollBar;

        private void Show()
        {
            _lastActivity = DateTime.UtcNow;
            if (_shown) return;

            var v = Bar("PART_VerticalScrollBar");
            var h = Bar("PART_HorizontalScrollBar");
            if (v?.Visibility != Visibility.Visible && h?.Visibility != Visibility.Visible) return;

            _shown = true;
            Apply(1, animate: true);
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background, _sv.Dispatcher)
                    { Interval = TimeSpan.FromMilliseconds(250) };
                _timer.Tick += (_, _) => Tick();
            }
            _timer.Start();
        }

        private void Tick()
        {
            if (DateTime.UtcNow - _lastActivity < Linger) return;

            // Pointing at a bar, or dragging its thumb (the thumb holds mouse capture, so the bar
            // still counts as under the mouse), keeps it on screen.
            if (Bar("PART_VerticalScrollBar")?.IsMouseOver == true ||
                Bar("PART_HorizontalScrollBar")?.IsMouseOver == true)
            {
                _lastActivity = DateTime.UtcNow;
                return;
            }

            _timer!.Stop();
            _shown = false;
            Apply(0, animate: true);
        }

        private void Apply(double opacity, bool animate)
        {
            var ms = animate && SystemParameters.ClientAreaAnimation ? (opacity > 0 ? 100 : 300) : 0;
            var anim = new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(ms));
            anim.Freeze();
            Bar("PART_VerticalScrollBar")?.BeginAnimation(UIElement.OpacityProperty, anim);
            Bar("PART_HorizontalScrollBar")?.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }
}
