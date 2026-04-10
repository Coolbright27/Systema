using System.Globalization;
using System.Windows.Data;

namespace Systema.Core.Converters;

/// <summary>
/// Two-way converter for binding an int property to a group of RadioButtons.
/// Convert:     returns true when (int)value == (int)parameter
/// ConvertBack: returns (int)parameter when the RadioButton is checked (true),
///              otherwise Binding.DoNothing so unchecked buttons don't overwrite the value.
/// Usage: IsChecked="{Binding Level, Converter={StaticResource IntEqualConverter}, ConverterParameter=2}"
/// </summary>
public class IntEqualConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intVal && int.TryParse(parameter?.ToString(), out int param))
            return intVal == param;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && int.TryParse(parameter?.ToString(), out int param))
            return param;
        return Binding.DoNothing;
    }
}
