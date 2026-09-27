using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Systema.ViewModels;
using Button = System.Windows.Controls.Button;

namespace Systema.Views;

public partial class TaskSleepView : UserControl
{
    public TaskSleepView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens a Live monitor row's "More" menu under its button, and holds the list still until
    /// the menu closes (the list is rebuilt every 2 s, which would replace the row under it).
    /// </summary>
    private void ProcessMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.ContextMenu is not ContextMenu menu) return;

        if (DataContext is TaskSleepViewModel vm)
        {
            vm.LiveListFrozen = true;
            menu.Closed -= Menu_Closed;
            menu.Closed += Menu_Closed;
        }
        menu.PlacementTarget = btn;
        menu.Placement       = PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    private void Menu_Closed(object sender, RoutedEventArgs e)
    {
        if (DataContext is TaskSleepViewModel vm) vm.LiveListFrozen = false;
    }

    /// <summary>Opening the never-nap picker puts the cursor in its search box, ready to type.</summary>
    private void PickerSearchBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is System.Windows.Controls.TextBox box)
            Dispatcher.BeginInvoke(() => box.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }
}
