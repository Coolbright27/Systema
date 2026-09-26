// ════════════════════════════════════════════════════════════════════════════
// UiLagMonitor.cs  ·  Logs short UI-thread stalls ("hitches")
// ════════════════════════════════════════════════════════════════════════════
//
// CrashGuard only reports a UI thread that has been frozen for seconds. A hitch is much shorter:
// 100-500 ms where the window can't repaint or respond. This watches for exactly that, so a
// "the UI hitches" report comes with numbers and the page it happened on, in the session log:
//
//   [UiLag] UI thread busy 340 ms (2 stalls over 100 ms, page: TaskSleep)
//
// How: a background thread posts an empty work item to the UI dispatcher every 250 ms and times
// how long it waits to run. Only while the main window is actually on screen (a hidden window in
// Ghost Mode runs at idle priority, where waiting is expected), and at most one line every 3 s so
// a bad stretch can't flood the log. Costs nothing measurable: one no-op per quarter second.
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Windows.Threading;
using Systema.Services;

namespace Systema.Core;

public static class UiLagMonitor
{
    private const int ProbeEveryMs   = 250;
    private const int ReportAboveMs  = 100;
    private const int MinLogGapMs    = 3_000;

    private static Thread? _thread;
    private static volatile bool _running;

    /// <summary>True while the main window is visible; set by MainWindow.</summary>
    public static volatile bool WindowVisible;

    /// <summary>Nav key of the page on screen, for the log line; set by MainWindow.</summary>
    public static volatile string? CurrentPage;

    public static void Start(Dispatcher ui)
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(() => Loop(ui))
        {
            IsBackground = true,
            Name = "Systema UI lag monitor",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    public static void Stop() => _running = false;

    private static void Loop(Dispatcher ui)
    {
        long lastLog = 0;
        int worst = 0, stalls = 0;
        while (_running)
        {
            try
            {
                Thread.Sleep(ProbeEveryMs);
                if (!WindowVisible || ui.HasShutdownStarted) continue;

                var sw = Stopwatch.StartNew();
                var op = ui.BeginInvoke(DispatcherPriority.Normal, new Action(() => { }));
                if (!op.Task.Wait(10_000)) continue;   // a real freeze is CrashGuard's job
                int ms = (int)sw.ElapsedMilliseconds;
                if (ms < ReportAboveMs) continue;

                worst = Math.Max(worst, ms);
                stalls++;
                long now = Environment.TickCount64;
                if (now - lastLog < MinLogGapMs) continue;

                LoggerService.Instance.Info("UiLag",
                    $"UI thread busy {worst} ms ({stalls} stall{(stalls == 1 ? "" : "s")} over {ReportAboveMs} ms, page: {CurrentPage ?? "?"})");
                lastLog = now;
                worst = 0;
                stalls = 0;
            }
            catch { /* diagnostics only; never let this take anything down */ }
        }
    }
}
