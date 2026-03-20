using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WPoint = System.Windows.Point;
using WSize  = System.Windows.Size;

namespace Systema.Core.Converters;

/// <summary>
/// Converts a 0-100 integer score to a StreamGeometry arc drawn from 12 o'clock
/// clockwise by (score/100 * 360) degrees.
/// Center (75,75), radius 65, designed for a 150x150 canvas.
/// </summary>
public class ScoreToArcConverter : IValueConverter
{
    private const double CenterX = 75;
    private const double CenterY = 75;
    private const double Radius  = 65;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int score = value is int s ? Math.Clamp(s, 0, 100) : 0;
        double angleDeg  = score / 100.0 * 360.0;
        double startAngle = -90.0;
        double endAngle   = startAngle + angleDeg;

        var startPt = Pt(startAngle);
        var endPt   = Pt(endAngle);
        bool largeArc = angleDeg > 180;

        var geo = new StreamGeometry();
        using var ctx = geo.Open();

        if (score <= 0)
        {
            ctx.BeginFigure(startPt, false, false);
        }
        else if (score >= 100)
        {
            // Full circle: ArcSegment can't do exactly 360° so split into two halves
            var mid = Pt(startAngle + 180);
            ctx.BeginFigure(startPt, false, false);
            ctx.ArcTo(mid,     new WSize(Radius, Radius), 0, false, SweepDirection.Clockwise, true, false);
            ctx.ArcTo(startPt, new WSize(Radius, Radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        else
        {
            ctx.BeginFigure(startPt, false, false);
            ctx.ArcTo(endPt, new WSize(Radius, Radius), 0, largeArc, SweepDirection.Clockwise, true, false);
        }

        geo.Freeze();
        return geo;
    }

    private static WPoint Pt(double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new WPoint(CenterX + Radius * Math.Cos(rad), CenterY + Radius * Math.Sin(rad));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
