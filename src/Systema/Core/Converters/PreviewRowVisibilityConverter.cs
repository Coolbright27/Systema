using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Systema.Core.Converters;

/// <summary>
/// Shows only the first <see cref="Limit"/> rows of a list until it's expanded, like the "Show
/// more" lists in Windows Settings. Rows are hidden rather than dropped, so the list stays bound
/// to the live collection: a new entry is the only row created (and the only one that fades in).
///
/// Values: [0] the row's item, [1] the whole collection, [2] expanded (bool),
/// [3] the collection's Count (only there so the rows re-check when items come and go).
/// </summary>
public sealed class PreviewRowVisibilityConverter : IMultiValueConverter
{
    public int Limit { get; set; } = 10;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return Visibility.Visible;
        if (values[2] is true) return Visibility.Visible;
        if (values[1] is not IList list) return Visibility.Visible;
        int index = list.IndexOf(values[0]);
        return index >= 0 && index < Limit ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
