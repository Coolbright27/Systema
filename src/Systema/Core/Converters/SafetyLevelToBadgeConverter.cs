using System.Globalization;
using System.Windows.Data;

namespace Systema.Core.Converters;

public class SafetyLevelToBadgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SafetyLevel level)
        {
            return level switch
            {
                SafetyLevel.Safe => "SAFE",
                SafetyLevel.ModeratelySafe => "MODERATE",
                SafetyLevel.Advanced => "ADVANCED",
                _ => "UNKNOWN"
            };
        }
        return "UNKNOWN";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
