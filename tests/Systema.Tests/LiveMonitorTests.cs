using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Systema.Core.Converters;
using Systema.Models;
using Systema.Views;

namespace Systema.Tests;

/// <summary>
/// The Task Sleep page's Live monitor (0.7.361): laid out like a Windows 11 Settings list instead
/// of uppercase labels and coloured pills. Pins the look, the icon rule, and that the page really
/// loads and renders its rows (a bad resource reference compiles fine and crashes at runtime).
///
/// Set SYSTEMA_RENDER_PREVIEW to a folder to also save dark and light PNGs of the section.
/// </summary>
[Collection(nameof(PageRenderCollection))]
public class LiveMonitorTests
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
        int a = page.IndexOf("===== LIVE MONITOR =====", StringComparison.Ordinal);
        int b = page.IndexOf("===== SLEEP RULES =====", a, StringComparison.Ordinal);
        Assert.True(a > 0 && b > a);
        return page[a..b];
    }

    // ── One icon per kind of event ──────────────────────────────────────────

    [Theory]
    [InlineData("Minimize Nap", ActivityKind.Nap)]
    [InlineData("Hidden Nap", ActivityKind.Nap)]
    [InlineData("Tray Nap", ActivityKind.Nap)]
    [InlineData("Background Nap", ActivityKind.Nap)]
    [InlineData("Idle Nap", ActivityKind.Nap)]
    [InlineData("Child Nap", ActivityKind.Nap)]
    [InlineData("Napping", ActivityKind.Nap)]
    [InlineData("Re-napping", ActivityKind.Nap)]
    [InlineData("Tray Re-nap", ActivityKind.Nap)]
    [InlineData("Brief Wake", ActivityKind.Wake)]
    [InlineData("Deep Wake", ActivityKind.Wake)]
    [InlineData("Tray Wake", ActivityKind.Wake)]
    [InlineData("Tray Deep Wake", ActivityKind.Wake)]
    [InlineData("Woke up", ActivityKind.Wake)]
    [InlineData("Restored", ActivityKind.Wake)]
    [InlineData("Launch Boost", ActivityKind.Boost)]
    [InlineData("Boost ended", ActivityKind.Boost)]
    [InlineData("Auto-whitelisted", ActivityKind.Protect)]
    [InlineData("Re-enforced", ActivityKind.Enforce)]
    [InlineData("Error", ActivityKind.Error)]
    [InlineData("Something new", ActivityKind.Other)]
    [InlineData(null, ActivityKind.Other)]
    public void EachActivity_GetsItsKind(string? action, ActivityKind kind) =>
        Assert.Equal(kind, ActivityKinds.Of(action));

    // Every name the engine can log is one the list above covers (so none falls back to "Other").
    [Fact]
    public void TheEngineStillWritesTheseNames()
    {
        string engine = Src("Services", "TaskSleepService.cs") + Src("Services", "TaskSleepService.NapTriggers.cs")
                      + Src("Services", "TaskSleepService.LaunchBoost.cs") + Src("Core", "NapRules.cs");
        foreach (var name in new[] { "Minimize Nap", "Hidden Nap", "Tray Nap", "Background Nap", "Idle Nap", "Child Nap",
                                     "Napping", "Re-napping", "Tray Re-nap", "Brief Wake", "Deep Wake", "Tray Wake",
                                     "Tray Deep Wake", "Woke up", "Restored", "Launch Boost", "Boost ended",
                                     "Auto-whitelisted", "Re-enforced" })
            Assert.Contains($"\"{name}\"", engine);
    }

    // ── Windows-native look ────────────────────────────────────────────────

    [Fact]
    public void Section_UsesNoPillsOrShoutingLabels()
    {
        string s = Section();
        foreach (var gone in new[] { "Badge", "SectionLabel", "\"LIVE MONITOR\"", "\"RECENT ACTIVITY\"", "\"PROCESS\"",
                                     "\"STATUS\"", "\"AFFINITY\"", "\"SINCE\"", "···", "CpuFreed", "StatusMessage" })
            Assert.DoesNotContain(gone, s);

        Assert.Contains("{StaticResource IconFont}", s);                    // Windows icons, not coloured pills
        Assert.Contains("Converter={StaticResource ProcessIcon}", s);       // real app icons
        Assert.Contains("<ComboBoxItem Content=\"Napping apps\"/>", s);     // Settings-style filter
    }

    // The menu opens in a popup outside the visual tree, so an ItemsControl ancestor lookup there
    // finds nothing. Its commands go through the button's Tag instead.
    [Fact]
    public void RowMenu_BindsThroughThePlacementTarget()
    {
        string s = Section();
        Assert.Contains("PlacementTarget.Tag.WakeProcessCommand", s);
        Assert.Contains("PlacementTarget.Tag.WhitelistProcessCommand", s);
        Assert.DoesNotContain("DataContext.WakeProcessCommand", s);
    }

    // ── It really loads and renders ─────────────────────────────────────────

    [Fact]
    public void TaskSleepPage_LoadsAndRendersTheMonitor()
    {
        PageRender.OnSta(() =>
        {
            PageRender.UseTheme();
            var page = new TaskSleepView { DataContext = FakeEngine.Sample() };
            var root = PageRender.Host(page);
            var texts = PageRender.Texts(root);

            // Rows, with each state spelled out the way the triggers build it
            Assert.Contains("Deep sleep for 10m 3s", texts);
            Assert.Contains("Napping for 8m 35s", texts);
            Assert.Contains("Naps in ~12s", texts);
            Assert.Contains("System process (whitelist)", texts);
            Assert.Contains(" · On E-cores", texts);
            Assert.Contains("4 napping, 1 pending", texts);

            // Windows icons: the moon for naps and the More button's glyph, plus an app icon per row
            Assert.Contains("\uE708", texts);
            Assert.Contains(PageRender.Tree(root).OfType<Button>(), b => b.Content as string == "\uE712");
            Assert.True(PageRender.Tree(PageRender.Card(page, "Live monitor")).OfType<Image>().Count(i => i.Source != null) >= 5);

            // Recent activity: one icon per kind
            Assert.Contains("\uE945", texts);   // boost
            Assert.Contains("\uE706", texts);   // wake
            Assert.Contains("\uE8FB", texts);   // protect

            if (PageRender.PreviewDir is not { } dir) return;
            Thread.Sleep(1500);   // let the icon cache fill so the preview shows real app icons
            foreach (var (palette, file) in new[] { ("Palette.Dark.xaml", "live-monitor-dark.png"), ("Palette.Light.xaml", "live-monitor-light.png") })
            {
                PageRender.UseTheme(palette);
                var p = new TaskSleepView { DataContext = FakeEngine.Sample() };
                PageRender.Host(p);
                PageRender.SavePng(PageRender.Card(p, "Live monitor"), Path.Combine(dir, file));
            }
        });
    }
}

/// <summary>Stands in for TaskSleepViewModel: just what the Live monitor and Never-nap list bind to.</summary>
public sealed class FakeEngine
{
    public bool   IsEnabled             { get; set; } = true;
    public bool   ShowLiveMonitor       { get; set; } = true;
    public bool   ShowAllProcesses      { get; set; }
    public string ThrottledCountDisplay { get; set; } = "4 napping, 1 pending";
    public string SystemCpuDisplay      { get; set; } = "System CPU 16%";
    public ObservableCollection<ProcessSnapshot> LiveProcesses { get; } = new();
    public ObservableCollection<MonitorEvent>    RecentEvents  { get; } = new();

    public bool   ShowNeverNap          { get; set; } = true;
    public string NeverNapSummary       { get; set; } = "2 apps";
    public ObservableCollection<string> Whitelist { get; } = new();
    public bool   ShowAppPicker         { get; set; } = true;
    public bool   PickerShowAll         { get; set; }
    public string PickerSearch          { get; set; } = "";
    public bool   PickerCanAddTyped     { get; set; }
    public ObservableCollection<Systema.Core.RunningApp> PickerApps { get; } = new();

    private static int Pid(string name) =>
        System.Diagnostics.Process.GetProcessesByName(name).FirstOrDefault()?.Id ?? Environment.ProcessId;

    public static FakeEngine Sample()
    {
        var m = new FakeEngine();
        m.LiveProcesses.Add(new ProcessSnapshot { Pid = Pid("firefox"), Name = "firefox", CpuPercent = 0.2, IsThrottled = true, StatusLabel = "Deep Sleep", CoreLabel = "All Cores", ThrottledFor = "10m 3s" });
        m.LiveProcesses.Add(new ProcessSnapshot { Pid = Pid("DefenderSessionHelper"), Name = "DefenderSessionHelper", IsThrottled = true, StatusLabel = "Napping", CoreLabel = "All Cores", ThrottledFor = "8m 35s" });
        m.LiveProcesses.Add(new ProcessSnapshot { Pid = Pid("WidgetBoard"), Name = "WidgetBoard", IsThrottled = true, StatusLabel = "Napping", CoreLabel = "E-cores", ThrottledFor = "2m 5s" });
        m.LiveProcesses.Add(new ProcessSnapshot { Pid = Pid("Discord"), Name = "Discord", CpuPercent = 1.4, IsPendingNap = true, StatusLabel = "Pending", CoreLabel = "All Cores", ThrottledFor = "~12s" });
        m.LiveProcesses.Add(new ProcessSnapshot { Pid = Pid("explorer"), Name = "explorer", CpuPercent = 0.4, StatusLabel = "", CoreLabel = "All Cores", SkipReason = "System process (whitelist)" });
        var t = new DateTime(2026, 9, 27, 13, 59, 47);
        m.RecentEvents.Add(new MonitorEvent(t,                  "SnippingTool", 1, "Launch Boost", "boosted for 40s — CPU/I-O High, efficiency off, GPU High"));
        m.RecentEvents.Add(new MonitorEvent(t.AddSeconds(-2),   "firefox",      2, "Deep Wake",    "CPU 4%"));
        m.RecentEvents.Add(new MonitorEvent(t.AddSeconds(-94),  "PID 7196",     3, "Woke up",      "process exited"));
        m.RecentEvents.Add(new MonitorEvent(t.AddSeconds(-126), "firefox",      4, "Child Nap",    "app hidden — napping tree"));
        m.RecentEvents.Add(new MonitorEvent(t.AddSeconds(-243), "bash",         5, "Idle Nap",     "CPU 0.00% for 2+ min"));
        m.RecentEvents.Add(new MonitorEvent(t.AddSeconds(-300), "Steam",        6, "Auto-whitelisted", "kept forcing high priority — removed from nap"));

        m.Whitelist.Add("claude");
        m.Whitelist.Add("obs64");
        m.PickerApps.Add(new Systema.Core.RunningApp("firefox", Pid("firefox"), IsApp: true));
        m.PickerApps.Add(new Systema.Core.RunningApp("ProcessLasso", Pid("ProcessLasso"), IsApp: true));
        m.PickerApps.Add(new Systema.Core.RunningApp("crashhelper", Pid("crashhelper"), IsApp: false));
        return m;
    }
}
