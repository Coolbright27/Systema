// EcoreAffinityTests.cs
//
// Napped processes are confined to the E-cores by affinity mask. The enforcement tick re-asserted
// CPU priority and the kernel CPU cap every cycle but NOT affinity — so any app that called
// SetProcessAffinityMask on itself (common when spinning up worker pools, on config reload, or as
// a multi-process app launching a child) silently escaped the E-cores while still counting as
// fully napped. That is the "sometimes not everything gets moved to the E-cores" report: it WAS
// moved, then drifted back, and nothing put it there again.
//
// Source-scan (not reflection) because Application Control blocks loading Systema.dll into the
// test host on this machine — same constraint as the other *SourceTests.

using System.IO;
using Xunit;

namespace Systema.Tests;

public class EcoreAffinityTests
{
    private static string TaskSleep()
    {
        var asmDir = Path.GetDirectoryName(typeof(EcoreAffinityTests).Assembly.Location)!;
        var root   = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        var path   = Path.Combine(root, "src", "Systema", "Services", "TaskSleepService.cs");
        Assert.True(File.Exists(path), $"Expected file not found: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void AffinityIsReassertedOnTheEnforcementTick()
    {
        var src = TaskSleep();

        Assert.Contains("affinity drifted off the E-cores", src);
        Assert.Contains("GetProcessAffinityMask(h, out UIntPtr nowMask, out _)", src);

        // It must sit in the same enforcement block that re-asserts priority and the CPU cap —
        // that is the loop with a live handle running every tick.
        int cap = src.IndexOf("UpdateCpuCap(pid, Math.Clamp(s.NappedCpuCapPercent, 1, 100));",
                              System.StringComparison.Ordinal);
        int aff = src.IndexOf("affinity drifted off the E-cores", System.StringComparison.Ordinal);
        Assert.True(cap > 0 && aff > cap, "the affinity re-assert belongs in the enforcement tick");
    }

    [Fact]
    public void OnlyProcessesSystemaMovedAreReconfined()
    {
        // _originalAffinities holds what we captured before changing it, so it doubles as the
        // record of "we moved this one". Without that gate the re-assert could confine a process
        // Systema never touched.
        var src = TaskSleep();
        Assert.Contains("_throttledPids.ContainsKey(pid) && _originalAffinities.ContainsKey(pid)", src);
    }

    [Fact]
    public void TheECoreConditionIsOneExpression()
    {
        // The two branches evaluated to the same thing — the non-force one omitted DetectECores,
        // but GetOrDetectECoreMask gates on it regardless, so the ternary only looked meaningful.
        var src = TaskSleep();
        Assert.Contains("bool   moveToECores = s.MoveToECores && s.DetectECores;", src);
        Assert.DoesNotContain("forceMaxThrottle ? (s.MoveToECores && s.DetectECores) : s.MoveToECores", src);
    }
}
