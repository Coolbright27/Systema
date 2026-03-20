using System.Globalization;
using System.Windows.Data;

namespace Systema.Core.Converters;

/// <summary>
/// IValueConverter:  returns true when value.ToString() == parameter.ToString()
/// IMultiValueConverter: returns true when values[0].ToString() == values[1].ToString()
/// Used by NavButton active-state DataTrigger.
/// </summary>
public class EqualityConverter : IValueConverter, IMultiValueConverter
{
    // Single: value == parameter
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    // Multi: values[0] == values[1]
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length >= 2 && values[0]?.ToString() == values[1]?.ToString();

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
