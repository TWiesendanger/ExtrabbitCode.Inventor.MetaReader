using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ExtrabbitCode.Inventor.MetaReader.App.Common;

/// <summary>Lays children out left to right and wraps to a new line when the row is full - used for
/// the sidebar's badge row, whose badges (category, iLogic, triggers) can outgrow the narrow card.
/// Collapsed children take no space and no spacing.</summary>
public sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 6;
    public double VerticalSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, lineHeight = 0, width = 0;
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            Size d = child.DesiredSize;
            if (child.Visibility == Visibility.Collapsed || (d.Width <= 0 && d.Height <= 0))
            {
                continue;
            }
            if (x > 0 && x + d.Width > availableSize.Width)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            width = Math.Max(width, x + d.Width);
            x += d.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, d.Height);
        }
        return new Size(width, y + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (UIElement child in Children)
        {
            Size d = child.DesiredSize;
            if (child.Visibility == Visibility.Collapsed || (d.Width <= 0 && d.Height <= 0))
            {
                continue;
            }
            if (x > 0 && x + d.Width > finalSize.Width)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            child.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, d.Height);
        }
        return finalSize;
    }
}
