using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// 0.7.360: an app's process tree only half napped, and a tab or renderer it opened while napped
/// ran at full speed. Tick disposed each Process inside the nap loop, but the tree steps (the
/// whole-tree nap inside that loop, and the new-child sweep 6d after it) look processes up by id
/// from the same array, and a disposed Process throws on .Id. Those lookups came back empty.
/// Seen live: Firefox minimized, a new tab stayed at Normal priority for 2 minutes.
/// </summary>
public class WholeAppNapTests
{
    private static string Read(string file)
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        return File.ReadAllText(Path.Combine(dir, "src", "Systema", "Services", file));
    }

    // The root cause, pinned so nobody "tidies" the dispose back into the loop.
    [Fact]
    public void ADisposedProcess_NoLongerHasAnId()
    {
        var p = Process.GetProcessById(Environment.ProcessId);
        Assert.Equal(Environment.ProcessId, p.Id);
        p.Dispose();
        Assert.Throws<InvalidOperationException>(() => p.Id);
    }

    [Fact]
    public void Tick_KeepsItsProcessesUntilTheTickEnds()
    {
        var engine = Read("TaskSleepService.cs");
        int tick  = engine.IndexOf("private void Tick()", StringComparison.Ordinal);
        int loop  = engine.IndexOf("RunNapRules(napCtx, proc);", tick, StringComparison.Ordinal);
        int sweep = engine.IndexOf("// 6d. New-child sweep", tick, StringComparison.Ordinal);
        int end   = engine.IndexOf("PersistNapJournalIfChanged();", tick, StringComparison.Ordinal);
        Assert.True(tick > 0 && loop > tick && sweep > loop && end > sweep);

        // Released on every exit path, right after the snapshot is taken.
        int release = engine.IndexOf("using var releaseAll = new DisposeAllOnExit(all);", tick, StringComparison.Ordinal);
        Assert.True(release > tick && release < loop);

        // Nothing in the tick disposes a process early.
        Assert.DoesNotContain(".Dispose()", engine[tick..end]);
    }

    // A background updater that relaunches itself has a young parent, but no one "opened" it.
    // (0.7.365 moved this into Core/LaunchBoostRules.cs, so it's tested as behaviour now.)
    [Fact]
    public void LaunchBoost_YoungParentCountsOnlyIfTheShellStartedIt()
    {
        Systema.Core.LaunchFacts Young(bool shellStartedIt) => new(
            Boostable: true, ParentPid: 42, ParentBoosted: false,
            ParentNapped: new(() => false), ParentName: new(() => "MicrosoftEdgeUpdate"),
            ParentAge: new(() => TimeSpan.FromSeconds(3)), ParentStartedByShell: new(() => shellStartedIt));

        Assert.Equal(Systema.Core.LaunchAction.Skip,  Systema.Core.LaunchBoostRules.Decide(Young(shellStartedIt: false)).Action);
        Assert.Equal(Systema.Core.LaunchAction.Boost, Systema.Core.LaunchBoostRules.Decide(Young(shellStartedIt: true)).Action);

        foreach (var name in new[] { "MicrosoftEdgeUpdate", "MpSigStub", "nvngx_update", "OAWrapper", "timeout" })
            Assert.Contains(name, Systema.Core.LaunchBoostNames.Helpers);
    }
}
