using System.Windows;
using System.Windows.Input;
using Systema.ViewModels;

namespace Systema.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    // Minimize → hide to tray (Ghost Mode is handled by App.xaml.cs via Closed event)
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        Hide(); // collapses to tray; App.xaml.cs re-shows on tray icon double-click
        (Application.Current as App)?.NotifyWindowHidden();
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    // Close button → hide to tray, not exit (use tray "Exit" to fully quit)
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        (Application.Current as App)?.NotifyWindowHidden();
    }

    private void Window_Activated(object sender, EventArgs e)
        => (DataContext as MainViewModel)?.SetFocused(true);

    private void Window_Deactivated(object sender, EventArgs e)
        => (DataContext as MainViewModel)?.SetFocused(false);
}
