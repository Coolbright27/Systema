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

    private static readonly string AutoRestartStampPath = Path.Combine(DataDir, "last_autorestart.txt");

    private static Thread?  _watchdog;
    private static volatile bool _uiAlive;
    private static volatile bool _running;
    private static volatile string? _activeBreadcrumb;
    private static int _reportInProgress;   // prevents reentrant crash reports (0 = idle, 1 = in progress)
    private static readonly object _writeLock = new();

    // UI-liveness timestamp (UTC ticks) — refreshed every UI tick by Heartbeat().
    // Drives the "ghost process" auto-restart: a wedged UI thread shows no window
    // and no working tray icon while the process stays alive at ~0% CPU. We detect
    // the sustained heartbeat gap and relaunch a fresh instance.
    private static long _lastUiBeatTicks;
    private static long _processStartTicks;
    private static int  _selfRestartDone;   // 0 = not yet, 1 = already restarted this process

    // Lightweight, in-memory-only breadcrumb for the periodic UI-thread refresh. Unlike
    // Mark(), it does NO disk I/O (so it's cheap enough to set every 1-5 s tick) and is
    // SEPARATE from _activeBreadcrumb (so it never triggers a false "abnormal exit"
    // report on a clean shutdown). Surfaced in the ghost-hang report so a wedge inside a
    // view's RefreshAsync names the culprit view instead of just "(idle)".
    private static volatile string? _lastRefreshContext;

    // How long the UI may be unresponsive before we treat it as a hang and restart.
    private const int UiHangRestartSeconds  = 40;   // UI beat at least once, then stopped
    private const int UiHangRestartSecondsDeprioritised = 150; // same, but while at Idle priority
    private const int StartupHangSeconds    = 90;   // UI never beat (startup wedged)
    private const int RestartLoopGuardSeconds = 120; // don't auto-restart more than once / 2 min
    // A watchdog iteration normally takes ~3-5 s. If one suddenly took far longer, the wall clock
    // jumped — the system slept / hibernated / entered modern standby and froze the WHOLE process.
    // That gap must NOT be counted as a UI hang; doing so is what relaunched Systema after every
    // sleep (and orphaned its naps). On detection we give the just-resumed UI a fresh beat window.
    private const int SuspendDetectSeconds  = 30;
    // Extra rope for a heartbeat that stalls while the process is at Idle priority (Ghost Mode).
    // See ConfirmFreeze — CPU starvation under game load is not a crash.
    private const int DeprioritisedGraceSeconds = 25;

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
        _processStartTicks = DateTime.UtcNow.Ticks;
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

    /// <summary>
    /// Records (in memory only, no disk I/O) which periodic UI refresh is running, so a
    /// ghost-hang report can name the culprit view. Pass <c>null</c> when the refresh
    /// finishes. Does NOT touch the on-disk sentinel or <see cref="_activeBreadcrumb"/>.
    /// </summary>
    public static void NoteRefresh(string? context) => _lastRefreshContext = context;

    // ── Heartbeat ───────────────────────────────────────────────────────────

    /// <summary>
    /// Call this from the UI thread's DispatcherTimer to prove it's alive.
    /// </summary>
    public static void Heartbeat()
    {
        _uiAlive = true;
        Volatile.Write(ref _lastUiBeatTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// True when the UI thread has beaten within the last <paramref name="withinSeconds"/>
    /// seconds. Before the UI ever beats (startup), returns true so startup is never
    /// mistaken for a hang. Read by HeartbeatService to gate its liveness-file touch.
    /// </summary>
    public static bool IsUiResponsive(int withinSeconds)
    {
        long beat = Volatile.Read(ref _lastUiBeatTicks);
        if (beat == 0) return true; // UI not up yet — don't penalise startup
        return (DateTime.UtcNow - new DateTime(beat, DateTimeKind.Utc)).TotalSeconds <= withinSeconds;
    }

    // ── Watchdog loop (runs on its own thread) ──────────────────────────────

    private static void WatchdogLoop()
    {
        while (_running)
        {
            try
            {
                _uiAlive = false;
                // Timed around the 3 s sleep ONLY, not the whole iteration. ConfirmFreeze can add
                // up to 25 s of deliberate waiting, and measuring the full loop counted our own
                // sleep as a wall-clock jump — so the round after a confirmed freeze was misread
                // as a system suspend and skipped its checks.
                long beforeSleepTicks = DateTime.UtcNow.Ticks;
                Thread.Sleep(3_000); // give UI thread 3 seconds to heartbeat

                if (!_running) return;

                // ── Sleep / resume detection ──
                // If the wall clock jumped far past our 3s sleep, the system was suspended and the
                // whole process (UI thread + this watchdog) was frozen. That gap is NOT a UI hang.
                // Reset the heartbeat baselines so the freshly-resumed UI gets a clean window to beat,
                // and skip this round's hang checks. Without this, every sleep > ~40s makes Systema
                // relaunch itself and hard-kill the old instance, orphaning whatever it had napped.
                long nowTicks = DateTime.UtcNow.Ticks;
                double sleptSeconds = (nowTicks - beforeSleepTicks) / (double)TimeSpan.TicksPerSecond;
                if (sleptSeconds > SuspendDetectSeconds)
                {
                    Volatile.Write(ref _lastUiBeatTicks,   nowTicks); // treat as "UI just beat"
                    Volatile.Write(ref _processStartTicks, nowTicks); // and reset the startup-hang clock
                    continue;
                }

                // If UI thread didn't heartbeat AND we're in a marked operation → freeze detected
                if (!_uiAlive && _activeBreadcrumb != null)
                {
                    // Wait one more cycle to be sure it's not just a slow GC pause
                    Thread.Sleep(2_000);
                    if (!_uiAlive && _activeBreadcrumb != null && ConfirmFreeze())
                    {
                        WriteCrashReport(
                            "UI THREAD FREEZE / CRASH DETECTED",
                            _activeBreadcrumb,
                            "The UI thread stopped responding for 5+ seconds during a marked operation.\n" +
                            "This typically means a native driver crashed the thread (AccessViolation)\n" +
                            "or caused a StackOverflowException that .NET cannot catch.");
                    }
                }

                // ── Ghost-process auto-restart ──────────────────────────────────
                // Detect a SUSTAINED UI hang regardless of whether a breadcrumb is
                // active. A wedged UI thread leaves the process alive (~0% CPU) with no
                // window and no working tray icon — and the scheduled-task watchdog
                // never helps because the process is still "running". When that happens
                // we relaunch a fresh instance (which reclaims via the now-stale
                // heartbeat) so the app heals itself.
                CheckForGhostHangAndRestart();
            }
            catch
            {
                // Never let the watchdog die
                Thread.Sleep(1_000);
            }
        }
    }

    /// <summary>
    /// Second opinion before calling a stalled heartbeat a crash.
    ///
    /// Ghost Mode drops the WHOLE process to IDLE_PRIORITY_CLASS and trims its working set.
    /// When a game launches it saturates every core and the disk, and an idle-priority thread
    /// can simply fail to be scheduled for well over five seconds while its pages fault back in.
    /// That is starvation, not a crash — but to this watchdog the two looked identical, which is
    /// why boosting a game reliably produced a "UI THREAD FREEZE" report on a machine where
    /// nothing had actually gone wrong (three of them on 2026-08-09 alone, one per boost).
    ///
    /// So when the process is deliberately deprioritised, give it a much longer rope and only
    /// report if the heartbeat never comes back at all. A genuinely dead UI thread still gets
    /// reported, just <see cref="DeprioritisedGraceSeconds"/> later.
    /// </summary>
    private static bool ConfirmFreeze()
    {
        if (!IsProcessDeprioritised()) return true;   // normal priority — 5 s really is a freeze

        for (int i = 0; i < DeprioritisedGraceSeconds; i++)
        {
            Thread.Sleep(1_000);
            if (!_running) return false;
            if (_uiAlive || _activeBreadcrumb == null) return false;  // it caught up — not a freeze
        }
        return true;
    }

    /// <summary>True while Ghost Mode (or anything else) has us below Normal priority.</summary>
    private static bool IsProcessDeprioritised()
    {
        try
        {
            var priority = System.Diagnostics.Process.GetCurrentProcess().PriorityClass;
            return priority == System.Diagnostics.ProcessPriorityClass.Idle
                || priority == System.Diagnostics.ProcessPriorityClass.BelowNormal;
        }
        catch { return false; }
    }

    // ── Ghost-process auto-restart ──────────────────────────────────────────

    private static void CheckForGhostHangAndRestart()
    {
        if (Volatile.Read(ref _selfRestartDone) != 0) return; // only restart once per process

        long beat = Volatile.Read(ref _lastUiBeatTicks);
        DateTime now = DateTime.UtcNow;

        bool hung;
        string kind;
        if (beat != 0)
        {
            // UI beat at least once, then stopped → runtime hang.
            // At Idle priority under heavy load (Ghost Mode while a game loads) a long stall is
            // expected, and relaunching mid-session is far more disruptive than waiting: it drops
            // the active boost and orphans whatever Task Sleep had napped. Wait much longer before
            // deciding the process is genuinely wedged.
            int limit = IsProcessDeprioritised() ? UiHangRestartSecondsDeprioritised : UiHangRestartSeconds;
            hung = (now - new DateTime(beat, DateTimeKind.Utc)).TotalSeconds > limit;
            kind = "runtime UI hang";
        }
        else
        {
            // UI never beat → startup wedged (window never came up).
            long start = _processStartTicks != 0 ? _processStartTicks : now.Ticks;
            hung = (now - new DateTime(start, DateTimeKind.Utc)).TotalSeconds > StartupHangSeconds;
            kind = "startup hang (UI never came up)";
        }
        if (!hung) return;

        // Loop guard: if we auto-restarted very recently, don't do it again — a fresh
        // instance that wedges the same way would otherwise spin in a respawn loop.
        if (RecentlyAutoRestarted()) return;

        if (Interlocked.CompareExchange(ref _selfRestartDone, 1, 0) != 0) return;

        WriteCrashReport(
            "GHOST PROCESS — AUTO-RESTART",
            _activeBreadcrumb
                ?? (_lastRefreshContext != null ? $"(idle) last UI refresh: {_lastRefreshContext}" : null)
                ?? "(idle — no active operation)",
            $"The UI thread was unresponsive for too long ({kind}). The window and tray icon\n" +
            "were unreachable while the process stayed alive. Systema relaunched itself to recover.");
        TrySelfRestart();
    }

    private static bool RecentlyAutoRestarted()
    {
        try
        {
            if (!File.Exists(AutoRestartStampPath)) return false;
            if (long.TryParse(File.ReadAllText(AutoRestartStampPath).Trim(), out long ticks))
                return (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds < RestartLoopGuardSeconds;
        }
        catch { }
        return false;
    }

    private static void TrySelfRestart()
    {
        try
        {
            EnsureDirectory();
            File.WriteAllText(AutoRestartStampPath, DateTime.UtcNow.Ticks.ToString());

            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exe)) return;

            // Clear the liveness file so the fresh instance immediately sees this one
            // as stale and reclaims the single-instance mutex (killing this ghost).
            try { Systema.Services.HeartbeatService.Clear(); } catch { }

            // Relaunch tray-only (--silent) so the tray icon returns without stealing focus.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = exe,
                Arguments       = "--silent",
                UseShellExecute = false,
            });
        }
        catch { /* best-effort recovery */ }
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
                // Write to a temp file first, then atomically rename to the sentinel path.
                // This prevents a crash mid-write from leaving a corrupt/empty sentinel file
                // that would show a misleading crash report on the next startup.
                var tmp = SentinelPath + ".tmp";
                File.WriteAllText(tmp, content);
                File.Move(tmp, SentinelPath, overwrite: true);
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
