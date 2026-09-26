// ════════════════════════════════════════════════════════════════════════════
// UsageGraph.cs  ·  Task Manager-style usage graph
// ════════════════════════════════════════════════════════════════════════════
//
// The memory graph from Task Manager's Performance tab: a thin frame, a faint grid, and the last
// minute of usage as a translucent fill under a crisp line, newest sample on the right edge.
// The scale is fixed at 0 to 100% (like Task Manager), so the height means something, instead
// of the old sparkline that stretched every wiggle to fill its box.
//
// Drawn in OnRender at whatever size the layout gives it, so it stays sharp at any width or DPI.
// Values are fractions (0..1), oldest first; give it a new array and it redraws.
// ════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Media;

namespace Systema.Controls;

public sealed class UsageGraph : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(UsageGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(UsageGraph),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(System.Windows.Media.Brush), typeof(UsageGraph),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(System.Windows.Media.Brush), typeof(UsageGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(System.Windows.Media.Brush), typeof(UsageGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameBrushProperty = DependencyProperty.Register(
        nameof(FrameBrush), typeof(System.Windows.Media.Brush), typeof(UsageGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Usage samples as fractions of the maximum (0..1), oldest first.</summary>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>How many samples span the full width (60 = one minute at one sample a second).</summary>
    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public System.Windows.Media.Brush? LineBrush  { get => (System.Windows.Media.Brush?)GetValue(LineBrushProperty);  set => SetValue(LineBrushProperty, value); }
    public System.Windows.Media.Brush? FillBrush  { get => (System.Windows.Media.Brush?)GetValue(FillBrushProperty);  set => SetValue(FillBrushProperty, value); }
    public System.Windows.Media.Brush? GridBrush  { get => (System.Windows.Media.Brush?)GetValue(GridBrushProperty);  set => SetValue(GridBrushProperty, value); }
    public System.Windows.Media.Brush? FrameBrush { get => (System.Windows.Media.Brush?)GetValue(FrameBrushProperty); set => SetValue(FrameBrushProperty, value); }

    public UsageGraph()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        // Half-pixel offsets keep 1 px lines crisp instead of smeared across two pixels.
        const double half = 0.5;

        // Grid: 10 columns x 5 rows, very faint, like Task Manager.
        if (GridBrush != null)
        {
            var gridPen = new System.Windows.Media.Pen(GridBrush, 1);
            gridPen.Freeze();
            for (int i = 1; i < 10; i++)
            {
                double x = Math.Round(w * i / 10) + half;
                dc.DrawLine(gridPen, new System.Windows.Point(x, 0), new System.Windows.Point(x, h));
            }
            for (int i = 1; i < 5; i++)
            {
                double y = Math.Round(h * i / 5) + half;
                dc.DrawLine(gridPen, new System.Windows.Point(0, y), new System.Windows.Point(w, y));
            }
        }

        var values = Values;
        if (values != null && values.Count >= 2)
        {
            int cap = Math.Max(2, Capacity);
            double step = w / (cap - 1);
            int count = Math.Min(values.Count, cap);
            int first = values.Count - count;

            // Newest sample sits on the right edge; older ones step left.
            System.Windows.Point At(int i) =>
                new(w - (count - 1 - i) * step,
                    h - Math.Clamp(values[first + i], 0, 1) * (h - 1));

            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                var start = At(0);
                g.BeginFigure(new System.Windows.Point(start.X, h), true, true);
                for (int i = 0; i < count; i++) g.LineTo(At(i), true, false);
                g.LineTo(new System.Windows.Point(w, h), true, false);
            }
            area.Freeze();
            if (FillBrush != null) dc.DrawGeometry(FillBrush, null, area);

            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(At(0), false, false);
                for (int i = 1; i < count; i++) g.LineTo(At(i), true, true);
            }
            line.Freeze();
            var linePen = new System.Windows.Media.Pen(LineBrush, 1.5) { LineJoin = PenLineJoin.Round };
            linePen.Freeze();
            dc.DrawGeometry(null, linePen, line);
        }

        if (FrameBrush != null)
        {
            var framePen = new System.Windows.Media.Pen(FrameBrush, 1);
            framePen.Freeze();
            dc.DrawRectangle(null, framePen, new Rect(half, half, w - 1, h - 1));
        }
    }
}
