// ════════════════════════════════════════════════════════════════════════════
// PageViewCache.cs  ·  Build each page once, then reuse it
// ════════════════════════════════════════════════════════════════════════════
//
// The page host used to hand each page view-model to its DataTemplate, and WPF built a brand-new
// view every single time you clicked a page: every card, template and binding, hundreds of
// elements for the bigger pages. That construction ran on the UI thread right as the page
// animated in, so switching pages hitched.
//
// This converter sits on the page host's binding instead. The first visit builds the view from
// the same DataTemplate (MainWindow.xaml keeps them all) and keeps it; later visits hand back the
// same instance, so switching pages is just swapping what's on screen. Pages keep binding to their
// view-model exactly as before; nothing about them changes.
// ════════════════════════════════════════════════════════════════════════════

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Systema.Views;

public sealed class PageViewCache : IValueConverter
{
    private readonly Dictionary<object, FrameworkElement> _views = new(ReferenceEqualityComparer.Instance);

    /// <summary>Where the page DataTemplates live (the main window). Set before the first page shows.</summary>
    public FrameworkElement? Owner { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null) return null;
        if (_views.TryGetValue(value, out var cached)) return cached;

        // No template found: hand the view-model back and let WPF template it the old way.
        if (Owner?.TryFindResource(new DataTemplateKey(value.GetType())) is not DataTemplate template) return value;
        if (template.LoadContent() is not FrameworkElement view) return value;

        view.DataContext = value;
        _views[value] = view;
        return view;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
