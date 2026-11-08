#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     The viewport that shows a graph at a readable zoom. The whole extent is fitted down to
///     <see cref="MinZoom" />; when it does not fit there, the viewport frames the extent's width from its
///     left-most column through the authored column, anchored at the extent's top-left, so the chain reads
///     source to stat and what is left over is a pan down. Pure, so the rule is testable without an editor.
/// </summary>
/// <param name="Zoom">The viewport zoom.</param>
/// <param name="Location">The viewport's top-left in graph coordinates.</param>
/// <param name="FramesSourceToStat">Whether the whole extent was too large and only its source-to-stat width is framed.</param>
public sealed record ReadableFit(double Zoom, Point Location, bool FramesSourceToStat)
{
    public const double MinZoom = 0.6;

    public const double MaxZoom = 1.0;

    public const double Pad = 40;

    /// <param name="nodes">Every node: where it is, how big it is estimated, and whether the author wrote it.</param>
    /// <param name="viewport">The editor's size in screen pixels.</param>
    public static ReadableFit Of(IEnumerable<(Point Location, Size Size, bool IsAuthored)> nodes, Size viewport) =>
        Of(nodes, viewport, 0);

    /// <param name="nodes">Every node: where it is, how big it is estimated, and whether the author wrote it.</param>
    /// <param name="viewport">The editor's size in screen pixels.</param>
    /// <param name="bottomReserve">Screen pixels along the viewport's bottom that overlays cover; the fit stays above them.</param>
    public static ReadableFit Of(IEnumerable<(Point Location, Size Size, bool IsAuthored)> nodes, Size viewport, double bottomReserve)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        List<(Point Location, Size Size, bool IsAuthored)> all = [.. nodes];
        if (all.Count == 0)
        {
            return new ReadableFit(MaxZoom, new Point(0, 0), false);
        }

        double usableHeight = Math.Max(1, viewport.Height - Math.Max(0, bottomReserve));
        Rect extent = ExtentOf(all);
        double fit = Math.Min(viewport.Width / Math.Max(1, extent.Width), usableHeight / Math.Max(1, extent.Height));
        if (fit >= MinZoom)
        {
            return new ReadableFit(Math.Min(fit, MaxZoom), extent.TopLeft, false);
        }

        // Source to stat: from the extent's left edge through the right edge of the right-most authored node.
        // Without an authored node the whole width is the reading direction.
        double right = all.Any(n => n.IsAuthored)
            ? all.Where(n => n.IsAuthored).Max(n => n.Location.X + n.Size.Width) + Pad
            : extent.Right;
        double widthFit = viewport.Width / Math.Max(1, right - extent.Left);
        return new ReadableFit(Math.Clamp(widthFit, MinZoom, MaxZoom), extent.TopLeft, true);
    }

    private static Rect ExtentOf(List<(Point Location, Size Size, bool IsAuthored)> nodes)
    {
        double minX = nodes.Min(n => n.Location.X) - Pad;
        double minY = nodes.Min(n => n.Location.Y) - Pad;
        double maxX = nodes.Max(n => n.Location.X + n.Size.Width) + Pad;
        double maxY = nodes.Max(n => n.Location.Y + n.Size.Height) + Pad;
        return new Rect(new Point(minX, minY), new Point(maxX, maxY));
    }
}
