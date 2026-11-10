#region

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Views.ScoutingReport;

/// <summary>
///     The graphic part of one strip-mini row: a 100% stacked bar (<see cref="ScoutingStackRow" />), a strip-plot
///     lane (<see cref="ScoutingStripLane" />) or a dumbbell (<see cref="ScoutingDumbbell" />). Labels and numbers
///     stay in the template as text; this draws only the marks, in the theme's tokens, so it reads in both themes.
///     <para>
///         The buy ramp is one hue, light to dark (eco → full), with the segment's own label drawn inside when it
///         fits, in a colour picked against the step. Strip dots are filled for a won round and a ring for a lost
///         one, so colour is never the only cue.
///     </para>
/// </summary>
public sealed class ScoutingMiniGraphic : Control
{
    public static readonly StyledProperty<object?> ValueProperty =
        AvaloniaProperty.Register<ScoutingMiniGraphic, object?>(nameof(Value));

    // The one-hue ordinal ramp for buys, eco → full, and the ink each step's label is drawn in.
    private static readonly IBrush[] _ramp =
    [
        new SolidColorBrush(Color.Parse("#C9DCF5")), new SolidColorBrush(Color.Parse("#8FB6EA")),
        new SolidColorBrush(Color.Parse("#4F8EDC")), new SolidColorBrush(Color.Parse("#2A63AD"))
    ];

    private static readonly IBrush[] _ink =
    [
        new SolidColorBrush(Color.Parse("#12141C")), new SolidColorBrush(Color.Parse("#12141C")),
        Brushes.White, Brushes.White
    ];

    private const double SegmentGap = 1;
    private const double DotRadius = 3.5;

    static ScoutingMiniGraphic() => AffectsRender<ScoutingMiniGraphic>(ValueProperty);

    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, Value switch
        {
            ScoutingStackRow => 16,
            ScoutingStripLane => 18,
            _ => 16
        });

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 4)
        {
            return;
        }

        switch (Value)
        {
            case ScoutingStackRow row:
                RenderStack(context, bounds, row);
                break;
            case ScoutingStripLane lane:
                RenderLane(context, bounds, lane);
                break;
            case ScoutingDumbbell bell:
                RenderDumbbell(context, bounds, bell);
                break;
        }
    }

    private static void RenderStack(DrawingContext context, Rect bounds, ScoutingStackRow row)
    {
        double x = 0;
        double total = row.Segments.Sum(s => s.Share);
        if (total <= 0)
        {
            return;
        }

        foreach (ScoutingStackSegment segment in row.Segments)
        {
            double width = bounds.Width * segment.Share / total;
            int step = Math.Clamp(segment.Step, 0, _ramp.Length - 1);
            Rect rect = new(x, 0, Math.Max(1, width - SegmentGap), bounds.Height);
            context.DrawRectangle(_ramp[step], null, rect, 2, 2);
            FormattedText text = Text(segment.Label, 10, _ink[step]);
            if (text.Width + 6 <= rect.Width)
            {
                context.DrawText(text, new Point(rect.X + 4, (bounds.Height - text.Height) / 2));
            }

            x += width;
        }
    }

    private static void RenderLane(DrawingContext context, Rect bounds, ScoutingStripLane lane)
    {
        double max = lane.Max > 0 ? lane.Max : 1;
        double X(double v) => DotRadius + (bounds.Width - 2 * DotRadius) * Math.Clamp(v / max, 0, 1);
        IBrush? track = Token("BorderSubtle");
        IBrush? band = Token("BorderSubtle") is ISolidColorBrush b
            ? new SolidColorBrush(b.Color, 0.55)
            : null;
        context.DrawRectangle(track, null, new Rect(0, bounds.Height / 2 - 0.5, bounds.Width, 1));
        foreach (ScoutingStripBand shaded in lane.Bands)
        {
            double from = X(shaded.From);
            context.DrawRectangle(band, null, new Rect(from, 1, Math.Max(0, X(shaded.To) - from), bounds.Height - 2));
        }

        IBrush? color = ScoutingColorBrush.For(lane.Color);
        IBrush? surface = Token("PanelBg");
        Pen ring = new(color, 1.5);
        for (int i = 0; i < lane.Dots.Count; i++)
        {
            ScoutingStripDot dot = lane.Dots[i];
            // A small fixed jitter so dots at one time do not hide each other.
            double y = bounds.Height / 2 + (i % 3 - 1) * 3.5;
            context.DrawEllipse(dot.Filled ? color : surface, ring, new Point(X(dot.Value), y), DotRadius, DotRadius);
        }

        if (lane.Median is { } median)
        {
            context.DrawRectangle(Token("TextValue"), null, new Rect(X(median) - 1, 0, 2, bounds.Height));
        }
    }

    private static void RenderDumbbell(DrawingContext context, Rect bounds, ScoutingDumbbell bell)
    {
        const double radius = 5;
        double X(double v) => radius + (bounds.Width - 2 * radius) * Math.Clamp(v / 100, 0, 1);
        double y = bounds.Height / 2;
        IBrush? color = ScoutingColorBrush.For(bell.Color);
        context.DrawRectangle(Token("BorderSubtle"), null, new Rect(0, y - 1, bounds.Width, 2));
        if (bell is { Low: { } low, High: { } high })
        {
            double from = X(Math.Min(low, high));
            context.DrawRectangle(color, null, new Rect(from, y - 2, Math.Abs(X(high) - X(low)), 4));
        }

        if (bell.Low is { } l)
        {
            context.DrawEllipse(Token("PanelBg"), new Pen(color, 2), new Point(X(l), y), radius, radius);
        }

        if (bell.High is { } h)
        {
            context.DrawEllipse(color, null, new Point(X(h), y), radius + 0.5, radius + 0.5);
        }
    }

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    private static IBrush? Token(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out object? found)
            ? found as IBrush
            : null;
}
