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

    /// <summary>Stands in for TaskSleepViewModel: just what the Live monitor binds to.</summary>
    public sealed class FakeMonitor
    {
        public bool   IsEnabled             { get; set; } = true;
        public bool   ShowLiveMonitor       { get; set; } = true;
        public bool   ShowAllProcesses      { get; set; }
        public string ThrottledCountDisplay { get; set; } = "4 napping, 1 pending";
        public string SystemCpuDisplay      { get; set; } = "System CPU 16%";
        public ObservableCollection<ProcessSnapshot> LiveProcesses { get; } = new();
        public ObservableCollection<MonitorEvent>    RecentEvents  { get; } = new();
    }

    private static FakeMonitor Sample()
    {
        int Pid(string name) => System.Diagnostics.Process.GetProcessesByName(name).FirstOrDefault()?.Id ?? Environment.ProcessId;
        var m = new FakeMonitor();
        m.LiveProcesses.Add(new ProcessSnapshot { Pid = Pid("firefox"), Name = "firefox", CpuPercent = 0.2, IsThrottled = true, StatusLabel = "Deep Sleep", CoreLabel = "All Cores", ThrottledFor = "10m 3s" });
        m.LiveProcesses.Add(new ProcessSnapshot { Pid = Pid("explorer"), Name = "DefenderSessionHelper", IsThrottled = true, StatusLabel = "Napping", CoreLabel = "All Cores", ThrottledFor = "8m 35s" });
        m.LiveProcesses.Add(new ProcessSnapshot { Pid = Pid("WidgetBoard"), Name = "WidgetBoard", IsThrottled = true, StatusLabel = "Napping", CoreLabel = "E-cores", ThrottledFor = "2m 5s" });
        m.LiveProcesses.Add(new ProcessSnapshot { Pid = Pid("Discord"), Name = "Discord", CpuPercent = 1.4, IsPendingNap = true, StatusLabel = "Pending", CoreLabel = "All Cores", ThrottledFor = "~12s" });
        m.LiveProcesses.Add(new ProcessSnapshot { Pid = Pid("explorer"), Name = "explorer", CpuPercent = 0.4, StatusLabel = "", CoreLabel = "All Cores", SkipReason = "System process (whitelist)" });
        var t = new DateTime(2026, 9, 27, 13, 59, 47);
        m.RecentEvents.Add(new MonitorEvent(t,                    "SnippingTool", 1, "Launch Boost", "boosted for 40s — CPU/I-O High, efficiency off, GPU High"));
        m.RecentEvents.Add(new MonitorEvent(t.AddSeconds(-2),     "firefox",      2, "Deep Wake",    "CPU 4%"));
        m.RecentEvents.Add(new MonitorEvent(t.AddSeconds(-94),    "PID 7196",     3, "Woke up",      "process exited"));
        m.RecentEvents.Add(new MonitorEvent(t.AddSeconds(-126),   "firefox",      4, "Child Nap",    "app hidden — napping tree"));
        m.RecentEvents.Add(new MonitorEvent(t.AddSeconds(-243),   "bash",         5, "Idle Nap",     "CPU 0.00% for 2+ min"));
        m.RecentEvents.Add(new MonitorEvent(t.AddSeconds(-300),   "Steam",        6, "Auto-whitelisted", "kept forcing high priority — removed from nap"));
        return m;
    }

    private static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static ResourceDictionary Theme(string palette) => new()
    {
        MergedDictionaries =
        {
            new ResourceDictionary { Source = new Uri($"pack://application:,,,/Systema;component/Resources/Themes/{palette}") },
            new ResourceDictionary { Source = new Uri("pack://application:,,,/Systema;component/Resources/Themes/Dark.xaml") },
        },
    };

    private static (FrameworkElement Root, TaskSleepView View) Build(string palette)
    {
        var theme = Theme(palette);
        theme["GlobalBoolToVis"]   = new BoolToVisibilityConverter();
        theme["GlobalSafetyColor"] = new SafetyLevelToColorConverter();
        theme["GlobalSafetyBadge"] = new SafetyLevelToBadgeConverter();
        theme["GlobalEquality"]    = new EqualityConverter();
        // App scope, as in App.xaml: StaticResource lookups run while the page is being built,
        // before it has a parent to search.
        Application.Current.Resources = theme;

        var view = new TaskSleepView { DataContext = Sample() };
        var root = new Border { Child = view, Padding = new Thickness(24) };
        root.SetResourceReference(Border.BackgroundProperty, "BgPrimaryBrush");
        root.Measure(new Size(1000, 6000));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        return (root, view);
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject d)
    {
        yield return d;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            foreach (var c in Tree(VisualTreeHelper.GetChild(d, i))) yield return c;
    }

    [Fact]
    public void TaskSleepPage_LoadsAndRendersTheMonitor()
    {
        OnSta(() =>
        {
            if (Application.Current == null) _ = new Application();   // registers pack://application

            var (root, _) = Build("Palette.Dark.xaml");
            var texts = Tree(root).OfType<TextBlock>().Select(t => t.Text).ToList();

            // Rows, with each state spelled out the way the triggers build it
            Assert.Contains("Deep sleep for 10m 3s", texts);
            Assert.Contains("Napping for 8m 35s", texts);
            Assert.Contains("Naps in ~12s", texts);
            Assert.Contains("System process (whitelist)", texts);
            Assert.Contains(" · On E-cores", texts);
            Assert.Contains("4 napping, 1 pending", texts);

            // Windows icons: the moon for naps and the More button's glyph, plus an app icon per row
            Assert.Contains("", texts);
            Assert.Contains(Tree(root).OfType<Button>(), b => b.Content as string == "");
            Assert.True(Tree(root).OfType<Image>().Count(i => i.Source != null) >= 5);

            // Recent activity: one icon per kind
            Assert.Contains("", texts);   // boost
            Assert.Contains("", texts);   // wake
            Assert.Contains("", texts);   // protect

            string? dir = Environment.GetEnvironmentVariable("SYSTEMA_RENDER_PREVIEW");
            if (string.IsNullOrEmpty(dir)) return;

            // Warm the icon cache so the preview shows real app icons, then render both themes.
            Thread.Sleep(1500);
            foreach (var (palette, file) in new[] { ("Palette.Dark.xaml", "live-monitor-dark.png"), ("Palette.Light.xaml", "live-monitor-light.png") })
            {
                var (r, v) = Build(palette);
                var section = Tree(v).OfType<TextBlock>().First(t => t.Text == "Live monitor");
                var card = Ancestors(section).OfType<Border>().First(b => b.Style == (Style)v.FindResource("ExpanderCard"));
                Save(card, Path.Combine(dir, file));
            }
        });
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p)) yield return p;
    }

    private static void Save(FrameworkElement card, string path)
    {
        // Paint the card over the window colour, as it appears in the app.
        var bounds = card.TransformToAncestor((Visual)Ancestors(card).Last()).TransformBounds(new Rect(card.RenderSize));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)card.FindResource("BgPrimaryBrush"), null, new Rect(0, 0, bounds.Width + 32, bounds.Height + 32));
            dc.DrawRectangle(new VisualBrush(card), null, new Rect(16, 16, bounds.Width, bounds.Height));
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
