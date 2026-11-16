using System;
using System.Windows;
using System.Windows.Controls;

namespace ProjectSync.App.Views;

// Children are the batch buttons, the project/tool buttons, and the selection summary.
public sealed class HomeToolbarPanel : Panel
{
    private const double ColumnGap = 12;
    private const double RowGap = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var (batch, tools, summary) = GetRectangles(availableSize.Width);
        return new Size(Math.Min(availableSize.Width, tools.Right), Math.Max(tools.Bottom, Math.Max(batch.Bottom, summary.Bottom)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (batch, tools, summary) = GetRectangles(finalSize.Width);
        InternalChildren[0].Arrange(batch);
        InternalChildren[1].Arrange(tools);
        InternalChildren[2].Arrange(summary);
        return finalSize;
    }

    private (Rect Batch, Rect Tools, Rect Summary) GetRectangles(double width)
    {
        Size batch = InternalChildren[0].DesiredSize;
        Size tools = InternalChildren[1].DesiredSize;
        Size summary = InternalChildren[2].DesiredSize;
        double actionWidth = batch.Width + ColumnGap + tools.Width;
        double preferredWidth = actionWidth + ColumnGap + summary.Width;
        width = double.IsPositiveInfinity(width) ? preferredWidth : width;

        double rowHeight = Math.Max(batch.Height, tools.Height);
        double toolsLeft = InternalChildren[0].Visibility == Visibility.Collapsed ? 0 : Math.Max(0, width - tools.Width);
        if (width >= preferredWidth)
        {
            rowHeight = Math.Max(rowHeight, summary.Height);
            return (Centered(batch, 0, 0, rowHeight),
                Centered(tools, toolsLeft, 0, rowHeight),
                Centered(summary, batch.Width + ColumnGap, 0, rowHeight));
        }

        if (width >= actionWidth)
        {
            return (Centered(batch, 0, 0, rowHeight),
                Centered(tools, toolsLeft, 0, rowHeight),
                new Rect(0, rowHeight + RowGap, Math.Min(summary.Width, width), summary.Height));
        }

        // At unusually large text sizes, pair the tools with the summary on the second row.
        double secondRow = batch.Height + RowGap;
        double secondHeight = Math.Max(tools.Height, summary.Height);
        summary.Width = Math.Min(summary.Width, Math.Max(0, toolsLeft - ColumnGap));
        return (new Rect(0, 0, batch.Width, batch.Height),
            Centered(tools, toolsLeft, secondRow, secondHeight),
            Centered(summary, 0, secondRow, secondHeight));
    }

    private static Rect Centered(Size desired, double x, double y, double rowHeight) =>
        new Rect(x, y + (rowHeight - desired.Height) / 2, desired.Width, desired.Height);
}
