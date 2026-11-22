using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace SiteLift.App.Views;

internal static class FlyoutKeyboard
{
    internal static void Handle(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool menuKey = key == Key.F4 || (key == Key.Down && e.Key == Key.System);
        bool activate = key is Key.Enter or Key.Space;
        object? flyout = sender switch
        {
            SplitButton split when menuKey || (activate && e.OriginalSource is ToggleButton) => split.Flyout,
            DropDownButton dropDown when menuKey || activate => dropDown.Flyout,
            _ => null
        };

        if (flyout is not ContextMenu menu || sender is not System.Windows.Controls.Button button)
            return;

        Open(menu, button);
        e.Handled = true;
    }

    internal static void Open(ContextMenu menu, System.Windows.Controls.Button button)
    {
        menu.SetCurrentValue(FrameworkElement.MinWidthProperty, button.ActualWidth);
        menu.SetCurrentValue(ContextMenu.PlacementTargetProperty, button);
        menu.SetCurrentValue(ContextMenu.PlacementProperty, PlacementMode.Bottom);
        menu.SetCurrentValue(ContextMenu.IsOpenProperty, true);
    }
}
