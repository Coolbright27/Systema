namespace Systema.Models;

/// <summary>
/// Represents the configurable ProBalance settings written to Process Lasso's registry hive.
/// All threshold values are percentages unless the property name includes a unit suffix.
/// </summary>
public class ProcessLassoSettings
{
    // ── CPU Thresholds ────────────────────────────────────────────────────────
    /// <summary>System-wide CPU activity threshold (%) above which ProBalance activates.</summary>
    public int SystemwideCpuThreshold { get; set; } = 10;

    /// <summary>Per-process CPU threshold (%) above which a single process is restrained.</summary>
    public int PerProcessCpuThreshold { get; set; } = 7;

    /// <summary>
    /// Per-process CPU threshold (%) below which a previously restrained process is relaxed.
    /// </summary>
    public int PerProcessRelaxThreshold { get; set; } = 4;

    // ── Timing ────────────────────────────────────────────────────────────────
    /// <summary>Initial restraint period in milliseconds.</summary>
    public int RestraintPeriodMs { get; set; } = 1000;

    /// <summary>Maximum restraint duration in milliseconds.</summary>
    public int MaxRestraintDurationMs { get; set; } = 3000;

    /// <summary>Minimum restraint duration in milliseconds.</summary>
    public int MinRestraintDurationMs { get; set; } = 0;

    // ── Behaviour Flags ───────────────────────────────────────────────────────
    /// <summary>Lower restrained processes to Idle priority class.</summary>
    public bool LowerToIdle { get; set; } = true;

    /// <summary>Ignore the foreground (focused) process — do not restrain it.</summary>
    public bool IgnoreForeground { get; set; } = false;

    /// <summary>Exclude child processes of the foreground process from restraint.</summary>
    public bool ExcludeForegroundChildren { get; set; } = false;

    /// <summary>Skip processes that are already running below Normal priority.</summary>
    public bool ExcludeNonNormalPriority { get; set; } = false;

    /// <summary>Skip Windows service host processes.</summary>
    public bool ExcludeSystemServices { get; set; } = false;

    /// <summary>Apply Windows 11 Efficiency Mode (EcoQoS) instead of dropping priority class.</summary>
    public bool UseEfficiencyMode { get; set; } = true;

    /// <summary>Also lower the I/O priority of restrained processes.</summary>
    public bool LowerIoPriority { get; set; } = true;
}
