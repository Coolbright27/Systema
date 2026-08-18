using System;
using System.IO;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// "Keep the PC awake" silently did nothing for two independent reasons. Both are easy to
/// reintroduce, so both are pinned here.
/// </summary>
public class PreventSleepTests
{
    private static string Service()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(dir, "src", "Systema", "Services", "GameBoosterService.cs");
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new FileNotFoundException("GameBoosterService.cs not found");
    }

    // Bug 1: SetThreadExecutionState is per-thread and Windows clears it when that thread exits.
    // ApplyBoostOptions runs on a RunOnLargeStackAsync worker that finishes moments later, so the
    // flag died with it every single time. A power request is scoped to a handle the process
    // holds, not to a thread, so thread churn cannot drop it.
    [Fact]
    public void SleepPreventionIsNotScopedToADyingWorkerThread()
    {
        var src = Service();
        Assert.Contains("PowerCreateRequest", src);
        Assert.Contains("PowerSetRequest", src);
        Assert.Contains("PowerClearRequest", src);

        // If the execution-state API is still used as a fallback, every real call site must run
        // somewhere that outlives the boost. The dispatcher (UI) thread does; a worker does not.
        // Occurrences inside comments are prose about the bug, not call sites, so skip them.
        const string call = "SetThreadExecutionState(ES_CONTINUOUS";
        for (int i = src.IndexOf(call, StringComparison.Ordinal); i >= 0;
                 i = src.IndexOf(call, i + 1, StringComparison.Ordinal))
        {
            int lineStart = src.LastIndexOf('\n', i) + 1;
            if (src[lineStart..i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;

            string before = src[Math.Max(0, i - 400)..i];
            Assert.True(before.Contains("Dispatcher", StringComparison.Ordinal),
                        "SetThreadExecutionState is being called from a thread that may exit. " +
                        "Windows clears the execution state when that thread dies, which is exactly " +
                        "why Keep the PC awake did nothing.");
        }
    }

    // Bug 2: the toggle promises to stop the SCREEN sleeping or locking, but the code only ever
    // asked for ES_SYSTEM_REQUIRED. Keeping the machine powered while the display still blanks
    // is not what the copy says.
    [Fact]
    public void SleepPreventionAlsoKeepsTheDisplayAwake()
    {
        var src = Service();
        Assert.Contains("PowerRequestDisplayRequired", src);
        Assert.Contains("ES_DISPLAY_REQUIRED", src);
    }

    // The request handle has to be released, or the machine stays awake after the boost ends.
    [Fact]
    public void ThePowerRequestIsReleasedOnRestore()
    {
        var src = Service();
        int restore = src.IndexOf("private void RestorePreventSleep", StringComparison.Ordinal);
        Assert.True(restore > 0, "RestorePreventSleep not found");

        string body = src[restore..Math.Min(src.Length, restore + 1400)];
        Assert.Contains("PowerClearRequest", body);
        Assert.Contains("CloseHandle", body);
    }
}
