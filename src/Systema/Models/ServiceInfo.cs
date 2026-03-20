// ════════════════════════════════════════════════════════════════════════════
// ServiceInfo.cs  ·  Windows service metadata row for the Services tab
// ════════════════════════════════════════════════════════════════════════════
//
// Carries display metadata for one Windows service: ServiceName, DisplayName,
// current StartType and Status, and a Recommendation category (Recommended vs
// Expert) plus a SafetyLevel badge. Populated by ServiceControlService and
// bound to the Services tab DataGrid.
//
// RELATED FILES
//   ServiceControlService.cs  — creates and returns ServiceInfo instances
//   ServicesViewModel.cs      — binds the collection to the Services DataGrid
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

public class ServiceInfo
{
    public string ServiceName  { get; set; } = string.Empty;
    public string DisplayName  { get; set; } = string.Empty;
    public string Status       { get; set; } = string.Empty;
    public string StartType    { get; set; } = string.Empty;
    public string Description  { get; set; } = string.Empty;
    public string Tooltip      { get; set; } = string.Empty;
    public bool   IsOptimized  { get; set; }

    /// <summary>
    /// Whether this service carries the "Recommended to disable" label.
    /// False for Print Spooler and Xbox Services unless gaming is detected.
    /// </summary>
    public bool IsRecommended { get; set; }

    /// <summary>
    /// For optional Windows features: true when the feature has been removed/disabled via DISM.
    /// Used to gray out the Remove button once a feature is already gone.
    /// </summary>
    public bool IsRemoved { get; set; }

    /// <summary>
    /// Color-coded start type: Disabled=Red, Manual=Yellow, Auto/Boot/System=Green.
    /// </summary>
    public ServiceColorState ColorState => StartType switch
    {
        "Disabled" => ServiceColorState.Red,
        "Manual"   => ServiceColorState.Yellow,
        "Auto"     => ServiceColorState.Green,
        "Boot"     => ServiceColorState.Green,
        "System"   => ServiceColorState.Green,
        _          => ServiceColorState.Neutral
    };
}

public enum ServiceColorState { Neutral, Green, Yellow, Red }
