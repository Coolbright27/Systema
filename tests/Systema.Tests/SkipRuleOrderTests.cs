using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// The skip rules decide what the nap engine will not touch. Order is load-bearing: first match
/// wins, and a Permanent rule appearing after a UserSetting rule would mean a toggle could
/// un-protect a system process, an AV process or a service account.
/// </summary>
public class SkipRuleOrderTests
{
    private static string Src()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(dir, "src", "Systema", "Services", "TaskSleepService.SkipRules.cs");
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new FileNotFoundException("TaskSleepService.SkipRules.cs not found");
    }

    private static (string Reason, string Tag)[] Rules()
    {
        var src = Src();
        int i = src.IndexOf("private SkipRule[] SkipRules", StringComparison.Ordinal);
        Assert.True(i > 0, "skip rule table not found");
        int end = src.IndexOf("};", i, StringComparison.Ordinal);

        return Regex.Matches(src[i..end], @"new\(""([^""]+)"",\s*SkipTag\.(\w+)")
                    .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
                    .ToArray();
    }

    [Fact]
    public void EverySkipRuleIsTaggedAndHasAReason()
    {
        var rules = Rules();
        Assert.True(rules.Length >= 14, $"expected the full rule set, found {rules.Length}");
        Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
    }

    // The safety guarantee. A user toggle must never be evaluated before a permanent protection,
    // or turning that toggle on could expose a system process to napping.
    [Fact]
    public void PermanentRulesAllPrecedeUserConfigurableOnes()
    {
        var rules = Rules();
        int firstUser = Array.FindIndex(rules, r => r.Tag == "UserSetting");
        int lastPerm  = Array.FindLastIndex(rules, r => r.Tag == "Permanent");

        Assert.True(firstUser > 0, "no user-configurable rules found");
        Assert.True(lastPerm < firstUser,
            $"'{rules[lastPerm].Reason}' is Permanent but runs after the user-configurable " +
            $"'{rules[firstUser].Reason}'. A setting could then bypass a permanent protection.");
    }

    // These four are the ones that corrupt system state or break security software if napped.
    [Fact]
    public void TheNonNegotiableProtectionsAreStillPermanent()
    {
        var rules = Rules();
        foreach (var reason in new[]
                 { "System process (whitelist)", "Windows system component",
                   "Elevated/System integrity (non-bypassable)", "Security/AV critical" })
        {
            var rule = rules.FirstOrDefault(r => r.Reason == reason);
            Assert.True(rule.Reason != null, $"rule '{reason}' has gone missing");
            Assert.Equal("Permanent", rule.Tag);
        }
    }

    // Launch Boost must be checked before anything naps the process: the nap path captures the
    // CURRENT priority to restore later, so napping a boosted process makes its High permanent.
    [Fact]
    public void LaunchBoostIsCheckedBeforeUserRules()
    {
        var rules = Rules();
        int boost    = Array.FindIndex(rules, r => r.Reason == "Launch Boost active");
        int firstUser = Array.FindIndex(rules, r => r.Tag == "UserSetting");
        Assert.True(boost >= 0, "Launch Boost skip rule is missing");
        Assert.True(boost < firstUser, "Launch Boost must be checked before user-configurable rules");
    }
}
