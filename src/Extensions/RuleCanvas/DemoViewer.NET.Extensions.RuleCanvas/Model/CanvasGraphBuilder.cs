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

        // One input per kind a node takes and one output per node, typed by what they carry; a dependency
        // node has one output per entry read, titled by the entry.
        Dictionary<(RulesetDocumentNodeKey, PortKind), CanvasConnectorViewModel> inputs = new();
        Dictionary<(RulesetDocumentNodeKey, string?), CanvasConnectorViewModel> outputs = new();
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

        CanvasConnectorViewModel Output(RulesetDocumentNodeKey key, string? entry = null)
        {
            if (!outputs.TryGetValue((key, entry), out CanvasConnectorViewModel? connector))
            {
                CanvasNodeViewModel owner = byKey[key];
                PortKind kind = OutputKindOf(owner);
                connector = new CanvasConnectorViewModel
                {
                    Title = entry ?? (kind == PortKind.Value ? owner.OutputTitle : CanvasConnectorViewModel.TitleOf(kind)), Owner = owner, Kind = kind
                };
                outputs[(key, entry)] = connector;
                owner.Outputs.Add(connector);
            }

            return connector;
        }

        // A dependency's outputs exist before its wires, in the order the open document reads them.
        foreach (CanvasNodeViewModel node in nodes.Where(n => n.IsDependency))
        {
            foreach (string entry in node.DependencyEntries)
            {
                Output(node.Key, entry);
            }
        }

        // An authored node always has its ports, wired or not, so a wire can start or end there before
        // anything feeds or reads it. Scaffolds get theirs with their first wire.
        foreach (CanvasNodeViewModel node in nodes.Where(n => n.IsAuthorable && !n.IsScaffold))
        {
            // A show node takes one read input per column, so each column's wire lands on its own row.
            foreach (CanvasColumnViewModel column in node.Columns)
            {
                column.Port = new CanvasConnectorViewModel { Title = column.Label, Owner = node, Kind = PortKind.Read };
                node.Inputs.Add(column.Port);
            }

            foreach (PortKind kind in InputKindsOf(node))
            {
                Input(node.Key, kind);
            }

            if (!node.IsHighlight && !node.IsShow)
            {
                Output(node.Key);
            }
        }

        IReadOnlyList<WireSpec> specs = ChainWiring.Wires(composed, byKey.Keys.ToHashSet());
        List<CanvasConnectionViewModel> wires = new(specs.Count);
        foreach (WireSpec spec in specs)
        {
            PortKind into = spec.IsGate ? PortKind.Gate : spec.IsRead ? PortKind.Read : spec.IsAnd || spec.IsOr ? PortKind.Logic : PortKind.Trigger;
            CanvasNodeViewModel target = byKey[spec.Target];
            CanvasConnectorViewModel input = spec.Column is { } column && column < target.Columns.Count && target.Columns[column].Port is { } port
                ? port
                : Input(spec.Target, into);
            wires.Add(new CanvasConnectionViewModel(Output(spec.Source, spec.SourceOutput), input,
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

        // Decided once per wire from the wire and its target's when: text; the view asks the wire.
        foreach (CanvasConnectionViewModel wire in wires)
        {
            wire.ConditionEdit = CanvasEditSemantics.WireEditOf(wire);
        }

        // An authored entry nothing fires and nothing reads is said on the node, as a warning, unless an
        // error already explains it or nothing lowered at all, which is the error's doing.
        if (composed.Chain.IsLowered)
        {
            HashSet<CanvasNodeViewModel> wired = [.. wires.SelectMany(w => new[] { w.Source.Owner, w.Target.Owner }).OfType<CanvasNodeViewModel>()];
            foreach (CanvasNodeViewModel node in nodes.Where(n => n.IsAuthorable && !n.IsScaffold && !wired.Contains(n) && n.Diagnostics.Count == 0))
            {
                node.Diagnostics = [UnwiredWarning(node)];
            }
        }

        List<CanvasPlacement.Item> items = new(nodes.Count);
        foreach (CanvasNodeViewModel node in nodes)
        {
            node.EstimatedSize = NodeSizeEstimator.Estimate(node);
            items.Add(new CanvasPlacement.Item(node.Key, node.EstimatedSize.Width, node.EstimatedSize.Height, RoleOf(node)));
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

    /// <summary>The warning an unwired entry carries: what is missing, and the fix for its kind.</summary>
    public static RuleDiagnostic UnwiredWarning(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        string message = node switch
        {
            { IsParams: true } => "params is unwired: no entry reads a params.<name> yet",
            { IsShow: true } => $"{node.Title} is unwired: no column names a drawn entry",
            { IsDefine: true } => $"define {node.Title} is unwired: nothing fires on it",
            { IsHighlight: true } => $"{node.Title} is unwired: its when: reads no drawn stat, and nothing surfaces it",
            _ => $"{node.Title} is unwired: nothing fires it and nothing reads it; give it a trigger, or read it from a sibling or a show: column"
        };
        return new RuleDiagnostic(message, message, DiagnosticWeight.Warning, node.Ruleset) { Node = node.Key };
    }

    /// <summary>The placement's role for a node: the key's, except that a highlight's face is what tells it from a stat.</summary>
    public static CanvasNodeRole RoleOf(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.IsHighlight ? CanvasNodeRole.Highlight : CanvasPlacement.RoleOf(node.Key);
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
        if (node.IsShow || node.IsParams)
        {
            // A show node's inputs are its columns, made by the builder; a params node reads nothing.
            return [];
        }

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
