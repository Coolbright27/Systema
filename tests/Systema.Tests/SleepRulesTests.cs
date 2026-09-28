using System.IO;
using System.Windows.Controls;
using Systema.Models;
using Systema.Views;

namespace Systema.Tests;

/// <summary>
/// Systema Engine > Sleep rules (0.7.364): Settings rows under plain headings, with descriptions
/// that give the engine's real timings. Each number the text quotes is pinned to the value the
/// engine uses, so changing one without the other breaks the build.
/// </summary>
[Collection(nameof(PageRenderCollection))]
public class SleepRulesTests
{
    private static string Src(params string[] parts)
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        return File.ReadAllText(Path.Combine(new[] { dir, "src", "Systema" }.Concat(parts).ToArray()));
    }

    private static string Section()
    {
        string page = Src("Views", "TaskSleepView.xaml");
        int a = page.IndexOf("===== SLEEP RULES =====", StringComparison.Ordinal);
        int b = page.IndexOf("===== ADVANCED TIMING & CAPS =====", a, StringComparison.Ordinal);
        Assert.True(a > 0 && b > a);
        return page[a..b];
    }

    [Fact]
    public void TheTimingsInTheText_AreTheEnginesTimings()
    {
        string s = Section(), engine = Src("Services", "TaskSleepService.cs"), vm = Src("ViewModels", "TaskSleepViewModel.cs");
        var d = new TaskSleepSettings();

        // "after 30 seconds" (minimized and tray)
        Assert.Contains("MinimizeTrayGraceMs   = 30_000", engine);
        Assert.Contains("minimize goes to sleep after 30 seconds", s);
        Assert.Contains("notification area, go to sleep after 30 seconds", s);

        // "for 3 minutes" (unused)
        Assert.Equal(180_000, d.BackgroundNapAfterMs);
        Assert.Contains("ReadInt(key, \"BackgroundNapAfterMs\",         180_000)", vm);
        Assert.Contains("haven't used for 3 minutes", s);

        // "under 0.5%" for "2 minutes" (idle)
        Assert.Equal(120_000, d.IdleNapAfterMs);
        Assert.Contains("ReadInt(key, \"IdleNapAfterMs\",               120_000)", vm);
        Assert.Contains("IdleNapCpuThreshold                = 0.5,", vm);
        Assert.Contains("(under 0.5%) for 2 minutes", s);

        // "more than 20%" (busy)
        Assert.Equal(20, d.BusyMinimizedCpuThresholdPercent);
        Assert.Contains("ReadInt(key, \"BusyMinimizedCpuThresholdPercent\", 20)", vm);
        Assert.Contains("more than 20% of your CPU", s);
    }

    // The switch used to control only the re-trims: every app was still trimmed as it napped.
    [Fact]
    public void CompressMemorySwitch_ControlsAllTrimming()
    {
        string vm = Src("ViewModels", "TaskSleepViewModel.cs");
        Assert.Contains("TrimWorkingSet          = CompressDeepSleep,", vm);
        Assert.DoesNotContain("TrimWorkingSet          = true,", vm);
        Assert.Contains("if (s.TrimWorkingSet) TrimProcessWorkingSet(handle);", Src("Services", "TaskSleepService.cs"));
    }

    // Audio protection is a skip rule every nap path runs, so it's shown as always on, not a switch.
    [Fact]
    public void AudioProtection_IsShownAsAlwaysOn()
    {
        Assert.Contains("new(\"Audio/media active\"", Src("Services", "TaskSleepService.SkipRules.cs"));
        string s = Section();
        Assert.Contains("Header=\"Apps playing sound, in a call or recording\"", s);
        Assert.Contains("Text=\"Always on\"", s);
    }

    [Fact]
    public void Section_IsSettingsRowsUnderPlainHeadings()
    {
        string s = Section();
        foreach (var heading in new[] { "What goes to sleep", "What stays awake", "While apps sleep" })
            Assert.Contains($"Text=\"{heading}\"", s);
        Assert.DoesNotContain("Nap hidden apps", s);          // now "Sleep hidden apps", like its neighbours
        Assert.DoesNotContain("SectionLabel", s);
        // Ctrl+K titles on this section still match
        foreach (var title in new[] { "Sleep minimized apps", "Sleep hidden apps", "Sleep tray apps", "Sleep unused apps",
                                      "Sleep idle apps", "Keep busy apps awake", "Compress napped app memory" })
            Assert.Contains($"Header=\"{title}\"", s);
    }

    [Fact]
    public void Section_LoadsAndRenders()
    {
        PageRender.OnSta(() =>
        {
            PageRender.UseTheme();
            var page = new TaskSleepView { DataContext = FakeEngine.Sample() };
            PageRender.Host(page);
            var card = PageRender.Card(page, "Sleep rules");
            var texts = PageRender.Texts(card);
            foreach (var t in new[] { "What goes to sleep", "Sleep hidden apps", "What stays awake", "Always on", "While apps sleep" })
                Assert.Contains(t, texts);
            Assert.Equal(7, PageRender.Tree(card).OfType<CheckBox>().Count());

            if (PageRender.PreviewDir is not { } dir) return;
            foreach (var (palette, file) in new[] { ("Palette.Dark.xaml", "sleep-rules-dark.png"), ("Palette.Light.xaml", "sleep-rules-light.png") })
            {
                PageRender.UseTheme(palette);
                var p = new TaskSleepView { DataContext = FakeEngine.Sample() };
                PageRender.Host(p);
                PageRender.SavePng(PageRender.Card(p, "Sleep rules"), Path.Combine(dir, file));
            }
        });
    }
}
