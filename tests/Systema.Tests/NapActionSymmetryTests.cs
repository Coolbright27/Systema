using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// An action applied but never reversed leaves a process permanently altered after it wakes.
/// That exact mistake shipped twice this month in core parking, where apply and remove walked
/// different setting lists, so it is pinned here rather than left to review.
/// </summary>
public class NapActionSymmetryTests
{
    private static string Read(string file)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(dir, "src", "Systema", "Services", file);
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new FileNotFoundException(file);
    }

    private static (string Name, string Apply, string? Restore)[] Manifest()
    {
        var src = Read("TaskSleepService.Actions.cs");
        int i = src.IndexOf("NapActions =", StringComparison.Ordinal);
        Assert.True(i > 0, "NapActions manifest not found");

        // new("Name", NapEffect.X, "ApplyCall", "RestoreCall"|null, ...
        return Regex.Matches(src[i..],
                   @"new\(""([^""]+)"",\s*NapEffect\.\w+,\s*""([^""]+)"",\s*(?:""([^""]+)""|null)")
               .Select(m => (m.Groups[1].Value, m.Groups[2].Value,
                             m.Groups[3].Success ? m.Groups[3].Value : null))
               .ToArray();
    }

    [Fact]
    public void TheManifestCoversEveryAction()
    {
        var actions = Manifest();
        Assert.True(actions.Length >= 8, $"expected all nap actions, found {actions.Length}");
        Assert.All(actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Apply)));
    }

    // The point of the whole file: anything that can be undone MUST be undone.
    [Fact]
    public void EveryReversibleActionIsActuallyRestored()
    {
        var engine  = Read("TaskSleepService.cs");
        var actions = Manifest();

        int applyAt   = engine.IndexOf("private bool TryThrottle", StringComparison.Ordinal);
        int restoreAt = engine.IndexOf("TryRestoreProcess", StringComparison.Ordinal);
        Assert.True(applyAt > 0 && restoreAt > 0);

        foreach (var a in actions)
        {
            Assert.True(engine.Contains(a.Apply, StringComparison.Ordinal),
                        $"'{a.Name}' claims to apply via {a.Apply}, which is not in the engine.");

            if (a.Restore == null) continue;   // irreversible by design; see the manifest notes
            Assert.True(engine.Contains(a.Restore, StringComparison.Ordinal),
                        $"'{a.Name}' is reversible via {a.Restore}, but nothing calls it. " +
                        "A napped process would keep this change after waking.");
        }
    }

    // Only working-set trimming may be irreversible: trimmed pages fault back on their own.
    // A null restore on anything else means someone forgot, not that it cannot be undone.
    [Fact]
    public void OnlyWorkingSetTrimIsAllowedToBeIrreversible()
    {
        var irreversible = Manifest().Where(a => a.Restore == null).Select(a => a.Name).ToArray();
        Assert.Equal(new[] { "Working set trim" }, irreversible);
    }
}
