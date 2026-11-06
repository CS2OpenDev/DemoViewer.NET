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

/// <summary>
///     First placement of a graph the user has never arranged. Each connected component is laid out on its
///     own by MSAGL's layered layout, rotated so values flow left to right, and the components are then
///     shelved into rows of a landscape canvas, wired components first and largest first. Shelving is done
///     here rather than by MSAGL's own packing because most stats are components of one node, and MSAGL
///     stacks those into one tall column whatever aspect ratio it is asked for.
/// </summary>
public static class CanvasPlacement
{
    private const double NodeSeparation = 40;
    private const double LayerSeparation = 140;
    private const double Gap = 60;
    private const double Pad = 40;

    /// <summary>One node to place, with the size the canvas is expected to give it.</summary>
    public sealed record Item(RulesetDocumentNodeKey Key, double Width, double Height);

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

        List<Component> components = Components(items, edges);
        List<(Component Component, Dictionary<RulesetDocumentNodeKey, Point> Local, Size Box)> boxes = [];
        foreach (Component component in components)
        {
            (Dictionary<RulesetDocumentNodeKey, Point> local, Size box) = component.Items.Count == 1
                ? (new Dictionary<RulesetDocumentNodeKey, Point> { [component.Items[0].Key] = new Point(0, 0) },
                    new Size(component.Items[0].Width, component.Items[0].Height))
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

    // One wired component through MSAGL, returned as top-left corners relative to the component's own origin.
    private static (Dictionary<RulesetDocumentNodeKey, Point> Local, Size Box) LayOut(Component component)
    {
        GeometryGraph graph = new();
        Dictionary<RulesetDocumentNodeKey, Node> nodes = new();
        foreach (Item item in component.Items)
        {
            Node node = new(CurveFactory.CreateRectangle(item.Width, item.Height, new MsaglPoint(0, 0))) { UserData = item };
            graph.Nodes.Add(node);
            nodes[item.Key] = node;
        }

        foreach (RulesetDocumentEdge edge in component.Edges)
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
        double width = 0, height = 0;
        foreach ((RulesetDocumentNodeKey key, Node node) in nodes)
        {
            Item item = (Item)node.UserData;
            double x = node.Center.X - bounds.Left - item.Width / 2;
            double y = bounds.Top - node.Center.Y - item.Height / 2;
            local[key] = new Point(x, y);
            width = Math.Max(width, x + item.Width);
            height = Math.Max(height, y + item.Height);
        }

        return (local, new Size(width, height));
    }
}
