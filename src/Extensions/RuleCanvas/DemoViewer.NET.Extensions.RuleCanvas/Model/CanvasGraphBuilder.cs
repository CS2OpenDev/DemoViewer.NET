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

        // One input per kind a node takes and one output per node, typed by what they carry.
        Dictionary<(RulesetDocumentNodeKey, PortKind), CanvasConnectorViewModel> inputs = new();
        Dictionary<RulesetDocumentNodeKey, CanvasConnectorViewModel> outputs = new();
        CanvasConnectorViewModel Input(RulesetDocumentNodeKey key, PortKind kind)
        {
            if (!inputs.TryGetValue((key, kind), out CanvasConnectorViewModel? connector))
            {
                connector = new CanvasConnectorViewModel { Title = CanvasConnectorViewModel.TitleOf(kind), Owner = byKey[key], Kind = kind };
                inputs[(key, kind)] = connector;
                byKey[key].Inputs.Add(connector);
            }

            return connector;
        }

        CanvasConnectorViewModel Output(RulesetDocumentNodeKey key)
        {
            if (!outputs.TryGetValue(key, out CanvasConnectorViewModel? connector))
            {
                CanvasNodeViewModel owner = byKey[key];
                PortKind kind = OutputKindOf(owner);
                connector = new CanvasConnectorViewModel
                {
                    Title = kind == PortKind.Value ? owner.OutputTitle : CanvasConnectorViewModel.TitleOf(kind), Owner = owner, Kind = kind
                };
                outputs[key] = connector;
                owner.Outputs.Add(connector);
            }

            return connector;
        }

        // An authored node always has its ports, wired or not, so a wire can start or end there before
        // anything feeds or reads it. Scaffolds get theirs with their first wire.
        foreach (CanvasNodeViewModel node in nodes.Where(n => n.IsAuthorable && !n.IsScaffold))
        {
            foreach (PortKind kind in InputKindsOf(node))
            {
                Input(node.Key, kind);
            }

            if (!node.IsHighlight)
            {
                Output(node.Key);
            }
        }

        IReadOnlyList<WireSpec> specs = ChainWiring.Wires(composed, byKey.Keys.ToHashSet());
        List<CanvasConnectionViewModel> wires = new(specs.Count);
        foreach (WireSpec spec in specs)
        {
            PortKind into = spec.IsGate ? PortKind.Gate : spec.IsRead ? PortKind.Read : spec.IsAnd || spec.IsOr ? PortKind.Logic : PortKind.Trigger;
            wires.Add(new CanvasConnectionViewModel(Output(spec.Source), Input(spec.Target, into),
                spec.Text, spec.Condition, spec.IsRead, spec.IsGate, spec.IsAnd, spec.IsOr));
        }

        // The rows carry their ports: a where or compute line the reads, a when line the logic input.
        foreach (CanvasNodeViewModel node in nodes)
        {
            foreach (CanvasExpressionViewModel line in node.Lines)
            {
                line.Port = line.Key switch
                {
                    "where" or "compute" => node.ReadPort,
                    "when" => node.LogicPort,
                    _ => null
                };
            }

            node.PortsChanged();
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

    /// <summary>What a node gives: an event or a define fires, a context flag holds, everything else is a value.</summary>
    public static PortKind OutputKindOf(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.IsDefine || node.Ruleset == "~event" ? PortKind.Fire
            : node.Ruleset == "~context" ? PortKind.Flag
            : PortKind.Value;
    }

    /// <summary>
    ///     The inputs an authored node takes, in face order: a trigger where the kind fires on one, a gate
    ///     where <c>while:</c> applies, reads for the values its body names, a logic input for a <c>when:</c>.
    /// </summary>
    public static IReadOnlyList<PortKind> InputKindsOf(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsDefine)
        {
            return [PortKind.Trigger, PortKind.Gate];
        }

        if (node.IsHighlight)
        {
            return [PortKind.Logic];
        }

        return node.Kind switch
        {
            "compute" => [PortKind.Read],
            "flag" when node.Trigger is null => [PortKind.Logic, PortKind.Gate, PortKind.Read],
            _ when node.Trigger is null => [PortKind.Gate, PortKind.Read],
            _ => [PortKind.Trigger, PortKind.Gate, PortKind.Read]
        };
    }
}
