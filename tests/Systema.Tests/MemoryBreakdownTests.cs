// ════════════════════════════════════════════════════════════════════════════
// MemoryBreakdownTests.cs
// Validates MemoryService.GetMemoryBreakdown() — the In use / Cached / Free split
// that feeds the Memory tab's usage bar. Runs against the real machine, so it also
// confirms the NtQuerySystemInformation(SystemMemoryListInformation) struct offsets
// are correct here: if they were wrong, Cached+Free wouldn't track the OS-reported
// "available" figure.
// ════════════════════════════════════════════════════════════════════════════

using Systema.Services;

namespace Systema.Tests;

public class MemoryBreakdownTests
{
    [Fact]
    public void GetMemoryBreakdown_ReconstructsTotal_AndTracksAvailable()
    {
        var svc = new MemoryService();

        var (total, avail) = svc.GetRamStats();
        Assert.True(total > 0, "total physical RAM should be positive on any real machine");

        var (inUse, cached, free) = svc.GetMemoryBreakdown();

        // No negative segments.
        Assert.True(inUse >= 0 && cached >= 0 && free >= 0,
            $"segments must be non-negative (inUse={inUse}, cached={cached}, free={free})");

        // The three segments always reconstruct the total exactly (inUse is the remainder).
        Assert.Equal(total, inUse + cached + free);

        // Cached + Free is the OS's "available" memory. If the kernel page-list offsets
        // are right, it tracks GlobalMemoryStatusEx's AvailPhys closely (sampled ms apart,
        // so allow ~1 GB of drift). A wrong struct layout would blow this out.
        long availFromBreakdown = cached + free;
        Assert.InRange(availFromBreakdown, avail - 1024, avail + 1024);
    }
}

/// <summary>
/// The Task Manager-style Memory card (In use / Modified / Standby / Free, Committed, pools).
/// Runs against the real machine, like the breakdown test above.
/// </summary>
public class MemoryDetailsTests
{
    [Fact]
    public void GetMemoryDetails_SegmentsRebuildTheTotal_AndCommitMakesSense()
    {
        var svc = new MemoryService();
        var (total, avail) = svc.GetRamStats();
        var d = svc.GetMemoryDetails();

        Assert.True(d.InUseMb >= 0 && d.ModifiedMb >= 0 && d.StandbyMb >= 0 && d.FreeMb >= 0);
        Assert.Equal(total, d.TotalMb);                            // the four segments fill the bar exactly
        Assert.InRange(d.StandbyMb + d.FreeMb, avail - 1024, avail + 1024);   // same offsets as the breakdown
        Assert.Equal(d.StandbyMb + d.ModifiedMb, d.CachedMb);      // Task Manager's "Cached"

        Assert.True(d.CommitLimitMb >= total / 2, "commit limit should be at least most of physical RAM");
        Assert.InRange(d.CommittedMb, 1, d.CommitLimitMb);
        Assert.True(d.PagedPoolMb > 0 && d.NonPagedPoolMb > 0, "kernel pools are never empty on a running system");
    }

    [Theory]
    [InlineData(8,  "DIMM")]
    [InlineData(12, "SODIMM")]
    [InlineData(22, "Soldered")]
    [InlineData(0,  "")]
    [InlineData(-1, "")]
    public void FormFactor_ReadsLikeTaskManager(int code, string name) =>
        Assert.Equal(name, MemoryService.MemoryFormFactorName(code));
}
