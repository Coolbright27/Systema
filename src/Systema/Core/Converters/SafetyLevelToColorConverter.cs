using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Systema.Core.Converters;

public class SafetyLevelToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SafetyLevel level)
        {
            return level switch
            {
                SafetyLevel.Safe => new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xA0)),
                SafetyLevel.ModeratelySafe => new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x00)),
                SafetyLevel.Advanced => new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x60)),
                _ => Brushes.Gray
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
