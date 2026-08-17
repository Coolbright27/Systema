// SearchIndexerThrottleTests.cs
//
// Game Booster used to call NtSuspendProcess on SearchIndexer.exe to "pause indexing". That is a
// FREEZE, not a pause, and SearchIndexer hosts the Windows Search RPC and COM endpoints. Any app
// making a synchronous call into Windows Search — the Shell property system, a file picker, a
// screen-source picker fetching shell thumbnails — got no answer at all until the boost ended.
//
// Reported live: Spotify stopped working and Discord screen share would not start while a boost
// was running. A frozen RPC server is a deadlock for its callers, not a slowdown, which is why it
// looked like those apps were broken rather than slow.
//
// The indexer is now throttled instead: Idle priority, very low I/O, EcoQoS on. It makes
// effectively no progress while a game has the machine but stays responsive to callers.
//
// Source-scan (not reflection) because Application Control blocks loading Systema.dll into the
// test host on this machine — same constraint as the other *SourceTests.

using System.IO;
using Xunit;

namespace Systema.Tests;

public class SearchIndexerThrottleTests
{
    private static string Service()
    {
        var asmDir = Path.GetDirectoryName(typeof(SearchIndexerThrottleTests).Assembly.Location)!;
        var root   = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        var path   = Path.Combine(root, "src", "Systema", "Services", "GameBoosterService.cs");
        Assert.True(File.Exists(path), $"Expected file not found: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void TheIndexerIsThrottledNotSuspended()
    {
        var src = Service();

        int pause = src.IndexOf("private void PauseIndexing(", System.StringComparison.Ordinal);
        int end   = src.IndexOf("private void ResumeIndexing(", System.StringComparison.Ordinal);
        Assert.True(pause > 0 && end > pause, "could not locate PauseIndexing");

        var body = src[pause..end];

        // Freezing an RPC server deadlocks everything that calls it. Never again.
        Assert.DoesNotContain("NtSuspendProcess", body);

        Assert.Contains("ProcessPriorityClass.Idle", body);
        Assert.Contains("IoPriorityVeryLow", body);
        Assert.Contains("SetProcessEcoQoS(h, on: true)", body);
        Assert.Contains("SetProcessMemoryPriority(h, MemoryPriorityLow)", body);
    }

    [Fact]
    public void ResumeStillUnfreezesAnIndexerAnOlderBuildLeftSuspended()
    {
        // A build before this change could be killed mid-boost, leaving SearchIndexer frozen with
        // nothing to resume it. Upgrading must heal that, not ignore it.
        var src = Service();
        int resume = src.IndexOf("private void ResumeIndexing()", System.StringComparison.Ordinal);
        Assert.True(resume > 0);
        Assert.Contains("NtResumeProcess", src[resume..]);
    }

    [Fact]
    public void TheOriginalPriorityIsRestoredNotGuessed()
    {
        var src = Service();
        Assert.Contains("_indexerOriginalPriority ??= p.PriorityClass", src);
        Assert.Contains("_indexerOriginalPriority ?? ProcessPriorityClass.Normal", src);
    }

    // The indexer service host spawns SearchProtocolHost/SearchFilterHost to do the actual file
    // crawling. Throttling only the parent leaves the process doing the disk reads at full speed.
    [Fact]
    public void ThrottleCoversTheIndexerWorkerProcesses()
    {
        var src = Service();
        Assert.Contains("SearchProtocolHost", src);
        Assert.Contains("SearchFilterHost", src);

        // SearchHost.exe is the Start menu search box, not indexing. Slowing it makes Start feel
        // broken, so it must never be swept in with the indexer.
        Assert.DoesNotContain("\"SearchHost\"", src);
    }

    // Indexer workers are transient: SearchProtocolHost/SearchFilterHost spawn and exit
    // constantly during a crawl. Children inherit the parent's Idle CPU class and EcoQoS but NOT
    // its I/O or memory priority, so applying the throttle once at boost start is not enough.
    [Fact]
    public void ThrottleIsReassertedWhileTheBoostRuns()
    {
        var src = Service();
        Assert.Contains("PauseIndexing(bool reassert", src);
        Assert.Contains("reassert: true", src);

        // The re-assert has to bypass the _indexingPaused early-return or it is a no-op.
        Assert.Contains("if (_indexingPaused && !reassert) return;", src);
    }

}
