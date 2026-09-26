using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Systema.Views;

public partial class DashboardView : UserControl
{
    /// <summary>Below this width the two card columns stack into one, as Windows Settings does.</summary>
    private const double TwoColumnMinWidth = 860;

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

    // Two columns of cards when there's room, one column (right under left) when there isn't.
    private void CardGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < TwoColumnMinWidth;
        Grid.SetColumn(RightColumn, narrow ? 0 : 2);
        Grid.SetRow(RightColumn, narrow ? 1 : 0);
        Grid.SetColumnSpan(LeftColumn, narrow ? 3 : 1);
        Grid.SetColumnSpan(RightColumn, narrow ? 3 : 1);
    }

    // "Get help" opens the Systema Discord, the same link the crash report points people to.
    private void GetHelp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://discord.gg/yYhM7mdupH") { UseShellExecute = true });
        }
        catch { /* no default browser; nothing useful to do */ }
    }
}
