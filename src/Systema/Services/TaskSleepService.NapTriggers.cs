// ════════════════════════════════════════════════════════════════════════════
// TaskSleepService.NapTriggers.cs  ·  When the Systema Engine naps a process
// ════════════════════════════════════════════════════════════════════════════
//
// Every tick, each running process goes through two ordered lists:
//
//   1. GATES — stop here, nap nothing (first gate that stops wins):
//        Systema itself · busy app kept awake · mid brief wake · already napped ·
//        skip rules (SkipRules.cs) · just woken (5 s cooldown)
//
//   2. NAP TRIGGERS — the first that applies decides (order is load-bearing):
//        Minimized / hidden → Tray → Background (unfocused) → Idle (≈0% CPU) → Known waster
//
//      Each trigger says:
//        AppliesTo  is this process in its situation right now?
//        Wait       how long it has been (grace / unfocused / idle / over-quota timers live here):
//                     Due    → Nap it
//                     NotYet → still waiting
//                     Hold   → leave it this tick, try nothing else (cloud sync mid-transfer)
//        Nap        throttle it and record why
//        Owns       once it applies, later triggers aren't tried even while it waits.
//                   Minimized, tray and known-waster own their processes; background and idle
//                   don't, so a process waiting out the unfocused timer can still idle-nap.
//
// To add a trigger, insert a NapTrigger at the right point in NapTriggers. To see why something
// napped, read the list top to bottom: the event label each trigger logs is in its Nap action.
//
// This file was split out of Tick in 0.7.359 with no change in behaviour: the same checks, timers,
// order and log lines as the inline if/else it replaced. Wake decisions live in Core/NapRules.cs;
// "never nap" rules in TaskSleepService.SkipRules.cs.
//
// None of the triggers re-checks audio: the skip-rules gate ("Audio/media active") already turned
// away every process playing or recording audio, with the same check on the same data.
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using Systema.Core;
using Systema.Models;

namespace Systema.Services;

public sealed partial class TaskSleepService
{
    /// <summary>Everything the gates and triggers need for one tick, gathered once in Tick.</summary>
    private sealed record NapTickContext(
        TaskSleepSettings S,
        Dictionary<string, TaskSleepAppRule> Rules,
        Process[] All,
        Dictionary<int, int> ParentMap,
        Dictionary<int, double> CpuMap,
        HashSet<int> ProtectedPids,
        HashSet<int> AudioPids,
        HashSet<int> MinimizedPids,   // minimized AND fully-covered (hidden) windows
        HashSet<int> HiddenPids,
        HashSet<int> TrayPids,
        HashSet<int> BusyAwakePids);

    // ── 1. Gates ─────────────────────────────────────────────────────────────

    /// <summary>A check that, when it returns true, ends this process's turn with no nap.</summary>
    private sealed record NapGate(string Name, Func<NapTickContext, Process, bool> Stops);

    private NapGate[]? _napGates;

    /// <summary>The gates, in order. First one that stops wins.</summary>
    private NapGate[] NapGates => _napGates ??= new NapGate[]
    {
        // Systema must never be throttled, including a second instance (the name catches that;
        // the "Systema itself" skip rule only knows its own PID).
        new("Systema itself", (_, p) => p.ProcessName.Equals("Systema", StringComparison.OrdinalIgnoreCase)),

        // Part of a minimized/tray app whose whole tree is over the busy-CPU threshold: kept awake
        // on every nap path (woken earlier in Tick if it was napped). Grace cleared so it re-arms
        // cleanly once it settles.
        new("Busy app kept awake", (c, p) =>
        {
            if (!c.BusyAwakePids.Contains(p.Id)) return false;
            StateFor(p.Id).SkipReason = "Busy app — kept awake";
            _minimizeGraceSince.Remove(p.Id);
            _trayGraceSince.Remove(p.Id);
            return true;
        }),

        // Mid brief wake: BeginBriefWake takes the pid out of _throttledPids, so the wake rules
        // can't see it. Its own rule (Core/NapRules.cs BriefWakeRules) wakes it for real, re-naps
        // it when the window is over, or lets the window run.
        new("Mid brief wake", (c, p) =>
        {
            if (!TryGetBriefWakeKind(p.Id, c.S, out NapReason kind)) return false;
            HandleBriefWakeInProgress(p, kind, kind == NapReason.Tray ? c.TrayPids : c.MinimizedPids,
                                      c.ProtectedPids, c.AudioPids, c.S);
            return true;
        }),

        new("Already napped", (_, p) => _throttledPids.ContainsKey(p.Id)),

        // The "never nap" rules (TaskSleepService.SkipRules.cs). Pending timers are cleared so the
        // monitor doesn't show "Pending" for a skipped process (e.g. Firefox playing audio).
        new("Skip rules", (c, p) =>
        {
            if (!ShouldSkip(p, c.ProtectedPids, c.S, c.Rules, c.AudioPids)) return false;
            _minimizeGraceSince.Remove(p.Id);
            _trayGraceSince.Remove(p.Id);
            if (TryState(p.Id, out var st)) { st.LowCpuTickCount = 0; st.IdleSince = null; st.OverThresholdSince = null; }
            return true;
        }),

        // Just woken: don't re-nap for 5 s.
        new("Just woken (5 s cooldown)", (_, p) =>
            TryState(p.Id, out var st) && st.RestoredAt is { } rt && (DateTime.UtcNow - rt).TotalMilliseconds < 5_000),
    };

    // ── 2. Nap triggers ──────────────────────────────────────────────────────

    private enum TriggerWait { NotYet, Due, Hold }

    private sealed record NapTrigger(
        string Name,
        bool Owns,
        Func<NapTickContext, Process, bool> AppliesTo,
        Func<NapTickContext, Process, TriggerWait> Wait,
        Action<NapTickContext, Process> Nap);

    private NapTrigger[]? _napTriggers;

    /// <summary>The nap triggers, in order. See the file header for how they're read.</summary>
    private NapTrigger[] NapTriggers => _napTriggers ??= new NapTrigger[]
    {
        // Minimized, or open but fully covered by other windows ("Nap hidden apps"). Naps the whole
        // app tree as a unit. (Busy minimized apps never get here: the busy gate keeps them awake.)
        new("Minimized or hidden", Owns: true,
            AppliesTo: (c, p) => (c.S.MinimizeNapEnabled || c.S.HiddenNapEnabled) && c.MinimizedPids.Contains(p.Id),
            Wait: MinimizedOrHiddenWait,
            Nap:  NapMinimizedOrHidden),

        // No visible window (tray / background helper) for the grace period. Naps the whole tree.
        new("Tray", Owns: true,
            AppliesTo: (c, p) => c.S.TrayNapEnabled && c.TrayPids.Contains(p.Id) && !_napBuckets.Is(p.Id, NapReason.Tray),
            Wait: (c, p) => GraceWait(_trayGraceSince, p.Id, MinimizeTrayGraceMs),
            Nap:  NapTray),

        // Unfocused for BackgroundNapAfterMs: the broadest nap, anything the user isn't using.
        // Session 0 (services) never has a foreground window, so it would always "time out"; it's
        // left alone. Minimized and tray apps are the triggers above's job.
        new("Background", Owns: false,
            AppliesTo: (c, p) => c.S.BackgroundNapEnabled && p.SessionId != 0 &&
                                 !c.MinimizedPids.Contains(p.Id) && !c.TrayPids.Contains(p.Id),
            Wait: BackgroundWait,
            Nap:  NapBackground),

        // ≈0% CPU for IdleNapAfterMs, whatever else is going on. Session 0 excluded (napping idle
        // services saves nothing and risks breaking drivers).
        new("Idle", Owns: false,
            AppliesTo: (c, p) => c.S.IdleNapEnabled && p.SessionId != 0,
            Wait: IdleWait,
            Nap:  NapIdle),

        // Known background wasters (AggressiveNapTargets): napped even at low CPU. A cloud-sync
        // agent that's mid-transfer is held off so the sync can finish.
        new("Known waster", Owns: true,
            AppliesTo: (c, p) => AggressiveNapTargets.Contains(p.ProcessName) &&
                                 !c.TrayPids.Contains(p.Id) && !c.MinimizedPids.Contains(p.Id),
            Wait: KnownWasterWait,
            Nap:  NapKnownWaster),

        // Deliberately absent: high-CPU "off-screen" napping. Nap decisions are visibility and time
        // based, so a process none of these triggers applies to is left alone whatever its CPU.
    };

    /// <summary>One process's turn: the gates, then the first trigger that applies.</summary>
    private void RunNapRules(NapTickContext c, Process proc)
    {
        foreach (var gate in NapGates)
            if (gate.Stops(c, proc)) return;

        foreach (var trigger in NapTriggers)
        {
            if (!trigger.AppliesTo(c, proc)) continue;
            switch (trigger.Wait(c, proc))
            {
                case TriggerWait.Due:    trigger.Nap(c, proc); return;
                case TriggerWait.Hold:   return;
                case TriggerWait.NotYet: if (trigger.Owns) return; break;
            }
        }
    }

    // ── Trigger parts ────────────────────────────────────────────────────────

    /// <summary>Starts a grace timer the first time it's asked, then reports whether it has run out.</summary>
    private static TriggerWait GraceWait(Dictionary<int, DateTime> since, int pid, double graceMs)
    {
        if (!since.ContainsKey(pid)) since[pid] = DateTime.UtcNow;
        return (DateTime.UtcNow - since[pid]).TotalMilliseconds >= graceMs ? TriggerWait.Due : TriggerWait.NotYet;
    }

    private TriggerWait MinimizedOrHiddenWait(NapTickContext c, Process p)
    {
        // Hidden (fully-covered) apps get their own, longer, user-adjustable grace so a window
        // you're just flipping in front of isn't napped the instant it's covered. Minimized apps
        // keep the short MinimizeTrayGraceMs. IsPendingHidden tells the monitor which one to show.
        bool pendingHidden = c.HiddenPids.Contains(p.Id);
        if (!_minimizeGraceSince.ContainsKey(p.Id)) _minimizeGraceSince[p.Id] = DateTime.UtcNow;
        StateFor(p.Id).IsPendingHidden = pendingHidden;
        double graceMs = pendingHidden ? c.S.HiddenNapGraceMs : MinimizeTrayGraceMs;
        return (DateTime.UtcNow - _minimizeGraceSince[p.Id]).TotalMilliseconds >= graceMs
            ? TriggerWait.Due : TriggerWait.NotYet;
    }

    private void NapMinimizedOrHidden(NapTickContext c, Process proc)
    {
        _minimizeGraceSince.Remove(proc.Id);
        if (!TryThrottle(proc, c.S, c.Rules, forceMaxThrottle: true)) return;

        double mnCpu = TryState(proc.Id, out var lcMn) && lcMn.LastCpuPercent is { } vMn ? vMn : 0;
        StateFor(proc.Id).CpuAtThrottle = mnCpu;
        StateFor(proc.Id).ThrottledAt   = DateTime.UtcNow;
        MarkNap(proc.Id, NapReason.Minimized);
        StateFor(proc.Id).NapSince ??= DateTime.UtcNow;   // deep-sleep timer
        _nextBriefWakeAt[proc.Id] = DateTime.UtcNow.AddMilliseconds(c.S.MinimizedBriefWakeIntervalMs);
        _briefWakeEndAt.Remove(proc.Id);
        bool isHidden = c.HiddenPids.Contains(proc.Id);
        AddEvent(proc.ProcessName, proc.Id,
            isHidden ? "Hidden Nap"                  : "Minimize Nap",
            isHidden ? "hidden behind other windows" : "app minimized");
        // Nap the whole tree as a unit (renderers/helpers too, not just the window owner); the
        // full-app wake sweep brings it all back together when the window returns.
        NapChildProcesses(proc.Id, c.All, c.ParentMap, c.ProtectedPids, c.AudioPids, c.S, c.Rules);
    }

    private void NapTray(NapTickContext c, Process proc)
    {
        _trayGraceSince.Remove(proc.Id);
        if (!TryThrottle(proc, c.S, c.Rules, forceMaxThrottle: true)) return;

        double tnCpu = TryState(proc.Id, out var lcTn) && lcTn.LastCpuPercent is { } vTn ? vTn : 0;
        StateFor(proc.Id).CpuAtThrottle = tnCpu;
        StateFor(proc.Id).ThrottledAt   = DateTime.UtcNow;
        MarkNap(proc.Id, NapReason.Tray);
        _trayNextBriefWakeAt[proc.Id] = DateTime.UtcNow.AddMilliseconds(c.S.TrayBriefWakeIntervalMs);
        _trayBriefWakeEndAt.Remove(proc.Id);
        StateFor(proc.Id).NapSince ??= DateTime.UtcNow;   // deep-sleep timer
        AddEvent(proc.ProcessName, proc.Id, "Tray Nap", "no visible window");
        NapChildProcesses(proc.Id, c.All, c.ParentMap, c.ProtectedPids, c.AudioPids, c.S, c.Rules);
    }

    private TriggerWait BackgroundWait(NapTickContext c, Process p)
    {
        var st = StateFor(p.Id);
        if (st.LastForegroundAt is not { } lastFg)
        {
            st.LastForegroundAt = DateTime.UtcNow;   // first time seen: assume it just started
            return TriggerWait.NotYet;
        }
        return (DateTime.UtcNow - lastFg).TotalMilliseconds >= c.S.BackgroundNapAfterMs
            ? TriggerWait.Due : TriggerWait.NotYet;
    }

    private void NapBackground(NapTickContext c, Process proc)
    {
        if (!TryThrottle(proc, c.S, c.Rules)) return;

        DateTime lastFg = StateFor(proc.Id).LastForegroundAt ?? DateTime.UtcNow;
        StateFor(proc.Id).ThrottledAt = DateTime.UtcNow;
        double bgCpu = TryState(proc.Id, out var lcBg) && lcBg.LastCpuPercent is { } vBg ? vBg : 0;
        StateFor(proc.Id).CpuAtThrottle = bgCpu;
        MarkNap(proc.Id, NapReason.Background);
        int mins = (int)((DateTime.UtcNow - lastFg).TotalMinutes);
        AddEvent(proc.ProcessName, proc.Id, "Background Nap", $"unfocused {mins}m — CPU {bgCpu:F1}%");
    }

    private TriggerWait IdleWait(NapTickContext c, Process p)
    {
        c.CpuMap.TryGetValue(p.Id, out double idleCpu);
        if (idleCpu >= c.S.IdleNapCpuThreshold)
        {
            if (TryState(p.Id, out var busySt)) busySt.IdleSince = null;   // not idle: restart the clock
            return TriggerWait.NotYet;
        }
        var st = StateFor(p.Id);
        st.IdleSince ??= DateTime.UtcNow;
        return (DateTime.UtcNow - st.IdleSince.Value).TotalMilliseconds >= c.S.IdleNapAfterMs
            ? TriggerWait.Due : TriggerWait.NotYet;
    }

    private void NapIdle(NapTickContext c, Process proc)
    {
        c.CpuMap.TryGetValue(proc.Id, out double idleCpu);
        StateFor(proc.Id).IdleSince = null;
        if (!TryThrottle(proc, c.S, c.Rules)) return;

        StateFor(proc.Id).ThrottledAt   = DateTime.UtcNow;
        StateFor(proc.Id).CpuAtThrottle = idleCpu;
        MarkNap(proc.Id, NapReason.Idle);
        AddEvent(proc.ProcessName, proc.Id, "Idle Nap", $"CPU {idleCpu:F2}% for 2+ min");
    }

    private TriggerWait KnownWasterWait(NapTickContext c, Process p)
    {
        // Cloud sync guard: an agent that's actively syncing is left alone this tick so the
        // transfer finishes, and its clock restarts for the next idle stretch.
        if (CloudSyncAgents.Contains(p.ProcessName) &&
            c.CpuMap.TryGetValue(p.Id, out double syncCpu) && syncCpu >= CloudSyncActiveCpuThreshold)
        {
            if (TryState(p.Id, out var csst)) csst.OverThresholdSince = null;
            return TriggerWait.Hold;
        }

        var st = StateFor(p.Id);
        if (st.OverThresholdSince == null)
        {
            st.OverThresholdSince = DateTime.UtcNow;
            return TriggerWait.NotYet;
        }
        return (DateTime.UtcNow - st.OverThresholdSince.Value).TotalMilliseconds >= c.S.TimeOverQuotaMs
            ? TriggerWait.Due : TriggerWait.NotYet;
    }

    private void NapKnownWaster(NapTickContext c, Process proc)
    {
        if (!TryThrottle(proc, c.S, c.Rules)) return;   // clock kept: tried again next tick

        var st = StateFor(proc.Id);
        st.ThrottledAt        = DateTime.UtcNow;
        st.OverThresholdSince = null;
        double agCpu = st.LastCpuPercent ?? 0;
        st.CpuAtThrottle = agCpu;
        AddEvent(proc.ProcessName, proc.Id, "Napping", $"background waster — CPU {agCpu:F1}%");
    }
}
