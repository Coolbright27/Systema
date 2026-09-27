using System.IO;
using System.Windows.Controls;
using Systema.Core;
using Systema.ViewModels;
using Systema.Views;

namespace Systema.Tests;

/// <summary>
/// The Never-nap list (0.7.362): its apps as Settings-style rows with icons and Remove, and an
/// "Add app" button that opens a picker of running apps with a search box and a "Show all
/// processes" switch. The old box + raw process-name dropdown + "Add to whitelist" is gone.
/// </summary>
[Collection(nameof(PageRenderCollection))]
public class NeverNapListTests
{
    private static readonly RunningApp[] Running =
    {
        new("Discord", 10, IsApp: true),
        new("firefox", 11, IsApp: true),
        new("crashhelper", 12, IsApp: false),
        new("svchost", 13, IsApp: false),
        new("Systema", 14, IsApp: true),
        new("steam", 15, IsApp: true),
    };

    private static string[] Names(IEnumerable<RunningApp> apps) => apps.Select(a => a.Name).ToArray();

    [Fact]
    public void Picker_ShowsAppsOnly_UntilShowAllIsOn()
    {
        Assert.Equal(new[] { "Discord", "firefox", "steam" },
                     Names(RunningApps.Filter(Running, showAll: false, search: "", alreadyListed: Array.Empty<string>())));
        Assert.Equal(new[] { "crashhelper", "Discord", "firefox", "steam", "svchost" },
                     Names(RunningApps.Filter(Running, showAll: true, search: "", alreadyListed: Array.Empty<string>())));
    }

    [Fact]
    public void Picker_LeavesOutWhatsListed_AndSystemaItself()
    {
        var rows = Names(RunningApps.Filter(Running, showAll: true, search: null, alreadyListed: new[] { "discord", "STEAM" }));
        Assert.DoesNotContain("Discord", rows);
        Assert.DoesNotContain("steam", rows);
        Assert.DoesNotContain("Systema", rows);
    }

    [Theory]
    [InlineData("fire", new[] { "firefox" })]
    [InlineData("FIREFOX.EXE", new[] { "firefox" })]
    [InlineData("  cord ", new[] { "Discord" })]
    [InlineData("helper", new[] { "crashhelper" })]
    [InlineData("zzz", new string[0])]
    public void Picker_SearchMatchesAnywhereInTheName(string search, string[] expected) =>
        Assert.Equal(expected, Names(RunningApps.Filter(Running, showAll: true, search, Array.Empty<string>())));

    [Theory]
    [InlineData("Discord.exe", "discord")]
    [InlineData("  OBS64  ", "obs64")]
    [InlineData("steam", "steam")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NamesAreStoredTheWayTheEngineReadsThem(string? raw, string stored) =>
        Assert.Equal(stored, TaskSleepViewModel.NeverNapName(raw));

    // The live snapshot works at all (it runs on a worker thread in the app).
    [Fact]
    public void Snapshot_FindsThisTestRun()
    {
        var all = RunningApps.Snapshot();
        Assert.Contains(all, a => a.Pid == Environment.ProcessId);
        Assert.Equal(all.Count, all.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ── Windows-native look ────────────────────────────────────────────────

    private static string Section()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        string page = File.ReadAllText(Path.Combine(dir, "src", "Systema", "Views", "TaskSleepView.xaml"));
        int a = page.IndexOf("===== NEVER-NAP LIST =====", StringComparison.Ordinal);
        Assert.True(a > 0);
        return page[a..];
    }

    [Fact]
    public void Section_IsAnAddButtonAndAList_NotAForm()
    {
        string s = Section();
        foreach (var gone in new[] { "PROTECTED", "Add to whitelist", "RunningProcessNames", "WhitelistNewApp", "Cascadia Mono", "↻" })
            Assert.DoesNotContain(gone, s);
        foreach (var here in new[] { "Header=\"Add an app\"", "\"Add app\"", "Text=\"Show all processes\"", "Text=\"Search running apps\"",
                                     "Content=\"Remove\"", "Converter={StaticResource ProcessIcon}", "AddPickedAppCommand" })
            Assert.Contains(here, s);
    }

    [Fact]
    public void ViewModel_NoLongerPollsProcessesInTheBackground()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        string vm = File.ReadAllText(Path.Combine(dir, "src", "Systema", "ViewModels", "TaskSleepViewModel.cs"));
        Assert.DoesNotContain("_processRefreshTimer", vm);                  // was Process.GetProcesses() on the UI thread every 15 s
        Assert.Contains("await Task.Run(RunningApps.Snapshot)", vm);        // now: once, off the UI thread, when the picker opens
    }

    // ── It really loads and renders ─────────────────────────────────────────

    [Fact]
    public void NeverNapSection_LoadsAndRenders()
    {
        PageRender.OnSta(() =>
        {
            PageRender.UseTheme();
            var page = new TaskSleepView { DataContext = FakeEngine.Sample() };
            PageRender.Host(page);
            var card = PageRender.Card(page, "Never-nap list");
            var texts = PageRender.Texts(card);

            Assert.Contains("2 apps", texts);
            Assert.Contains("claude", texts);
            Assert.Contains("obs64", texts);
            Assert.Contains("ProcessLasso", texts);           // picker row
            Assert.Contains("Background process", texts);      // crashhelper, only listed with Show all
            Assert.Contains("Search running apps", texts);    // hint text while the box is empty
            Assert.Equal(2, PageRender.Tree(card).OfType<Button>().Count(b => b.Content as string == "Remove"));
            Assert.Contains(PageRender.Tree(card).OfType<Button>(), b => b.Content as string == "Cancel");   // picker is open
            Assert.True(PageRender.Tree(card).OfType<Image>().Count(i => i.Source != null) >= 5);

            if (PageRender.PreviewDir is not { } dir) return;
            Thread.Sleep(1500);   // let the icon cache fill so the preview shows real app icons
            foreach (var (palette, file) in new[] { ("Palette.Dark.xaml", "never-nap-dark.png"), ("Palette.Light.xaml", "never-nap-light.png") })
            {
                PageRender.UseTheme(palette);
                var p = new TaskSleepView { DataContext = FakeEngine.Sample() };
                PageRender.Host(p);
                PageRender.SavePng(PageRender.Card(p, "Never-nap list"), Path.Combine(dir, file));
            }

            // And as it opens today: nothing on the list, picker closed.
            PageRender.UseTheme();
            var empty = FakeEngine.Sample();
            empty.Whitelist.Clear();
            empty.ShowAppPicker = false;
            empty.NeverNapSummary = "None";
            var e = new TaskSleepView { DataContext = empty };
            PageRender.Host(e);
            PageRender.SavePng(PageRender.Card(e, "Never-nap list"), Path.Combine(dir, "never-nap-empty-dark.png"));
        });
    }
}
