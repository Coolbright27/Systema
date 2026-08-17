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
        Assert.Contains("private readonly record struct GameMatch(string Name, bool IsKnownGame", src);
    }

    [Fact]
    public void PlaceholderNamesAreNotUsedAsLogic()
    {
        var src = Service();

        // A renamed placeholder string must never silently change behaviour. IsKnownGame carries
        // that fact explicitly.
        Assert.DoesNotContain("gameName.Contains(\"Anti-Cheat\"", src);
        Assert.DoesNotContain("gameName.Contains(\"Unknown Game\"", src);
        Assert.DoesNotContain("ActiveGameName.Contains(\"Anti-Cheat\"", src);
        Assert.DoesNotContain("ActiveGameName.Contains(\"Unknown Game\"", src);
        Assert.Contains("match.IsKnownGame", src);
    }

    [Fact]
    public void SystemaNeverChangesTheGameProcessItself()
    {
        var src = Service();

        // v0.7.281 raised the game's CPU, I/O, GPU and memory priority. Anti-cheat treats external
        // manipulation of a protected game as tampering: Fortnite (EAC), BeamNG and Roblox all
        // FORCE-CLOSED. That is intended behaviour on their side, not something a different API or
        // access mask can work around, and no amount of boost is worth ending someone's session.
        //
        // A boost is SYSTEM-level only — power plan, indexing, network, notifications. If any of
        // these reappear, that decision is being re-made without the crash reports that drove it.
        Assert.DoesNotContain("BoostGameProcess", src);
        Assert.DoesNotContain("ReassertGameBoost", src);
        Assert.DoesNotContain("D3DKMTSetProcessSchedulingPriorityClass", src);
        Assert.DoesNotContain("proc.PriorityClass = ProcessPriorityClass.High", src);

        // NOTE: EcoQoS (ProcessPowerThrottling) and memory priority ARE used, but only to THROTTLE
        // Windows' search indexer during a boost. That is a background service Systema owns the
        // decision for, not the game. The ban above is specifically on touching the GAME process.
        Assert.DoesNotContain("SetProcessEcoQoS(gameHandle", src);
        Assert.DoesNotContain("SetProcessMemoryPriority(gameHandle", src);

        // Enforce that literally: every call site of the memory-priority helper has to live inside
        // PauseIndexing or ResumeIndexing. A string ban would just get deleted the next time it got
        // in the way; this stays true only while the behaviour is actually correct.
        int pause  = src.IndexOf("private void PauseIndexing", StringComparison.Ordinal);
        int resume = src.IndexOf("private void ResumeIndexing", StringComparison.Ordinal);
        Assert.True(pause > 0 && resume > 0, "PauseIndexing/ResumeIndexing not found");

        int indexingStart = Math.Min(pause, resume);
        int indexingEnd   = Math.Max(pause, resume) + 2500;   // both methods are well under this

        for (int i = src.IndexOf("SetProcessMemoryPriority(", StringComparison.Ordinal); i >= 0;
                 i = src.IndexOf("SetProcessMemoryPriority(", i + 1, StringComparison.Ordinal))
        {
            bool isDeclaration = src.LastIndexOf("private static void", i, StringComparison.Ordinal) is int d
                                 && d > 0 && i - d < 40;
            if (isDeclaration) continue;

            Assert.True(i >= indexingStart && i <= indexingEnd,
                        "Memory priority is being set outside the search-indexer throttle. It must " +
                        "never be applied to a game process: anti-cheat force-closes the game.");
        }
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
        int loopEnd  = src.IndexOf("if (visible    != null) return visible;", System.StringComparison.Ordinal);
        int fallback = src.IndexOf("return new GameMatch(UnknownGameName, IsKnownGame: false);", System.StringComparison.Ordinal);
        Assert.True(loopEnd > 0, "expected the on-screen result to be returned before the anti-cheat fallback");
        Assert.True(fallback > loopEnd, "anti-cheat fallback must come AFTER the full on-screen scan");
    }

    [Fact]
    public void ForegroundGameWinsOverOtherOnScreenGames()
    {
        var src = Service();
        Assert.Contains("if (proc.Id == fgPid)                   foreground ??= match;", src);
        Assert.Contains("if (foreground != null) return foreground;", src);
    }

    [Fact]
    public void Javaw_IsTitleQualified_NotAPlainGameName()
    {
        var src = Service();

        int listStart = src.IndexOf("GameNames = new(StringComparer.OrdinalIgnoreCase)", System.StringComparison.Ordinal);
        int listEnd   = src.IndexOf("};", listStart, System.StringComparison.Ordinal);
        Assert.True(listStart > 0 && listEnd > listStart, "could not locate the GameNames initialiser");

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
        Assert.Contains("LooksLikeGame", src);

        // The per-title Unreal entries are redundant once the suffix exists; keeping them around
        // is how the list drifts back into being a maintenance burden.
        int listStart = src.IndexOf("GameNames = new(StringComparer.OrdinalIgnoreCase)", System.StringComparison.Ordinal);
        int listEnd   = src.IndexOf("};", listStart, System.StringComparison.Ordinal);
        var list = src[listStart..listEnd];
        Assert.DoesNotContain("-Win64-Shipping", list);
    }

    [Fact]
    public void ARecognisedGameIsNamedEvenWhenItIsNotOnScreen()
    {
        var src = Service();

        // A fullscreen game MINIMISES when you alt-tab, so "not on screen" is the normal state
        // whenever the user looks at anything else — including Systema itself. Reporting
        // "Unknown Game (Anti-Cheat detected)" in that situation was actively harmful: the
        // placeholder carries IsKnownGame: false, which skips the per-process priority boost.
        // Observed live with Fortnite: detected, named "Unknown", never boosted.
        Assert.Contains("GameMatch? running    = null;", src);
        Assert.Contains("if (antiCheat && running != null) return running;", src);

        // The named result must be preferred over the placeholder.
        int named       = src.IndexOf("if (antiCheat && running != null) return running;", System.StringComparison.Ordinal);
        int placeholder = src.IndexOf("return new GameMatch(UnknownGameName", System.StringComparison.Ordinal);
        Assert.True(named > 0 && placeholder > named,
            "the named off-screen game must be returned before falling back to Unknown Game");
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

    [Fact]
    public void EngineHelperProcessesAreNotTreatedAsGames()
    {
        var src = Service();

        // Unreal's "-Shipping" suffix catches the engine's OWN helpers, which outlive the game.
        // EOSOverlayRenderer-Win64-Shipping kept a boost running for 19 HOURS after Fortnite
        // closed: it satisfied the suffix match, and then the "process is still alive" session
        // rule kept it alive indefinitely.
        Assert.Contains("NotAGameFragments", src);
        Assert.Contains("\"Overlay\"", src);
        Assert.Contains("\"CrashReport\"", src);

        // The rejection must happen INSIDE the suffix branch — applying it to the hand-curated
        // exact-name list too would silently drop deliberately added titles.
        int matcher = src.IndexOf("private static bool LooksLikeGame", System.StringComparison.Ordinal);
        int exact   = src.IndexOf("GameNames.Contains(processName)", matcher, System.StringComparison.Ordinal);
        int deny    = src.IndexOf("NotAGameFragments", matcher, System.StringComparison.Ordinal);
        Assert.True(deny > exact, "the denylist must not gate the curated exact-name list");
    }

    [Fact]
    public void AnAutoBoostCannotRunForever()
    {
        var src = Service();

        // Backstop for the next mis-detection: "the process is alive" is not "you are playing".
        Assert.Contains("MaxAutoBoostDuration", src);
        Assert.Contains("TimeSpan.FromHours(12)", src);

        // And it has to be checked in the stickiness branch, which is what kept the boost alive.
        int sticky = src.IndexOf("SESSION STICKINESS", System.StringComparison.Ordinal);
        Assert.True(src.IndexOf("MaxAutoBoostDuration", sticky, System.StringComparison.Ordinal) > 0,
            "the cap must gate session stickiness, not just exist");
    }
}
