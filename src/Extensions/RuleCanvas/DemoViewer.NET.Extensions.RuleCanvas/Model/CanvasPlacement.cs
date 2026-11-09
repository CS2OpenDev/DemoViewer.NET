#region

using Avalonia;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Core.Routing;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using MsaglPoint = Microsoft.Msagl.Core.Geometry.Point;
using MsaglRectangle = Microsoft.Msagl.Core.Geometry.Rectangle;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>What a node is to the placement: what fires, what is read, what is written, what is shown.</summary>
public enum CanvasNodeRole
{
    /// <summary>Not said; read off the key, which tells every role apart except a highlight from a stat.</summary>
    Unknown,

    /// <summary>A declared stat: fired by an event or a define, or derived from other stats.</summary>
    Stat,

    /// <summary>A trigger-bodied define, which fires the stats that name it.</summary>
    Define,

    /// <summary>A highlight: logic in, nothing out but its count.</summary>
    Highlight,

    /// <summary>A show table, the scoreboard or the params block.</summary>
    Surface,

    /// <summary>A wire event.</summary>
    Event,

    /// <summary>A built-in enrichment.</summary>
    Enrichment,

    /// <summary>A built-in context flag or value.</summary>
    Context,

    /// <summary>Any other engine node.</summary>
    Other
}

/// <summary>
///     First placement of a graph the user has never arranged. Each connected component is laid out on its
///     own, rotated so values flow left to right, and the components are then shelved into rows of a
///     landscape canvas, wired components first and largest first. A component of up to
///     <see cref="BlockThreshold" /> nodes goes through MSAGL's layered layout; a bigger one is laid out in
///     event blocks by <see cref="CanvasBlockLayout" />, since Sugiyama stacks a hundred stats into one
///     column many screens tall. Shelving is done here rather than by MSAGL's own packing because most
///     stats are components of one node, and MSAGL stacks those into one tall column whatever aspect ratio
///     it is asked for.
/// </summary>
public static class CanvasPlacement
{
    private const double NodeSeparation = 40;
    private const double LayerSeparation = 140;
    private const double Gap = 60;
    private const double Pad = 40;

    /// <summary>Above this many nodes a component is laid out in event blocks rather than layers.</summary>
    public const int BlockThreshold = 30;

    /// <summary>The gap a seated scaffold keeps from the stat it sits beside, and between two seated beside one stat.</summary>
    public const double SeatGap = 24;

    /// <summary>The vertical pitch of scaffolds seated beside one stat.</summary>
    public const double SeatPitch = 38;

    /// <summary>One node to place, with the size the canvas is expected to give it.</summary>
    public sealed record Item(RulesetDocumentNodeKey Key, double Width, double Height, CanvasNodeRole Role = CanvasNodeRole.Unknown)
    {
        /// <summary>The role given, or the one the key tells.</summary>
        public CanvasNodeRole EffectiveRole => Role == CanvasNodeRole.Unknown ? RoleOf(Key) : Role;

        /// <summary>An engine node: an event, an enrichment, a context value or another built-in.</summary>
        public bool IsScaffold => EffectiveRole is CanvasNodeRole.Event or CanvasNodeRole.Enrichment or CanvasNodeRole.Context or CanvasNodeRole.Other;

        /// <summary>A scaffold that feeds rather than fires: an enrichment, a context value or another built-in.</summary>
        public bool IsFeeder => EffectiveRole is CanvasNodeRole.Enrichment or CanvasNodeRole.Context or CanvasNodeRole.Other;
    }

    /// <summary>The role a key tells on its own; a highlight reads as a stat here, since only its face knows.</summary>
    public static CanvasNodeRole RoleOf(RulesetDocumentNodeKey key)
    {
        if (key.IsShow || key.IsParams)
        {
            return CanvasNodeRole.Surface;
        }

        if (key.IsDefine)
        {
            return CanvasNodeRole.Define;
        }

        return key.Ruleset switch
        {
            "~event" => CanvasNodeRole.Event,
            "~enrichment" => CanvasNodeRole.Enrichment,
            "~context" => CanvasNodeRole.Context,
            _ when key.Ruleset.StartsWith('~') => CanvasNodeRole.Other,
            _ => CanvasNodeRole.Stat
        };
    }

    private static long _placements;

    /// <summary>How many times the layout engine has run in this process: a test's witness that a redraw did not place.</summary>
    public static long Placements => Interlocked.Read(ref _placements);

    /// <summary>
    ///     Places only the items <paramref name="known" /> has no position for, which is how a redraw after
    ///     an edit keeps MSAGL off its path: with every node arranged nothing is computed, and a node that
    ///     has no position yet is placed on its own, below the arranged ones, so it cannot land on one.
    /// </summary>
    public static IReadOnlyDictionary<RulesetDocumentNodeKey, Point> Place(
        IReadOnlyList<Item> items, IReadOnlyList<RulesetDocumentEdge> edges, RulesetNodeLayout known)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(known);
        Dictionary<RulesetDocumentNodeKey, Point> placed = new();
        List<Item> missing = [];
        double bottom = 0;
        foreach (Item item in items)
        {
            if (known.PositionFor(item.Key) is { } at)
            {
                placed[item.Key] = at;
                bottom = Math.Max(bottom, at.Y + item.Height);
            }
            else
            {
                missing.Add(item);
            }
        }

        if (missing.Count == 0)
        {
            return placed;
        }

        if (placed.Count == 0)
        {
            return Place(items, edges);
        }

        double below = Math.Round(bottom + Gap);
        foreach ((RulesetDocumentNodeKey key, Point at) in Place(missing, edges))
        {
            placed[key] = new Point(at.X, at.Y + below);
        }

        return placed;
    }

    /// <summary>
    ///     Places <paramref name="items" /> and returns each node's top-left corner in canvas coordinates.
    /// </summary>
    public static IReadOnlyDictionary<RulesetDocumentNodeKey, Point> Place(
        IReadOnlyList<Item> items, IReadOnlyList<RulesetDocumentEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(edges);
        Dictionary<RulesetDocumentNodeKey, Point> placed = new();
        if (items.Count == 0)
        {
            return placed;
        }

        Interlocked.Increment(ref _placements);
        List<Component> components = Components(items, edges);
        List<(Component Component, Dictionary<RulesetDocumentNodeKey, Point> Local, Size Box)> boxes = [];
        foreach (Component component in components)
        {
            (Dictionary<RulesetDocumentNodeKey, Point> local, Size box) = component.Items.Count == 1
                ? (new Dictionary<RulesetDocumentNodeKey, Point> { [component.Items[0].Key] = new Point(0, 0) },
                    new Size(component.Items[0].Width, component.Items[0].Height))
                : component.Items.Count > BlockThreshold
                    ? CanvasBlockLayout.LayOut(component.Items, component.Edges)
                    : LayOut(component);
            boxes.Add((component, local, box));
        }

        // Wired components first, then by area, so the picture a reader scans starts with the relationships.
        boxes.Sort((a, b) =>
        {
            int wired = b.Component.Edges.Count.CompareTo(a.Component.Edges.Count);
            return wired != 0 ? Math.Sign(wired) : (b.Box.Width * b.Box.Height).CompareTo(a.Box.Width * a.Box.Height);
        });

        double totalArea = boxes.Sum(b => (b.Box.Width + Gap) * (b.Box.Height + Gap));
        double shelfWidth = Math.Max(1400, Math.Sqrt(totalArea * 1.8));
        double x = Pad, y = Pad, rowHeight = 0;
        foreach ((Component _, Dictionary<RulesetDocumentNodeKey, Point> local, Size box) in boxes)
        {
            if (x > Pad && x + box.Width > shelfWidth)
            {
                x = Pad;
                y += rowHeight + Gap;
                rowHeight = 0;
            }

            foreach ((RulesetDocumentNodeKey key, Point at) in local)
            {
                placed[key] = new Point(Math.Round(x + at.X), Math.Round(y + at.Y));
            }

            x += box.Width + Gap;
            rowHeight = Math.Max(rowHeight, box.Height);
        }

        return placed;
    }

    private sealed record Component(List<Item> Items, List<RulesetDocumentEdge> Edges);

    private static List<Component> Components(IReadOnlyList<Item> items, IReadOnlyList<RulesetDocumentEdge> edges)
    {
        Dictionary<RulesetDocumentNodeKey, RulesetDocumentNodeKey> parent = items.ToDictionary(i => i.Key, i => i.Key);

        RulesetDocumentNodeKey Find(RulesetDocumentNodeKey key)
        {
            while (!parent[key].Equals(key))
            {
                parent[key] = parent[parent[key]];
                key = parent[key];
            }

            return key;
        }

        foreach (RulesetDocumentEdge edge in edges)
        {
            if (parent.ContainsKey(edge.Source) && parent.ContainsKey(edge.Target))
            {
                parent[Find(edge.Source)] = Find(edge.Target);
            }
        }

        Dictionary<RulesetDocumentNodeKey, Component> byRoot = new();
        foreach (Item item in items)
        {
            RulesetDocumentNodeKey root = Find(item.Key);
            if (!byRoot.TryGetValue(root, out Component? component))
            {
                component = new Component([], []);
                byRoot[root] = component;
            }

            component.Items.Add(item);
        }

        foreach (RulesetDocumentEdge edge in edges)
        {
            if (parent.ContainsKey(edge.Source) && parent.ContainsKey(edge.Target) && !edge.Source.Equals(edge.Target))
            {
                byRoot[Find(edge.Source)].Edges.Add(edge);
            }
        }

        return [.. byRoot.Values];
    }

    // One wired component through MSAGL, returned as top-left corners relative to the component's own
    // origin. A feeder read by exactly one stat is laid out as part of that stat and seated beside it after.
    private static (Dictionary<RulesetDocumentNodeKey, Point> Local, Size Box) LayOut(Component component)
    {
        Dictionary<RulesetDocumentNodeKey, Item> byKey = component.Items.ToDictionary(i => i.Key);
        Dictionary<RulesetDocumentNodeKey, List<Item>> seated = SingleFeeders(component, byKey);
        HashSet<RulesetDocumentNodeKey> seatedKeys = [.. seated.Values.SelectMany(s => s).Select(s => s.Key)];
        Dictionary<RulesetDocumentNodeKey, RulesetDocumentNodeKey> seatOf = seated
            .SelectMany(pair => pair.Value.Select(s => (s.Key, Stat: pair.Key)))
            .ToDictionary(p => p.Key, p => p.Stat);

        List<Item> layoutItems = [];
        foreach (Item item in component.Items)
        {
            if (seatedKeys.Contains(item.Key))
            {
                continue;
            }

            if (seated.TryGetValue(item.Key, out List<Item>? feeders))
            {
                double lane = feeders.Max(f => f.Width) + SeatGap;
                layoutItems.Add(item with { Width = item.Width + lane, Height = Math.Max(item.Height, feeders.Count * SeatPitch - (SeatPitch - 30)) });
            }
            else
            {
                layoutItems.Add(item);
            }
        }

        // An edge into a seated feeder lands on its stat for the layout; the seat wire itself is gone.
        List<RulesetDocumentEdge> layoutEdges = [];
        foreach (RulesetDocumentEdge edge in component.Edges)
        {
            RulesetDocumentNodeKey source = seatOf.GetValueOrDefault(edge.Source, edge.Source);
            RulesetDocumentNodeKey target = seatOf.GetValueOrDefault(edge.Target, edge.Target);
            if (!source.Equals(target))
            {
                layoutEdges.Add(edge with { Source = source, Target = target });
            }
        }

        (Dictionary<RulesetDocumentNodeKey, Point> local, Dictionary<RulesetDocumentNodeKey, Node> nodes) = Sugiyama(layoutItems, layoutEdges);
        LeftAlignLayers(local, nodes);

        // Seated feeders take the left of the merged box, stacked up from the stat's foot so the wires into
        // its ports, which sit near its head, pass over them; the stat moves right by the lane.
        foreach ((RulesetDocumentNodeKey stat, List<Item> feeders) in seated)
        {
            Point at = local[stat];
            Item item = byKey[stat];
            double lane = feeders.Max(f => f.Width) + SeatGap;
            double foot = Math.Max(item.Height, feeders.Count * SeatPitch - (SeatPitch - feeders[0].Height));
            for (int i = 0; i < feeders.Count; i++)
            {
                double y = Math.Max(0, foot - feeders[i].Height - i * SeatPitch);
                local[feeders[i].Key] = new Point(at.X + lane - SeatGap - feeders[i].Width, at.Y + y);
            }

            local[stat] = new Point(at.X + lane, at.Y);
        }

        AvoidMidpoints(local, byKey, component.Edges, NodeSeparation);
        return (local, BoxOf(local, byKey));
    }

    /// <summary>
    ///     The feeders (enrichments, context values) read or gating exactly one node, by that node, when it is
    ///     a stat, a define or a highlight: these sit beside the stat rather than in the scaffold column.
    /// </summary>
    public static Dictionary<RulesetDocumentNodeKey, List<Item>> SingleFeeders(IReadOnlyList<Item> items, IReadOnlyList<RulesetDocumentEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(edges);
        return SingleFeeders(new Component([.. items], [.. edges]), items.ToDictionary(i => i.Key));
    }

    private static Dictionary<RulesetDocumentNodeKey, List<Item>> SingleFeeders(Component component, Dictionary<RulesetDocumentNodeKey, Item> byKey)
    {
        Dictionary<RulesetDocumentNodeKey, HashSet<RulesetDocumentNodeKey>> targets = new();
        foreach (RulesetDocumentEdge edge in component.Edges)
        {
            if (!targets.TryGetValue(edge.Source, out HashSet<RulesetDocumentNodeKey>? set))
            {
                set = [];
                targets[edge.Source] = set;
            }

            set.Add(edge.Target);
        }

        Dictionary<RulesetDocumentNodeKey, List<Item>> seated = new();
        foreach (Item item in component.Items)
        {
            if (!item.IsFeeder || !targets.TryGetValue(item.Key, out HashSet<RulesetDocumentNodeKey>? set) || set.Count != 1)
            {
                continue;
            }

            RulesetDocumentNodeKey stat = set.Single();
            if (!byKey.TryGetValue(stat, out Item? target)
                || target.EffectiveRole is not (CanvasNodeRole.Stat or CanvasNodeRole.Define or CanvasNodeRole.Highlight))
            {
                continue;
            }

            if (!seated.TryGetValue(stat, out List<Item>? list))
            {
                list = [];
                seated[stat] = list;
            }

            list.Add(item);
        }

        return seated;
    }

    private static (Dictionary<RulesetDocumentNodeKey, Point> Local, Dictionary<RulesetDocumentNodeKey, Node> Nodes) Sugiyama(
        List<Item> items, List<RulesetDocumentEdge> edges)
    {
        GeometryGraph graph = new();
        Dictionary<RulesetDocumentNodeKey, Node> nodes = new();
        foreach (Item item in items)
        {
            Node node = new(CurveFactory.CreateRectangle(item.Width, item.Height, new MsaglPoint(0, 0))) { UserData = item };
            graph.Nodes.Add(node);
            nodes[item.Key] = node;
        }

        foreach (RulesetDocumentEdge edge in edges)
        {
            graph.Edges.Add(new Edge(nodes[edge.Source], nodes[edge.Target]));
        }

        SugiyamaLayoutSettings settings = new()
        {
            Transformation = PlaneTransformation.Rotation(Math.PI / 2),
            NodeSeparation = NodeSeparation,
            LayerSeparation = LayerSeparation,
            EdgeRoutingSettings = { EdgeRoutingMode = EdgeRoutingMode.StraightLine }
        };
        LayoutHelpers.CalculateLayout(graph, settings, null);

        // MSAGL is Y-up; the canvas is Y-down. Flip against the component's bounding box.
        MsaglRectangle bounds = graph.BoundingBox;
        Dictionary<RulesetDocumentNodeKey, Point> local = new();
        foreach ((RulesetDocumentNodeKey key, Node node) in nodes)
        {
            Item item = (Item)node.UserData;
            double x = node.Center.X - bounds.Left - item.Width / 2;
            double y = bounds.Top - node.Center.Y - item.Height / 2;
            local[key] = new Point(x, y);
        }

        return (local, nodes);
    }

    // Sugiyama centres a layer's nodes on one x, so faces of different widths stagger their left edges. Each
    // layer is pulled to its left-most node, one node at a time, where the move lands on no other node.
    private static void LeftAlignLayers(Dictionary<RulesetDocumentNodeKey, Point> local, Dictionary<RulesetDocumentNodeKey, Node> nodes)
    {
        List<(RulesetDocumentNodeKey Key, double Centre)> byCentre = [.. nodes.Select(kv => (Key: kv.Key, Centre: kv.Value.Center.X)).OrderBy(n => n.Centre)];
        int start = 0;
        for (int i = 1; i <= byCentre.Count; i++)
        {
            if (i < byCentre.Count && byCentre[i].Centre - byCentre[start].Centre <= 1)
            {
                continue;
            }

            List<RulesetDocumentNodeKey> layer = [.. byCentre.Skip(start).Take(i - start).Select(n => n.Key)];
            double left = layer.Min(key => local[key].X);
            foreach (RulesetDocumentNodeKey key in layer)
            {
                Point at = local[key];
                if (at.X <= left)
                {
                    continue;
                }

                Item item = (Item)nodes[key].UserData;
                Rect moved = new(new Point(left, at.Y), new Size(item.Width, item.Height));
                bool lands = local.Any(other => !other.Key.Equals(key) && moved.Intersects(RectOf(other.Value, (Item)nodes[other.Key].UserData)));
                if (!lands)
                {
                    local[key] = new Point(left, at.Y);
                }
            }

            start = i;
        }
    }

    /// <summary>
    ///     A wire's straight run, as the midpoint rule and the crossing count see it: from the source's right
    ///     edge to the target's left edge, each at mid-height.
    /// </summary>
    public static (Point From, Point To) Run(Point source, Item sourceItem, Point target, Item targetItem)
    {
        ArgumentNullException.ThrowIfNull(sourceItem);
        ArgumentNullException.ThrowIfNull(targetItem);
        return (new Point(source.X + sourceItem.Width, source.Y + sourceItem.Height / 2), new Point(target.X, target.Y + targetItem.Height / 2));
    }

    /// <summary>Whether two runs cross properly; runs that only touch, or lie along one line, do not.</summary>
    public static bool Crosses(Point a, Point b, Point c, Point d)
    {
        double d1 = Orient(c, d, a), d2 = Orient(c, d, b), d3 = Orient(a, b, c), d4 = Orient(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Orient(Point p, Point q, Point r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);

    /// <summary>
    ///     The midpoint rule: a face never sits on the middle of a wire that is not its own. A node under a
    ///     foreign wire's midpoint is nudged down by its height plus <paramref name="rowGap" />, and the nodes
    ///     below it in its column go with it so the column stays whole. At most three passes.
    /// </summary>
    public static void AvoidMidpoints(Dictionary<RulesetDocumentNodeKey, Point> local, IReadOnlyDictionary<RulesetDocumentNodeKey, Item> items,
        IReadOnlyList<RulesetDocumentEdge> edges, double rowGap)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(edges);
        for (int pass = 0; pass < 3; pass++)
        {
            bool moved = false;
            foreach (RulesetDocumentEdge edge in edges)
            {
                if (!local.TryGetValue(edge.Source, out Point source) || !local.TryGetValue(edge.Target, out Point target)
                    || !items.TryGetValue(edge.Source, out Item? sourceItem) || !items.TryGetValue(edge.Target, out Item? targetItem))
                {
                    continue;
                }

                (Point from, Point to) = Run(source, sourceItem, target, targetItem);
                Point mid = new((from.X + to.X) / 2, (from.Y + to.Y) / 2);
                RulesetDocumentNodeKey? under = null;
                foreach ((RulesetDocumentNodeKey key, Point at) in local)
                {
                    if (!key.Equals(edge.Source) && !key.Equals(edge.Target) && items.TryGetValue(key, out Item? item) && RectOf(at, item).Contains(mid))
                    {
                        under = key;
                        break;
                    }
                }

                if (under is { } hit)
                {
                    ShiftColumn(local, items, hit, items[hit].Height + rowGap);
                    moved = true;
                }
            }

            if (!moved)
            {
                return;
            }
        }
    }

    // Moves key down by delta, and with it every node whose x-range overlaps it and starts at or below it.
    private static void ShiftColumn(Dictionary<RulesetDocumentNodeKey, Point> local, IReadOnlyDictionary<RulesetDocumentNodeKey, Item> items,
        RulesetDocumentNodeKey key, double delta)
    {
        Point top = local[key];
        Item item = items[key];
        foreach ((RulesetDocumentNodeKey other, Point at) in local.ToList())
        {
            Item otherItem = items[other];
            bool sameColumn = at.X < top.X + item.Width && at.X + otherItem.Width > top.X;
            if (other.Equals(key) || (sameColumn && at.Y >= top.Y))
            {
                local[other] = new Point(at.X, at.Y + delta);
            }
        }
    }

    internal static Size BoxOf(Dictionary<RulesetDocumentNodeKey, Point> local, IReadOnlyDictionary<RulesetDocumentNodeKey, Item> items)
    {
        double width = 0, height = 0;
        foreach ((RulesetDocumentNodeKey key, Point at) in local)
        {
            Item item = items[key];
            width = Math.Max(width, at.X + item.Width);
            height = Math.Max(height, at.Y + item.Height);
        }

        return new Size(width, height);
    }

    internal static Rect RectOf(Point at, Item item) => new(at, new Size(item.Width, item.Height));
}
