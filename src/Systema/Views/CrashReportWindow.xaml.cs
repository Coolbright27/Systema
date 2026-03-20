using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Systema.Core;
using Systema.Services;

namespace Systema.Views;

public partial class CrashReportWindow : Window
{
    private string? _crashFilePath;
    private string _fullReport = string.Empty;
    private readonly LoggerService _logger = LoggerService.Instance;
    private bool _isFatal = true;

    public CrashReportWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Show the crash window for a fatal unhandled exception. Closing shuts down the app.
    /// </summary>
    public static void ShowCrash(Exception? ex, string context = "Unhandled Exception")
    {
        try
        {
            var logger = LoggerService.Instance;
            logger.Fatal("CrashHandler", $"[{context}] {ex?.GetType().Name}: {ex?.Message}", ex);

            var crashFile = logger.SaveCrashReport(ex, context);

            var win = new CrashReportWindow { _isFatal = true };
            win.Populate(ex, context, crashFile, logger.GetRecentLog(30));
            win.ShowDialog();
        }
        catch
        {
            MessageBox.Show(
                $"Systema encountered a fatal error and could not display the crash report.\n\n{ex?.Message}",
                "Fatal Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Show a crash report from a PREVIOUS session that was recovered on startup.
    /// Non-fatal — the app continues running after the user dismisses it.
    /// </summary>
    public static void ShowPreviousCrash(string report)
    {
        try
        {
            var win = new CrashReportWindow { _isFatal = false };
            win._fullReport = report;

            var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            var osInfo     = GetWindowsVersionString();
            var timestamp  = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            win.SubtitleText.Text     = "Systema detected that the previous session crashed. The report below can be copied and shared with the developer.";
            win.ErrorMessageText.Text = "The previous session was terminated abnormally (native crash — no .NET exception available).";
            win.SystemInfoText.Text   = $"Systema v{appVersion}  ·  {osInfo}  ·  {timestamp}";
            win.StackTraceText.Text   = report;
            win.LogText.Text          = LoggerService.Instance.GetRecentLog(30);
            win._crashFilePath        = CrashGuard.CrashReportPath;
            win.CrashPathText.Text    = CrashGuard.CrashReportPath;
            win.CrashPathText.ToolTip = CrashGuard.CrashReportPath;
            win.CloseButton.Content   = "Dismiss";

            win.ShowDialog();
        }
        catch
        {
            MessageBox.Show(
                $"Previous session crash detected:\n\n{report}",
                "Previous Crash — Systema",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Show the error window for a non-fatal error in any area. Closing just dismisses — app keeps running.
    /// </summary>
    public static void ShowError(Exception? ex, string context)
    {
        try
        {
            var logger = LoggerService.Instance;
            logger.Error("ErrorHandler", $"[{context}] {ex?.GetType().Name}: {ex?.Message}", ex);

            var crashFile = logger.SaveCrashReport(ex, context);

            var win = new CrashReportWindow { _isFatal = false };
            win.Populate(ex, context, crashFile, logger.GetRecentLog(30));
            win.ShowDialog();
        }
        catch
        {
            MessageBox.Show(
                $"An error occurred in {context}.\n\n{ex?.Message}",
                "Error — Systema",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Populate(Exception? ex, string context, string crashFilePath, string recentLog)
    {
        _crashFilePath = crashFilePath;

        var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.3.0";
        var osInfo     = GetWindowsVersionString();
        var timestamp  = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        SubtitleText.Text = _isFatal
            ? "The application has stopped working. A crash report has been saved."
            : $"An error occurred in: {context}. The report below can be copied and shared.";

        ErrorMessageText.Text = ex != null
            ? $"{ex.GetType().Name}: {ex.Message}"
            : "The application stopped unexpectedly with no exception details.";

        SystemInfoText.Text = $"Systema v{appVersion}  ·  {osInfo}  ·  {timestamp}";

        StackTraceText.Text = ex != null
            ? BuildStackTrace(ex)
            : "(No stack trace available)";

        LogText.Text = string.IsNullOrWhiteSpace(recentLog)
            ? "(No log entries available)"
            : recentLog;

        CrashPathText.Text    = crashFilePath;
        CrashPathText.ToolTip = crashFilePath;

        // Non-fatal: change the close button to "Dismiss" so it's clear the app keeps running
        if (!_isFatal)
            CloseButton.Content = "Dismiss";

        // Pre-build the clipboard report
        _fullReport = BuildFullReport(ex, context, crashFilePath, recentLog, osInfo, appVersion, timestamp);

        // Auto-scroll recent activity log to bottom
        Loaded += (_, _) =>
        {
            if (LogText.Parent is System.Windows.Controls.ScrollViewer sv)
                sv.ScrollToEnd();
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string BuildStackTrace(Exception ex)
    {
        var sb = new StringBuilder();
        var current = ex;
        int depth = 0;

        while (current != null)
        {
            if (depth > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"─── Inner Exception ({depth}) ───");
                sb.AppendLine();
            }

            sb.AppendLine($"{current.GetType().FullName}: {current.Message}");

            if (!string.IsNullOrEmpty(current.StackTrace))
                sb.AppendLine(current.StackTrace);

            current = current.InnerException;
            depth++;
        }

        return sb.ToString().TrimEnd();
    }

    private static string GetWindowsVersionString()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                var productName    = key.GetValue("ProductName")      as string ?? "Windows";
                var displayVersion = key.GetValue("DisplayVersion")   as string ?? string.Empty;
                var build          = key.GetValue("CurrentBuildNumber") as string ?? string.Empty;
                var result = $"{productName} {displayVersion} (Build {build})".Trim();
                return result;
            }
        }
        catch { }
        return Environment.OSVersion.VersionString;
    }

    private static string BuildFullReport(
        Exception? ex, string context, string crashFilePath,
        string recentLog, string osInfo, string appVersion, string timestamp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
        sb.AppendLine($"║           SYSTEMA CRASH REPORT v{appVersion,-26}  ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine($"Time:        {timestamp}");
        sb.AppendLine($"Version:     Systema v{appVersion}");
        sb.AppendLine($"Context:     {context}");
        sb.AppendLine($"OS:          {osInfo}");
        sb.AppendLine($"Runtime:     .NET {Environment.Version}");
        sb.AppendLine($"CPU Cores:   {Environment.ProcessorCount}");
        sb.AppendLine($"Working Set: {Environment.WorkingSet / 1024 / 1024} MB (process)");
        sb.AppendLine($"64-bit OS:   {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"Machine:     {Environment.MachineName}");
        sb.AppendLine();

        // Extra runtime diagnostics
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            sb.AppendLine($"GC Total RAM:    {gcInfo.TotalAvailableMemoryBytes / 1024 / 1024} MB");
            sb.AppendLine($"GC Committed:    {gcInfo.TotalCommittedBytes / 1024 / 1024} MB");
        }
        catch { }

        try
        {
            var drive = new System.IO.DriveInfo("C");
            if (drive.IsReady)
                sb.AppendLine($"Disk C:          {drive.AvailableFreeSpace / 1024 / 1024 / 1024} GB free / {drive.TotalSize / 1024 / 1024 / 1024} GB total");
        }
        catch { }

        try
        {
            sb.AppendLine($"Process Count:   {System.Diagnostics.Process.GetProcesses().Length}");
        }
        catch { }

        sb.AppendLine();
        sb.AppendLine("══════════════════ ERROR ══════════════════════════════════════");
        sb.AppendLine(ex != null ? $"{ex.GetType().FullName}: {ex.Message}" : "(No exception)");
        if (ex?.InnerException != null)
            sb.AppendLine($"  Inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
        sb.AppendLine();
        sb.AppendLine("══════════════════ STACK TRACE ════════════════════════════════");
        sb.AppendLine(ex != null ? BuildStackTrace(ex) : "(No stack trace)");
        sb.AppendLine();
        sb.AppendLine("══════════════════ RECENT ACTIVITY ════════════════════════════");
        sb.AppendLine(string.IsNullOrWhiteSpace(recentLog) ? "(No log entries)" : recentLog);
        sb.AppendLine();
        sb.AppendLine("══════════════════════════════════════════════════════════════");
        sb.AppendLine($"Report file: {crashFilePath}");
        sb.AppendLine("Please paste this report in the Systema Discord: https://discord.gg/DjxBswDeN8");
        sb.AppendLine("══════════════════════════════════════════════════════════════");
        return sb.ToString();
    }

    // ── Event Handlers ───────────────────────────────────────────────────────

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_fullReport)) return;

            System.Windows.Clipboard.SetText(_fullReport);

            // Briefly show "Copied!" confirmation on the button
            var btn = (System.Windows.Controls.Button)sender;
            var original = btn.Content;
            btn.Content = "✓ Copied!";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { btn.Content = original; timer.Stop(); };
            timer.Start();
        }
        catch { }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetDirectoryName(_crashFilePath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                Process.Start("explorer.exe", folder);
        }
        catch { }
    }

    private void Discord_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://discord.gg/DjxBswDeN8")
            {
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isFatal)
            Application.Current?.Shutdown(1);
        else
            Close();
    }
}
