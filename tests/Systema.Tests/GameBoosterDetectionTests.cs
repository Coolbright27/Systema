// GameBoosterDetectionTests.cs
//
// Regression guards for two detection bugs fixed in v0.7.276.
//
//  1. ANTI-CHEAT SHADOWED THE REAL GAME.
//     FindRunningGame used to check the anti-cheat list INSIDE the per-process loop, so on a PC
//     with Vanguard / EAC / BattlEye installed it returned whichever came first in the arbitrary
//     enumeration order — usually the anti-cheat service, which has no window and isn't the game.
//     The result was "Unknown Game (Anti-Cheat detected)", and because ActivateBoost decided
//     whether to raise the game's priority by SUBSTRING-MATCHING that display name, the single
//     most valuable part of the boost was silently skipped for exactly the competitive titles
//     that need it most. Now detection returns a GameMatch carrying an explicit IsKnownGame flag,
//     the anti-cheat result is a fallback used only after the whole scan finds nothing on screen,
//     and both ActivateBoost and DeactivateBoost read the flag rather than parsing prose.
//
//  2. "javaw" WAS TREATED AS A GAME.
//     javaw is Minecraft Java — and also IntelliJ, Ghidra, JDownloader and every other Java
//     desktop app. Having an IDE open was enough to switch the power plan and turn off Wi-Fi.
//     It now lives in TitleQualifiedGames and only counts when the window title says Minecraft.
//
// Source-scan (not reflection) because Application Control blocks loading Systema.dll into the
// test host on this machine — same constraint as the other *SourceTests.

using System.IO;
using System.Linq;
using Xunit;

namespace Systema.Tests;

public class GameBoosterDetectionTests
{
    private static string RepoRoot()
    {
        var asmDir = Path.GetDirectoryName(typeof(GameBoosterDetectionTests).Assembly.Location)!;
        // tests/Systema.Tests/bin/<Cfg>/<TFM>/  →  ../../../../../
        return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
    }

    private static string Service()
    {
        var path = Path.Combine(RepoRoot(), "src", "Systema", "Services", "GameBoosterService.cs");
        Assert.True(File.Exists(path), $"Expected file not found: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ActivateBoost_TakesAGameMatch_NotABareString()
    {
        var src = Service();
        Assert.Contains("private Action? ActivateBoost(GameMatch match)", src);
        Assert.Contains("private readonly record struct GameMatch(string Name, bool IsKnownGame)", src);
    }

    [Fact]
    public void PriorityDecision_UsesTheFlag_NotSubstringMatchingTheDisplayName()
    {
        var src = Service();

        // The old, fragile form. If either of these comes back, a renamed placeholder string
        // silently turns the priority boost (and its restore) into a no-op.
        Assert.DoesNotContain("gameName.Contains(\"Anti-Cheat\"", src);
        Assert.DoesNotContain("gameName.Contains(\"Unknown Game\"", src);
        Assert.DoesNotContain("ActiveGameName.Contains(\"Anti-Cheat\"", src);
        Assert.DoesNotContain("ActiveGameName.Contains(\"Unknown Game\"", src);

        Assert.Contains("if (match.IsKnownGame)", src);
        Assert.Contains("_boostedRealGame", src);
    }

    [Fact]
    public void AntiCheatIsAFallback_NotCheckedPerProcessAgainstTheForeground()
    {
        var src = Service();

        // The old inner-loop form re-ran a foreground lookup for every process × every
        // anti-cheat name, and could return before ever seeing the real game.
        Assert.DoesNotContain("IsAntiCheatProcess(proc.ProcessName) && IsRealAppForeground()", src);
        Assert.DoesNotContain("proc.ProcessName.Contains(ac, StringComparison.OrdinalIgnoreCase) && IsRealAppForeground()", src);

        // The fallback must sit after the loop, gated on nothing having been found on screen.
        int loopEnd  = src.IndexOf("if (onScreenGame != null) return onScreenGame;", System.StringComparison.Ordinal);
        int fallback = src.IndexOf("return new GameMatch(UnknownGameName, IsKnownGame: false);", System.StringComparison.Ordinal);
        Assert.True(loopEnd > 0, "expected the on-screen result to be returned before the anti-cheat fallback");
        Assert.True(fallback > loopEnd, "anti-cheat fallback must come AFTER the full on-screen scan");
    }

    [Fact]
    public void ForegroundGameWinsOverOtherOnScreenGames()
    {
        var src = Service();
        Assert.Contains("if (proc.Id == fgPid) return new GameMatch(proc.ProcessName, IsKnownGame: true);", src);
    }

    [Fact]
    public void Javaw_IsTitleQualified_NotAPlainGameName()
    {
        var src = Service();

        int listStart = src.IndexOf("KnownGameProcesses = new(StringComparer.OrdinalIgnoreCase)", System.StringComparison.Ordinal);
        int listEnd   = src.IndexOf("};", listStart, System.StringComparison.Ordinal);
        Assert.True(listStart > 0 && listEnd > listStart, "could not locate the KnownGameProcesses initialiser");

        var list = src[listStart..listEnd];
        Assert.DoesNotContain("\"javaw\"", list);

        // It must still be detectable, just gated on the window title.
        Assert.Contains("(\"javaw\", \"Minecraft\")", src);
    }

    [Fact]
    public void UnrealShippingBuildsAreMatchedBySuffix()
    {
        var src = Service();
        Assert.Contains("\"-Win64-Shipping\"", src);
        Assert.Contains("IsKnownGameProcess", src);

        // The per-title Unreal entries are redundant once the suffix exists; keeping them around
        // is how the list drifts back into being a maintenance burden.
        int listStart = src.IndexOf("KnownGameProcesses = new(StringComparer.OrdinalIgnoreCase)", System.StringComparison.Ordinal);
        int listEnd   = src.IndexOf("};", listStart, System.StringComparison.Ordinal);
        var list = src[listStart..listEnd];
        Assert.DoesNotContain("-Win64-Shipping", list);
    }

    [Fact]
    public void ManualBoost_DoesNotHuntForAProcessNamedManualBoost()
    {
        var src = Service();
        Assert.Contains("new GameMatch(\"Manual Boost\", IsKnownGame: false)", src);
    }

    [Fact]
    public void ServicePausingRemains_Removed()
    {
        var src = Service();

        // Service pausing was dropped in June 2026. The list and its accessors came back as dead
        // weight once; this pins them out. The _killedServices restore path stays — it un-does
        // work done by OLDER builds after an upgrade.
        Assert.DoesNotContain("DefaultKillList", src);
        Assert.DoesNotContain("public List<string> GetKillList()", src);
        Assert.DoesNotContain("PausedServiceCount", src);

        // And the tray balloon must not claim it still happens.
        Assert.DoesNotContain("Non-essential services suspended", src);
    }
}
