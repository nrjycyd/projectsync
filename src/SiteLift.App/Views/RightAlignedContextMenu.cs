using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SiteLift.App.Views;

public sealed class RightAlignedContextMenu : ContextMenu
{
    static RightAlignedContextMenu()
    {
        // Both WPF UI and the keyboard handler request Bottom when opening the menu.
        PlacementProperty.OverrideMetadata(typeof(RightAlignedContextMenu),
            new FrameworkPropertyMetadata(PlacementMode.Custom, null, (_, _) => PlacementMode.Custom));
    }

    public RightAlignedContextMenu()
    {
        CustomPopupPlacementCallback = PlaceAtRightEdge;
    }

    private CustomPopupPlacement[] PlaceAtRightEdge(Size popupSize, Size targetSize, Point offset)
    {
        // Popup placement uses screen pixels; WPF UI reserves the shadow margin in DIPs.
        var dpi = VisualTreeHelper.GetDpi(PlacementTarget ?? this);
        double left = targetSize.Width - popupSize.Width + offset.X - Margin.Left * dpi.DpiScaleX;
        double top = offset.Y - Margin.Top * dpi.DpiScaleY;
        return new[]
        {
            new CustomPopupPlacement(new Point(left, targetSize.Height + top), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(left, top - popupSize.Height), PopupPrimaryAxis.Horizontal)
        };
    }
}
