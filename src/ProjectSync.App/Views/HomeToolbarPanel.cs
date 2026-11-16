using System;
using System.Windows;
using System.Windows.Controls;

namespace ProjectSync.App.Views;

// Children are the batch buttons and the project/tool buttons.
public sealed class HomeToolbarPanel : Panel
{
    private const double ColumnGap = 12;
    private const double RowGap = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));

        var (batch, tools) = GetRectangles(availableSize.Width);
        return new Size(Math.Min(availableSize.Width, tools.Right), Math.Max(batch.Bottom, tools.Bottom));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (batch, tools) = GetRectangles(finalSize.Width);
        InternalChildren[0].Arrange(batch);
        InternalChildren[1].Arrange(tools);
        return finalSize;
    }

    private (Rect Batch, Rect Tools) GetRectangles(double width)
    {
        Size batch = InternalChildren[0].DesiredSize;
        Size tools = InternalChildren[1].DesiredSize;
        if (InternalChildren[0].Visibility == Visibility.Collapsed)
            return (new Rect(), new Rect(new Point(), tools));

        double actionWidth = batch.Width + ColumnGap + tools.Width;
        width = double.IsPositiveInfinity(width) ? actionWidth : width;
        double toolsLeft = Math.Max(0, width - tools.Width);
        if (width < actionWidth)
            return (new Rect(new Point(), batch), new Rect(new Point(toolsLeft, batch.Height + RowGap), tools));

        double rowHeight = Math.Max(batch.Height, tools.Height);
        return (Centered(batch, 0, 0, rowHeight), Centered(tools, toolsLeft, 0, rowHeight));
    }

    private static Rect Centered(Size desired, double x, double y, double rowHeight) =>
        new Rect(x, y + (rowHeight - desired.Height) / 2, desired.Width, desired.Height);
}
