using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Systema.Core.Converters;

namespace Systema.Tests;

/// <summary>
/// Loads a real Systema page off-screen so a test can check what it renders, and (with
/// SYSTEMA_RENDER_PREVIEW=&lt;folder&gt;) save a PNG of a section to look at, without ever showing a
/// window. Tests that use it share <see cref="PageRenderCollection"/>: the theme lives in the
/// process-wide Application resources, so two of them must never run at once.
/// </summary>
internal static class PageRender
{
    public static string? PreviewDir => Environment.GetEnvironmentVariable("SYSTEMA_RENDER_PREVIEW") is { Length: > 0 } d ? d : null;

    /// <summary>Runs WPF work on an STA thread and rethrows its failure here.</summary>
    public static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>
    /// Puts the app's theme at application scope, as App.xaml does. StaticResource lookups run while a
    /// page is being built, before it has a parent to search, so it can't live on a host element.
    /// </summary>
    public static void UseTheme(string palette = "Palette.Dark.xaml")
    {
        if (Application.Current == null) _ = new Application();   // also registers pack://application
        var theme = new ResourceDictionary
        {
            MergedDictionaries =
            {
                new ResourceDictionary { Source = new Uri($"pack://application:,,,/Systema;component/Resources/Themes/{palette}") },
                new ResourceDictionary { Source = new Uri("pack://application:,,,/Systema;component/Resources/Themes/Dark.xaml") },
            },
        };
        theme["GlobalBoolToVis"]   = new BoolToVisibilityConverter();
        theme["GlobalSafetyColor"] = new SafetyLevelToColorConverter();
        theme["GlobalSafetyBadge"] = new SafetyLevelToBadgeConverter();
        theme["GlobalEquality"]    = new EqualityConverter();
        Application.Current!.Resources = theme;
    }

    /// <summary>Lays a page out on the window colour at a desktop width.</summary>
    public static FrameworkElement Host(FrameworkElement page)
    {
        var root = new Border { Child = page, Padding = new Thickness(24) };
        root.SetResourceReference(Border.BackgroundProperty, "BgPrimaryBrush");
        root.Measure(new Size(1000, 8000));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        return root;
    }

    public static IEnumerable<DependencyObject> Tree(DependencyObject d)
    {
        yield return d;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            foreach (var c in Tree(VisualTreeHelper.GetChild(d, i))) yield return c;
    }

    public static List<string> Texts(DependencyObject d) => Tree(d).OfType<TextBlock>().Select(t => t.Text).ToList();

    public static IEnumerable<DependencyObject> Ancestors(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p)) yield return p;
    }

    /// <summary>The settings expander card whose header reads <paramref name="title"/>.</summary>
    public static FrameworkElement Card(FrameworkElement page, string title)
    {
        var header = Tree(page).OfType<TextBlock>().First(t => t.Text == title);
        var cardStyle = (Style)page.FindResource("ExpanderCard");
        return Ancestors(header).OfType<Border>().First(b => b.Style == cardStyle);
    }

    /// <summary>Saves an element as a PNG, painted over the window colour as it appears in the app.</summary>
    public static void SavePng(FrameworkElement element, string path)
    {
        var bounds = element.TransformToAncestor((Visual)Ancestors(element).Last()).TransformBounds(new Rect(element.RenderSize));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)element.FindResource("BgPrimaryBrush"), null, new Rect(0, 0, bounds.Width + 32, bounds.Height + 32));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(16, 16, bounds.Width, bounds.Height));
        }
        var bmp = new RenderTargetBitmap((int)bounds.Width + 32, (int)bounds.Height + 32, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        png.Save(fs);
    }
}

[CollectionDefinition(nameof(PageRenderCollection), DisableParallelization = true)]
public sealed class PageRenderCollection { }
