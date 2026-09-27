using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Systema.Core;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Ctrl+K search jumps to a setting by finding its title on the page. If a title drifts from
/// the page text, the result would open the right page and then land nowhere, so these tests
/// hold the index and the pages together.
/// </summary>
public class UiSearchIndexTests
{
    private static string RepoRoot()
    {
        var asmDir = Path.GetDirectoryName(typeof(UiSearchIndexTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
    }

    private static readonly Dictionary<string, string> ViewFor = new()
    {
        ["Dashboard"] = "DashboardView", ["Memory"] = "MemoryView", ["Services"] = "ServicesView",
        ["Visual"] = "VisualView", ["GameBooster"] = "GameBoosterView", ["Tools"] = "ToolsView",
        ["TaskSleep"] = "TaskSleepView", ["Bloatware"] = "BloatwareView", ["Graphics"] = "GraphicsView",
        ["Audio"] = "AudioView", ["Intel"] = "IntelView", ["Nvidia"] = "NvidiaView",
        ["Dell"] = "DellView", ["Settings"] = "SettingsView",
    };

    [Fact]
    public void EveryPageKey_IsARealNavSection()
    {
        foreach (var e in SettingsSearchIndex.Entries)
            Assert.Contains(e.Section, UiOverhaulGuardTests.Sections);
        Assert.Equal(UiOverhaulGuardTests.Sections.OrderBy(s => s), SettingsSearchIndex.SectionNames.Keys.OrderBy(s => s));
    }

    [Fact]
    public void EveryTitle_StillAppearsOnItsPageExactly()
    {
        var missing = new List<string>();
        foreach (var e in SettingsSearchIndex.Entries)
        {
            string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Systema", "Views", ViewFor[e.Section] + ".xaml"));
            string encoded = e.Title.Replace("&", "&amp;");
            // A plain TextBlock title, or a SettingsCard header (rendered as a TextBlock too).
            if (!xaml.Contains($"Text=\"{encoded}\"", StringComparison.Ordinal) &&
                !xaml.Contains($"Header=\"{encoded}\"", StringComparison.Ordinal))
                missing.Add($"{e.Section}: {e.Title}");
        }
        Assert.True(missing.Count == 0,
            "Search titles that no longer match their page (rename the entry to the new page text):\n  " +
            string.Join("\n  ", missing));
    }

    [Fact]
    public void EveryTitle_IsUnique_SoAJumpIsNeverAmbiguous()
    {
        var dupes = SettingsSearchIndex.Entries.GroupBy(e => (e.Section, e.Title)).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(dupes);
    }

    private static bool All(string _) => true;

    [Fact]
    public void EverydayWords_FindTheRightSetting()
    {
        Assert.Contains(SettingsSearchIndex.Search("fan", All), e => e.Title == "Thermal profile");
        Assert.Contains(SettingsSearchIndex.Search("tracking", All), e => e.Title == "No Telemetry Pro");
        Assert.Contains(SettingsSearchIndex.Search("charge", All), e => e.Title == "Charging mode");
        Assert.Contains(SettingsSearchIndex.Search("pagefile", All), e => e.Title.Contains("page file"));
    }

    [Fact]
    public void TitleMatches_RankAboveKeywordMatches()
    {
        var r = SettingsSearchIndex.Search("launch", All);
        Assert.Equal("Launch Boost", r[0].Title);
    }

    [Fact]
    public void EveryWordTyped_MustMatch()
    {
        var r = SettingsSearchIndex.Search("battery charging", All);
        Assert.All(r, e => Assert.Contains("battery",
            $"{e.Title} {SettingsSearchIndex.SectionNames[e.Section]} {e.Keywords}".ToLowerInvariant()));
    }

    [Fact]
    public void HiddenPages_AreLeftOutOfResults()
    {
        Func<string, bool> noDell = s => s != "Dell";
        Assert.DoesNotContain(SettingsSearchIndex.Search("charging mode", noDell), e => e.Section == "Dell");
        Assert.DoesNotContain(SettingsSearchIndex.Search("", noDell), e => e.Section == "Dell");
    }

    [Fact]
    public void EmptyQuery_ListsThePages()
    {
        var r = SettingsSearchIndex.Search("  ", All);
        Assert.All(r, e => Assert.True(e.IsPage));
        Assert.Equal(SettingsSearchIndex.SectionNames.Count, r.Count);
    }
}
