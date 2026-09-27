using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// When the Systema Engine naps a process is two ordered lists in TaskSleepService.NapTriggers.cs
/// (0.7.359): GATES that stop a process, then NAP TRIGGERS, first that applies decides. They replaced
/// a 230-line if/else in Tick with no change in behaviour, so the order and the "owns" flags are
/// pinned here: reordering them would change which nap an app gets.
/// </summary>
public class NapTriggerTests
{
    private static string Read(string file)
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        return File.ReadAllText(Path.Combine(dir, "src", "Systema", "Services", file));
    }

    private static string Triggers() => Read("TaskSleepService.NapTriggers.cs");

    private static string[] NamesIn(string listStart, string listEnd)
    {
        var t = Triggers();
        int a = t.IndexOf(listStart, StringComparison.Ordinal);
        int b = t.IndexOf(listEnd, a, StringComparison.Ordinal);
        Assert.True(a > 0 && b > a);
        return Regex.Matches(t[a..b], "\\n\\s+new\\(\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
    }

    [Fact]
    public void Gates_RunInThisOrder()
    {
        Assert.Equal(new[]
        {
            "Systema itself", "Busy app kept awake", "Mid brief wake", "Already napped",
            "Skip rules", "Just woken (5 s cooldown)",
        }, NamesIn("private NapGate[] NapGates =>", "};"));
    }

    [Fact]
    public void Triggers_RunInThisOrder_AndTheRightOnesOwnTheirProcess()
    {
        Assert.Equal(new[] { "Minimized or hidden", "Tray", "Background", "Idle", "Known waster" },
                     NamesIn("private NapTrigger[] NapTriggers =>", "};"));

        // Background and idle don't own a process, so one still waiting out the unfocused timer
        // can idle-nap (and a known waster can still be napped). The others do.
        var t = Triggers();
        Assert.Contains("new(\"Minimized or hidden\", Owns: true,", t);
        Assert.Contains("new(\"Tray\", Owns: true,", t);
        Assert.Contains("new(\"Background\", Owns: false,", t);
        Assert.Contains("new(\"Idle\", Owns: false,", t);
        Assert.Contains("new(\"Known waster\", Owns: true,", t);
    }

    [Fact]
    public void TheRunner_StopsAtTheFirstGate_ThenFirstApplicableTrigger()
    {
        var t = Triggers();
        int run = t.IndexOf("private void RunNapRules(", StringComparison.Ordinal);
        var body = t[run..t.IndexOf("// ── Trigger parts", run, StringComparison.Ordinal)];
        Assert.Contains("if (gate.Stops(c, proc)) return;", body);
        Assert.Contains("if (!trigger.AppliesTo(c, proc)) continue;", body);
        Assert.Contains("case TriggerWait.Due:    trigger.Nap(c, proc); return;", body);
        Assert.Contains("case TriggerWait.Hold:   return;", body);
        Assert.Contains("case TriggerWait.NotYet: if (trigger.Owns) return; break;", body);
    }

    // The log labels each trigger writes (the monitor and Home's feed read these).
    [Fact]
    public void EachTrigger_KeepsItsLogLabel()
    {
        var t = Triggers();
        foreach (var label in new[] { "\"Hidden Nap\"", "\"Minimize Nap\"", "\"Tray Nap\"", "\"Background Nap\"", "\"Idle Nap\"", "\"Napping\"" })
            Assert.Contains(label, t);
        Assert.Contains("$\"background waster — CPU {agCpu:F1}%\"", t);
        Assert.Contains("$\"unfocused {mins}m — CPU {bgCpu:F1}%\"", t);
    }

    [Fact]
    public void Tick_HandsEveryProcessToTheRules()
    {
        var engine = Read("TaskSleepService.cs");
        Assert.Contains("RunNapRules(napCtx, proc);", engine);
        // None of the trigger code is left inline in Tick.
        Assert.DoesNotContain("\"Background Nap\"", engine);
        Assert.DoesNotContain("\"Idle Nap\"", engine);
        Assert.DoesNotContain("_minimizeGraceSince[proc.Id] = DateTime.UtcNow", engine);
    }
}
