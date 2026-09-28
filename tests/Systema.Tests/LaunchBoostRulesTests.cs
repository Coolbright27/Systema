using System.IO;
using Systema.Core;

namespace Systema.Tests;

/// <summary>
/// Who gets a Launch Boost is two ordered rule lists in Core/LaunchBoostRules.cs (0.7.365). They
/// replaced if/return chains across four methods with no change in behaviour, so the order is pinned
/// here and every path the old code took has a case.
/// </summary>
public class LaunchBoostRulesTests
{
    // ── The facts, with the costly ones countable ───────────────────────────

    private sealed class Probe
    {
        public int NameLookups, AgeLookups, ShellLookups;
    }

    private static LaunchFacts Facts(Probe p, string? parent, bool boostable = true, int ppid = 42,
                                     bool parentBoosted = false, bool parentNapped = false,
                                     double parentAgeSeconds = 3600, bool shellStartedIt = false) => new(
        Boostable: boostable,
        ParentPid: ppid,
        ParentBoosted: parentBoosted,
        ParentNapped: new(() => parentNapped),
        ParentName: new(() => { p.NameLookups++; return parent; }),
        ParentAge: new(() => { p.AgeLookups++; return TimeSpan.FromSeconds(parentAgeSeconds); }),
        ParentStartedByShell: new(() => { p.ShellLookups++; return shellStartedIt; }));

    private static LaunchDecision Decide(LaunchFacts f) => LaunchBoostRules.Decide(f);

    // ── Order ───────────────────────────────────────────────────────────────

    [Fact]
    public void LaunchRules_RunInThisOrder()
    {
        Assert.Equal(new[]
        {
            "Rides its parent's boost", "Parent is boosting, but this isn't an app", "Not an app", "No parent",
            "Started by a sleeping app", "Parent already gone", "Started by a background host", "Opened from the shell",
            "Started by a command shell or script", "Opened through a launcher stub",
            "Started by a young background process", "Started by an app that's already running",
        }, LaunchBoostRules.RuleNames.ToArray());
    }

    [Fact]
    public void NameRules_RunInThisOrder()
    {
        Assert.Equal(new[]
        {
            "No name", "Systema itself", "Windows process", "Security software",
            "Command-line tool or background helper", "Systema's installer",
        }, LaunchBoostNames.RuleNames.ToArray());
    }

    // ── Every path ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("explorer", "Boost", "Opened from the shell")]
    [InlineData("EXPLORER", "Boost", "Opened from the shell")]
    [InlineData("svchost", "Skip", "Started by a background host")]
    [InlineData("taskhostw", "Skip", "Started by a background host")]
    [InlineData("powershell", "Skip", "Started by a command shell or script")]
    [InlineData("node", "Skip", "Started by a command shell or script")]
    [InlineData("Discord", "Skip", "Started by an app that's already running")]
    public void ByWhoStartedIt(string parent, string action, string rule)
    {
        var d = Decide(Facts(new Probe(), parent));
        Assert.Equal(action, d.Action.ToString());
        Assert.Equal(rule, d.Rule);
    }

    [Fact]
    public void AChildOfABoostingApp_RidesItsBoost_UnlessItIsAHelper()
    {
        Assert.Equal(LaunchAction.RideParentBoost, Decide(Facts(new Probe(), "firefox", parentBoosted: true)).Action);
        var helper = Decide(Facts(new Probe(), "firefox", boostable: false, parentBoosted: true));
        Assert.Equal(LaunchAction.Skip, helper.Action);
        Assert.Equal("Parent is boosting, but this isn't an app", helper.Rule);   // doesn't fall through
    }

    [Fact]
    public void NeverBoosted_NoParent_SleepingParent_GoneParent()
    {
        Assert.Equal("Not an app",                Decide(Facts(new Probe(), "explorer", boostable: false)).Rule);
        Assert.Equal("No parent",                 Decide(Facts(new Probe(), "explorer", ppid: 0)).Rule);
        Assert.Equal("Started by a sleeping app", Decide(Facts(new Probe(), "explorer", parentNapped: true)).Rule);
        Assert.Equal("Parent already gone",       Decide(Facts(new Probe(), null)).Rule);
    }

    [Fact]
    public void AYoungParent_IsALauncherStubOnlyIfTheShellStartedIt()
    {
        var stub = Decide(Facts(new Probe(), "firefox", parentAgeSeconds: 2, shellStartedIt: true));
        Assert.Equal((LaunchAction.Boost, "Opened through a launcher stub"), (stub.Action, stub.Rule));

        var updater = Decide(Facts(new Probe(), "MicrosoftEdgeUpdate", parentAgeSeconds: 2, shellStartedIt: false));
        Assert.Equal((LaunchAction.Skip, "Started by a young background process"), (updater.Action, updater.Rule));

        // 20 s is the line
        Assert.Equal(LaunchAction.Skip, Decide(Facts(new Probe(), "firefox", parentAgeSeconds: 20, shellStartedIt: true)).Action);
    }

    // The slow facts are only fetched when a rule gets that far, as the old if-chain did: a shell
    // launch never reads the parent's age or takes a process snapshot.
    [Fact]
    public void TheCostlyFacts_AreOnlyFetchedWhenNeeded()
    {
        var p = new Probe();
        Decide(Facts(p, "explorer"));
        Assert.Equal((1, 0, 0), (p.NameLookups, p.AgeLookups, p.ShellLookups));

        p = new Probe();
        Decide(Facts(p, "Discord"));                                        // old parent: age read, no snapshot
        Assert.Equal((1, 1, 0), (p.NameLookups, p.AgeLookups, p.ShellLookups));

        p = new Probe();
        Decide(Facts(p, "explorer", parentBoosted: true));                  // decided before any lookup
        Assert.Equal((0, 0, 0), (p.NameLookups, p.AgeLookups, p.ShellLookups));
    }

    // ── Names ───────────────────────────────────────────────────────────────

    private static readonly LaunchBoostNames.NameLists Lists = new(
        IsWindowsProcess:   n => n is "svchost" or "explorer",
        IsSecuritySoftware: n => n is "MsMpEng");

    [Theory]
    [InlineData("",                        "No name")]
    [InlineData("Systema",                 "Systema itself")]
    [InlineData("svchost",                 "Windows process")]
    [InlineData("MsMpEng",                 "Security software")]
    [InlineData("cmd",                     "Command-line tool or background helper")]
    [InlineData("MicrosoftEdgeUpdate",     "Command-line tool or background helper")]
    [InlineData("Systema_Setup_0.7.364",   "Systema's installer")]
    [InlineData("firefox",                 null)]
    [InlineData("RobloxPlayerBeta",        null)]
    public void NeverBoostedNames(string name, string? reason) =>
        Assert.Equal(reason, LaunchBoostNames.NeverBoostReason(name, Lists));

    // ── The engine uses them ────────────────────────────────────────────────

    [Fact]
    public void Engine_DecidesThroughTheRules()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        string lb = File.ReadAllText(Path.Combine(dir, "src", "Systema", "Services", "TaskSleepService.LaunchBoost.cs"));
        Assert.Contains("LaunchBoostRules.Decide(new LaunchFacts(", lb);
        Assert.Contains("LaunchBoostNames.NeverBoostReason(name, BoostNameLists)", lb);
        // The old scattered checks are gone.
        foreach (var gone in new[] { "IsUserLaunch", "IsBoostableLaunch", "IsBoostableChild", "LaunchBoostExclusionExtras",
                                     "LaunchBoostBackgroundParents", "LaunchBoostShellParents" })
            Assert.DoesNotContain(gone, lb);
    }
}
