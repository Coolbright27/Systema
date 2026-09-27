using System.Globalization;
using System.Windows.Data;

namespace Systema.Core.Converters;

/// <summary>What kind of thing an entry in the engine's recent-activity list is.</summary>
public enum ActivityKind { Nap, Wake, Boost, Protect, Enforce, Error, Other }

/// <summary>
/// Sorts the engine's activity names ("Minimize Nap", "Deep Wake", "Launch Boost"...) into a few
/// kinds, so the Live monitor can give each kind one Windows icon instead of a coloured pill per
/// name. First match wins; a new nap or wake name is picked up without touching the view.
/// </summary>
public static class ActivityKinds
{
    public static ActivityKind Of(string? action)
    {
        string a = action ?? "";
        if (a == "Error")                                               return ActivityKind.Error;
        if (a is "Launch Boost" or "Boost ended")                       return ActivityKind.Boost;
        if (a == "Auto-whitelisted")                                    return ActivityKind.Protect;
        if (a == "Re-enforced")                                         return ActivityKind.Enforce;
        if (a.Contains("nap", StringComparison.OrdinalIgnoreCase))      return ActivityKind.Nap;     // Minimize Nap, Re-napping, Napping…
        if (a.Contains("Wake", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("Woke", StringComparison.OrdinalIgnoreCase) ||
            a == "Restored")                                            return ActivityKind.Wake;    // Brief Wake, Deep Wake, Woke up…
        return ActivityKind.Other;
    }
}

/// <summary>Binds an activity's Action text to its <see cref="ActivityKind"/> (for DataTriggers).</summary>
public sealed class ActivityKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ActivityKinds.Of(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
