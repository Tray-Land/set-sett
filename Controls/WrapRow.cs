using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace SetSett.Controls;

/// <summary>
/// Lays its children out in a row and wraps onto more rows when they don't fit, so a group of controls
/// stays reachable in a narrow window. Each child gets the full height of its row, so its own
/// VerticalAlignment decides where it sits (the editor lines controls up along the bottom).
/// </summary>
internal sealed partial class WrapRow : Panel
{
    public double Spacing { get; init; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        return Flow(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Flow(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Flow(double width, bool arrange)
    {
        double top = 0;
        double widest = 0;
        int next = 0;
        while (next < Children.Count)
        {
            // Take what fits on this row. A row always takes at least one child.
            int first = next;
            double used = 0;
            double height = 0;
            for (; next < Children.Count; next++)
            {
                if (Children[next].Visibility == Visibility.Collapsed)
                {
                    continue;
                }

                Size size = Children[next].DesiredSize;
                double extended = used == 0 ? size.Width : used + Spacing + size.Width;
                if (used > 0 && extended > width)
                {
                    break;
                }

                used = extended;
                height = Math.Max(height, size.Height);
            }

            if (arrange)
            {
                double left = 0;
                for (int i = first; i < next; i++)
                {
                    UIElement child = Children[i];
                    if (child.Visibility != Visibility.Collapsed)
                    {
                        child.Arrange(new Rect(left, top, child.DesiredSize.Width, height));
                        left += child.DesiredSize.Width + Spacing;
                    }
                }
            }

            widest = Math.Max(widest, used);
            top += height + Spacing;
        }

        return new Size(widest, Math.Max(0, top - Spacing));
    }
}
