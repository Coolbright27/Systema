// ════════════════════════════════════════════════════════════════════════════
// BoostedGameRegistry.cs  ·  "A game boost session is running"
// ════════════════════════════════════════════════════════════════════════════
//
// One flag, so other features don't undo a boost's work mid-session. Today that means the
// power plan: VisualViewModel's AC-transition handler used to restore the pre-optimization
// plan while a game was running, which quietly cancelled the High Performance switch eight
// seconds into a session.
//
// HISTORY — this file used to track which PIDs Game Booster "owned", because Game Booster
// raised the game process's CPU, I/O, GPU and memory priority and needed the engine to keep
// off it. That whole feature was removed in v0.7.282: anti-cheat treats external manipulation
// of a protected game as tampering, and Fortnite (EAC), BeamNG and Roblox all force-closed.
// Systema no longer touches game processes at all, so there is nothing to own — only a session
// to announce.
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Core;

public static class BoostedGameRegistry
{
    /// <summary>
    /// True from the moment a boost session starts to the moment it ends. Read by features that
    /// would otherwise reverse something the boost set up while the user is still playing.
    /// </summary>
    public static volatile bool SessionActive;
}
