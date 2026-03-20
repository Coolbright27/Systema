// ════════════════════════════════════════════════════════════════════════════
// DiagnosticsReportWindow.xaml.cs  ·  Live colour-coded activity log viewer
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Systema.Services;

namespace Systema.Views;

public partial class DiagnosticsReportWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private LogLevel?  _currentFilter; // null = ALL
    private string     _searchText    = string.Empty;

    // Active / inactive pill colours
    private static readonly SolidColorBrush BrushActive   = new(Color.FromRgb(0x00, 0x77, 0xAA));
    private static readonly SolidColorBrush BrushInactive = new(Color.FromRgb(0x1A, 0x22, 0x35));
    private static readonly SolidColorBrush BrushFgActive = new(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush BrushFgMuted  = new(Color.FromRgb(0x6B, 0x7A, 0x99));

    public DiagnosticsReportWindow()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshLog();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>Opens the activity log as a dialog.</summary>
    public static new void Show()
    {
        try
        {
            var win = new DiagnosticsReportWindow();
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open the activity log.\n\n{ex.Message}",
                "Activity Log — Systema",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HighlightActiveFilter();
        RefreshLog();
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
    }

    // ── Log refresh ───────────────────────────────────────────────────────────

    private void RefreshLog()
    {
        try
        {
            var all = LoggerService.Instance.RecentEntries;

            // Filter by level
            IEnumerable<LogEntry> filtered = _currentFilter.HasValue
                ? all.Where(e => e.Level == _currentFilter.Value)
                : all;

            // Filter by search text
            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                var txt = _searchText;
                filtered = filtered.Where(e =>
                    e.Message.Contains(txt, StringComparison.OrdinalIgnoreCase) ||
                    e.Source.Contains(txt,  StringComparison.OrdinalIgnoreCase) ||
                    e.Level.ToString().Contains(txt, StringComparison.OrdinalIgnoreCase));
            }

            var list = filtered.ToList();

            LogListBox.ItemsSource = list;

            // Entry count
            CountText.Text = list.Count == 1 ? "1 entry" : $"{list.Count} entries";

            // Error count badge
            int errorCount = all.Count(e => e.Level >= LogLevel.Error);
            ErrorCountText.Text = errorCount > 0
                ? $"{errorCount} error{(errorCount == 1 ? "" : "s")}"
                : string.Empty;

            // Auto-scroll to the newest (bottom) entry
            if (list.Count > 0)
            {
                LogListBox.ScrollIntoView(list[^1]);
            }
        }
        catch { /* never crash the UI refresh */ }
    }

    // ── Toolbar event handlers ────────────────────────────────────────────────

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        _currentFilter = (btn.Tag as string) switch
        {
            "Info"    => (LogLevel?)LogLevel.Info,
            "Warning" => (LogLevel?)LogLevel.Warning,
            "Error"   => (LogLevel?)LogLevel.Error,
            _         => null
        };

        HighlightActiveFilter();
        RefreshLog();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        RefreshLog();
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = LoggerService.Instance.LogFilePath;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open log file:\n{ex.Message}", "Systema",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Footer event handlers ─────────────────────────────────────────────────

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Build tab-separated text of whatever is currently shown
            if (LogListBox.ItemsSource is not IEnumerable<LogEntry> entries) return;

            var sb = new StringBuilder();
            sb.AppendLine($"Systema Activity Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(new string('─', 80));

            foreach (var entry in entries)
            {
                sb.AppendLine(
                    $"{entry.Timestamp:HH:mm:ss.fff}  {entry.Level,-7}  [{entry.Source}]  {entry.Message}");
                if (!string.IsNullOrEmpty(entry.StackTrace))
                {
                    sb.AppendLine($"  Stack: {entry.StackTrace}");
                }
            }

            System.Windows.Clipboard.SetText(sb.ToString());

            // Flash button
            if (sender is Button btn)
            {
                var origContent = btn.Content;
                btn.Content    = "✓  Copied!";
                btn.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x8B, 0x22));

                var flash = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                flash.Tick += (_, _) =>
                {
                    btn.Content    = origContent;
                    btn.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x77, 0xAA));
                    flash.Stop();
                };
                flash.Start();
            }
        }
        catch { /* clipboard can fail silently */ }
    }

    private void CopyFullReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = LoggerService.Instance.GetDiagnosticsReport(errorCount: 10, logLines: 80);
            System.Windows.Clipboard.SetText(report);

            // Flash button
            if (sender is Button btn)
            {
                var origContent = btn.Content;
                var origBg      = btn.Background;
                btn.Content    = "✓  Copied!";
                btn.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x8B, 0x22));

                var flash = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                flash.Tick += (_, _) =>
                {
                    btn.Content    = origContent;
                    btn.Background = origBg;
                    flash.Stop();
                };
                flash.Start();
            }
        }
        catch { /* clipboard can fail silently */ }
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

    // ── Window chrome ─────────────────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void HighlightActiveFilter()
    {
        // Reset all pills
        foreach (var btn in new[] { BtnAll, BtnInfo, BtnWarn, BtnError })
        {
            btn.Background = BrushInactive;
            if (btn.Content is string) { /* foreground set via DataTemplate trigger — skip */ }
        }

        // Highlight the active one
        Button active = _currentFilter switch
        {
            LogLevel.Info    => BtnInfo,
            LogLevel.Warning => BtnWarn,
            LogLevel.Error   => BtnError,
            _                => BtnAll
        };
        active.Background = BrushActive;
    }
}
