// ════════════════════════════════════════════════════════════════════════════
// CrashGuard.cs  ·  Sentinel-file crash detection and UI heartbeat watchdog
// ════════════════════════════════════════════════════════════════════════════
//
// Writes a sentinel file before risky operations and removes it on clean exit.
// A watchdog thread monitors whether the UI heartbeat (updated by MainViewModel
// each tick) has stopped; if the process hangs the sentinel remains. On the
// next app launch, the presence of the sentinel is detected and a crash report
// dialog is shown to the user.
//
// RELATED FILES
//   App.xaml.cs              — calls CrashGuard.Initialize() at startup
//   MainViewModel.cs         — updates the UI heartbeat timestamp each tick
//   Views/CrashReportWindow.xaml — shown when a previous crash is detected
// ════════════════════════════════════════════════════════════════════════════

using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace Systema.Core;

/// <summary>
/// Crash guard that runs on a separate high-priority thread.
///
/// How it works:
///   1. Before each risky operation, the ViewModel calls <see cref="Mark"/> with a breadcrumb.
///      This is written to a sentinel file on disk IMMEDIATELY.
///   2. When the operation completes, <see cref="Clear"/> deletes the sentinel.
///   3. A background watchdog thread pings the UI thread every 3 s via <see cref="Heartbeat"/>.
///      If the UI thread doesn't respond for 5 s while a breadcrumb is active, the watchdog
///      writes a full crash report to disk.
///   4. On next app startup, <see cref="CheckPreviousCrash"/> finds the report/sentinel
///      and returns it so the app can show it to the user.
///
/// Because the watchdog runs on its own thread with AboveNormal priority, it survives
/// UI-thread StackOverflow, AccessViolation, and native driver crashes that kill the
/// main thread instantly.
/// </summary>
public static class CrashGuard
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Systema");

    private static readonly string SentinelPath  = Path.Combine(DataDir, "crash_sentinel.txt");
    private static readonly string CrashFilePath = Path.Combine(DataDir, "last_crash_report.txt");

    private static Thread?  _watchdog;
    private static volatile bool _uiAlive;
    private static volatile bool _running;
    private static volatile string? _activeBreadcrumb;
    private static int _reportInProgress;   // prevents reentrant crash reports (0 = idle, 1 = in progress)
    private static readonly object _writeLock = new();

    // ── Startup check ───────────────────────────────────────────────────────

    /// <summary>
    /// Call this FIRST in App.OnStartup — returns a crash report string if the
    /// previous session crashed, or null if it exited cleanly.
    /// Reports are kept on disk so users can view them later.
    /// </summary>
    public static string? CheckPreviousCrash()
    {
        try
        {
            // Prefer the full crash report (written by the watchdog or ProcessExit)
            if (File.Exists(CrashFilePath))
            {
                var report = File.ReadAllText(CrashFilePath);

                // Archive it with a timestamp so users can still find it,
                // then delete the trigger file so it never shows again after this startup.
                try
                {
                    var archivePath = Path.Combine(
                        DataDir,
                        $"crash_seen_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
                    File.Move(CrashFilePath, archivePath, overwrite: true);
                }
                catch
                {
                    // If rename fails, just delete it so it doesn't repeat forever
                    try { File.Delete(CrashFilePath); } catch { }
                }

                return report;
            }

            // Fall back to raw sentinel (breadcrumb only — app died so fast the watchdog couldn't write)
            if (File.Exists(SentinelPath))
            {
                var breadcrumb = File.ReadAllText(SentinelPath);
                // Delete sentinel so it doesn't trigger on every startup
                File.Delete(SentinelPath);

                var sb = new StringBuilder();
                sb.AppendLine("=== SYSTEMA CRASH REPORT (Previous Session) ===");
                sb.AppendLine($"Detected:  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Version:   Systema v{GetVersion()}");
                sb.AppendLine($"OS:        {GetOsString()}");
                sb.AppendLine($"Runtime:   .NET {Environment.Version}");
                sb.AppendLine($"CPU cores: {Environment.ProcessorCount}");
                sb.AppendLine($"RAM:       {GetTotalRamMb()} MB total");
                sb.AppendLine();
                sb.AppendLine("The previous session crashed or was terminated abnormally.");
                sb.AppendLine("The app was in the middle of this operation when it died:");
                sb.AppendLine();
                sb.AppendLine($"  → {breadcrumb}");
                sb.AppendLine();
                sb.AppendLine("No .NET exception was available — the crash was likely caused by");
                sb.AppendLine("a native driver (VPN, antivirus, or network filter) triggering an");
                sb.AppendLine("AccessViolationException or StackOverflowException that .NET cannot catch.");
                sb.AppendLine();
                sb.AppendLine("If this keeps happening, try:");
                sb.AppendLine("  • Disabling VPN software temporarily");
                sb.AppendLine("  • Updating network adapter drivers");
                sb.AppendLine("  • Running Systema without third-party antivirus active");
                sb.AppendLine();
                sb.AppendLine($"Report saved to: {CrashFilePath}");

                // Write this constructed report to the crash file so it persists for users
                var fullReport = sb.ToString();
                try
                {
                    EnsureDirectory();
                    File.WriteAllText(CrashFilePath, fullReport);
                }
                catch { /* best-effort persist */ }
                return fullReport;
            }
        }
        catch { /* never throw during crash recovery */ }

        return null;
    }

    /// <summary>
    /// Returns the path to the crash report file so it can be shown to the user.
    /// </summary>
    public static string CrashReportPath => CrashFilePath;

    // ── Start / Stop ────────────────────────────────────────────────────────

    /// <summary>
    /// Start the watchdog. Call once after the UI is up.
    /// </summary>
    public static void Start()
    {
        _running = true;
        EnsureDirectory();

        // Register ProcessExit — fires on normal exit AND some abnormal exits.
        // We write a crash report if a breadcrumb is still active.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        _watchdog = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name         = "CrashGuard-Watchdog",
            Priority     = ThreadPriority.AboveNormal
        };
        _watchdog.Start();
    }

    /// <summary>Clean shutdown — remove sentinel, clear breadcrumb, and stop watchdog.</summary>
    public static void Stop()
    {
        _running          = false;
        _activeBreadcrumb = null; // prevent OnProcessExit from writing a false crash report
        try { if (File.Exists(SentinelPath)) File.Delete(SentinelPath); } catch { }
    }

    // ── Breadcrumbs ─────────────────────────────────────────────────────────

    /// <summary>
    /// Mark the start of a risky operation. Writes a breadcrumb to disk immediately
    /// so it persists even if the process is killed by native code.
    /// </summary>
    public static void Mark(string context)
    {
        var crumb = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}";
        _activeBreadcrumb = crumb;
        WriteSentinel(crumb);
    }

    /// <summary>
    /// Clear the breadcrumb — the risky operation completed successfully.
    /// </summary>
    public static void Clear()
    {
        _activeBreadcrumb = null;
        try { if (File.Exists(SentinelPath)) File.Delete(SentinelPath); } catch { }
    }

    // ── Heartbeat ───────────────────────────────────────────────────────────

    /// <summary>
    /// Call this from the UI thread's DispatcherTimer to prove it's alive.
    /// </summary>
    public static void Heartbeat() => _uiAlive = true;

    // ── Watchdog loop (runs on its own thread) ──────────────────────────────

    private static void WatchdogLoop()
    {
        while (_running)
        {
            try
            {
                _uiAlive = false;
                Thread.Sleep(3_000); // give UI thread 3 seconds to heartbeat

                if (!_running) return;

                // If UI thread didn't heartbeat AND we're in a marked operation → freeze detected
                if (!_uiAlive && _activeBreadcrumb != null)
                {
                    // Wait one more cycle to be sure it's not just a slow GC pause
                    Thread.Sleep(2_000);
                    if (!_uiAlive && _activeBreadcrumb != null)
                    {
                        WriteCrashReport(
                            "UI THREAD FREEZE / CRASH DETECTED",
                            _activeBreadcrumb,
                            "The UI thread stopped responding for 5+ seconds during a marked operation.\n" +
                            "This typically means a native driver crashed the thread (AccessViolation)\n" +
                            "or caused a StackOverflowException that .NET cannot catch.");
                    }
                }
            }
            catch
            {
                // Never let the watchdog die
                Thread.Sleep(1_000);
            }
        }
    }

    // ── ProcessExit handler ─────────────────────────────────────────────────

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        if (_activeBreadcrumb != null)
        {
            WriteCrashReport(
                "ABNORMAL PROCESS EXIT",
                _activeBreadcrumb,
                "The process exited while a risky operation was still in progress.\n" +
                "This indicates the operation caused a fatal crash.");
        }
        else
        {
            // Clean exit — remove sentinel
            try { if (File.Exists(SentinelPath)) File.Delete(SentinelPath); } catch { }
        }
    }

    // ── File I/O ────────────────────────────────────────────────────────────

    private static void WriteCrashReport(string title, string breadcrumb, string explanation)
    {
        // Guard against reentrant calls — use atomic compare-exchange so concurrent callers
        // (watchdog thread + ProcessExit handler) cannot both write at the same time.
        if (Interlocked.CompareExchange(ref _reportInProgress, 1, 0) != 0) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== SYSTEMA CRASH REPORT ===");
            sb.AppendLine($"Type:      {title}");
            sb.AppendLine($"Time:      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Version:   Systema v{GetVersion()}");
            sb.AppendLine($"OS:        {GetOsString()}");
            sb.AppendLine($"Runtime:   .NET {Environment.Version}");
            sb.AppendLine($"CPU cores: {Environment.ProcessorCount}");
            sb.AppendLine($"RAM:       {GetTotalRamMb()} MB total");
            sb.AppendLine($"Uptime:    {GetProcessUptime()}");
            sb.AppendLine();
            sb.AppendLine("--- Last Known Operation ---");
            sb.AppendLine(breadcrumb);
            sb.AppendLine();
            sb.AppendLine("--- What Happened ---");
            sb.AppendLine(explanation);
            sb.AppendLine();
            sb.AppendLine("--- Suggestions ---");
            sb.AppendLine("• Disable VPN software and try again");
            sb.AppendLine("• Update network adapter drivers");
            sb.AppendLine("• Temporarily disable third-party antivirus");
            sb.AppendLine("• If the problem persists, share this report with the developer");
            sb.AppendLine();
            sb.AppendLine($"Report saved to: {CrashFilePath}");

            lock (_writeLock)
            {
                EnsureDirectory();
                File.WriteAllText(CrashFilePath, sb.ToString());
            }
        }
        catch { /* never throw from crash handler */ }
        finally { Interlocked.Exchange(ref _reportInProgress, 0); }
    }

    private static void WriteSentinel(string content)
    {
        try
        {
            lock (_writeLock)
            {
                EnsureDirectory();
                File.WriteAllText(SentinelPath, content);
            }
        }
        catch { /* never throw from crash handler */ }
    }

    private static void EnsureDirectory()
    {
        try { Directory.CreateDirectory(DataDir); } catch { }
    }

    private static string GetVersion()
    {
        try { return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"; }
        catch { return "?"; }
    }

    private static string GetOsString()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                var name    = key.GetValue("ProductName") as string ?? "Windows";
                var display = key.GetValue("DisplayVersion") as string ?? "";
                var build   = key.GetValue("CurrentBuildNumber") as string ?? "";
                return $"{name} {display} (Build {build})".Trim();
            }
        }
        catch { }
        return Environment.OSVersion.VersionString;
    }

    private static string GetTotalRamMb()
    {
        try
        {
            // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes == total physical RAM on 64-bit
            long mb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            return mb > 0 ? mb.ToString("N0") : "Unknown";
        }
        catch { return "Unknown"; }
    }

    private static string GetProcessUptime()
    {
        try
        {
            var start = System.Diagnostics.Process.GetCurrentProcess().StartTime;
            var span  = DateTime.Now - start;
            return span.TotalMinutes < 1
                ? $"{(int)span.TotalSeconds}s"
                : $"{(int)span.TotalMinutes}m {span.Seconds}s";
        }
        catch { return "Unknown"; }
    }
}
