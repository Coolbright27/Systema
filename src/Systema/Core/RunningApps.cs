// ════════════════════════════════════════════════════════════════════════════
// RunningApps.cs  ·  What the never-nap list's "Add an app" picker offers
// ════════════════════════════════════════════════════════════════════════════
//
// One row per program name. By default the picker shows only APPS, roughly what Task Manager
// lists under "Apps": something in your session that owns a top-level window (a tray icon's
// hidden window counts, so Discord or Steam sitting in the tray still shows) and isn't part of
// Windows itself. "Show all processes" lifts that filter.
//
// Snapshot() touches the OS and runs off the UI thread; Filter() is pure and unit-tested.
//
// RELATED FILES
//   ViewModels/TaskSleepViewModel.cs — the picker (ShowAppPicker, PickerApps, AddPickedApp)
//   Views/TaskSleepView.xaml          — the Never-nap list section
//   Systema.Tests/NeverNapListTests.cs

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Systema.Core;

/// <summary>One running program, as the never-nap picker lists it.</summary>
public sealed record RunningApp(string Name, int Pid, bool IsApp);

public static class RunningApps
{
    /// <summary>Every running program, one per name, with whether it counts as an app.</summary>
    public static List<RunningApp> Snapshot()
    {
        var windowOwners = TopLevelWindowOwners();
        int session = Process.GetCurrentProcess().SessionId;
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var byName = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string name = p.ProcessName;
                if (string.IsNullOrEmpty(name) || p.Id <= 4) continue;

                bool isApp = p.SessionId == session
                          && windowOwners.Contains(p.Id)
                          && ImagePath(p.Id) is { } path
                          && !path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase);

                // One row per name; prefer the pid that makes it an app (its icon is the app's).
                if (!byName.TryGetValue(name, out var seen) || (isApp && !seen.IsApp))
                    byName[name] = new RunningApp(name, p.Id, isApp);
            }
            catch { /* exited while we looked */ }
            finally { p.Dispose(); }
        }
        return byName.Values.ToList();
    }

    /// <summary>
    /// The picker's rules: apps only unless <paramref name="showAll"/>; the search matches anywhere
    /// in the name; names already on the list, and Systema itself, are left out. Sorted by name.
    /// </summary>
    public static List<RunningApp> Filter(IEnumerable<RunningApp> all, bool showAll, string? search,
                                          IEnumerable<string> alreadyListed)
    {
        var listed = new HashSet<string>(alreadyListed, StringComparer.OrdinalIgnoreCase);
        string q = (search ?? "").Trim();
        if (q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) q = q[..^4];

        return all
            .Where(a => showAll || a.IsApp)
            .Where(a => !listed.Contains(a.Name))
            .Where(a => !a.Name.Equals("Systema", StringComparison.OrdinalIgnoreCase))
            .Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HashSet<int> TopLevelWindowOwners()
    {
        var pids = new HashSet<int>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            pids.Add((int)pid);
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    private static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
