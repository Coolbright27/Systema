using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;

namespace Systema.Views;

public partial class TaskSleepView : UserControl
{
    public TaskSleepView()
    {
        InitializeComponent();
    }

    /// <summary>Opens the ContextMenu of the ··· button relative to the button itself.</summary>
    private void ProcessMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is ContextMenu menu)
        {
            menu.PlacementTarget = btn;
            menu.Placement       = PlacementMode.Bottom;
            menu.IsOpen          = true;
        }
    }
}
