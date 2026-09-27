using System.IO;
using System.Windows.Controls;
using Systema.Core.Converters;
using Systema.Views;

namespace Systema.Tests;

/// <summary>
/// Systema Engine > Advanced timing &amp; caps (0.7.363): Settings rows under plain headings, pickers
/// that say their unit in words, and labels that match what the engine really does.
/// </summary>
[Collection(nameof(PageRenderCollection))]
public class AdvancedTimingTests
{
    [Theory]
    [InlineData(30, "seconds", "30 seconds")]
    [InlineData(1, "seconds", "1 second")]
    [InlineData(60, "seconds", "1 minute")]
    [InlineData(90, "seconds", "90 seconds")]
    [InlineData(300, "seconds", "5 minutes")]
    [InlineData(1, "minutes", "1 minute")]
    [InlineData(45, "minutes", "45 minutes")]
    [InlineData(60, "minutes", "1 hour")]
    [InlineData(120, "minutes", "2 hours")]
    [InlineData(3, "percent", "3%")]
    [InlineData(1, "apps", "1 app")]
    [InlineData(3, "apps", "3 apps")]
    public void PickerValues_ReadInWords(int value, string unit, string expected) =>
        Assert.Equal(expected, UnitLabels.Format(value, unit));

    private static string Section()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        string page = File.ReadAllText(Path.Combine(dir, "src", "Systema", "Views", "TaskSleepView.xaml"));
        int a = page.IndexOf("===== ADVANCED TIMING & CAPS =====", StringComparison.Ordinal);
        int b = page.IndexOf("===== NEVER-NAP LIST =====", a, StringComparison.Ordinal);
        Assert.True(a > 0 && b > a);
        return page[a..b];
    }

    [Fact]
    public void Section_MatchesWhatTheEngineDoes()
    {
        string s = Section();
        // Elevated processes are skipped by a permanent rule, so there's no switch that pretends otherwise.
        Assert.DoesNotContain("ElevatedProcessGuardEnabled", s);
        Assert.Contains("Text=\"Always on\"", s);
        // Minimized apps' deep-sleep wake has a picker (it had none, so deep sleep was invisible for them).
        Assert.Contains("MinimizeDeepSleepWakeIntervalMinutes", s);
        // "Regular naps" was wrong: those pickers only ever applied to minimized and hidden apps.
        Assert.DoesNotContain("Regular naps", s);
        Assert.Contains("Text=\"Minimized and hidden apps\"", s);
    }

    [Fact]
    public void Section_UsesSettingsRows_NotUppercaseLabelsAndUnitColumns()
    {
        string s = Section();
        foreach (var gone in new[] { "SectionLabel", "\"PROTECTION\"", "\"CPU CAP\"", "\"WAKE TIMING\"",
                                     "Text=\"sec\"", "Text=\"min\"", "Text=\"%\"", "Text=\"apps\"" })
            Assert.DoesNotContain(gone, s);
        Assert.Contains("Header=\"Hard CPU limit for sleeping apps\"", s);   // Ctrl+K search title
    }

    [Fact]
    public void HiddenNapDescription_DoesNotHardCodeTheDelay()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        string page = File.ReadAllText(Path.Combine(dir, "src", "Systema", "Views", "TaskSleepView.xaml"));
        Assert.DoesNotContain("fully covered for 5 minutes", page);
        Assert.DoesNotContain("under Wake Timing", page);
    }

    [Fact]
    public void Section_LoadsAndShowsValuesInWords()
    {
        PageRender.OnSta(() =>
        {
            PageRender.UseTheme();
            var page = new TaskSleepView { DataContext = FakeEngine.Sample() };
            PageRender.Host(page);
            var card = PageRender.Card(page, "Advanced timing & caps");
            var texts = PageRender.Texts(card);

            // Selected values, as the closed pickers show them
            foreach (var shown in new[] { "1%", "3%", "3 apps", "5 minutes", "5 seconds", "2 minutes", "30 minutes", "1 hour" })
                Assert.Contains(shown, texts);
            Assert.Contains("Always on", texts);
            Assert.Contains("Minimized and hidden apps", texts);
            Assert.True(PageRender.Tree(card).OfType<ComboBox>().Count() >= 10);

            if (PageRender.PreviewDir is not { } dir) return;
            foreach (var (palette, file) in new[] { ("Palette.Dark.xaml", "advanced-dark.png"), ("Palette.Light.xaml", "advanced-light.png") })
            {
                PageRender.UseTheme(palette);
                var p = new TaskSleepView { DataContext = FakeEngine.Sample() };
                PageRender.Host(p);
                PageRender.SavePng(PageRender.Card(p, "Advanced timing & caps"), Path.Combine(dir, file));
            }
        });
    }
}
