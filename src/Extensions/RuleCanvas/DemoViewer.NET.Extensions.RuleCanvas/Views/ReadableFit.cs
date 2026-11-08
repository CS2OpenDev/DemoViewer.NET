#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     The viewport that shows a graph at a readable zoom. The zoom is the fit clamped to
///     <see cref="MinZoom" /> to <see cref="MaxZoom" />; when the whole extent does not fit at the floor, the
///     authored nodes are framed instead, anchored to the left-most column. Pure, so the rule is testable
///     without an editor.
/// </summary>
/// <param name="Zoom">The viewport zoom.</param>
/// <param name="Location">The viewport's top-left in graph coordinates.</param>
/// <param name="FramesAuthoredOnly">Whether the whole chain was too large and only the authored nodes are framed.</param>
public sealed record ReadableFit(double Zoom, Point Location, bool FramesAuthoredOnly)
{
    public const double MinZoom = 0.8;

    public const double MaxZoom = 1.0;

    public const double Pad = 40;

    /// <param name="nodes">Every node: where it is, how big it is estimated, and whether the author wrote it.</param>
    /// <param name="viewport">The editor's size in screen pixels.</param>
    public static ReadableFit Of(IEnumerable<(Point Location, Size Size, bool IsAuthored)> nodes, Size viewport)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        List<(Point Location, Size Size, bool IsAuthored)> all = [.. nodes];
        if (all.Count == 0)
        {
            return new ReadableFit(MaxZoom, new Point(0, 0), false);
        }

        (Rect extent, double fit) = Frame(all, viewport);
        if (fit >= MinZoom || !all.Any(n => n.IsAuthored))
        {
            return new ReadableFit(Math.Clamp(fit, MinZoom, MaxZoom), extent.TopLeft, false);
        }

        List<(Point Location, Size Size, bool IsAuthored)> authored = [.. all.Where(n => n.IsAuthored)];
        (Rect frame, double authoredFit) = Frame(authored, viewport);
        return new ReadableFit(Math.Clamp(authoredFit, MinZoom, MaxZoom), frame.TopLeft, true);
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

    // The padded extent of the nodes and the zoom that fits it to the viewport, anchored at the extent's
    // top-left so the left-most column is in view whatever the clamp does to the zoom.
    private static (Rect Extent, double Fit) Frame(List<(Point Location, Size Size, bool IsAuthored)> nodes, Size viewport)
    {
        double minX = nodes.Min(n => n.Location.X) - Pad;
        double minY = nodes.Min(n => n.Location.Y) - Pad;
        double maxX = nodes.Max(n => n.Location.X + n.Size.Width) + Pad;
        double maxY = nodes.Max(n => n.Location.Y + n.Size.Height) + Pad;
        Rect extent = new(new Point(minX, minY), new Point(maxX, maxY));
        double fit = Math.Min(viewport.Width / Math.Max(1, extent.Width), viewport.Height / Math.Max(1, extent.Height));
        return (extent, fit);
    }
}
