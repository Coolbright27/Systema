using System;
using System.IO;
using System.Linq;
using Systema.Services;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Home's "Today" feed reads the log instead of being called from each service. That keeps every
/// service untouched, but it means the feed depends on log wording. These tests pin each rule to
/// the exact text its service writes, so rewording a log line breaks the build instead of quietly
/// removing that event from the feed.
/// </summary>
public class ActivityFeedTests
{
    private static string Src(params string[] parts)
    {
        var asmDir = Path.GetDirectoryName(typeof(ActivityFeedTests).Assembly.Location)!;
        string root = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(new[] { root, "src", "Systema" }.Concat(parts).ToArray()));
    }

    // ── Real log lines, as the services write them, become plain sentences ─────

    [Theory]
    [InlineData("GameBoosterService", "Boost activated for: Minecraft.Windows", "Game Boost started for Minecraft.")]
    [InlineData("GameBoosterService", "Game session ended — restoring services", "Game Boost ended and put everything back the way it was.")]
    [InlineData("CoreParkingService", "Parking value reset by something else (poll): dfd10d17 AC is 5, expected 10. Re-applying.", "Another app changed core parking, so Systema put it back.")]
    [InlineData("CoreParkingService", "Power plan changed (poll), re-applying parking to the new plan.", "The power plan changed, so Systema re-applied core parking.")]
    [InlineData("AutoUpdate", "Update found: v0.7.340", "Found Systema v0.7.340. It installs when your PC is idle.")]
    [InlineData("BatteryPauseService", "SetChargeMode('Custom:75:80') via DellModern → OK", "Charging mode set to Custom (75 to 80%).")]
    [InlineData("BatteryPauseService", "SetChargeMode('PrimAcUse') via DellModern → OK", "Charging mode set to Always AC.")]
    [InlineData("ThermalManagementService", "SetMode('UltraPerformance') applied — BIOS now reports '(unread)'", "Thermal profile set to Ultra Performance.")]
    [InlineData("DashboardViewModel", "Auto-Pilot Mode OFF — controls unlocked", "Auto Pilot turned off. Your settings stay as they are.")]
    public void LogLine_BecomesASentence(string source, string message, string expected)
    {
        var ev = ActivityFeed.Interpret(source, message);
        Assert.NotNull(ev);
        Assert.Equal(expected, ev!.Value.Text);
    }

    [Theory]
    [InlineData("Minimize Nap: firefox (PID 20472) — app minimized", "Firefox")]
    [InlineData("Tray Nap: ChatGPT Classic (PID 311) — no visible window", "ChatGPT Classic")]
    [InlineData("Napping: Discord (+2 more)", "Discord")]
    public void NapLines_RollUpUnderTheAppName(string message, string app)
    {
        var ev = ActivityFeed.Interpret("TaskSleepService", message);
        Assert.NotNull(ev);
        Assert.Equal(FeedKind.Nap, ev!.Value.Kind);
        Assert.Equal(app, ev.Value.Subject);
    }

    [Fact]
    public void LaunchBoost_RollsUp_ButItsSetupMessagesDoNot()
    {
        var ev = ActivityFeed.Interpret("TaskSleepService", "Launch Boost: RobloxPlayerBeta (PID 9) — boosted for 20s — CPU/I-O High");
        Assert.Equal(FeedKind.Launch, ev!.Value.Kind);
        Assert.Equal("RobloxPlayerBeta", ev.Value.Subject);

        Assert.Null(ActivityFeed.Interpret("TaskSleepService", "Launch Boost: instant process-start watcher armed"));
    }

    [Theory]
    [InlineData("Re-napping: firefox (PID 1) — brief wake ended")]   // same app again
    [InlineData("Child Nap: crashhelper (PID 2) — app hidden — napping tree")] // helper of a counted app
    [InlineData("Woke up: firefox (PID 3) — opened by user")]
    [InlineData("CPU-CAP DIAG: top hog RobloxPlayerBeta")]
    public void NoisyOrDuplicateLines_StayOutOfTheFeed(string message) =>
        Assert.Null(ActivityFeed.Interpret("TaskSleepService", message));

    [Fact]
    public void FailedChargeChanges_StayOutOfTheFeed() =>
        Assert.Null(ActivityFeed.Interpret("BatteryPauseService", "SetChargeMode('Express') via DellModern → FAILED"));

    [Fact]
    public void RollupText_ReadsNaturally()
    {
        Assert.Equal("Rested Discord while you weren't using it.", ActivityFeed.RollupText(FeedKind.Nap, new[] { "Discord" }));
        Assert.Equal("Rested Discord and Spotify while you weren't using them.", ActivityFeed.RollupText(FeedKind.Nap, new[] { "Discord", "Spotify" }));
        Assert.Equal("Rested Discord and 3 other apps you weren't using.", ActivityFeed.RollupText(FeedKind.Nap, new[] { "Discord", "A", "B", "C" }));
        Assert.Equal("Gave Chrome a faster start.", ActivityFeed.RollupText(FeedKind.Launch, new[] { "Chrome" }));
    }

    // ── Every anchor still matches what its service actually writes ─────────────

    [Theory]
    [InlineData("TaskSleepService.cs", "$\"{action}: {name} (PID {pid})")]
    [InlineData("TaskSleepService.LaunchBoost.cs", "AddLaunchBoostEvent(name, pid, \"Launch Boost\"")]
    [InlineData("TaskSleepService.LaunchBoost.cs", "$\"{action}: {name} (PID {pid}) — {detail}\"")]
    [InlineData("GameBoosterService.cs", "\"Boost activated for: {gameName}")]
    [InlineData("GameBoosterService.cs", "\"Game session ended")]
    [InlineData("CoreParkingService.cs", "\"Parking value reset by something else")]
    [InlineData("CoreParkingService.cs", "\"Power plan changed (")]
    [InlineData("UpdateService.cs", "\"Update found: {")]
    [InlineData("BatteryPauseService.cs", "\"SetChargeMode('{mode}') via")]
    [InlineData("ThermalManagementService.cs", "\"SetMode('{biosValue}') applied")]
    public void ServiceStillWritesTheAnchor(string file, string anchor) =>
        Assert.Contains(anchor, Src("Services", file));

    [Fact]
    public void AutoPilotToggle_StillWritesTheAnchors()
    {
        string vm = Src("ViewModels", "DashboardViewModel.cs");
        Assert.Contains("\"Auto-Pilot Mode ON", vm);
        Assert.Contains("\"Auto-Pilot Mode OFF", vm);
    }

    // Each nap action the feed counts must still be one TaskSleepService actually logs.
    [Fact]
    public void EveryNapAction_IsStillLogged()
    {
        string all = Src("Services", "TaskSleepService.cs") + Src("Services", "TaskSleepService.LaunchBoost.cs");
        foreach (var action in ActivityFeed.NapActions)
            Assert.Contains($"\"{action}\"", all);
    }

    // The feed runs inside the logger's call, on whatever thread logged. It must never be able
    // to throw back into logging.
    [Fact]
    public void Logger_ContainsListenerFailures()
    {
        string logger = Src("Services", "LoggerService.cs");
        int at = logger.IndexOf("var listeners = EntryLogged;", StringComparison.Ordinal);
        Assert.True(at > 0);
        Assert.Contains("try { listeners(entry); } catch", logger[at..(at + 200)]);
    }
}
