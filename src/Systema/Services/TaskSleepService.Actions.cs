// ════════════════════════════════════════════════════════════════════════════
// TaskSleepService.Actions.cs  ·  What napping actually does to a process
// ════════════════════════════════════════════════════════════════════════════
//
// The engine applies EIGHT distinct things to a napped process, spread across
// TryThrottle, and reverses seven of them in TryRestoreProcess. This file is the
// manifest: one row per action, saying what it changes, whether it is reversible,
// and which setting gates it.
//
// WHY A MANIFEST RATHER THAN A DELEGATE TABLE
//   TryThrottle is the hot path (every candidate process, every tick) and its
//   actions share mutable local state: the captured original priority, and a
//   `changed` flag that decides whether the trailing work runs at all. Rewriting
//   it as a list of delegates would mean threading that state through a context
//   object for modest benefit and real risk of a subtle behaviour change.
//
//   The actual bug class worth defending against is an action that gets applied
//   but not reversed. That is not hypothetical: the same mistake shipped twice
//   this month in core parking, where settings were written and never restored
//   because apply and remove walked different lists. So the manifest is data a
//   TEST reads, asserting every reversible action appears in BOTH TryThrottle
//   and TryRestoreProcess. Adding an action means adding a row here, and the
//   test then insists you wire up its restore.
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Services;

public sealed partial class TaskSleepService
{
    /// <summary>What a nap action touches, for reasoning about blast radius.</summary>
    internal enum NapEffect
    {
        /// <summary>Scheduling: priority, affinity, CPU rate.</summary>
        Cpu,
        /// <summary>Disk queue priority.</summary>
        Io,
        /// <summary>Page priority and working set.</summary>
        Memory,
        /// <summary>GPU scheduling priority.</summary>
        Gpu,
        /// <summary>Processor power hints (EcoQoS).</summary>
        Power,
    }

    internal sealed record NapAction(
        string Name,
        NapEffect Effect,
        /// <summary>The call in TryThrottle.</summary>
        string ApplyCall,
        /// <summary>The call in TryRestoreProcess, or null when the action cannot be undone.</summary>
        string? RestoreCall,
        /// <summary>The setting that gates it, or null when it always applies to a napped process.</summary>
        string? Setting,
        string Notes);

    /// <summary>
    /// Every action napping performs. Kept in apply order.
    ///
    /// RestoreCall of null means genuinely irreversible, not forgotten. Only working-set trimming
    /// qualifies: pages are handed back to the OS and fault in again on their own, so there is
    /// nothing to undo. Anything else with a null restore is a bug the test will catch.
    /// </summary>
    internal static readonly NapAction[] NapActions =
    {
        new("CPU priority", NapEffect.Cpu,
            "SetPriorityClass", "SetPriorityClass", "LowerCpuPriority",
            "Idle class. The original is captured first so restore returns the real value, not a guess."),

        new("Efficiency mode", NapEffect.Power,
            "SetEfficiencyMode", "SetEfficiencyMode", null,
            "EcoQoS. Asks the scheduler to prefer efficiency cores and lower clocks."),

        new("I/O priority", NapEffect.Io,
            "SetIoPriorityLevel", "SetIoPriorityLevel", "LowerIoPriority",
            "Very low. Does most of the work for disk-bound background apps."),

        new("E-core affinity", NapEffect.Cpu,
            "SetProcessAffinityMask", "SetProcessAffinityMask", "MoveToECores",
            "Hybrid CPUs only. Re-asserted each tick because affinity drifts back."),

        new("Memory priority", NapEffect.Memory,
            "SetMemoryPriority", "SetMemoryPriority", "LowerMemoryPriority",
            "Lowest page priority, so these pages are evicted first under pressure."),

        new("Working set trim", NapEffect.Memory,
            "TrimProcessWorkingSet", null, "TrimWorkingSet",
            "IRREVERSIBLE BY DESIGN. Pages go to the standby list and fault back in when needed."),

        new("CPU cap", NapEffect.Cpu,
            "ApplyCpuCap", "RemoveCpuCap", "NappedCpuCapEnabled",
            "Job Object rate control. Fails on processes already in a job; falls back to soft throttles."),

        new("GPU priority", NapEffect.Gpu,
            "LowerNapGpuPriority", "RestoreNapGpuPriority", null,
            "Idle GPU scheduling priority, Win11+. Reversed on wake."),
    };
}
