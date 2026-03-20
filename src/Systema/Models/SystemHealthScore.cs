// ════════════════════════════════════════════════════════════════════════════
// SystemHealthScore.cs  ·  Health score data shape with sub-scores and grade
// ════════════════════════════════════════════════════════════════════════════
//
// Immutable result record returned by HealthScoreService.ComputeAsync. Carries
// the Overall 0-100 score, individual sub-scores (CPU, RAM, Startup, Security),
// and a letter Grade string ("A"–"F"). DashboardViewModel binds directly to
// these properties.
//
// RELATED FILES
//   HealthScoreService.cs   — computes and returns instances of this class
//   DashboardViewModel.cs   — displays Overall, sub-scores, and Grade in the UI
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Models;

public class SystemHealthScore
{
    public int Overall { get; set; }
    public int CpuScore { get; set; }
    public int RamScore { get; set; }
    public int StartupScore { get; set; }
    public int SecurityScore { get; set; }
    public string Grade => Overall switch
    {
        >= 85 => "Excellent",
        >= 70 => "Good",
        >= 50 => "Fair",
        _ => "Poor"
    };
}
