// ════════════════════════════════════════════════════════════════════════════
// ThreadHelper.cs  ·  Runs delegates on a dedicated 8 MB stack thread
// ════════════════════════════════════════════════════════════════════════════
//
// Provides RunOnLargeStack(Action) and RunOnLargeStackAsync(Func<Task>) which
// spin up a Thread with an 8 MB stack size. Use instead of Task.Run whenever
// calling Process.GetProcesses(), deep WMI queries, or P/Invoke chains that
// risk StackOverflowException on the default 1 MB thread stack.
//
// RELATED FILES
//   (used by any ViewModel or Service doing heavy WMI or deep P/Invoke work)
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Core;

/// <summary>
/// Runs delegates on a dedicated thread with an explicit 8 MB stack.
/// Use this instead of Task.Run for work that calls Process.GetProcesses(),
/// WMI queries, or other OS APIs that can exhaust the 256 KB–1 MB threadpool stack.
/// </summary>
public static class ThreadHelper
{
    private const int StackSize = 8 * 1024 * 1024; // 8 MB — same as UI thread

    /// <summary>Runs <paramref name="work"/> on a large-stack thread and returns a Task.</summary>
    public static Task RunOnLargeStackAsync(Action work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try   { work(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, StackSize)
        {
            IsBackground = true,
            Name = "LargeStack-Worker"
        };
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Runs <paramref name="work"/> on a large-stack thread and returns a Task{T}.</summary>
    public static Task<T> RunOnLargeStackAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try   { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, StackSize)
        {
            IsBackground = true,
            Name = "LargeStack-Worker"
        };
        thread.Start();
        return tcs.Task;
    }
}
