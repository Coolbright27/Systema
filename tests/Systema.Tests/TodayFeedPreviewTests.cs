using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Systema.Core.Converters;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Home's "Today" card lists the newest 10 and "Show more" reveals the rest (the feed keeps 30),
/// like the lists in Windows Settings. Before, a busy day made it one endless card.
/// </summary>
public class TodayFeedPreviewTests
{
    private static readonly List<string> Feed = new() { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l" };
    private static readonly PreviewRowVisibilityConverter Rows = new() { Limit = 10 };

    private static object Row(string item, bool expanded) =>
        Rows.Convert(new object[] { item, Feed, expanded, Feed.Count }, typeof(Visibility), null!, null!);

    [Fact]
    public void OnlyTheNewestTen_ShowUntilExpanded()
    {
        Assert.Equal(Visibility.Visible,   Row("a", expanded: false));
        Assert.Equal(Visibility.Visible,   Row("j", expanded: false));   // 10th
        Assert.Equal(Visibility.Collapsed, Row("k", expanded: false));   // 11th
        Assert.Equal(Visibility.Visible,   Row("l", expanded: true));
    }

    private static string Read(params string[] parts)
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        return File.ReadAllText(Path.Combine(dir, "src", "Systema", Path.Combine(parts)));
    }

    [Fact]
    public void TheFeedKeepsThirty_AndTheCardOffersShowMore()
    {
        Assert.Contains("private const int MaxEntries = 30;", Read("Services", "ActivityFeed.cs"));
        Assert.Contains("internal const int ActivityPreviewCount = 10;", Read("ViewModels", "DashboardViewModel.cs"));

        var home = Read("Views", "DashboardView.xaml");
        Assert.Contains("<conv:PreviewRowVisibilityConverter x:Key=\"PreviewRows\" Limit=\"10\"/>", home);
        Assert.Contains("Command=\"{Binding ToggleShowAllActivityCommand}\"", home);
        Assert.Contains("Visibility=\"{Binding HasMoreActivity, Converter={StaticResource BoolToVis}}\"", home);
        // Still bound to the live feed, so only a new entry is created (and fades in).
        Assert.Contains("<ItemsControl ItemsSource=\"{Binding Activity}\"", home);
    }
}
