// ════════════════════════════════════════════════════════════════════════════
// NapRules.cs  ·  The Systema Engine's wake rules, as facts in → decision out
// ════════════════════════════════════════════════════════════════════════════
//
// The nap engine (TaskSleepService) gathers facts about each napped process every tick, and
// these rules turn them into a decision. They used to be if/else chains inside the 1,200-line
// Tick; here they're an ordered list you can read top to bottom, and they're pure (no process
// handles, no clocks, no state), so every rule is unit-tested.
//
// Nothing about the engine's behaviour changed in the move (0.7.358): same order, same
// conditions, same reason strings (the monitor and the log show those).
//
//   NapWakeRules      — should a NAPPED process wake, keep napping, or get a brief wake?
//   BriefWakeRules    — a process MID-brief-wake: wake it for real, re-nap it, or wait?
//   BriefWakeSchedule — how long a brief wake lasts, when the next one is due, and its label
//
// RELATED FILES
//   Services/TaskSleepService.cs            — gathers the facts and applies the decisions
//   Services/TaskSleepService.SkipRules.cs  — processes never napped
//   Services/TaskSleepService.NapTriggers.cs — when a process gets napped (gates + nap triggers)
//   Core/NapBuckets.cs                      — which category (NapReason) a pid is napped under
//   Systema.Tests/NapRulesTests.cs

using Systema.Models;

namespace Systema.Core;

internal enum WakeAction
{
    /// <summary>Leave it napping.</summary>
    KeepNapping,
    /// <summary>Wake it fully (and log the reason).</summary>
    Wake,
    /// <summary>It's due a brief wake; queue it (the engine limits how many run at once).</summary>
    BriefWake,
}

internal readonly record struct WakeDecision(WakeAction Action, string Reason = "")
{
    public static readonly WakeDecision Keep = new(WakeAction.KeepNapping);
    public static readonly WakeDecision QueueBriefWake = new(WakeAction.BriefWake);
    public static WakeDecision WakeUp(string reason) => new(WakeAction.Wake, reason);
}

/// <summary>What the engine knows about one napped process this tick.</summary>
internal readonly record struct NappedFacts(
    bool IsAlive,
    /// <summary>Protected as in use: the foreground app, visible on a monitor, or its family/tree.</summary>
    bool InUse,
    /// <summary>It is itself the foreground process or has a window visible on a monitor.</summary>
    bool OpenedDirectly,
    /// <summary>The category it's napped under, or null (a known-waster nap, or a nap child).</summary>
    NapReason? Reason,
    bool StillMinimized,
    bool StillInTray,
    bool HasAudio,
    /// <summary>Its next brief wake time has come (or it never had one scheduled).</summary>
    bool BriefWakeDue,
    /// <summary>Time since it was napped, for the classic time-based restore; null if unknown.</summary>
    double? NappedForMs,
    /// <summary>Its CPU this tick, if sampled.</summary>
    double? CpuPercent);

internal static class NapWakeRules
{
    /// <summary>
    /// The wake rules, FIRST MATCH WINS (order is load-bearing, exactly as it was in Tick):
    ///   1. It exited                                  → wake (clean up)
    ///   2. The user opened it directly                → wake
    ///   3. Its app is in use and it's a background/idle/waster nap → wake with the app
    ///   4. Minimized nap:  shown again or playing audio → wake; else brief wake when due
    ///   5. Tray nap:       got a window, audio, or its app is in use → wake; else brief wake when due
    ///   6. Background / idle nap: playing audio        → wake; else keep napping
    ///   7. "Nap until used" is on                     → keep napping
    ///   8. Classic timed nap: max duration, or its CPU dropped after the minimum → wake
    /// </summary>
    public static WakeDecision Decide(in NappedFacts f, TaskSleepSettings s)
    {
        if (!f.IsAlive)                        return WakeDecision.WakeUp("process exited");
        if (f.InUse && f.OpenedDirectly)       return WakeDecision.WakeUp("opened by user");

        // Minimize/tray naps are handled by their own rules below: the user deliberately hid
        // those, so an in-use sibling alone doesn't wake a minimized one.
        if (f.InUse && f.Reason is not (NapReason.Minimized or NapReason.Tray))
            return WakeDecision.WakeUp("app family focused");

        bool gameBlocksWakes = s.IsGameModeActive && s.SuppressBriefWakesDuringGameMode;

        switch (f.Reason)
        {
            case NapReason.Minimized:
                if (!f.StillMinimized || f.HasAudio)
                    return WakeDecision.WakeUp(f.HasAudio ? "audio detected" : "app un-minimized");
                return !gameBlocksWakes && f.BriefWakeDue ? WakeDecision.QueueBriefWake : WakeDecision.Keep;

            case NapReason.Tray:
                // A windowless helper never regains a window of its own, so "its app is in use"
                // (InUse) has to wake it too, or it cycles brief wakes while the app is open.
                if (!f.StillInTray || f.HasAudio || f.InUse)
                    return WakeDecision.WakeUp(f.HasAudio    ? "audio detected"
                                             : !f.StillInTray ? "window appeared"
                                             :                  "app family focused");
                return !gameBlocksWakes && f.BriefWakeDue ? WakeDecision.QueueBriefWake : WakeDecision.Keep;

            case NapReason.Background:
            case NapReason.Idle:
                return f.HasAudio ? WakeDecision.WakeUp("audio detected") : WakeDecision.Keep;
        }

        if (s.PersistentNapEnabled) return WakeDecision.Keep;   // "Nap until used": only focus wakes it

        if (f.NappedForMs is { } elapsed)
        {
            if (elapsed >= s.MaxAdjustmentDurationMs)
                return WakeDecision.WakeUp("max duration reached");
            if (elapsed >= s.MinAdjustmentDurationMs && f.CpuPercent is { } cpu && cpu < s.ProcessCpuStopPercent)
                return WakeDecision.WakeUp($"CPU dropped to {cpu:F1}%");
        }
        return WakeDecision.Keep;
    }
}

internal enum BriefWakeAction { Wait, FullWake, ReNap }

internal static class BriefWakeRules
{
    /// <summary>
    /// A minimized/hidden or tray app in the middle of a brief wake:
    ///   • shown again, focused, or playing audio → wake it for real, right away
    ///     (not after the window ends, or it sits at the loosened cap and feels broken)
    ///   • its wake window is over               → re-nap
    ///   • otherwise                             → let the window run
    /// </summary>
    public static (BriefWakeAction Action, string Reason) Decide(
        NapReason kind, bool stillHidden, bool userFocused, bool hasAudio, bool windowOver)
    {
        if (!stillHidden || userFocused || hasAudio)
            return (BriefWakeAction.FullWake,
                    hasAudio    ? "audio detected"
                  : userFocused ? "opened by user"
                  : kind == NapReason.Tray ? "window appeared"
                  :                          "window shown again");   // un-minimized or no longer covered
        return windowOver ? (BriefWakeAction.ReNap, "brief wake ended") : (BriefWakeAction.Wait, "");
    }
}

/// <summary>How long a brief wake lasts, when the next is due, and what the log calls it.</summary>
internal readonly record struct BriefWakePlan(int DurationMs, int NextIntervalMs, string EventLabel);

internal static class BriefWakeSchedule
{
    /// <summary>
    /// Deep sleep: once an app has been napped past the threshold its brief wakes come less often.
    /// Minimized apps always use deep sleep past MinimizeDeepSleepThresholdMs; tray apps only when
    /// "Tray deep sleep" is on.
    /// </summary>
    public static BriefWakePlan For(NapReason kind, double nappedForMs, TaskSleepSettings s)
    {
        if (kind == NapReason.Tray)
        {
            bool deep = s.TrayDeepSleepEnabled && nappedForMs >= s.TrayDeepSleepThresholdMs;
            return new(s.TrayBriefWakeDurationMs,
                       deep ? s.TrayDeepSleepWakeIntervalMs : s.TrayBriefWakeIntervalMs,
                       deep ? "Tray Deep Wake" : "Tray Wake");
        }

        bool minDeep = nappedForMs >= s.MinimizeDeepSleepThresholdMs;
        return new(s.MinimizedBriefWakeDurationMs,
                   minDeep ? s.MinimizeDeepSleepWakeIntervalMs : s.MinimizedBriefWakeIntervalMs,
                   minDeep ? "Deep Wake" : "Brief Wake");
    }
}
