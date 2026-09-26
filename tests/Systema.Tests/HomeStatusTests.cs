using Systema.ViewModels;
using Xunit;
using Facts = Systema.ViewModels.DashboardViewModel.HomeFacts;

namespace Systema.Tests;

/// <summary>
/// Home's "Right now" headline used to say "Your PC is running normally" no matter what, with
/// "17 of 17 ... apply the rest" underneath. It now names what's worth fixing.
/// </summary>
public class HomeStatusTests
{
    private static Facts AllGood(bool autoPilot = false) => new(
        GameBoost: false, GameName: null, AutoPilotOn: autoPilot, IsAdmin: true, EngineOn: true,
        DataCollection: "Off", Applied: 17, Total: 17, Resting: 0);

    private static (string Headline, string Subline, bool HasIssues) Compose(Facts f) =>
        DashboardViewModel.ComposeHomeStatus(f);

    [Fact]
    public void NothingToFix_SaysSo_WithoutTellingYouToApplyTheRest()
    {
        var s = Compose(AllGood());
        Assert.False(s.HasIssues);
        Assert.Equal("Your PC is in great shape.", s.Headline);
        Assert.DoesNotContain("the rest", s.Subline);
        Assert.Contains("All 17", s.Subline);
    }

    [Fact]
    public void EngineOff_IsTheHeadline()
    {
        var s = Compose(AllGood() with { EngineOn = false });
        Assert.True(s.HasIssues);
        Assert.Equal("The Systema Engine is off.", s.Headline);
        Assert.Contains("Systema Engine", s.Subline);
    }

    [Theory]
    [InlineData("On",      "Windows is collecting data about how you use your PC.")]
    [InlineData("Reduced", "Windows data collection is only partly blocked.")]
    public void DataCollection_IsFlagged(string state, string headline)
    {
        var s = Compose(AllGood() with { DataCollection = state });
        Assert.True(s.HasIssues);
        Assert.Equal(headline, s.Headline);
        Assert.Contains("No Telemetry Pro", s.Subline);
    }

    [Fact]
    public void PendingOptimizations_AreFlaggedOnlyWhileAutoPilotIsOff()
    {
        Assert.Equal("3 recommended optimizations aren't on.",
                     Compose(AllGood() with { Applied = 14 }).Headline);
        Assert.False(Compose(AllGood(autoPilot: true) with { Applied = 14 }).HasIssues);
    }

    [Fact]
    public void SeveralIssues_AreCountedAndListed()
    {
        var s = Compose(AllGood() with { EngineOn = false, DataCollection = "On", Applied = 16 });
        Assert.Equal("3 things need your attention.", s.Headline);
        Assert.Contains("The Systema Engine is off.", s.Subline);
        Assert.Contains("Windows is collecting data", s.Subline);
        Assert.Contains("1 recommended optimization isn't on.", s.Subline);
    }

    [Fact]
    public void GameBoost_TakesTheHeadline()
    {
        var s = Compose(AllGood() with { GameBoost = true, GameName = "Minecraft", EngineOn = false });
        Assert.Equal("Game Boost is running for Minecraft.", s.Headline);
        Assert.False(s.HasIssues);
    }

    [Fact]
    public void RestingApps_AreMentionedWhenTheEngineIsOn()
    {
        Assert.Contains("12 background apps are resting", Compose(AllGood() with { Resting = 12 }).Subline);
        Assert.DoesNotContain("resting", Compose(AllGood() with { Resting = 12, EngineOn = false }).Subline);
    }

    [Fact]
    public void NoWordingUsesEmDashes()
    {
        foreach (var f in new[]
        {
            AllGood(), AllGood(true), AllGood() with { IsAdmin = false }, AllGood() with { EngineOn = false },
            AllGood() with { DataCollection = "On" }, AllGood() with { Applied = 10 },
        })
        {
            var s = Compose(f);
            Assert.DoesNotContain("—", s.Headline + s.Subline);
        }
    }
}
