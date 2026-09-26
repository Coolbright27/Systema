// ════════════════════════════════════════════════════════════════════════════
// Motion.cs  ·  The small, Windows 11-style animations shared by every page
// ════════════════════════════════════════════════════════════════════════════
//
// Attached properties, so a page opts in from XAML and the look stays in one place:
//
//   ctl:Motion.Entrance="True"      Cards fade in and rise a few pixels when a page opens, in a
//                                   quick top-to-bottom cascade. Set by the SettingsCard, Card and
//                                   ListRowCard styles, so pages get it without any edits.
//   ctl:Motion.RevealOnShow="True"  A section that appears (an expander opening) eases down into
//                                   place instead of snapping in.
//   ctl:Motion.Flipped="{Binding}"  A chevron turns to point up while its section is open.
//
// Every animation is skipped when Windows "Animation effects" is off (the same switch Systema's
// Maximum speed preset flips), and all of them are short (167-250 ms, WinUI's decelerate curve).
//
// None of them hold a value once finished (FillBehavior.Stop), so a page's own Opacity or
// transform setters (a dimmed, Auto Pilot-managed section, say) are never overridden.
// ════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Systema.Controls;

public static class Motion
{
    private static readonly IEasingFunction Decelerate = new QuinticEase { EasingMode = EasingMode.EaseOut };

    /// <summary>False when the user (or Systema's Maximum speed preset) turned animations off.</summary>
    internal static bool Enabled => SystemParameters.ClientAreaAnimation;

    // ── Entrance ────────────────────────────────────────────────────────────

    public static readonly DependencyProperty EntranceProperty = DependencyProperty.RegisterAttached(
        "Entrance", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEntranceChanged));

    public static bool GetEntrance(DependencyObject d) => (bool)d.GetValue(EntranceProperty);
    public static void SetEntrance(DependencyObject d, bool value) => d.SetValue(EntranceProperty, value);

    private static readonly DependencyProperty PlayedProperty = DependencyProperty.RegisterAttached(
        "Played", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    // When the current page opened. Entrance only plays for cards that load right after that,
    // so a list that rebuilds its rows on a refresh timer never flickers them back in.
    private static long _pageOpenedAt = Environment.TickCount64;
    private static bool _entranceAllowed = true;
    private const int EntranceWindowMs = 700;

    /// <summary>
    /// Called by MainWindow each time a page is shown. <paramref name="animate"/> is false for the
    /// first page after launch, which appears without the cascade.
    /// </summary>
    public static void PageOpened(bool animate = true)
    {
        _pageOpenedAt = Environment.TickCount64;
        _entranceAllowed = animate;
    }

    private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= PlayEntrance;
        if ((bool)e.NewValue) fe.Loaded += PlayEntrance;
    }

    private static void PlayEntrance(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if ((bool)fe.GetValue(PlayedProperty)) return;     // once per element, not on every re-load
        fe.SetValue(PlayedProperty, true);
        if (!Enabled || !_entranceAllowed || !fe.IsVisible) return;
        if (Environment.TickCount64 - _pageOpenedAt > EntranceWindowMs) return;

        try
        {
            // Only what the user can see on arrival. Cards further down just sit there.
            var page = FindAncestor<ScrollViewer>(fe);
            if (page == null || page.ViewportHeight <= 0) return;
            double y = fe.TranslatePoint(new System.Windows.Point(0, 0), page).Y;
            if (y > page.ViewportHeight || y + fe.ActualHeight < 0) return;

            // Top of the page first, bottom last: at most ~180 ms of spread.
            double delay = 30 + Math.Clamp(y / page.ViewportHeight, 0, 1) * 150;
            Play(fe, delay, rise: 10, duration: 250);
        }
        catch { /* purely cosmetic */ }
    }

    // ── Reveal on show ──────────────────────────────────────────────────────

    public static readonly DependencyProperty RevealOnShowProperty = DependencyProperty.RegisterAttached(
        "RevealOnShow", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnRevealChanged));

    public static bool GetRevealOnShow(DependencyObject d) => (bool)d.GetValue(RevealOnShowProperty);
    public static void SetRevealOnShow(DependencyObject d, bool value) => d.SetValue(RevealOnShowProperty, value);

    private static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.IsVisibleChanged -= Reveal;
        if ((bool)e.NewValue) fe.IsVisibleChanged += Reveal;
    }

    private static void Reveal(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Opening eases in. Closing stays instant: a lingering collapse just feels like lag.
        if (sender is FrameworkElement fe && e.NewValue is true && fe.IsLoaded && Enabled)
        {
            try { Play(fe, delay: 0, rise: -8, duration: 250); } catch { }
        }
    }

    // ── Flipped (expander chevron) ──────────────────────────────────────────

    public static readonly DependencyProperty FlippedProperty = DependencyProperty.RegisterAttached(
        "Flipped", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFlippedChanged));

    public static bool GetFlipped(DependencyObject d) => (bool)d.GetValue(FlippedProperty);
    public static void SetFlipped(DependencyObject d, bool value) => d.SetValue(FlippedProperty, value);

    private static void OnFlippedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        if (el.RenderTransform is not RotateTransform rotate || rotate.IsFrozen)
        {
            rotate = new RotateTransform();
            el.RenderTransform = rotate;
            el.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        }

        double to = (bool)e.NewValue ? 180 : 0;
        if (!Enabled || el is FrameworkElement { IsLoaded: false })
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            rotate.Angle = to;
            return;
        }
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(167)) { EasingFunction = Decelerate });
    }

    // ── Shared ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Fade in and slide by <paramref name="rise"/> px (positive = up from below) after
    /// <paramref name="delay"/> ms. Key frames rather than BeginTime, so the element is already
    /// hidden during the delay instead of flashing in at full opacity first.
    /// </summary>
    private static void Play(FrameworkElement fe, double delay, double rise, double duration)
    {
        var start = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay));
        var end   = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + duration));

        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, start));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, end, Decelerate));
        fe.BeginAnimation(UIElement.OpacityProperty, fade);

        // Only slide elements with no transform of their own; never replace one a page set.
        if (fe.RenderTransform == null || fe.RenderTransform == Transform.Identity)
            fe.RenderTransform = new TranslateTransform();
        if (fe.RenderTransform is TranslateTransform shift && !shift.IsFrozen)
        {
            var slide = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(rise, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(rise, start));
            slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, end, Decelerate));
            shift.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    internal static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        d = d == null ? null : VisualTreeHelper.GetParent(d);
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}

/// <summary>
/// Eased mouse-wheel scrolling for a page. WPF jumps 48 px per wheel notch with no motion,
/// which is most of why a page feels choppy next to Windows Settings. MainWindow routes the
/// page's wheel events here; nested scrollers (a list, a multi-line text box) keep their own
/// scrolling whenever they can still move in that direction.
/// </summary>
public static class SmoothScroll
{
    private const double PixelsPerNotch = 72;           // one wheel click (Delta 120)
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(250);
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    // Animated stand-in for VerticalOffset, which is read-only and can't be animated directly.
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SmoothScroll),
        new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    // Where the current animation is heading, so quick successive notches add up.
    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(double), typeof(SmoothScroll), new PropertyMetadata(double.NaN));

    /// <summary>
    /// Handles a wheel event for <paramref name="page"/>. Returns false (leave it to WPF) when
    /// animations are off or a nested scroller under the mouse should take it.
    /// </summary>
    public static bool TryHandle(ScrollViewer page, MouseWheelEventArgs e)
    {
        if (!Motion.Enabled || e.Delta == 0 || page.ScrollableHeight <= 0) return false;
        if (InnerScrollerWants(e.OriginalSource as DependencyObject, page, e.Delta)) return false;

        double target = (double)page.GetValue(TargetProperty);
        bool moving = !double.IsNaN(target);
        double from = moving ? (double)page.GetValue(OffsetProperty) : page.VerticalOffset;
        double to = Math.Clamp((moving ? target : from) - e.Delta / 120.0 * PixelsPerNotch,
                               0, page.ScrollableHeight);
        if (Math.Abs(to - from) < 0.5) return true;     // at the end already: swallow, don't bounce

        page.SetValue(TargetProperty, to);
        var anim = new DoubleAnimation(from, to, Duration) { EasingFunction = Ease };
        anim.Completed += (_, _) =>
        {
            if ((double)page.GetValue(TargetProperty) == to) page.SetValue(TargetProperty, double.NaN);
        };
        page.BeginAnimation(OffsetProperty, anim);
        return true;
    }

    /// <summary>Stops any glide in progress and puts the page back at the top.</summary>
    public static void ResetToTop(ScrollViewer page)
    {
        page.BeginAnimation(OffsetProperty, null);
        page.SetValue(TargetProperty, double.NaN);
        page.ScrollToVerticalOffset(0);
    }

    private static bool InnerScrollerWants(DependencyObject? source, ScrollViewer page, int delta)
    {
        // Runs inside text (a Run) aren't visuals; step out to the element that owns them.
        while (source != null && source is not Visual) source = LogicalTreeHelper.GetParent(source);

        for (var d = source; d != null && !ReferenceEquals(d, page); d = VisualTreeHelper.GetParent(d))
        {
            if (d is ScrollViewer inner && inner.ScrollableHeight > 0)
            {
                bool canMove = delta > 0 ? inner.VerticalOffset > 0
                                         : inner.VerticalOffset < inner.ScrollableHeight;
                if (canMove) return true;
            }
        }
        return false;
    }
}
