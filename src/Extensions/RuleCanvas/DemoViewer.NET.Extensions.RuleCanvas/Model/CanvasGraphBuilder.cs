#region

using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     Turns a composed ruleset into what the canvas binds to: a node view model per face, a connector per
///     side a wire lands on, a wire per distinct reference, and a first placement for every node, with a
///     saved arrangement winning per node.
/// </summary>
public static class CanvasGraphBuilder
{
    public static (IReadOnlyList<CanvasNodeViewModel> Nodes, IReadOnlyList<CanvasConnectionViewModel> Connections) Build(
        RulesetCorpus.Composed composed, RulesetNodeLayout? layout = null, CanvasNodeFactory? factory = null)
    {
        ArgumentNullException.ThrowIfNull(composed);
        layout ??= RulesetNodeLayout.Empty;
        factory ??= CanvasNodeFactory.ReadOnly;

        Dictionary<RulesetDocumentNodeKey, CanvasNodeViewModel> byKey = new();
        List<CanvasNodeViewModel> nodes = [];
        foreach (NodeFace face in NodeFaceBuilder.Build(composed))
        {
            CanvasNodeViewModel node = factory.Create(face);
            byKey[face.Key] = node;
            nodes.Add(node);
        }

        // One input and one output per node, made when the first wire lands on that side.
        Dictionary<RulesetDocumentNodeKey, CanvasConnectorViewModel> inputs = new();
        Dictionary<RulesetDocumentNodeKey, CanvasConnectorViewModel> outputs = new();
        CanvasConnectorViewModel Side(Dictionary<RulesetDocumentNodeKey, CanvasConnectorViewModel> side, RulesetDocumentNodeKey key, bool input)
        {
            if (!side.TryGetValue(key, out CanvasConnectorViewModel? connector))
            {
                connector = new CanvasConnectorViewModel { Title = input ? "" : byKey[key].OutputTitle, Owner = byKey[key] };
                side[key] = connector;
                (input ? byKey[key].Inputs : byKey[key].Outputs).Add(connector);
            }

            return connector;
        }

        IReadOnlyList<WireSpec> specs = ChainWiring.Wires(composed, byKey.Keys.ToHashSet());
        List<CanvasConnectionViewModel> wires = new(specs.Count);
        foreach (WireSpec spec in specs)
        {
            wires.Add(new CanvasConnectionViewModel(Side(outputs, spec.Source, false), Side(inputs, spec.Target, true),
                spec.Text, spec.Condition, spec.IsRead, spec.IsGate, spec.IsAnd, spec.IsOr));
        }

        List<CanvasPlacement.Item> items = new(nodes.Count);
        foreach (CanvasNodeViewModel node in nodes)
        {
            node.EstimatedSize = NodeSizeEstimator.Estimate(node);
            items.Add(new CanvasPlacement.Item(node.Key, node.EstimatedSize.Width, node.EstimatedSize.Height));
        }

        IReadOnlyDictionary<RulesetDocumentNodeKey, Avalonia.Point> placed =
            CanvasPlacement.Place(items, [.. specs.Select(s => s.AsEdge())], layout);
        foreach (CanvasNodeViewModel node in nodes)
        {
            if (placed.TryGetValue(node.Key, out Avalonia.Point at))
            {
                node.Location = at;
            }
        }

        return (nodes, wires);
    }
}
