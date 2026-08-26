using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// THE INVARIANT: everything Auto-Pilot does must also appear in Suggestions, but Suggestions may
/// contain extras Auto-Pilot never touches.
///
/// It is held together by a Label string matching across three places (the checklist, BuildRecMeta,
/// and the dismissed set), with no compiler help. A one-word rename in either place drops the item
/// out of Suggestions with no error at all, which is why this is pinned by a test rather than left
/// to review.
/// </summary>
public class AutoPilotSuggestionsTests
{
    private static string Dash()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(dir, "src", "Systema", "ViewModels", "DashboardViewModel.cs");
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new FileNotFoundException("DashboardViewModel.cs not found");
    }

    // Every checklist Label literal, including both arms of the MPO ternary, which is written as
    // Label = mpoUnsafe ? "Keep Multi-Plane Overlay on" : "Disable MPO".
    private static string[] ChecklistLabels(string src) =>
        Regex.Matches(src, @"Label\s*=\s*(?:[A-Za-z]+\s*\?\s*)?""([^""]+)""(?:\s*:\s*""([^""]+)"")?")
             .SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value })
             .Where(s => !string.IsNullOrEmpty(s))
             .Distinct().ToArray();

    private static string[] SuggestionKeys(string src)
    {
        int i = src.IndexOf("BuildRecMeta()", StringComparison.Ordinal);
        Assert.True(i > 0, "BuildRecMeta not found");
        return Regex.Matches(src[i..], @"\[""([^""]+)""\]\s*=\s*\(")
                    .Select(m => m.Groups[1].Value).Distinct().ToArray();
    }

    [Fact]
    public void EveryAutoPilotItemAppearsInSuggestions()
    {
        var src   = Dash();
        var items = ChecklistLabels(src);
        var keys  = SuggestionKeys(src);

        Assert.NotEmpty(items);
        var missing = items.Where(l => !keys.Contains(l)).ToArray();
        Assert.True(missing.Length == 0,
            "These Auto-Pilot items have no Suggestions entry, so they are silently invisible " +
            "in the feed: " + string.Join(", ", missing));
    }

    // A dropped item used to vanish with a bare `continue`. It now warns, so the break shows up
    // in the log instead of being invisible.
    [Fact]
    public void AMissingSuggestionEntryIsLoggedNotSwallowed()
    {
        var src = Dash();
        int i = src.IndexOf("_recMeta.TryGetValue(item.Label", StringComparison.Ordinal);
        Assert.True(i > 0);
        Assert.Contains("No suggestion metadata for", src[i..(i + 700)]);
    }

    // The other half of the invariant: suggestion-only extras must NOT be applied by a full
    // Auto-Pilot run, or Auto-Pilot is doing things it never told the user about.
    [Fact]
    public void SuggestionOnlyExtrasAreNotAppliedByAutoPilot()
    {
        var src = Dash();
        int run = src.IndexOf("RunAutoPilotAsync", StringComparison.Ordinal);
        Assert.True(run > 0);

        // Stop at BuildRecMeta: the per-suggestion Apply actions live there and SHOULD call these,
        // since clicking a suggestion is exactly how the user opts into one.
        int end = src.IndexOf("BuildRecMeta()", run, StringComparison.Ordinal);
        if (end < 0) end = Math.Min(src.Length, run + 14000);
        var body = src[run..end];

        // These are recommendation-only by design: they change how games look or how the Start
        // menu behaves, which is a user taste call rather than a safe blanket optimization.
        foreach (var extra in new[] { "SetGameDvrDisabled", "SetWebSearchDisabled", "SetHagsEnabled" })
            Assert.DoesNotContain(extra, body);
    }
}
