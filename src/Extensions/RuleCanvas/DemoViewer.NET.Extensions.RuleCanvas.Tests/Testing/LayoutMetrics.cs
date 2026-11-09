#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     What a placement is measured by: the extent and its aspect, the wire pairs whose straight runs
///     cross, and the wires whose midpoint lands inside a node that is neither end. A wire runs from the
///     source's right edge to the target's left edge, at the port's row: mid-height, or the column's row on
///     a show node.
/// </summary>
internal static class LayoutMetrics
{
    private const double ShowHeaderHeight = 40 + 24;
    private const double ShowRowHeight = 22;

    public static Rect ExtentOf(IEnumerable<CanvasNodeViewModel> nodes)
    {
        Rect? union = null;
        foreach (CanvasNodeViewModel node in nodes)
        {
            Rect rect = new(node.Location, node.EstimatedSize);
            union = union is { } u ? u.Union(rect) : rect;
        }

        return union ?? default;
    }

    public static double AspectOf(IEnumerable<CanvasNodeViewModel> nodes)
    {
        Rect extent = ExtentOf(nodes);
        return extent.Height <= 0 ? 0 : extent.Width / extent.Height;
    }

    /// <summary>The straight run of <paramref name="wire" />, from the source's output rail to the target's input row.</summary>
    public static (Point From, Point To)? RunOf(CanvasConnectionViewModel wire)
    {
        if (wire.Source.Owner is not { } source || wire.Target.Owner is not { } target)
        {
            return null;
        }

        Point from = new(source.Location.X + source.EstimatedSize.Width, source.Location.Y + source.EstimatedSize.Height / 2);
        double row = target.EstimatedSize.Height / 2;
        if (target.IsShow)
        {
            int index = target.Inputs.IndexOf(wire.Target);
            row = ShowHeaderHeight + Math.Max(0, index) * ShowRowHeight + ShowRowHeight / 2;
        }

        return (from, new Point(target.Location.X, target.Location.Y + row));
    }

    /// <summary>Wire pairs whose runs cross; pairs that share a node do not count, they meet at a port.</summary>
    public static int Crossings(IReadOnlyList<CanvasConnectionViewModel> wires)
    {
        List<(Point From, Point To, CanvasNodeViewModel Source, CanvasNodeViewModel Target)> runs = [];
        foreach (CanvasConnectionViewModel wire in wires)
        {
            if (RunOf(wire) is { } run && wire.Source.Owner is { } source && wire.Target.Owner is { } target)
            {
                runs.Add((run.From, run.To, source, target));
            }
        }

        int crossings = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            for (int j = i + 1; j < runs.Count; j++)
            {
                bool shared = ReferenceEquals(runs[i].Source, runs[j].Source) || ReferenceEquals(runs[i].Source, runs[j].Target)
                              || ReferenceEquals(runs[i].Target, runs[j].Source) || ReferenceEquals(runs[i].Target, runs[j].Target);
                if (!shared && Cross(runs[i].From, runs[i].To, runs[j].From, runs[j].To))
                {
                    crossings++;
                }
            }
        }

        return crossings;
    }

    /// <summary>The wires whose midpoint lies inside a node that is neither their source nor their target, with that node.</summary>
    public static List<(CanvasConnectionViewModel Wire, CanvasNodeViewModel Under)> MidpointHits(
        IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires)
    {
        List<(CanvasConnectionViewModel, CanvasNodeViewModel)> hits = [];
        foreach (CanvasConnectionViewModel wire in wires)
        {
            if (RunOf(wire) is not { } run)
            {
                continue;
            }

            Point mid = new((run.From.X + run.To.X) / 2, (run.From.Y + run.To.Y) / 2);
            foreach (CanvasNodeViewModel node in nodes)
            {
                if (!ReferenceEquals(node, wire.Source.Owner) && !ReferenceEquals(node, wire.Target.Owner)
                    && new Rect(node.Location, node.EstimatedSize).Contains(mid))
                {
                    hits.Add((wire, node));
                }
            }
        }

        return hits;
    }

    /// <summary>Whether two nodes' boxes overlap.</summary>
    public static bool Overlap(CanvasNodeViewModel a, CanvasNodeViewModel b) =>
        new Rect(a.Location, a.EstimatedSize).Intersects(new Rect(b.Location, b.EstimatedSize));

    // Proper intersection of two segments; touching or collinear runs do not count.
    private static bool Cross(Point a, Point b, Point c, Point d)
    {
        double d1 = Orient(c, d, a), d2 = Orient(c, d, b), d3 = Orient(a, b, c), d4 = Orient(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Orient(Point p, Point q, Point r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
}
