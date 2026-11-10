#region

using Avalonia;
using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Views.ScoutingDashboard;

/// <summary>
///     The Dashboard's card grid: as many columns as fit at <see cref="MinColumnWidth" />, each card dropped into the
///     shortest column so cards of different heights pack without gaps. A child marked <see cref="IsWideProperty" />
///     starts below everything placed so far and spans every column.
/// </summary>
public sealed class ScoutingCardGrid : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<ScoutingCardGrid, double>(nameof(MinColumnWidth), 380);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ScoutingCardGrid, double>(nameof(Spacing), 14);

    public static readonly AttachedProperty<bool> IsWideProperty =
        AvaloniaProperty.RegisterAttached<ScoutingCardGrid, Control, bool>("IsWide");

    static ScoutingCardGrid()
    {
        AffectsMeasure<ScoutingCardGrid>(MinColumnWidthProperty, SpacingProperty);
        AffectsParentMeasure<ScoutingCardGrid>(IsWideProperty);
    }

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static bool GetIsWide(Control control) => control.GetValue(IsWideProperty);

    public static void SetIsWide(Control control, bool value) => control.SetValue(IsWideProperty, value);

    private readonly List<Rect> _slots = [];

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? MinColumnWidth * 2 : availableSize.Width;
        return new Size(width, Layout(width, measure: true));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, measure: false);
        int i = 0;
        foreach (Control child in Children)
        {
            if (child.IsVisible && i < _slots.Count)
            {
                child.Arrange(_slots[i++]);
            }
        }

        return finalSize;
    }

    // Places every visible child; measures them first when asked. Returns the height used.
    private double Layout(double width, bool measure)
    {
        int columns = Math.Max(1, (int)((width + Spacing) / (MinColumnWidth + Spacing)));
        double column = (width - Spacing * (columns - 1)) / columns;
        double[] heights = new double[columns];
        _slots.Clear();
        foreach (Control child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            bool wide = GetIsWide(child) || columns == 1;
            double w = wide ? width : column;
            if (measure)
            {
                child.Measure(new Size(w, double.PositiveInfinity));
            }

            double h = child.DesiredSize.Height;
            if (wide)
            {
                double top = heights.Max();
                _slots.Add(new Rect(0, top, w, h));
                Array.Fill(heights, top + h + Spacing);
                continue;
            }

            int shortest = Array.IndexOf(heights, heights.Min());
            _slots.Add(new Rect(shortest * (column + Spacing), heights[shortest], w, h));
            heights[shortest] += h + Spacing;
        }

        return Math.Max(0, heights.Max() - Spacing);
    }
}
