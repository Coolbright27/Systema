using System;
using System.IO;
using System.Linq;
using Systema.ViewModels;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Home's "Data collection" row used to check only the DiagTrack and dmwappushservice services,
/// so it read "Protected" while No Telemetry Pro was off or partly undone by a Windows update.
/// It now mirrors the No Telemetry Pro switch on Cleanup &amp; Privacy.
/// </summary>
public class HomeDataCollectionTests
{
    [Theory]
    [InlineData(true,  true,  "Blocked")]
    [InlineData(false, true,  "Reduced")]
    [InlineData(false, false, "On")]
    public void Status_FollowsNoTelemetryProFirst(bool noTelPro, bool servicesOff, string expected) =>
        Assert.Equal(expected, DashboardViewModel.DescribeDataCollection(noTelPro, servicesOff).Status);

    [Fact]
    public void EveryState_ExplainsItself_WithoutEmDashes()
    {
        foreach (var (a, b) in new[] { (true, true), (false, true), (false, false) })
        {
            string detail = DashboardViewModel.DescribeDataCollection(a, b).Detail;
            Assert.False(string.IsNullOrWhiteSpace(detail));
            Assert.DoesNotContain("—", detail);
        }
    }

    [Fact]
    public void Home_ReadsTheSameCheckAsTheNoTelemetryProSwitch()
    {
        var asmDir = Path.GetDirectoryName(typeof(HomeDataCollectionTests).Assembly.Location)!;
        string root = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        string vm = File.ReadAllText(Path.Combine(root, "src", "Systema", "ViewModels", "DashboardViewModel.cs"));
        int refresh = vm.IndexOf("public Task RefreshAsync()", StringComparison.Ordinal);
        int status  = vm.IndexOf("DescribeDataCollection(", refresh, StringComparison.Ordinal);
        string block = vm[refresh..status];
        Assert.Contains("_serviceControl.IsNoTelemetryProEnabled()", block);
    }
}
