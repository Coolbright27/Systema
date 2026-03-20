// ════════════════════════════════════════════════════════════════════════════
// RestorePointInfo.cs  ·  Data shape for a Windows System Restore point
// ════════════════════════════════════════════════════════════════════════════
//
// Populated by RestorePointService.GetRestorePointsAsync() and displayed in
// RestorePointManagerWindow.
//
// RELATED FILES
//   RestorePointService.cs           — lists / deletes restore points
//   Views/RestorePointManagerWindow  — shows the list to the user
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

public class RestorePointInfo
{
    public int    SequenceNumber { get; set; }
    public string Description   { get; set; } = string.Empty;
    public DateTime CreatedAt   { get; set; }
    public string TypeLabel     { get; set; } = string.Empty;

    /// <summary>Formatted date string for display in the UI.</summary>
    public string FormattedDate =>
        CreatedAt == DateTime.MinValue
            ? "Unknown date"
            : CreatedAt.ToString("MMM d, yyyy  h:mm tt");
}
