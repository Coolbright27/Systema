using System.Globalization;
using System.Windows.Data;

namespace Systema.Core.Converters;

/// <summary>
/// Picker values in words, the way Windows Settings writes them ("30 seconds", "5 minutes",
/// "1 hour", "3%", "2 apps"), so a dropdown needs no separate unit label beside it.
/// </summary>
public static class UnitLabels
{
    /// <param name="unit">"seconds", "minutes", "percent" or "apps".</param>
    public static string Format(int value, string? unit) => unit switch
    {
        "seconds" => Seconds(value),
        "minutes" => Minutes(value),
        "percent" => $"{value}%",
        "apps"    => value == 1 ? "1 app" : $"{value} apps",
        _         => value.ToString(CultureInfo.CurrentCulture),
    };

    private static string Seconds(int s) =>
        s >= 60 && s % 60 == 0 ? Minutes(s / 60) : Plural(s, "second");

    private static string Minutes(int m) =>
        m >= 60 && m % 60 == 0 ? Plural(m / 60, "hour") : Plural(m, "minute");

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}

/// <summary>Binds an int picker value to <see cref="UnitLabels.Format"/>; the unit is the ConverterParameter.</summary>
public sealed class UnitLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i ? UnitLabels.Format(i, parameter as string) : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
