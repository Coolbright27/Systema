using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Windows 11 style scroll bars (0.7.350). The old bars looked chunky because WPF's built-in theme
/// gives every ScrollBar a 17px minimum width, which beat our Width="8". They also took a column
/// of their own, so the sidebar and pages reflowed whenever one appeared.
/// </summary>
public class ScrollBarStyleTests
{
    private static string Read(params string[] parts)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(dir, Path.Combine(parts));
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    private static string Dark() =>
        Regex.Replace(Read("src", "Systema", "Resources", "Themes", "Dark.xaml"), "<!--.*?-->", "", RegexOptions.Singleline);

    private static string Block(string xaml, string start, string end)
    {
        int a = xaml.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, $"missing: {start}");
        int b = xaml.IndexOf(end, a, StringComparison.Ordinal);
        return xaml[a..b];
    }

    [Fact]
    public void ScrollBars_DropTheThemes17pxMinimum()
    {
        var bar = Block(Dark(), "<Style TargetType=\"ScrollBar\">", "<Style TargetType=\"ScrollViewer\">");
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"0\"/>", bar);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"0\"/>", bar);
        Assert.Contains("<Setter Property=\"Width\" Value=\"12\"/>", bar);

        // Idle it's a thin line; pointing at it widens the thumb.
        Assert.Contains("Width=\"2\" HorizontalAlignment=\"Center\"", bar);
        Assert.Contains("Storyboard.TargetProperty=\"Width\"", bar);
        Assert.Contains("ScrollBar.LineUpCommand", bar);
        Assert.Contains("ScrollBar.PageDownCommand", bar);
    }

    [Fact]
    public void ScrollViewers_FloatTheirBarsOverTheContent()
    {
        var sv = Block(Dark(), "<Style TargetType=\"ScrollViewer\">", "</Style>");
        Assert.Contains("ctl:OverlayScroll.AutoHide\" Value=\"True\"", sv);

        // WPF's own part names and bindings, so every list, text box and dropdown still scrolls.
        foreach (var part in new[] { "PART_ScrollContentPresenter", "PART_VerticalScrollBar", "PART_HorizontalScrollBar" })
            Assert.Contains($"x:Name=\"{part}\"", sv);
        Assert.Contains("CanContentScroll=\"{TemplateBinding CanContentScroll}\"", sv);
        Assert.Contains("ComputedVerticalScrollBarVisibility", sv);
        Assert.Contains("Binding VerticalOffset, Mode=OneWay", sv);

        // Overlay: one cell, no column or row set aside for the bars.
        Assert.DoesNotContain("ColumnDefinition", sv);
        Assert.DoesNotContain("RowDefinition", sv);
    }

    [Fact]
    public void AutoHide_FollowsWindowsAlwaysShowScrollbarsSetting()
    {
        var src = Read("src", "Systema", "Controls", "OverlayScroll.cs");
        Assert.Contains(@"Control Panel\Accessibility", src);
        Assert.Contains("\"DynamicScrollbars\"", src);
        Assert.Contains("SystemParameters.ClientAreaAnimation", src);
        // Only real scrolling of this viewer shows its bar, not a text box scrolling inside it.
        Assert.Contains("ReferenceEquals(e.OriginalSource, sv)", src);
    }

    [Fact]
    public void Sidebar_OnlyShowsABarWhenItOverflows()
    {
        var main = Read("src", "Systema", "Views", "MainWindow.xaml");
        int nav = main.IndexOf("x:Name=\"NavList\"", StringComparison.Ordinal);
        int sv = main.LastIndexOf("<ScrollViewer", nav, StringComparison.Ordinal);
        Assert.True(sv > 0 && nav > sv);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", main[sv..nav]);

        // The nav items keep a gutter so the floating bar never covers them.
        Assert.Contains("x:Name=\"NavList\" Margin=\"8,12,14,0\"", main);
    }

    [Fact]
    public void Pages_KeepTheirRightMarginClearOfTheBar()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema", "Views"))) dir = Directory.GetParent(dir)!.FullName;
        foreach (var f in Directory.GetFiles(Path.Combine(dir, "src", "Systema", "Views"), "*View.xaml"))
        {
            var x = File.ReadAllText(f);
            int s = x.IndexOf("<ScrollViewer", StringComparison.Ordinal);
            if (s < 0) continue;
            var m = Regex.Match(x[s..], "Margin=\"\\d+,\\d+,(\\d+),\\d+\"");
            Assert.True(m.Success, $"{Path.GetFileName(f)}: no content margin under its ScrollViewer");
            Assert.True(int.Parse(m.Groups[1].Value) >= 14,
                        $"{Path.GetFileName(f)}: right margin {m.Groups[1].Value} puts content under the scroll bar");
        }
    }
}
