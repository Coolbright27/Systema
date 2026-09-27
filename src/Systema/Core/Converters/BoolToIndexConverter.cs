using System.Globalization;
using System.Windows.Data;

namespace Systema.Core.Converters;

/// <summary>
/// Two-way bool ↔ ComboBox.SelectedIndex for a two-choice filter: false is item 0, true is item 1.
/// Usage: SelectedIndex="{Binding ShowAllProcesses, Converter={StaticResource BoolToIndex}, Mode=TwoWay}"
/// </summary>
public sealed class BoolToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? 1 : 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? i == 1 : Binding.DoNothing;
}
