using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DeskBox.Controls;

/// <summary>
/// A horizontal wrap panel that centers each wrapped line: the toolkit
/// WrapPanel left-aligns every line, but the minimal form's chips read as a
/// centered block — the trailing (shorter) line must center against the
/// panel's full extent like centered text. Wrapping follows the measure
/// constraint exactly like the toolkit panel (fixed spacing, one line
/// broken whenever the next child would overflow); only the per-line
/// arrangement differs.
/// </summary>
public sealed partial class CenteredWrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(CenteredWrapPanel),
            new PropertyMetadata(0d, OnSpacingChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(CenteredWrapPanel),
            new PropertyMetadata(0d, OnSpacingChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnSpacingChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        ((CenteredWrapPanel)sender).InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double spacing = HorizontalSpacing;
        double lineGap = VerticalSpacing;
        double lineWidth = 0;
        double lineThickness = 0;
        double totalHeight = 0;
        bool unbounded = double.IsInfinity(availableSize.Width);

        foreach (UIElement child in Children)
        {
            child.Measure(availableSize);
            Size desired = child.DesiredSize;
            if (!unbounded &&
                lineWidth > 0 &&
                lineWidth + spacing + desired.Width > availableSize.Width)
            {
                // The next child overflows: close this line, open the next.
                totalHeight += lineThickness + lineGap;
                lineWidth = desired.Width;
                lineThickness = desired.Height;
            }
            else
            {
                lineWidth = lineWidth == 0
                    ? desired.Width
                    : lineWidth + spacing + desired.Width;
                lineThickness = Math.Max(lineThickness, desired.Height);
            }
        }

        totalHeight += lineThickness;

        // Claim the constraint width so every line centers against the same
        // extent; an unbounded measure keeps the single line's own width.
        return new Size(
            unbounded ? Math.Min(lineWidth, availableSize.Width) : availableSize.Width,
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double spacing = HorizontalSpacing;
        double lineGap = VerticalSpacing;
        double lineWidth = 0;
        double lineThickness = 0;
        double y = 0;
        var line = new List<UIElement>();

        void CloseLine()
        {
            double x = (finalSize.Width - lineWidth) / 2;
            foreach (UIElement child in line)
            {
                Size desired = child.DesiredSize;
                child.Arrange(new Rect(x, y, desired.Width, desired.Height));
                x += desired.Width + spacing;
            }

            y += lineThickness + lineGap;
            line.Clear();
            lineWidth = 0;
            lineThickness = 0;
        }

        foreach (UIElement child in Children)
        {
            Size desired = child.DesiredSize;
            if (lineWidth > 0 &&
                lineWidth + spacing + desired.Width > finalSize.Width)
            {
                CloseLine();
            }

            line.Add(child);
            lineWidth = lineWidth == 0
                ? desired.Width
                : lineWidth + spacing + desired.Width;
            lineThickness = Math.Max(lineThickness, desired.Height);
        }

        if (line.Count > 0)
        {
            CloseLine();
        }

        return finalSize;
    }
}
