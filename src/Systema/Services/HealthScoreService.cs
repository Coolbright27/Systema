// ════════════════════════════════════════════════════════════════════════════
// HealthScoreService.cs  ·  Computes 0-100 system health score from sub-scores
// ════════════════════════════════════════════════════════════════════════════
//
// Aggregates CPU load, RAM pressure, startup item count, and security posture
// (telemetry, visual effects, power plan) into individual 0-100 sub-scores and
// an overall weighted average. Maintains a rolling 3-scan history so
// DashboardViewModel can display an average across the last three explicit scans.
//
// RELATED FILES
//   MemoryService.cs           — supplies RAM stats for the RAM sub-score
//   StartupService.cs          — startup count used in the Startup sub-score
//   TelemetryService.cs        — telemetry state feeds the Security sub-score
//   AnimationService.cs        — visual effects state feeds the Security sub-score
//   PowerPlanService.cs        — power plan feeds the Security sub-score
//   Models/SystemHealthScore.cs — data shape returned by ComputeAsync
//   DashboardViewModel.cs      — calls ComputeAsync and displays results
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using Systema.Core;
using Systema.Models;

namespace Systema.Services;

public class HealthScoreService
{
    private readonly MemoryService    _memoryService;
    private readonly StartupService   _startupService;
    private readonly TelemetryService _telemetryService;
    private readonly AnimationService _animationService;
    private readonly PowerPlanService _powerPlanService;

    // If any sub-score hangs (e.g. PerformanceCounter PDH deadlock, Task Scheduler COM),
    // we fall back to defaults after this timeout rather than blocking forever.
    private static readonly TimeSpan ScoreTimeout = TimeSpan.FromSeconds(12);

    // CPU score thresholds — percentage of total CPU utilisation
    private const float CpuExcellentThreshold = 10f;  // < 10% → 100
    private const float CpuGoodThreshold      = 30f;  // < 30% → 85
    private const float CpuFairThreshold      = 60f;  // < 60% → 65
    private const float CpuPoorThreshold      = 80f;  // < 80% → 40, else 20

    // Rolling average over the last 3 scans — prevents scores from jumping 20+ points
    // between ticks due to a momentary CPU spike or a brief GC pause during sampling.
    private readonly Queue<SystemHealthScore> _scoreHistory = new();
    private const int ScoreHistoryDepth = 3;

    // Last-known sub-scores used as timeout fallbacks.
    // Seeded with realistic defaults: CPU/RAM/Startup start at 70 (neutral assumption),
    // Security starts at 50 (deliberately pessimistic — unknown security state should
    // pull the overall score down until the first real scan completes).
    private int _lastCpuScore      = 70;
    private int _lastRamScore      = 70;
    private int _lastStartupScore  = 70;
    private int _lastSecurityScore = 50;

    public HealthScoreService(
        MemoryService    memoryService,
        StartupService   startupService,
        TelemetryService telemetryService,
        AnimationService animationService,
        PowerPlanService powerPlanService)
    {
        _memoryService    = memoryService;
        _startupService   = startupService;
        _telemetryService = telemetryService;
        _animationService = animationService;
        _powerPlanService = powerPlanService;
    }

    public async Task<SystemHealthScore> ComputeAsync()
    {
        // All sub-scores run on large-stack threads:
        // • GetCpuScore  — PerformanceCounter makes PDH kernel calls that can exhaust the
        //                  ~1 MB threadpool stack, causing "cannot create guard page" crashes.
        // • GetStartupScore — TaskScheduler COM can also need extra stack depth.
        var cpuTask      = ThreadHelper.RunOnLargeStackAsync(GetCpuScore);
        var ramTask      = ThreadHelper.RunOnLargeStackAsync(GetRamScore);
        var startupTask  = ThreadHelper.RunOnLargeStackAsync(GetStartupScore);
        var securityTask = ThreadHelper.RunOnLargeStackAsync(GetSecurityScore);

        var allTasks = Task.WhenAll(cpuTask, ramTask, startupTask, securityTask);

        // Hard timeout — if any sub-score hangs, use a safe default (70) rather
        // than blocking the dashboard forever.
        if (await Task.WhenAny(allTasks, Task.Delay(ScoreTimeout)) != allTasks)
        {
            LoggerService.Instance.Warn("HealthScoreService",
                $"Health score computation timed out after {ScoreTimeout.TotalSeconds:0} s — using partial results");
        }

        // Read whatever completed; fall back to last-known scores for anything still pending or faulted
        int cpuScore      = SafeResult(cpuTask,      _lastCpuScore);
        int ramScore      = SafeResult(ramTask,      _lastRamScore);
        int startupScore  = SafeResult(startupTask,  _lastStartupScore);
        int securityScore = SafeResult(securityTask, _lastSecurityScore);

        // Update cached last-known scores only for tasks that actually completed
        if (cpuTask.IsCompletedSuccessfully)      _lastCpuScore      = cpuScore;
        if (ramTask.IsCompletedSuccessfully)       _lastRamScore      = ramScore;
        if (startupTask.IsCompletedSuccessfully)   _lastStartupScore  = startupScore;
        if (securityTask.IsCompletedSuccessfully)  _lastSecurityScore = securityScore;

        int overall = (cpuScore + ramScore + startupScore + securityScore) / 4;

        // Push raw result into rolling history, keep last ScoreHistoryDepth entries
        _scoreHistory.Enqueue(new SystemHealthScore
        {
            Overall       = overall,
            CpuScore      = cpuScore,
            RamScore      = ramScore,
            StartupScore  = startupScore,
            SecurityScore = securityScore
        });
        while (_scoreHistory.Count > ScoreHistoryDepth)
            _scoreHistory.Dequeue();

        // Return the average across history — smooths out momentary spikes
        return new SystemHealthScore
        {
            Overall       = (int)_scoreHistory.Average(s => s.Overall),
            CpuScore      = (int)_scoreHistory.Average(s => s.CpuScore),
            RamScore      = (int)_scoreHistory.Average(s => s.RamScore),
            StartupScore  = (int)_scoreHistory.Average(s => s.StartupScore),
            SecurityScore = (int)_scoreHistory.Average(s => s.SecurityScore)
        };
    }

    // ── Sub-scores ────────────────────────────────────────────────────────────

    private static int GetCpuScore()
    {
        try
        {
            using var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            counter.NextValue();
            Thread.Sleep(500);
            float cpu = counter.NextValue();
            return cpu < CpuExcellentThreshold ? 100
                 : cpu < CpuGoodThreshold      ? 85
                 : cpu < CpuFairThreshold      ? 65
                 : cpu < CpuPoorThreshold      ? 40
                 : 20;
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warn("HealthScoreService", $"GetCpuScore failed — using fallback 70: {ex.Message}");
            return 70;
        }
    }

    private int GetRamScore()
    {
        try
        {
            long total = _memoryService.GetTotalRamMb();
            long available = _memoryService.GetAvailableRamMb();
            if (total == 0) return 70;
            double usedPercent = (double)(total - available) / total * 100;
            return usedPercent < 40 ? 100 : usedPercent < 60 ? 80 : usedPercent < 80 ? 55 : 30;
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warn("HealthScoreService", $"GetRamScore failed — using fallback 70: {ex.Message}");
            return 70;
        }
    }

    private int GetStartupScore()
    {
        try
        {
            var items = _startupService.GetStartupItems();
            int enabled = items.Count(i => i.IsEnabled);
            return enabled <= 3 ? 100 : enabled <= 6 ? 80 : enabled <= 10 ? 60 : 35;
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warn("HealthScoreService", $"GetStartupScore failed — using fallback 70: {ex.Message}");
            return 70;
        }
    }

    private int GetSecurityScore()
    {
        int score = 50;
        try { if (_telemetryService.IsTelemetryDisabled()) score += 25; }
        catch (Exception ex) { LoggerService.Instance.Warn("HealthScoreService", $"Telemetry check failed in security score: {ex.Message}"); }

        try { if (_animationService.AreAnimationsDisabled()) score += 10; }
        catch (Exception ex) { LoggerService.Instance.Warn("HealthScoreService", $"Animation check failed in security score: {ex.Message}"); }

        try
        {
            var plan = _powerPlanService.GetActivePlan();
            if (plan.Contains("Performance", StringComparison.OrdinalIgnoreCase)) score += 15;
        }
        catch (Exception ex) { LoggerService.Instance.Warn("HealthScoreService", $"Power plan check failed in security score: {ex.Message}"); }

        return Math.Min(score, 100);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int SafeResult(Task<int> task, int fallback)
    {
        if (task.IsCompletedSuccessfully) return task.Result;
        return fallback;
    }
}
