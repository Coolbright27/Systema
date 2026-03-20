// ════════════════════════════════════════════════════════════════════════════
// RestorePointManagerWindow.xaml.cs  ·  Manage restore points dialog
// ════════════════════════════════════════════════════════════════════════════
//
// Code-behind for the Manage Restore Points window opened from the Settings tab.
// Uses RestorePointService directly (simple one-off dialog, not MVVM).
//
// ACTIONS
//   Create → prompts for a description → RestorePointService.CreateAsync
//   Delete → confirmation dialog → RestorePointService.DeleteRestorePointAsync
//   Restore My PC → RestorePointService.OpenSystemRestoreWizard (rstrui.exe)
//   Refresh → reload the list
// ════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using Systema.Models;
using Systema.Services;
using WpfButton      = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Systema.Views;

public partial class RestorePointManagerWindow : Window
{
    private readonly RestorePointService _service;
    private List<RestorePointInfo>       _points = [];

    private RestorePointManagerWindow(RestorePointService service)
    {
        _service = service;
        InitializeComponent();
        Loaded += async (_, _) => await LoadPointsAsync();
    }

    // ── Static factory ────────────────────────────────────────────────────────

    public static void Show(RestorePointService service, Window? owner = null)
    {
        var win = new RestorePointManagerWindow(service)
        {
            Owner = owner ?? Application.Current.MainWindow
        };
        win.ShowDialog();
    }

    // ── Load / refresh ────────────────────────────────────────────────────────

    private async Task LoadPointsAsync()
    {
        LoadingText.Visibility  = Visibility.Visible;
        EmptyText.Visibility    = Visibility.Collapsed;
        PointsList.Visibility   = Visibility.Collapsed;
        RefreshBtn.IsEnabled    = false;
        CreateBtn.IsEnabled     = false;
        SetStatus(string.Empty);

        try
        {
            _points = await _service.GetRestorePointsAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load restore points: {ex.Message}");
            _points = [];
        }
        finally
        {
            LoadingText.Visibility = Visibility.Collapsed;
            RefreshBtn.IsEnabled   = true;
            CreateBtn.IsEnabled    = true;
        }

        if (_points.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            PointsList.ItemsSource = _points;
            PointsList.Visibility  = Visibility.Visible;
        }
    }

    // ── Create ────────────────────────────────────────────────────────────────

    private async void CreateBtn_Click(object sender, RoutedEventArgs e)
    {
        // Ask the user for a description
        var dlg = new RestorePointNameDialog { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.PointDescription))
            return;

        SetStatus("Creating restore point…");
        CreateBtn.IsEnabled = false;
        RefreshBtn.IsEnabled = false;

        try
        {
            var result = await _service.CreateAsync(dlg.PointDescription);
            SetStatus(result.Success
                ? $"✓ {result.Message}"
                : $"✗ {result.Message}");

            if (result.Success) await LoadPointsAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            CreateBtn.IsEnabled  = true;
            RefreshBtn.IsEnabled = true;
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton btn || btn.Tag is not int seqNum) return;

        var point = _points.FirstOrDefault(p => p.SequenceNumber == seqNum);
        if (point == null) return;

        var confirm = MessageBox.Show(
            $"Are you sure you want to delete this restore point?\n\n" +
            $"  {point.FormattedDate}  —  {point.Description}\n\n" +
            "This cannot be undone.",
            "Delete Restore Point",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        SetStatus("Deleting restore point…");
        CreateBtn.IsEnabled  = false;
        RefreshBtn.IsEnabled = false;

        try
        {
            var result = await _service.DeleteRestorePointAsync(seqNum);
            SetStatus(result.Success
                ? $"✓ {result.Message}"
                : $"✗ {result.Message}");

            if (result.Success) await LoadPointsAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            CreateBtn.IsEnabled  = true;
            RefreshBtn.IsEnabled = true;
        }
    }

    // ── Restore wizard ────────────────────────────────────────────────────────

    private void RestoreWizardBtn_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "This will open the Windows System Restore wizard.\n\n" +
            "You will be able to choose a restore point and roll your PC's settings " +
            "back to that date. Systema will close after you confirm.\n\n" +
            "Do you want to continue?",
            "Open System Restore",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        _service.OpenSystemRestoreWizard();
        Close();
    }

    // ── Refresh / Close ───────────────────────────────────────────────────────

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        => await LoadPointsAsync();

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
        => Close();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetStatus(string message)
    {
        StatusText.Text       = message;
        StatusText.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}

// ── Simple name-prompt dialog ─────────────────────────────────────────────────

/// <summary>
/// A tiny inline dialog that asks the user for a restore point description.
/// Implemented in code-behind to avoid creating a separate XAML file for a
/// one-field dialog.
/// </summary>
internal class RestorePointNameDialog : Window
{
    private readonly System.Windows.Controls.TextBox _tb;
    public string PointDescription => _tb.Text.Trim();

    public RestorePointNameDialog()
    {
        Title  = "Create Restore Point";
        Width  = 440;
        Height = 200;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)
            Application.Current.Resources["BgPrimaryBrush"];

        var panel = new StackPanel { Margin = new Thickness(24) };

        panel.Children.Add(new TextBlock
        {
            Text       = "Give this restore point a name so you can identify it later:",
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
            FontSize   = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new Thickness(0, 0, 0, 12),
        });

        _tb = new System.Windows.Controls.TextBox
        {
            Text        = $"Systema — Manual backup  {DateTime.Now:yyyy-MM-dd}",
            Style       = (Style)Application.Current.Resources["DarkTextBox"],
            Margin      = new Thickness(0, 0, 0, 20),
        };
        _tb.SelectAll();
        panel.Children.Add(_tb);

        var row = new StackPanel
        {
            Orientation         = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        var ok = new WpfButton
        {
            Content = "Create",
            Style   = (Style)Application.Current.Resources["PrimaryButton"],
            Padding = new Thickness(20, 8, 20, 8),
            Margin  = new Thickness(0, 0, 10, 0),
        };
        ok.Click += (_, _) => { DialogResult = true; };

        var cancel = new WpfButton
        {
            Content = "Cancel",
            Style   = (Style)Application.Current.Resources["GhostButton"],
            Padding = new Thickness(14, 8, 14, 8),
        };
        cancel.Click += (_, _) => { DialogResult = false; };

        row.Children.Add(ok);
        row.Children.Add(cancel);
        panel.Children.Add(row);

        Content = panel;
        Loaded += (_, _) => _tb.Focus();
    }
}
