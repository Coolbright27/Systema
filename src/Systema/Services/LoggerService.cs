// ════════════════════════════════════════════════════════════════════════════
// LoggerService.cs  ·  Singleton thread-safe logger writing to disk
// ════════════════════════════════════════════════════════════════════════════
//
// Writes timestamped Info/Warn/Error/Fatal entries to rolling log files under
// %LOCALAPPDATA%\Systema\logs\. Access via LoggerService.Instance (singleton).
// Uses a ConcurrentQueue and a background thread so callers are never blocked.
//
// RELATED FILES
//   (consumed by almost every service and ViewModel in the project)
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Systema.Services;

public enum LogLevel { Info, Warning, Error, Fatal }

public record LogEntry(DateTime Timestamp, LogLevel Level, string Source, string Message, string? StackTrace = null)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level,-7}] [{Source}] {Message}");
        if (StackTrace != null)
        {
            sb.AppendLine();
            sb.Append("  Stack: ");
            sb.Append(StackTrace.Replace("\n", "\n         "));
        }
        return sb.ToString();
    }
}

public class LoggerService
{
    private const int RingBufferCapacity = 3000;
    private const string LogFileName     = "systema_session.log";
    private const string ChangeLogName   = "systema_changes.log";
    private const int    MaxChangeLines  = 500;

    private readonly ConcurrentQueue<LogEntry> _ring = new();
    private readonly string _logPath;
    private readonly string _changePath;
    private readonly object _fileLock = new();
    private static LoggerService? _instance;

    public static LoggerService Instance => _instance ??= new LoggerService();

    private LoggerService()
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Systema", "Logs");
        Directory.CreateDirectory(logsDir);
        _logPath    = Path.Combine(logsDir, LogFileName);
        _changePath = Path.Combine(logsDir, ChangeLogName);

        // Rotate: keep last 5 sessions
        RotateLogs(logsDir);

        Log(LogLevel.Info, "Logger", $"Systema v{GetDiagVersion()} starting — {DateTime.Now:R}");
        Log(LogLevel.Info, "Logger", $"Log file: {_logPath}");
    }

    public IReadOnlyCollection<LogEntry> RecentEntries => _ring.ToArray();

    public string LogFilePath    => _logPath;
    public string ChangeLogPath  => _changePath;

    /// <summary>
    /// Appends a timestamped change entry to systema_changes.log, which persists
    /// across sessions. Use this for any tweak that modifies system state so users
    /// can diagnose "what did Systema change?" if something breaks.
    /// </summary>
    public void LogChange(string action, string detail)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {action}: {detail}";
            _ = Task.Run(() =>
            {
                try
                {
                    lock (_fileLock)
                    {
                        var lines = File.Exists(_changePath)
                            ? new List<string>(File.ReadAllLines(_changePath))
                            : new List<string>();
                        lines.Add(line);
                        if (lines.Count > MaxChangeLines)
                            lines = lines.GetRange(lines.Count - MaxChangeLines, MaxChangeLines);
                        File.WriteAllLines(_changePath, lines);
                    }
                }
                catch { /* never throw from logger */ }
            });
        }
        catch { /* never throw from logger */ }
    }

    public void Log(LogLevel level, string source, string message, Exception? ex = null)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message, ex?.ToString());

        // Ring buffer — enqueue first, then drain excess in a single atomic TryDequeue
        // per iteration. ConcurrentQueue is thread-safe for individual operations but
        // Count+TryDequeue is not atomic, so we over-drain slightly rather than under-drain.
        _ring.Enqueue(entry);
        while (_ring.Count > RingBufferCapacity)
        {
            if (!_ring.TryDequeue(out _)) break; // nothing left to remove
        }

        // Write to file (non-blocking fire-and-forget — failures are silent)
        _ = Task.Run(() => WriteToFile(entry));
    }

    public void Info(string source, string message) => Log(LogLevel.Info, source, message);
    public void Warn(string source, string message, Exception? ex = null) => Log(LogLevel.Warning, source, message, ex);
    public void Error(string source, string message, Exception? ex = null) => Log(LogLevel.Error, source, message, ex);
    public void Fatal(string source, string message, Exception? ex = null) => Log(LogLevel.Fatal, source, message, ex);

    /// <summary>
    /// Get the last N log lines as a formatted string — for the crash report.
    /// </summary>
    public string GetRecentLog(int lines = 60)
    {
        var entries = _ring.ToArray();
        var slice = entries.Length > lines ? entries[^lines..] : entries;
        return string.Join(Environment.NewLine, slice.Select(e => e.ToString()));
    }

    public string GenerateCrashReport(Exception? ex, string context = "Unhandled Exception")
    {
        var version = GetDiagVersion();
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
        sb.AppendLine($"║           SYSTEMA CRASH REPORT v{version,-26}  ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine($"Date/Time  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Context    : {context}");
        sb.AppendLine($"OS         : {GetDiagOs()}");
        sb.AppendLine($"Runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"CPU        : {GetDiagCpuName()}");
        sb.AppendLine($"Processors : {Environment.ProcessorCount}");
        sb.AppendLine($"RAM Total  : {GetDiagRamMb()} MB");
        sb.AppendLine($"RAM Free   : {GetDiagFreeRamMb()} MB");
        sb.AppendLine($"Disk C:    : {GetDiagDisk()}");
        sb.AppendLine($"GPU        : {GetDiagGpu()}");
        sb.AppendLine($"Display    : {GetDiagDisplay()}");
        sb.AppendLine($"Processes  : {GetDiagProcessCount()} running");
        sb.AppendLine($"Uptime     : {GetDiagUptime()}");
        sb.AppendLine($"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");
        sb.AppendLine($"Machine    : {Environment.MachineName}");
        sb.AppendLine($"User       : {Environment.UserName}");
        sb.AppendLine();

        if (ex != null)
        {
            sb.AppendLine("══════════════════ EXCEPTION ══════════════════");
            sb.AppendLine($"Type    : {ex.GetType().FullName}");
            sb.AppendLine($"Message : {ex.Message}");
            if (ex.InnerException != null)
            {
                sb.AppendLine($"Inner   : {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                if (ex.InnerException.InnerException != null)
                    sb.AppendLine($"Inner2  : {ex.InnerException.InnerException.GetType().FullName}: {ex.InnerException.InnerException.Message}");
            }
            sb.AppendLine();
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(ex.StackTrace);
            if (ex.InnerException?.StackTrace != null)
            {
                sb.AppendLine();
                sb.AppendLine("Inner Stack Trace:");
                sb.AppendLine(ex.InnerException.StackTrace);
            }
            sb.AppendLine();
        }

        sb.AppendLine("══════════════════ SESSION LOG (last 60 entries) ══════════════");
        sb.AppendLine(GetRecentLog(60));
        sb.AppendLine();
        sb.AppendLine("══════════════════════════════════════════════════════════════");
        sb.AppendLine("Please send this file to the Systema Discord: https://discord.gg/DjxBswDeN8");
        sb.AppendLine("══════════════════════════════════════════════════════════════");

        return sb.ToString();
    }

    public string SaveCrashReport(Exception? ex, string context = "Unhandled Exception")
    {
        var logsDir = Path.GetDirectoryName(_logPath) ?? Path.GetTempPath();
        var crashFile = Path.Combine(logsDir,
            $"crash_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

        var report = GenerateCrashReport(ex, context);
        File.WriteAllText(crashFile, report, Encoding.UTF8);
        return crashFile;
    }

    /// <summary>
    /// Returns the last <paramref name="count"/> Error/Fatal log entries (newest last).
    /// </summary>
    public IReadOnlyList<LogEntry> GetLastErrors(int count = 5)
    {
        return _ring.ToArray()
                    .Where(e => e.Level >= LogLevel.Error)
                    .TakeLast(count)
                    .ToList();
    }

    /// <summary>
    /// Builds a rich, self-contained diagnostic report intended for pasting into Discord.
    /// Includes system info, the last <paramref name="errorCount"/> errors with full stack
    /// traces, and the last <paramref name="logLines"/> session log entries.
    /// </summary>
    public string GetDiagnosticsReport(int errorCount = 5, int logLines = 50)
    {
        var sb       = new StringBuilder();
        var now      = DateTime.Now;
        var version  = GetDiagVersion();
        var osInfo   = GetDiagOs();

        // ── Header ──────────────────────────────────────────────────────────
        sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
        sb.AppendLine($"║           SYSTEMA DIAGNOSTIC REPORT v{version,-15}         ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine($"Generated: {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Version:   Systema v{version}");
        sb.AppendLine($"OS:        {osInfo}");
        sb.AppendLine($"Runtime:   .NET {Environment.Version}");
        sb.AppendLine($"CPU cores: {Environment.ProcessorCount}");
        sb.AppendLine($"RAM:       {GetDiagRamMb()} MB total");
        sb.AppendLine($"Working:   {Environment.WorkingSet / 1024 / 1024} MB (process)");
        sb.AppendLine($"GPU:       {GetDiagGpu()}");
        sb.AppendLine($"Disk C:    {GetDiagDisk()}");
        sb.AppendLine($"CPU:       {GetDiagCpuName()}");
        sb.AppendLine($"Display:   {GetDiagDisplay()}");
        sb.AppendLine($"Free RAM:  {GetDiagFreeRamMb()} MB available");
        sb.AppendLine($"Processes: {GetDiagProcessCount()} running");
        sb.AppendLine($"Uptime:    {GetDiagUptime()}");
        sb.AppendLine($"Log file:  {_logPath}");
        sb.AppendLine();

        // ── About Systema ────────────────────────────────────────────────────
        sb.AppendLine("══════════════ ABOUT SYSTEMA ═══════════════════════════════════");
        sb.AppendLine();
        sb.AppendLine("  Systema is a Windows system optimisation tool. Here is what each");
        sb.AppendLine("  feature does, so you can describe what you use to a supporter:");
        sb.AppendLine();
        sb.AppendLine("  Dashboard        — Calculates a 0-100 Health Score from RAM usage,");
        sb.AppendLine("                     startup items, telemetry state, animations, and");
        sb.AppendLine("                     power plan. Shows live CPU/RAM stats.");
        sb.AppendLine("  Memory           — Frees standby RAM with one click (EmptyWorkingSet");
        sb.AppendLine("                     P/Invoke). Also lists all startup programs with an");
        sb.AppendLine("                     Impact badge (High/Medium/Low) so you can disable");
        sb.AppendLine("                     slow-boot entries.");
        sb.AppendLine("  Services         — Disables non-essential Windows services (telemetry,");
        sb.AppendLine("                     SysMain, Search indexer, etc.) and optional Windows");
        sb.AppendLine("                     features. Creates a restore point first.");
        sb.AppendLine("  Visual           — Toggles Windows animations and switches power plans");
        sb.AppendLine("                     (Balanced / High Performance / Ultimate Performance).");
        sb.AppendLine("  Game Boost       — Auto-detects 20+ games and pauses background");
        sb.AppendLine("                     services while you play, then restores them when");
        sb.AppendLine("                     you quit. Can also run manually.");
        sb.AppendLine("  App Cleanup      — Removes optional pre-installed Microsoft UWP apps");
        sb.AppendLine("                     (e.g. Candy Crush, Xbox apps, Tips, Mail). Safe");
        sb.AppendLine("                     list only — nothing that breaks Windows or the Store.");
        sb.AppendLine("  Tools            — Realtek audio driver cleaner, Core Parking toggle,");
        sb.AppendLine("                     Windows Update tweak (block preview builds), DNS");
        sb.AppendLine("                     switcher (Cloudflare / Google / Custom).");
        sb.AppendLine("  Task Sleep       — Puts background processes into efficiency/low-");
        sb.AppendLine("                     priority mode when the system is under load.");
        sb.AppendLine("  Settings         — Startup toggle, restore point manager, export/");
        sb.AppendLine("                     import settings, this diagnostic report.");
        sb.AppendLine();

        // ── Session activity summary ─────────────────────────────────────────
        sb.AppendLine("══════════════ SESSION ACTIVITY SUMMARY ═══════════════════════");
        sb.AppendLine();
        sb.Append(GetDiagSessionSummary());
        sb.AppendLine();

        // ── Feature usage (what has the user been doing) ─────────────────────
        sb.AppendLine("══════════════ FEATURE USAGE THIS SESSION ══════════════════════");
        sb.AppendLine();
        sb.Append(GetDiagFeatureUsage());
        sb.AppendLine();

        // ── Recent system changes ─────────────────────────────────────────────
        sb.AppendLine("══════════════ RECENT SYSTEM CHANGES (last 30) ════════════════");
        sb.AppendLine();
        sb.Append(GetDiagRecentChanges(30));
        sb.AppendLine();

        // ── App settings & applied tweaks ────────────────────────────────────
        sb.AppendLine("══════════════ APP SETTINGS & APPLIED TWEAKS ══════════════════");
        sb.AppendLine();
        sb.Append(GetDiagAppSettings());
        sb.AppendLine();

        // ── Runtime service & privacy health ─────────────────────────────────
        sb.AppendLine("══════════════ RUNTIME SERVICE STATUS ══════════════════════════");
        sb.AppendLine();
        sb.Append(GetDiagServiceHealth());
        sb.AppendLine();

        // ── Previous session crash (if any) ──────────────────────────────────
        var prevCrash = GetDiagPreviousCrash();
        if (prevCrash != null)
        {
            sb.AppendLine("══════════════ PREVIOUS SESSION CRASH ══════════════════════════");
            sb.AppendLine();
            sb.AppendLine(prevCrash);
            sb.AppendLine();
        }

        // ── Last N errors ────────────────────────────────────────────────────
        var errors = GetLastErrors(errorCount);
        sb.AppendLine($"══════════════ LAST {errorCount} ERRORS ({errors.Count} found) ══════════════");
        sb.AppendLine();

        if (errors.Count == 0)
        {
            sb.AppendLine("  ✓  No errors recorded in this session.");
            sb.AppendLine();
        }
        else
        {
            for (int i = 0; i < errors.Count; i++)
            {
                var e = errors[i];
                sb.AppendLine($"[{i + 1}/{errors.Count}] {e.Timestamp:yyyy-MM-dd HH:mm:ss.fff}  |  {e.Level,-5}  |  {e.Source}");
                sb.AppendLine($"  {e.Message}");
                if (!string.IsNullOrEmpty(e.StackTrace))
                {
                    sb.AppendLine("  Stack trace:");
                    // Limit to first 25 lines of stack trace to keep it readable
                    foreach (var line in e.StackTrace.Split('\n').Take(25))
                        sb.AppendLine($"    {line.TrimEnd()}");
                }
                sb.AppendLine();
            }
        }

        // ── Recent session log ───────────────────────────────────────────────
        sb.AppendLine($"══════════════ SESSION LOG (last {logLines} entries) ══════════════");
        sb.AppendLine();
        var recentLog = GetRecentLog(logLines);
        sb.AppendLine(string.IsNullOrEmpty(recentLog) ? "(no log entries)" : recentLog);
        sb.AppendLine();

        // ── Footer ───────────────────────────────────────────────────────────
        sb.AppendLine("══════════════════════════════════════════════════════════════");
        sb.AppendLine("Please paste this report in the Systema Discord:");
        sb.AppendLine("  https://discord.gg/DjxBswDeN8");
        sb.AppendLine("══════════════════════════════════════════════════════════════");

        return sb.ToString();
    }

    // ── Private helpers for diagnostics ──────────────────────────────────────

    private static string GetDiagVersion()
    {
        try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"; }
        catch { return "?"; }
    }

    private static string GetDiagOs()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                var name    = key.GetValue("ProductName")        as string ?? "Windows";
                var display = key.GetValue("DisplayVersion")     as string ?? "";
                var build   = key.GetValue("CurrentBuildNumber") as string ?? "";
                return $"{name} {display} (Build {build})".Trim();
            }
        }
        catch { }
        return Environment.OSVersion.VersionString;
    }

    private static string GetDiagRamMb()
    {
        try
        {
            long mb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            return mb > 0 ? mb.ToString("N0") : "Unknown";
        }
        catch { return "Unknown"; }
    }

    private static string GetDiagUptime()
    {
        try
        {
            var span = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            return span.TotalMinutes < 1
                ? $"{(int)span.TotalSeconds}s"
                : $"{(int)span.TotalMinutes}m {span.Seconds}s";
        }
        catch { return "Unknown"; }
    }

    private void WriteToFile(LogEntry entry)
    {
        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(_logPath, entry.ToString() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* never throw from logger */ }
    }

    private void RotateLogs(string logsDir)
    {
        lock (_fileLock)
        {
            try
            {
                var old = Directory.GetFiles(logsDir, "systema_session*.log")
                    .OrderByDescending(File.GetLastWriteTime)
                    .Skip(4)
                    .ToList();
                foreach (var f in old)
                {
                    try { File.Delete(f); }
                    catch { /* skip files locked by another process */ }
                }

                // Rename current session log
                var current = Path.Combine(logsDir, LogFileName);
                if (File.Exists(current))
                {
                    var archive = Path.Combine(logsDir,
                        $"systema_session_{File.GetLastWriteTime(current):yyyy-MM-dd_HH-mm-ss}.log");
                    File.Move(current, archive, overwrite: true);
                }
            }
            catch { }
        }
    }

    private static string GetDiagGpu()
    {
        try
        {
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name FROM Win32_VideoController WHERE PNPDeviceID IS NOT NULL");
                var names = searcher.Get()
                    .Cast<System.Management.ManagementObject>()
                    .Select(o => o["Name"]?.ToString()?.Trim() ?? "Unknown")
                    .Where(n => n != "Unknown")
                    .ToList();
                return names.Count > 0 ? string.Join(" | ", names) : "Unknown";
            });
            // 3-second timeout — WMI GPU query can hang on some machines
            return task.Wait(3000) ? task.Result : "Unknown (WMI timeout)";
        }
        catch { return "Unknown"; }
    }

    private static string GetDiagDisk()
    {
        try
        {
            var drive = new DriveInfo("C");
            if (drive.IsReady)
            {
                long freeGb  = drive.AvailableFreeSpace / (1024L * 1024 * 1024);
                long totalGb = drive.TotalSize          / (1024L * 1024 * 1024);
                return $"{freeGb} GB free / {totalGb} GB total";
            }
            return "Not ready";
        }
        catch { return "Unknown"; }
    }

    private static string GetDiagDisplay()
    {
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            return screen != null
                ? $"{screen.Bounds.Width}x{screen.Bounds.Height} (primary)"
                : "Unknown";
        }
        catch { return "Unknown"; }
    }

    private static string GetDiagAppSettings()
    {
        var sb = new StringBuilder();
        try
        {
            // ── App preferences stored in HKCU\Software\Systema ──
            using var appKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Systema");
            if (appKey != null)
            {
                sb.AppendLine($"  SkipRestorePoint:         {appKey.GetValue("SkipRestorePoint", "not set")}");
                sb.AppendLine($"  CoreParkingEnabled:       {appKey.GetValue("CoreParkingEnabled", "not set")}");
                sb.AppendLine($"  BlockPreviewUpdates:      {appKey.GetValue("BlockPreviewUpdatesEnabled", "not set")}");
                sb.AppendLine($"  GameCheckInterval (min):  {appKey.GetValue("GameCheckIntervalMinutes", "not set")}");
                sb.AppendLine($"  XboxServicesOverride:     {appKey.GetValue("XboxServicesUserOverride", "not set")}");
                sb.AppendLine($"  StartWithWindows:         {appKey.GetValue("StartWithWindows", "not set")}");
            }
            else
            {
                sb.AppendLine("  (no app preferences written yet — all defaults in use)");
            }

            sb.AppendLine();

            // ── Privacy hardening state ──
            using var privKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Systema\PrivacyBackup");
            if (privKey != null)
            {
                int backupCount = privKey.GetValueNames().Length;
                sb.AppendLine($"  Privacy Hardening:        Applied ({backupCount} registry value(s) backed up)");
                sb.AppendLine($"                            Revert available: yes");
            }
            else
            {
                sb.AppendLine("  Privacy Hardening:        Not applied (or reverted)");
            }

            sb.AppendLine();

            // ── Windows Update policies ──
            using var wuKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            sb.AppendLine($"  WU ManagePreviewBuilds:             {wuKey?.GetValue("ManagePreviewBuilds", "not set (default)") ?? "not set (default)"}");
            sb.AppendLine($"  WU ManagePreviewBuildsPolicyValue:  {wuKey?.GetValue("ManagePreviewBuildsPolicyValue", "not set (default)") ?? "not set (default)"}");

            // ── Telemetry policy ──
            using var telKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\DataCollection");
            sb.AppendLine($"  Telemetry AllowTelemetry:           {telKey?.GetValue("AllowTelemetry", "not set (default)") ?? "not set (default)"}");

            // ── Task Sleep settings ──
            using var tsKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Systema\TaskSleep");
            sb.AppendLine($"  TaskSleep.IsEnabled:              {tsKey?.GetValue("IsEnabled",               "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.LowerCpuPriority:       {tsKey?.GetValue("LowerCpuPriority",        "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.LowerGpuPriority:       {tsKey?.GetValue("LowerGpuPriority",        "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.LowerIoPriority:        {tsKey?.GetValue("LowerIoPriority",         "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.LowerMemoryPriority:    {tsKey?.GetValue("LowerMemoryPriority",     "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.TrimWorkingSet:         {tsKey?.GetValue("TrimWorkingSet",          "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.MoveToECores:           {tsKey?.GetValue("MoveToECores",            "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.DetectECores:           {tsKey?.GetValue("DetectECores",            "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.EfficiencyMode:         {tsKey?.GetValue("EnableEfficiencyMode",    "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.PersistentNap:          {tsKey?.GetValue("PersistentNapEnabled",    "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.AdaptiveTick:           {tsKey?.GetValue("AdaptiveTick",            "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.EnforceSettings:        {tsKey?.GetValue("EnforceSettings",         "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.IgnoreForeground:       {tsKey?.GetValue("IgnoreForeground",        "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.ActOnFgChildren:        {tsKey?.GetValue("ActOnForegroundChildren", "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.ExcludeSystemServices:  {tsKey?.GetValue("ExcludeSystemServices",   "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.SystemCpuTrigger%:      {tsKey?.GetValue("SystemCpuTriggerPercent", "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.ProcessCpuStart%:       {tsKey?.GetValue("ProcessCpuStartPercent",  "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.ProcessCpuStop%:        {tsKey?.GetValue("ProcessCpuStopPercent",   "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.TimeOverQuotaMs:        {tsKey?.GetValue("TimeOverQuotaMs",         "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.MinAdjustmentMs:        {tsKey?.GetValue("MinAdjustmentDurationMs", "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.MaxAdjustmentMs:        {tsKey?.GetValue("MaxAdjustmentDurationMs", "not set") ?? "not set"}");
            sb.AppendLine($"  TaskSleep.AppRules:               {GetTaskSleepRulesCount()}");

            // ── DNS (active adapter primary DNS) ──
            sb.AppendLine($"  Active DNS:                         {GetDiagDns()}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (error reading settings: {ex.Message})");
        }
        return sb.ToString();
    }

    private static string GetTaskSleepRulesCount()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Systema", "tasksleep_rules.json");
            if (!File.Exists(path)) return "0 rules";
            var text  = File.ReadAllText(path);
            int count = text.Split("\"ProcessName\"").Length - 1;
            return $"{count} rule(s)";
        }
        catch { return "unknown"; }
    }

    private static string GetDiagDns()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var dns = ni.GetIPProperties().DnsAddresses
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToList();
                if (dns.Count > 0) return string.Join(", ", dns.Take(2));
            }
            return "not detected";
        }
        catch { return "unknown"; }
    }

    private static string GetDiagCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string
                ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
                ?? "Unknown";
        }
        catch { return "Unknown"; }
    }

    // P/Invoke for free RAM — avoids WMI overhead and potential COM timeouts.
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        internal uint   dwLength;
        internal uint   dwMemoryLoad;
        internal ulong  ullTotalPhys;
        internal ulong  ullAvailPhys;
        internal ulong  ullTotalPageFile;
        internal ulong  ullAvailPageFile;
        internal ulong  ullTotalVirtual;
        internal ulong  ullAvailVirtual;
        internal ulong  ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    private static string GetDiagFreeRamMb()
    {
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return $"{status.ullAvailPhys / (1024UL * 1024):N0}";
            return "Unknown";
        }
        catch { return "Unknown"; }
    }

    private static string GetDiagProcessCount()
    {
        try { return System.Diagnostics.Process.GetProcesses().Length.ToString(); }
        catch { return "Unknown"; }
    }

    /// <summary>
    /// Checks the live status of key services and privacy registry values to
    /// help diagnose whether tweaks are actually applied and persisting.
    /// </summary>
    private static string GetDiagServiceHealth()
    {
        var sb = new StringBuilder();
        try
        {
            // ── Managed services ──────────────────────────────────────────────
            var watchedServices = new (string Name, string Label)[]
            {
                ("DiagTrack",         "Connected User Experiences (DiagTrack)"),
                ("dmwappushservice",  "Device Management WAP Push"),
                ("WSearch",           "Windows Search"),
                ("SysMain",           "SysMain (Superfetch)"),
                ("wuauserv",          "Windows Update"),
                ("WinDefend",         "Windows Defender"),
                ("DoSvc",             "Delivery Optimization"),
                ("BITS",              "Background Intelligent Transfer (BITS)"),
            };

            foreach (var (name, label) in watchedServices)
            {
                try
                {
                    using var svc = new System.ServiceProcess.ServiceController(name);
                    var startVal = Microsoft.Win32.Registry.LocalMachine
                        .OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}")
                        ?.GetValue("Start");
                    string startType = startVal is int s ? s switch
                    {
                        2 => "Auto",
                        3 => "Manual",
                        4 => "Disabled",
                        _ => $"Start={s}"
                    } : "Unknown";
                    sb.AppendLine($"  {label,-45} {svc.Status,-10} [{startType}]");
                }
                catch
                {
                    sb.AppendLine($"  {label,-45} Not Installed");
                }
            }

            sb.AppendLine();

            // ── Privacy registry state ────────────────────────────────────────
            using var telKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\DataCollection");
            sb.AppendLine($"  Telemetry AllowTelemetry:             {telKey?.GetValue("AllowTelemetry", "not set (Windows default)") ?? "not set (Windows default)"}");
            sb.AppendLine($"  Telemetry DisableEnterpriseAuthProxy: {telKey?.GetValue("DisableEnterpriseAuthProxy", "not set") ?? "not set"}");

            using var advKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo");
            sb.AppendLine($"  Advertising ID Enabled:               {advKey?.GetValue("Enabled", "not set (default=1)") ?? "not set (default=1)"}");

            using var feedbackKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Siuf\Rules");
            sb.AppendLine($"  Feedback Frequency (NumberOfSIUF):    {feedbackKey?.GetValue("NumberOfSIUFInPeriod", "not set (default)") ?? "not set (default)"}");

            // ── Scheduled task health ─────────────────────────────────────────
            sb.AppendLine();
            var ceipTasks = new[]
            {
                @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
                @"\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask",
                @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
                @"\Microsoft\Windows\Autochk\Proxy",
            };
            foreach (var task in ceipTasks)
            {
                try
                {
                    using var ts = new Microsoft.Win32.TaskScheduler.TaskService();
                    var t = ts.GetTask(task);
                    sb.AppendLine($"  Task {Path.GetFileName(task),-35} {(t?.Enabled == true ? "Enabled" : "Disabled")}");
                }
                catch
                {
                    sb.AppendLine($"  Task {Path.GetFileName(task),-35} Not Found");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (error reading service health: {ex.Message})");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Counts Info / Warning / Error / Fatal entries in the ring buffer and
    /// returns a short human-readable summary for the diagnostic report.
    /// </summary>
    private string GetDiagSessionSummary()
    {
        var sb = new StringBuilder();
        try
        {
            var all = _ring.ToArray();
            int info  = all.Count(e => e.Level == LogLevel.Info);
            int warn  = all.Count(e => e.Level == LogLevel.Warning);
            int error = all.Count(e => e.Level == LogLevel.Error);
            int fatal = all.Count(e => e.Level == LogLevel.Fatal);
            int total = all.Length;

            var start = all.Length > 0 ? all[0].Timestamp : DateTime.Now;
            var end   = all.Length > 0 ? all[^1].Timestamp : DateTime.Now;
            var span  = end - start;

            sb.AppendLine($"  Total entries:  {total} (buffer holds last 3 000)");
            sb.AppendLine($"  Info:           {info}");
            sb.AppendLine($"  Warnings:       {warn}");
            sb.AppendLine($"  Errors:         {error}");
            sb.AppendLine($"  Fatals:         {fatal}");
            if (total > 1)
                sb.AppendLine($"  Session span:   {(int)span.TotalMinutes}m {span.Seconds}s  ({start:HH:mm:ss} → {end:HH:mm:ss})");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (error building summary: {ex.Message})");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Analyses the ring buffer to identify which Systema features the user
    /// has actually been using this session (by matching log source names and
    /// key log message keywords).
    /// </summary>
    private string GetDiagFeatureUsage()
    {
        var sb = new StringBuilder();
        try
        {
            var all = _ring.ToArray();

            bool Seen(string keyword) =>
                all.Any(e => e.Source.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                          || e.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            var features = new (string Label, bool Used)[]
            {
                ("Dashboard / Health Score",    Seen("HealthScore") || Seen("Dashboard")),
                ("Memory Optimiser",             Seen("Memory") || Seen("FreeMemory") || Seen("WorkingSet")),
                ("Startup Manager",              Seen("Startup") || Seen("StartupService")),
                ("Services Tweaks",              Seen("ServiceControl") || Seen("ServicesVm") || Seen("OptionalFeatures")),
                ("Visual Tweaks / Power Plans",  Seen("Animation") || Seen("PowerPlan") || Seen("Visual")),
                ("Game Boost",                   Seen("GameBoost") || Seen("GameBooster") || Seen("game")),
                ("App Cleanup (Bloatware)",       Seen("Bloatware") || Seen("AppxPackage") || Seen("Remove-AppxPackage")),
                ("Tools (Realtek / Parking…)",   Seen("Realtek") || Seen("CoreParking") || Seen("Tools")),
                ("DNS Switcher",                  Seen("Dns") || Seen("DnsService")),
                ("Task Sleep",                   Seen("TaskSleep")),
                ("Privacy Hardening",             Seen("Privacy") || Seen("Telemetry") || Seen("PrivacyHardening")),
                ("Restore Point Manager",         Seen("RestorePoint") || Seen("SystemRestore")),
                ("Settings / Export",             Seen("Settings") || Seen("Export") || Seen("Import")),
            };

            bool any = false;
            foreach (var (label, used) in features)
            {
                if (used)
                {
                    sb.AppendLine($"  ✓  {label}");
                    any = true;
                }
            }

            if (!any)
                sb.AppendLine("  (no significant feature activity detected in this session's log)");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (error detecting feature usage: {ex.Message})");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the last <paramref name="count"/> lines from systema_changes.log —
    /// the persistent record of every tweak Systema has applied across all sessions.
    /// </summary>
    private string GetDiagRecentChanges(int count = 30)
    {
        var sb = new StringBuilder();
        try
        {
            if (!File.Exists(_changePath))
            {
                sb.AppendLine("  (no changes recorded yet — systema_changes.log does not exist)");
                return sb.ToString();
            }

            string[] lines;
            lock (_fileLock)
            {
                lines = File.ReadAllLines(_changePath);
            }

            var slice = lines.Length > count ? lines[^count..] : lines;
            if (slice.Length == 0)
            {
                sb.AppendLine("  (changes log exists but is empty)");
            }
            else
            {
                foreach (var line in slice)
                    sb.AppendLine($"  {line}");

                if (lines.Length > count)
                    sb.AppendLine($"  … ({lines.Length - count} older entries omitted — open the log file for the full history)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (error reading changes log: {ex.Message})");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads the CrashGuard crash report from the previous session, if one exists.
    /// The file lives at %LocalAppData%\Systema\last_crash_report.txt.
    /// Returns null if no crash occurred last session.
    /// </summary>
    private static string? GetDiagPreviousCrash()
    {
        try
        {
            var crashFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Systema", "last_crash_report.txt");

            if (!File.Exists(crashFile)) return null;

            var text    = File.ReadAllText(crashFile).Trim();
            var created = File.GetLastWriteTime(crashFile);
            // Only include if it is from a different (earlier) session — compare start time
            // Use process start time as a proxy for session start
            try
            {
                var sessionStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
                // If the crash file was written AFTER this process started, it's from this session
                // (e.g. the crash window wrote to it) — still show it, but note the timing
                if (created >= sessionStart)
                    return $"[Recorded at {created:yyyy-MM-dd HH:mm:ss} — current session]\n{text}";
            }
            catch { }

            return $"[Recorded at {created:yyyy-MM-dd HH:mm:ss}]\n{text}";
        }
        catch { return null; }
    }
}
