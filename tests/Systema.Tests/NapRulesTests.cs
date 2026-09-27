using System;
using System.IO;
using Systema.Core;
using Systema.Models;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// The Systema Engine's wake rules moved out of the 1,200-line Tick into Core/NapRules.cs
/// (0.7.358) without changing what they do. These pin every rule, its order, and the exact
/// reason text the monitor and log show, so the engine can't drift silently.
/// </summary>
public class NapRulesTests
{
    private static TaskSleepSettings S(Action<TaskSleepSettings>? tweak = null)
    {
        var s = new TaskSleepSettings();
        tweak?.Invoke(s);
        return s;
    }

    // A napped, alive, not-in-use process with nothing happening.
    private static NappedFacts Quiet(NapReason? reason) => new(
        IsAlive: true, InUse: false, OpenedDirectly: false, Reason: reason,
        StillMinimized: reason == NapReason.Minimized, StillInTray: reason == NapReason.Tray,
        HasAudio: false, BriefWakeDue: false, NappedForMs: null, CpuPercent: null);

    private static WakeDecision Decide(NappedFacts f, TaskSleepSettings? s = null) => NapWakeRules.Decide(f, s ?? S());

    // ── The wake rules, in order ──────────────────────────────────────────

    [Fact]
    public void ExitedProcesses_AreAlwaysCleanedUp()
    {
        foreach (var r in new NapReason?[] { NapReason.Minimized, NapReason.Tray, NapReason.Background, NapReason.Idle, null })
            Assert.Equal(WakeDecision.WakeUp("process exited"), Decide(Quiet(r) with { IsAlive = false, InUse = true, OpenedDirectly = true }));
    }

    [Fact]
    public void OpeningAnApp_WakesIt_WhateverItWasNappedFor()
    {
        foreach (var r in new NapReason?[] { NapReason.Minimized, NapReason.Tray, NapReason.Background, NapReason.Idle, null })
            Assert.Equal(WakeDecision.WakeUp("opened by user"), Decide(Quiet(r) with { InUse = true, OpenedDirectly = true }));
    }

    [Fact]
    public void AnInUseApp_WakesItsBackgroundHelpers_ButNotItsMinimizedOnes()
    {
        foreach (var r in new NapReason?[] { NapReason.Background, NapReason.Idle, null })
            Assert.Equal(WakeDecision.WakeUp("app family focused"), Decide(Quiet(r) with { InUse = true }));

        // The user hid a minimized app on purpose; an in-use sibling alone doesn't bring it back.
        Assert.Equal(WakeDecision.Keep, Decide(Quiet(NapReason.Minimized) with { InUse = true }));
    }

    [Fact]
    public void Minimized_WakesWhenShownOrPlayingAudio_ElseBriefWakesWhenDue()
    {
        var m = Quiet(NapReason.Minimized);
        Assert.Equal(WakeDecision.WakeUp("app un-minimized"), Decide(m with { StillMinimized = false }));
        Assert.Equal(WakeDecision.WakeUp("audio detected"),   Decide(m with { HasAudio = true }));
        Assert.Equal(WakeDecision.WakeUp("audio detected"),   Decide(m with { StillMinimized = false, HasAudio = true }));
        Assert.Equal(WakeDecision.QueueBriefWake, Decide(m with { BriefWakeDue = true }));
        Assert.Equal(WakeDecision.Keep,           Decide(m));
    }

    [Fact]
    public void Tray_WakesForAWindowAudioOrItsAppInUse_ElseBriefWakesWhenDue()
    {
        var t = Quiet(NapReason.Tray);
        Assert.Equal(WakeDecision.WakeUp("window appeared"),    Decide(t with { StillInTray = false }));
        Assert.Equal(WakeDecision.WakeUp("audio detected"),     Decide(t with { HasAudio = true, StillInTray = false }));
        Assert.Equal(WakeDecision.WakeUp("app family focused"), Decide(t with { InUse = true }));
        Assert.Equal(WakeDecision.QueueBriefWake, Decide(t with { BriefWakeDue = true }));
        Assert.Equal(WakeDecision.Keep,           Decide(t));
    }

    [Fact]
    public void GameMode_HoldsBriefWakes_WhenTheUserAskedForThat()
    {
        var game = S(s => { s.IsGameModeActive = true; s.SuppressBriefWakesDuringGameMode = true; });
        Assert.Equal(WakeDecision.Keep, Decide(Quiet(NapReason.Minimized) with { BriefWakeDue = true }, game));
        Assert.Equal(WakeDecision.Keep, Decide(Quiet(NapReason.Tray)      with { BriefWakeDue = true }, game));

        var gameButAllowed = S(s => { s.IsGameModeActive = true; s.SuppressBriefWakesDuringGameMode = false; });
        Assert.Equal(WakeDecision.QueueBriefWake, Decide(Quiet(NapReason.Tray) with { BriefWakeDue = true }, gameButAllowed));
    }

    [Fact]
    public void BackgroundAndIdleNaps_OnlyAudioWakesThem()
    {
        foreach (var r in new[] { NapReason.Background, NapReason.Idle })
        {
            Assert.Equal(WakeDecision.WakeUp("audio detected"), Decide(Quiet(r) with { HasAudio = true }));
            // Never timed out, even long after the classic max duration.
            Assert.Equal(WakeDecision.Keep, Decide(Quiet(r) with { NappedForMs = 10_000_000, CpuPercent = 0 }));
        }
    }

    [Fact]
    public void NapUntilUsed_KeepsOtherNapsUntilFocused()
    {
        var persistent = S(s => s.PersistentNapEnabled = true);
        Assert.Equal(WakeDecision.Keep, Decide(Quiet(null) with { NappedForMs = 10_000_000, CpuPercent = 0 }, persistent));
    }

    [Fact]
    public void ClassicTimedNap_EndsAtMaxDuration_OrWhenCpuDropsAfterTheMinimum()
    {
        var s = S(x => { x.PersistentNapEnabled = false; x.MinAdjustmentDurationMs = 1_000; x.MaxAdjustmentDurationMs = 5_000; x.ProcessCpuStopPercent = 2; });
        var f = Quiet(null);
        Assert.Equal(WakeDecision.WakeUp("max duration reached"), Decide(f with { NappedForMs = 5_000 }, s));
        Assert.Equal(WakeDecision.WakeUp("CPU dropped to 1.5%"),  Decide(f with { NappedForMs = 2_000, CpuPercent = 1.5 }, s));
        Assert.Equal(WakeDecision.Keep, Decide(f with { NappedForMs = 500,   CpuPercent = 0.1 }, s));   // before the minimum
        Assert.Equal(WakeDecision.Keep, Decide(f with { NappedForMs = 2_000, CpuPercent = 3 },   s));   // still busy
        Assert.Equal(WakeDecision.Keep, Decide(f with { NappedForMs = 2_000, CpuPercent = null }, s));  // not sampled
        Assert.Equal(WakeDecision.Keep, Decide(f with { NappedForMs = null }, s));                      // nap time unknown
    }

    // ── A process mid brief wake ─────────────────────────────────────────

    [Theory]
    [InlineData(false, "window shown again")]
    [InlineData(true,  "window appeared")]
    public void BriefWake_WakesForRealWhenShownFocusedOrPlaying(bool isTray, string shownReason)
    {
        var kind = isTray ? NapReason.Tray : NapReason.Minimized;
        Assert.Equal((BriefWakeAction.FullWake, shownReason),     BriefWakeRules.Decide(kind, stillHidden: false, userFocused: false, hasAudio: false, windowOver: true));
        Assert.Equal((BriefWakeAction.FullWake, "opened by user"), BriefWakeRules.Decide(kind, stillHidden: false, userFocused: true,  hasAudio: false, windowOver: false));
        Assert.Equal((BriefWakeAction.FullWake, "audio detected"), BriefWakeRules.Decide(kind, stillHidden: true,  userFocused: true,  hasAudio: true,  windowOver: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BriefWake_ReNapsWhenTheWindowIsOver_ElseWaits(bool isTray)
    {
        var kind = isTray ? NapReason.Tray : NapReason.Minimized;
        Assert.Equal((BriefWakeAction.ReNap, "brief wake ended"), BriefWakeRules.Decide(kind, true, false, false, windowOver: true));
        Assert.Equal((BriefWakeAction.Wait, ""),                  BriefWakeRules.Decide(kind, true, false, false, windowOver: false));
    }

    [Fact]
    public void BriefWakeSchedule_SlowsDownInDeepSleep()
    {
        var s = S(x =>
        {
            x.MinimizedBriefWakeDurationMs = 10_000; x.MinimizedBriefWakeIntervalMs = 60_000;
            x.MinimizeDeepSleepThresholdMs = 600_000; x.MinimizeDeepSleepWakeIntervalMs = 300_000;
            x.TrayBriefWakeDurationMs = 5_000; x.TrayBriefWakeIntervalMs = 120_000;
            x.TrayDeepSleepEnabled = true; x.TrayDeepSleepThresholdMs = 900_000; x.TrayDeepSleepWakeIntervalMs = 600_000;
        });

        Assert.Equal(new BriefWakePlan(10_000, 60_000,  "Brief Wake"),     BriefWakeSchedule.For(NapReason.Minimized, 1_000,   s));
        Assert.Equal(new BriefWakePlan(10_000, 300_000, "Deep Wake"),      BriefWakeSchedule.For(NapReason.Minimized, 600_000, s));
        Assert.Equal(new BriefWakePlan(5_000,  120_000, "Tray Wake"),      BriefWakeSchedule.For(NapReason.Tray,      1_000,   s));
        Assert.Equal(new BriefWakePlan(5_000,  600_000, "Tray Deep Wake"), BriefWakeSchedule.For(NapReason.Tray,      900_000, s));

        // Tray deep sleep has its own switch; minimized deep sleep doesn't.
        s.TrayDeepSleepEnabled = false;
        Assert.Equal(new BriefWakePlan(5_000, 120_000, "Tray Wake"), BriefWakeSchedule.For(NapReason.Tray, 10_000_000, s));
    }

    // ── How the engine uses them ─────────────────────────────────────────

    private static string Engine()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        return File.ReadAllText(Path.Combine(dir, "src", "Systema", "Services", "TaskSleepService.cs"));
    }

    [Fact]
    public void TheEngine_DecidesWakesThroughTheRules()
    {
        var e = Engine();
        Assert.Contains("NapWakeRules.Decide(new NappedFacts(", e);
        Assert.Contains("BriefWakeRules.Decide(kind,", e);
        Assert.Contains("BriefWakeSchedule.For(kind, nappedForMs, s)", e);
        // The old hand-copied chains are gone.
        Assert.DoesNotContain("bool   shouldRestore = false;", e);
        Assert.DoesNotContain("\"Tray Deep Wake\"", e);
    }

    // The nap triggers (minimize, tray, background, idle) don't re-check audio: ShouldSkip's
    // "Audio/media active" rule turns every audio-playing app away just before them, using the same
    // check on the same data. If that rule ever moved or went, audio apps would start napping.
    [Fact]
    public void AudioProtection_ComesFromTheSkipRules()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "src", "Systema"))) dir = Directory.GetParent(dir)!.FullName;
        var skip = File.ReadAllText(Path.Combine(dir, "src", "Systema", "Services", "TaskSleepService.SkipRules.cs"));
        Assert.Contains("new(\"Audio/media active\", SkipTag.Activity,", skip);
        Assert.Contains("c => c.AudioPids != null && IsAudioProtected(c.Proc.Id, c.Proc.ProcessName, c.AudioPids)", skip);

        // The skip rules are a gate, and every gate runs before any trigger.
        var triggers = File.ReadAllText(Path.Combine(dir, "src", "Systema", "Services", "TaskSleepService.NapTriggers.cs"));
        Assert.Contains("new(\"Skip rules\", (c, p) =>", triggers);
        Assert.Contains("if (!ShouldSkip(p, c.ProtectedPids, c.S, c.Rules, c.AudioPids)) return false;", triggers);
        int gates = triggers.IndexOf("foreach (var gate in NapGates)", StringComparison.Ordinal);
        int trig  = triggers.IndexOf("foreach (var trigger in NapTriggers)", StringComparison.Ordinal);
        Assert.True(gates > 0 && trig > gates, "the gates must run before the nap triggers");
        Assert.DoesNotContain("IsAudioProtected", triggers);   // unreachable in a trigger; see above
    }
}
