using System;
using System.IO;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// A process spawned by a NAPPED parent inherits that parent's page priority, so it starts at
/// MEMORY_PRIORITY_LOWEST and thrashes while faulting in its own binaries. Systema created that
/// situation by napping the parent, so Launch Boost undoes it.
/// </summary>
public class LaunchBoostMemoryTests
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

    [Fact]
    public void LaunchBoostRaisesPagePriorityWhenItIsBelowNormal()
    {
        var lb = Read("TaskSleepService.LaunchBoost.cs");
        Assert.Contains("GetMemoryPriority(h)", lb);
        Assert.Contains("TrySetMemoryPriority(h, MEMORY_PRIORITY_NORMAL)", lb);

        // Only when it is actually below par. Normal is the ceiling, so raising an already-Normal
        // process would be a pointless syscall on every launch.
        Assert.Contains("m < MEMORY_PRIORITY_NORMAL", lb);
    }

    // Put back what was found, not an assumed Normal: a process legitimately left low by its
    // parent should return to low once its launch window ends.
    [Fact]
    public void ItRestoresTheOriginalRatherThanAssumingNormal()
    {
        var lb = Read("TaskSleepService.LaunchBoost.cs");
        Assert.Contains("OriginalMem", lb);
        Assert.Contains("if (e.OriginalMem is uint om) TrySetMemoryPriority(h, om);", lb);

        // Nothing recorded when nothing changed, so restore stays a no-op for normal launches.
        Assert.Contains("origMem = null;", lb);
    }

    // The manifest is what a test reads to enforce apply/restore symmetry, so a new action that
    // is not listed there is invisible to that guarantee.
    [Fact]
    public void TheNewActionIsInTheManifest()
    {
        var actions = Read("TaskSleepService.Actions.cs");
        Assert.Contains("Launch page priority", actions);
    }
}
