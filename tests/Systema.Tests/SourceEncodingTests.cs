// SourceEncodingTests.cs
//
// Guard against the PowerShell double-encoding trap.
//
// Editing a source file with Get-Content / Set-Content (or WriteAllLines without an explicit
// encoding) reads UTF-8 as Windows-1252 and writes it back as UTF-8, so every non-ASCII
// character gains a mangled prefix: "—" becomes "â€"", "→" becomes "â†'", and the box-drawing
// comment banners turn into walls of "â•".
//
// This is not cosmetic. BatteryPauseService.cs was corrupted this way and the damage reached
// the user's log file, because the mangling hits string literals as well as comments:
//     Dell SetAttr(PrimaryBattChargeCfg=Custom:50:55) â†' rc=1
// It has now happened twice (a mojibake build shipped as 0.7.118 as well), so it gets a test.
//
// The tell is the byte pair C3 A2 (the UTF-8 encoding of "â") followed by another high byte —
// legitimate text in this codebase never contains "â".

using System.IO;
using System.Linq;
using Xunit;

namespace Systema.Tests;

public class SourceEncodingTests
{
    private static string RepoRoot()
    {
        var asmDir = Path.GetDirectoryName(typeof(SourceEncodingTests).Assembly.Location)!;
        // tests/Systema.Tests/bin/<Cfg>/<TFM>/  →  ../../../../../
        return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
    }

    /// <summary>Sequences that only appear when UTF-8 has been re-encoded through Windows-1252.</summary>
    private static readonly string[] MojibakeMarkers =
    {
        "â€",       // â€  — mangled em dash / quotes
        "â†",       // â†  — mangled arrow
        "â•",       // â•  — mangled box-drawing
        "â”",       // â”  — mangled box-drawing
        "Ã¢",       // Ã¢  — doubly mangled
    };

    [Fact]
    public void NoSourceFileIsDoubleEncoded()
    {
        var srcDir = Path.Combine(RepoRoot(), "src", "Systema");
        Assert.True(Directory.Exists(srcDir), $"source directory not found: {srcDir}");

        var files = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(srcDir, "*.xaml", SearchOption.AllDirectories))
            // obj/ and bin/ hold generated copies that mirror whatever the source said.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        var damaged = new System.Collections.Generic.List<string>();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var marker in MojibakeMarkers)
            {
                if (!text.Contains(marker)) continue;
                damaged.Add($"{Path.GetRelativePath(RepoRoot(), file)} (contains \"{marker}\")");
                break;
            }
        }

        Assert.True(damaged.Count == 0,
            "Source files are double-encoded — they were almost certainly edited with PowerShell " +
            "Get-Content/Set-Content. Repair by decoding as UTF-8 and re-encoding as Windows-1252, " +
            "then use the editor tools instead:\n  " + string.Join("\n  ", damaged));
    }
}
