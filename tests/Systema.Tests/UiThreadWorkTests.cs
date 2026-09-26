using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Slow hardware queries must never run on the UI thread. 0.7.341's own lag monitor caught the
/// window frozen for 5.5 s after launch: Home's recommendation checks (a Dell BIOS WMI query of
/// ~2.4 s, NVIDIA and Intel reads) ran after an await that resumed on the UI thread, even though
/// a comment claimed they were on the thread pool. These pin the fix in place.
/// </summary>
public class UiThreadWorkTests
{
    private static string Src(params string[] parts)
    {
        var asmDir = Path.GetDirectoryName(typeof(UiThreadWorkTests).Assembly.Location)!;
        string root = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(new[] { root, "src", "Systema" }.Concat(parts).ToArray()));
    }

    [Theory]
    [InlineData("_thermal.DetectSupport()")]
    [InlineData("_intelGpu.DetectIntelAdapters()")]
    [InlineData("_nvapi.GetMaxFrameRate()")]
    [InlineData("_nvidiaGpu.DetectNvidiaAdapters()")]
    public void HomeRecommendationChecks_RunOnAWorkerThread(string call)
    {
        string vm = Src("ViewModels", "DashboardViewModel.cs");
        int start = vm.IndexOf("private async Task CheckAutoPilotStatusAsync", StringComparison.Ordinal);
        int workerOpen = vm.IndexOf("await RunOnLargeStackAsync(() =>", vm.IndexOf("var extras", start, StringComparison.Ordinal), StringComparison.Ordinal);
        int workerClose = vm.IndexOf("end of the worker-thread recommendation checks", workerOpen, StringComparison.Ordinal);
        int at = vm.IndexOf(call, start, StringComparison.Ordinal);

        Assert.True(start > 0 && workerOpen > 0 && workerClose > 0, "worker block not found");
        Assert.True(at > workerOpen && at < workerClose, $"{call} must run inside the worker block");
    }

    [Fact]
    public void Startup_RunsTheAutoPilotCheckOnce()
    {
        string vm = Src("ViewModels", "DashboardViewModel.cs");
        int init = vm.IndexOf("private async Task InitAsync()", StringComparison.Ordinal);
        int end  = vm.IndexOf('}', init);
        Assert.DoesNotContain("await CheckAutoPilotStatusAsync()", vm[init..end]);
    }

    [Fact]
    public void UpdateService_PrimesItsPerfCounterOffTheCallersThread()
    {
        // The first PerformanceCounter in a process costs ~1.5 s; StartAutoUpdate is called on
        // the UI thread during startup, so the counter has to be created inside the Task.Run.
        string svc = Src("Services", "UpdateService.cs");
        int start = svc.IndexOf("public void StartAutoUpdate()", StringComparison.Ordinal);
        int taskRun = svc.IndexOf("Task.Run(", start, StringComparison.Ordinal);
        int counter = svc.IndexOf("new PerformanceCounter(", start, StringComparison.Ordinal);
        Assert.True(taskRun > 0 && counter > taskRun, "the perf counter must be created inside the Task.Run");
    }
}
