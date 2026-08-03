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
