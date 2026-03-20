// ════════════════════════════════════════════════════════════════════════════
// KillListEntry.cs  ·  Service kill/restore entry for GameBoosterService
// ════════════════════════════════════════════════════════════════════════════
//
// Records one Windows service that GameBoosterService stops when boost mode
// activates. Stores the ServiceName and the pre-boost StartType so the service
// can be restored to its original state when the game exits.
//
// RELATED FILES
//   GameBoosterService.cs    — builds and walks the KillListEntry collection
//   GameBoosterViewModel.cs  — exposes the kill list for user editing
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

public class KillListEntry
{
    public string ServiceName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
