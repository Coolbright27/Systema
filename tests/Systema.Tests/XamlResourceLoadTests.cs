// XamlResourceLoadTests.cs
//
// Static-analysis regression test for the bug class that crashed System Tweaks
// in v1.7.72 / v1.7.73:
//
//   <Setter Property="Resources"> ... </Setter>
//
// FrameworkElement.Resources, Triggers, ContextMenu, ToolTip's Triggers, and a
// handful of other XAML-only properties are NOT DependencyProperties. WPF's
// Setter type rejects them at runtime with ArgumentNullException("property")
// — surfacing as the dialog "Systema encountered a UI rendering error" and
// killing the entire view that uses the offending style.
//
// We can't catch this at compile time (the XAML compiler is happy with it) and
// we can't catch it via XamlReader.Load in this test environment (Application
// Control blocks the WPF dependencies). But we CAN catch it with a focused
// text scan of every XAML file — which is fast, deterministic, and would have
// shipped with a failing build instead of a crashing app.

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Systema.Tests;

public class XamlResourceLoadTests
{
    /// <summary>
    /// Repo root resolved relative to the test assembly so the test runs from
    /// any CI working directory.
    /// </summary>
    private static string RepoRoot()
    {
        var asmDir = Path.GetDirectoryName(typeof(XamlResourceLoadTests).Assembly.Location)!;
        // tests/Systema.Tests/bin/<Cfg>/<TFM>/  →  ../../../../../
        return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
    }

    /// <summary>
    /// Every <Setter Property="X"/> in our XAML must reference a real
    /// DependencyProperty. The list below is the set of FrameworkElement /
    /// Control properties that look settable but ARE NOT DependencyProperties
    /// — using them in a Setter triggers a runtime ArgumentNullException that
    /// kills the entire view. Add to this list whenever a new gotcha is found.
    /// </summary>
    private static readonly string[] NotADependencyProperty =
    {
        "Resources",       // FrameworkElement.Resources — the v1.7.73 crash
        "Triggers",        // FrameworkElement.Triggers
        "Style.Triggers",  // tooling-mistake variant
        "InputBindings",   // UIElement.InputBindings
        "CommandBindings", // UIElement.CommandBindings
    };

    [Fact]
    public void NoSetter_TargetsNonDependencyProperty()
    {
        var root = RepoRoot();
        var xamlFiles = Directory.EnumerateFiles(
            Path.Combine(root, "src", "Systema"),
            "*.xaml",
            SearchOption.AllDirectories);

        // Strip <!-- ... --> comments before scanning. Comments often contain
        // documentation that quotes the bad pattern as a warning to future
        // developers ("don't write <Setter Property=\"Resources\">"); we
        // don't want those quoted examples to trip the test.
        var commentStripper = new Regex(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

        // Match: <Setter Property="X" ... /> or <Setter Property="X">
        var setterRegex = new Regex(
            @"<Setter\s+(?:[^>]*\s)?Property\s*=\s*""(?<name>[^""]+)""",
            RegexOptions.Compiled);

        var failures = new System.Collections.Generic.List<string>();

        foreach (var file in xamlFiles)
        {
            var rawText = File.ReadAllText(file);

            // Replace each comment with whitespace that preserves newlines,
            // so reported line numbers still match the original file.
            var scanText = commentStripper.Replace(rawText, m =>
                new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));

            foreach (Match m in setterRegex.Matches(scanText))
            {
                var propName = m.Groups["name"].Value;
                if (!NotADependencyProperty.Contains(propName)) continue;

                var line = rawText[..m.Index].Count(c => c == '\n') + 1;
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                failures.Add($"  {rel}:{line} — <Setter Property=\"{propName}\"> ({propName} is not a DependencyProperty and will crash WPF at runtime)");
            }
        }

        Assert.True(failures.Count == 0,
            "XAML setters target non-DependencyProperties:" +
            System.Environment.NewLine + string.Join(System.Environment.NewLine, failures));
    }

    /// <summary>
    /// Sanity check: confirms we are actually scanning files. If the path
    /// resolution above breaks, the property test would silently pass with
    /// zero files scanned — this test ensures that never happens.
    /// </summary>
    [Fact]
    public void XamlFiles_AreDiscoverable()
    {
        var root = RepoRoot();
        var xamlFiles = Directory.EnumerateFiles(
            Path.Combine(root, "src", "Systema"),
            "*.xaml",
            SearchOption.AllDirectories).ToList();

        Assert.True(xamlFiles.Count > 5,
            $"Expected to discover several XAML files under src/Systema. Found {xamlFiles.Count}. " +
            $"Test path resolution is probably broken (root={root}).");
    }
}
