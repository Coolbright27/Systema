// ════════════════════════════════════════════════════════════════════════════
// StartupItem.cs  ·  Startup entry from registry or Task Scheduler
// ════════════════════════════════════════════════════════════════════════════
//
// Represents one startup entry discovered by StartupService, with a Source
// discriminator (Registry vs TaskScheduler), display Name, executable Path,
// and current IsEnabled state. Used in MemoryViewModel's startup list and
// counted by HealthScoreService for the Startup sub-score.
//
// RELATED FILES
//   StartupService.cs     — creates and returns StartupItem instances
//   MemoryViewModel.cs    — binds the list and enable/disable commands
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

public enum StartupItemSource { Registry, TaskScheduler }

/// <summary>Estimated boot-time impact of a startup program.</summary>
public enum StartupImpact { Unknown, Low, Medium, High }

public class StartupItem
{
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public StartupItemSource Source { get; set; }
    public string RegistryKey { get; set; } = string.Empty;

    /// <summary>Estimated impact on boot time, classified from the Command path.</summary>
    public StartupImpact Impact { get; set; } = StartupImpact.Unknown;

    /// <summary>Human-readable label for the Impact level.</summary>
    public string ImpactLabel => Impact switch
    {
        StartupImpact.High   => "High",
        StartupImpact.Medium => "Medium",
        StartupImpact.Low    => "Low",
        _                    => "—"
    };
}
