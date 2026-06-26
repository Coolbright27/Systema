using System.Windows;
using System.Windows.Controls;

namespace Systema.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    // Opens the dismissed-recommendations popup. It shares this view's DataContext (the
    // DashboardViewModel) so it can bind the Dismissed list and the Restore command directly.
    private void DismissedBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = new DismissedWindow
        {
            Owner       = Application.Current?.MainWindow,
            DataContext = DataContext,
        };
        win.ShowDialog();
    }
}
