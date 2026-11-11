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

        // A floating event from the gallery is drawn until the chain draws its event, which then takes its place.
        List<(CanvasNodeViewModel Node, Avalonia.Point At)> floating = [];
        foreach (CanvasScaffold scaffold in layout.Scaffolds)
        {
            if (CanvasEditSemantics.ScaffoldKey(scaffold.View) is { } key && !byKey.ContainsKey(key))
            {
                CanvasNodeViewModel node = factory.Create(new ScaffoldFace(key, "event", scaffold.View, "", "", [], 0, false,
                    ChainNodeKind.Event, key.Id, true) { Events = [key.Id] });
                node.FloatingView = scaffold.View;
                byKey[key] = node;
                nodes.Add(node);
                floating.Add((node, layout.PositionFor(key) ?? scaffold.At));
            }
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
                    Title = entry ?? (kind == PortKind.Value ? owner.OutputTitle : CanvasConnectorViewModel.TitleOf(kind)), Owner = owner, Kind = kind,
                    Entry = owner.IsDependency ? entry : null
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
        foreach ((CanvasNodeViewModel node, Avalonia.Point _) in floating)
        {
            Output(node.Key);
        }

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

            // Every written row of a flat where: or when: has its own ring, so a wire lands on its row.
            foreach (CanvasExpressionViewModel line in node.Lines.Where(l => l.IsCondition))
            {
                PortKind kind = line.Key == "when" ? PortKind.Logic : PortKind.Read;
                foreach (CanvasConditionRowViewModel row in line.Rows.Where(r => !r.IsPending))
                {
                    row.Port = new CanvasConnectorViewModel { Title = row.Path, Owner = node, Kind = kind, IsRow = true };
                    node.Inputs.Add(row.Port);
                }
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
                : RowPortOf(byKey[spec.Source], target, into, spec.Condition) ?? Input(spec.Target, into);
            CanvasConnectorViewModel output = Output(spec.Source, spec.SourceOutput);
            input.IsWired = output.IsWired = true;
            wires.Add(new CanvasConnectionViewModel(output, input, spec.Text, spec.Condition, spec.IsRead, spec.IsGate, spec.IsAnd, spec.IsOr));
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

        foreach ((CanvasNodeViewModel node, Avalonia.Point at) in floating)
        {
            node.Location = at;
        }

        return (nodes, wires);
    }

    /// <summary>
    ///     The row a read or logic wire from <paramref name="source" /> stands for on <paramref name="target" />:
    ///     the when: row the wire's label names, or the first where: row on the path the source is read
    ///     under. Null when no row names it (a facet's read), which lands on the line's own ring.
    /// </summary>
    public static CanvasConnectorViewModel? RowPortOf(CanvasNodeViewModel source, CanvasNodeViewModel target, PortKind into, string? condition)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (into is not (PortKind.Read or PortKind.Logic) || source.IsDependency)
        {
            return null;
        }

        CanvasExpressionViewModel? line = target.Lines.FirstOrDefault(l => l.Key == (into == PortKind.Logic ? "when" : "where") && l.IsFlat);
        if (line is null)
        {
            return null;
        }

        string read = CanvasEditSemantics.ReadPathOf(source, target);
        if (into == PortKind.Logic)
        {
            ConditionRow? row = ChainWiring.AuthoredRow(line.Text, read, condition);
            int index = row is null ? -1 : line.Condition.Rows.ToList().IndexOf(row);
            return index >= 0 && index < line.Rows.Count ? line.Rows[index].Port : null;
        }

        return line.Rows.FirstOrDefault(r => r.Path == read)?.Port;
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
