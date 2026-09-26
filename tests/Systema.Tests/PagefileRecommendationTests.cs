using System.Linq;
using Systema.Services;
using Systema.ViewModels;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// The Memory page offers the page file as a dropdown (32, 16, 12, 8, 4 GB or Windows decides).
/// Auto Pilot, the Home suggestion and the dropdown all use GetRecommendedPagefileMb, so every
/// recommendation must be one of the offered sizes or the three would disagree.
/// </summary>
public class PagefileRecommendationTests
{
    [Fact]
    public void OfferedSizes_AreTheOnesThePageShows()
    {
        Assert.Equal(new[] { 32768, 16384, 12288, 8192, 4096 }, MemoryService.PagefileSizeOptionsMb);
    }

    [Theory]
    [InlineData(1_900)]     //  2 GB RAM
    [InlineData(3_900)]     //  4 GB
    [InlineData(5_900)]     //  6 GB
    [InlineData(7_900)]     //  8 GB (reports a little under)
    [InlineData(8_999)]
    [InlineData(9_000)]
    [InlineData(16_200)]    // 16 GB
    [InlineData(17_499)]
    [InlineData(17_500)]
    [InlineData(20_400)]    // 20 GB
    [InlineData(24_400)]    // 24 GB
    [InlineData(32_491)]    // 32 GB (the reporter's machine)
    [InlineData(65_300)]    // 64 GB
    public void EveryRecommendation_IsAnOfferedSize(long ramMb) =>
        Assert.Contains(MemoryService.GetRecommendedPagefileMb(ramMb), MemoryService.PagefileSizeOptionsMb);

    [Theory]
    [InlineData(1_900,  4096)]   // 1.5x = 2.8 GB, floor 4 GB
    [InlineData(3_900,  8192)]   // 1.5x = 5.7 GB, up to 8
    [InlineData(5_900,  12288)]  // 1.5x = 8.6 GB, up to 12
    [InlineData(7_900,  12288)]  // 1.5x = 11.6 GB, up to 12
    [InlineData(16_200, 32768)]  // 8-16 GB RAM: 32 GB, unchanged
    [InlineData(20_400, 16384)]  // was 24 GB, which the page doesn't offer
    [InlineData(32_491, 16384)]  // 24 GB+: 16 GB, unchanged
    public void Recommendation_ByRam(long ramMb, int expectedMb) =>
        Assert.Equal(expectedMb, MemoryService.GetRecommendedPagefileMb(ramMb));

    [Fact]
    public void LowRam_NeverGetsLessThanOneAndAHalfTimesRam()
    {
        for (long ram = 1_000; ram < 9_000; ram += 100)
            Assert.True(MemoryService.GetRecommendedPagefileMb(ram) >= ram * 1.5 ||
                        MemoryService.GetRecommendedPagefileMb(ram) == MemoryService.PagefileSizeOptionsMb.Max(),
                        $"{ram} MB RAM got too small a page file");
    }

    [Theory]
    [InlineData(16384, "16 GB")]
    [InlineData(24576, "24 GB")]
    [InlineData(5000,  "4.9 GB")]
    public void SizesReadInGigabytes(int mb, string text) =>
        Assert.Equal(text, MemoryViewModel.PagefileGb(mb));
}
