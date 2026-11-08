#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     The viewport that shows a graph at a readable zoom. The whole extent is fitted down to
///     <see cref="MinZoom" />; when it does not fit there, the viewport frames the extent's width from its
///     left-most column through the authored column, anchored at the extent's top-left, so the chain reads
///     source to stat and what is left over is a pan down. When even that is too wide for the floor, the
///     authored nodes' own box is framed from its top-left and the upstream is a pan left. Pure, so the rule
///     is testable without an editor.
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
        List<(Point Location, Size Size, bool IsAuthored)> authored = [.. all.Where(n => n.IsAuthored)];
        if (authored.Count == 0)
        {
            return new ReadableFit(MinZoom, extent.TopLeft, true);
        }

        Rect column = ExtentOf(authored);
        double widthFit = viewport.Width / Math.Max(1, column.Right - extent.Left);
        if (widthFit >= MinZoom)
        {
            return new ReadableFit(Math.Min(widthFit, MaxZoom), extent.TopLeft, true);
        }

        // Wider than the floor allows even from source to stat: the authored nodes' own box is framed from
        // its top-left, so what the user edits is under the viewport and the upstream is a pan left.
        double columnFit = Math.Min(viewport.Width / Math.Max(1, column.Width), usableHeight / Math.Max(1, column.Height));
        return new ReadableFit(Math.Clamp(columnFit, MinZoom, MaxZoom), column.TopLeft, true);
    }

    /// <summary>
    ///     The viewport that frames <paramref name="bounds" /> (graph space, already padded): the zoom that
    ///     fits them, at most <see cref="MaxZoom" /> and at least <paramref name="minZoom" />, centred on them.
    ///     The readable floor does not apply: a wide selection is framed whole.
    /// </summary>
    /// <param name="bounds">The area to frame, in graph coordinates.</param>
    /// <param name="viewport">The editor's size in screen pixels.</param>
    /// <param name="minZoom">The editor's own zoom floor.</param>
    public static ReadableFit OfSelection(Rect bounds, Size viewport, double minZoom)
    {
        double fit = Math.Min(viewport.Width / Math.Max(1, bounds.Width), viewport.Height / Math.Max(1, bounds.Height));
        double zoom = Math.Clamp(fit, Math.Min(minZoom, MaxZoom), MaxZoom);
        Point location = new(bounds.Center.X - viewport.Width / zoom / 2, bounds.Center.Y - viewport.Height / zoom / 2);
        return new ReadableFit(zoom, location, false);
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
