using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Systema.Core.Converters;

/// <summary>
/// Converts a 0-100 score integer to a color brush:
/// 0-20 = Red, 21-89 = Yellow, 90-100 = Green
/// </summary>
public class ScoreToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush Red    = new(Color.FromRgb(0xFF, 0x45, 0x60));
    private static readonly SolidColorBrush Yellow = new(Color.FromRgb(0xFF, 0xB8, 0x00));
    private static readonly SolidColorBrush Green  = new(Color.FromRgb(0x00, 0xE5, 0xA0));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int score)
        {
            if (score <= 20) return Red;
            if (score >= 90) return Green;
            return Yellow;
        }
        return Yellow;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
