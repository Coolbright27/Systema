using System.Windows;

namespace Systema.Views;

public partial class DismissedWindow : Window
{
    public DismissedWindow()
    {
        InitializeComponent();
        Systema.Core.ThemeManager.FollowTheme(this);   // title bar light or dark with Windows
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
