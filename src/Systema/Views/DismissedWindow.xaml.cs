using System.Windows;

namespace Systema.Views;

public partial class DismissedWindow : Window
{
    public DismissedWindow() => InitializeComponent();

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
