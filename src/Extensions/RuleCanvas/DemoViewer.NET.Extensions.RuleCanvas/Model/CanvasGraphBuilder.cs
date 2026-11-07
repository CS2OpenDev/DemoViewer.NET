#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     Turns a composed ruleset into what the canvas binds to: a node view model per document node, dressed
///     with the chips, facets and expression text the checked model does not carry and the document does;
///     a connector per value read or produced; a wire per distinct reference between two nodes; and a
///     first placement for every node.
/// </summary>
public static class CanvasGraphBuilder
{
    private const double BaseWidth = 250;
    private const double HeaderHeight = 40;
    private const double ChipRowHeight = 24;
    private const double FacetRowHeight = 16;
    private const double ExpressionHeight = 20;
    private const double ConnectorHeight = 22;
    private const double DiagnosticHeight = 18;
    private const double PickerRowHeight = 30;
    private const double LineHeight = 26;
    private const int LineWrapColumns = 44;
    private const double EditableWidth = 360;

    /// <summary>The catalog's view names, in its order.</summary>
    public static IReadOnlyList<string> ViewNames => [.. EmbeddedCatalog.Root.Views.Select(v => v.Name)];

    public static (IReadOnlyList<CanvasNodeViewModel> Nodes, IReadOnlyList<CanvasConnectionViewModel> Connections) Build(
        RulesetCorpus.Composed composed, RulesetNodeLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(composed);
        layout ??= RulesetNodeLayout.Empty;
        ILookup<RulesetDocumentNodeKey, string> diagnostics = DiagnosticsByNode(composed);
        RulesetDocumentGraph graph = composed.Graph;

        Dictionary<RulesetDocumentNodeKey, CanvasNodeViewModel> byKey = new();
        List<CanvasNodeViewModel> nodes = new(graph.Nodes.Count + composed.Chain.Nodes.Count);
        foreach (RulesetDocumentNode node in graph.Nodes)
        {
            CanvasNodeViewModel vm = Dress(node, composed.Documents, [.. diagnostics[node.Key]]);
            byKey[node.Key] = vm;
            nodes.Add(vm);
        }

        // The engine's own nodes come after the declared ones: the events, the enrichments and the context
        // values the chain runs through. A declared node the chain also names is the same node.
        foreach (ChainNode node in composed.Chain.Nodes)
        {
            if (node.Kind != ChainNodeKind.Declared && !byKey.ContainsKey(node.Key))
            {
                CanvasNodeViewModel vm = Scaffold(node);
                byKey[node.Key] = vm;
                nodes.Add(vm);
            }
        }

        // Wires are the chain's when it built, else the declared reads. One input and one output per node;
        // what a wire means is written on the wire.
        List<RulesetDocumentEdge> placementEdges = [];
        List<CanvasConnectionViewModel> wires = [];
        Dictionary<RulesetDocumentNodeKey, CanvasConnectorViewModel> inputs = new();
        Dictionary<RulesetDocumentNodeKey, CanvasConnectorViewModel> outputs = new();
        CanvasConnectorViewModel Output(RulesetDocumentNodeKey key)
        {
            if (!outputs.TryGetValue(key, out CanvasConnectorViewModel? output))
            {
                output = new CanvasConnectorViewModel { Title = "", Owner = byKey[key] };
                outputs[key] = output;
                byKey[key].Outputs.Add(output);
            }

            return output;
        }

        CanvasConnectorViewModel Input(RulesetDocumentNodeKey key)
        {
            if (!inputs.TryGetValue(key, out CanvasConnectorViewModel? input))
            {
                input = new CanvasConnectorViewModel { Title = "", Owner = byKey[key] };
                inputs[key] = input;
                byKey[key].Inputs.Add(input);
            }

            return input;
        }

        if (composed.Chain.Edges.Count > 0)
        {
            foreach (ChainEdge edge in composed.Chain.Edges)
            {
                if (!byKey.ContainsKey(edge.Source) || !byKey.ContainsKey(edge.Target))
                {
                    continue;
                }

                wires.Add(new CanvasConnectionViewModel(Output(edge.Source), Input(edge.Target), WireText(edge),
                    string.IsNullOrWhiteSpace(edge.Condition) ? null : edge.Condition, edge.IsRead));
                placementEdges.Add(new RulesetDocumentEdge(edge.Source, edge.Target, edge.Label));
            }
        }
        else
        {
            HashSet<(RulesetDocumentNodeKey, RulesetDocumentNodeKey)> drawn = [];
            foreach (RulesetDocumentEdge edge in graph.Edges)
            {
                if (byKey.ContainsKey(edge.Source) && byKey.ContainsKey(edge.Target) && drawn.Add((edge.Source, edge.Target)))
                {
                    wires.Add(new CanvasConnectionViewModel(Output(edge.Source), Input(edge.Target), edge.Reference, null, true));
                    placementEdges.Add(edge);
                }
            }
        }

        List<CanvasPlacement.Item> items = new(nodes.Count);
        foreach (CanvasNodeViewModel vm in nodes)
        {
            vm.EstimatedSize = new Avalonia.Size(WidthOf(vm), HeightOf(vm));
            items.Add(new CanvasPlacement.Item(new RulesetDocumentNodeKey(vm.Ruleset, vm.Id), vm.EstimatedSize.Width,
                vm.EstimatedSize.Height));
        }

        IReadOnlyDictionary<RulesetDocumentNodeKey, Avalonia.Point> placed = CanvasPlacement.Place(items, placementEdges);
        // A saved arrangement wins per node; a node the sidecar does not know keeps its computed place.
        foreach (CanvasNodeViewModel vm in nodes)
        {
            RulesetDocumentNodeKey key = new(vm.Ruleset, vm.Id);
            if (placed.TryGetValue(key, out Avalonia.Point at))
            {
                vm.Location = layout.PositionOf(key, at);
            }
        }

        return (nodes, wires);
    }

    // What the wire says: the event for a write, with what it does when that is not a value; the test for a
    // logic input; reads for a read. The lowered condition is the hover text.
    private static string WireText(ChainEdge edge)
    {
        if (edge.IsRead)
        {
            return "reads";
        }

        return edge.Effect switch
        {
            EdgeEffect.Activate => edge.Label + " on",
            EdgeEffect.Deactivate => edge.Label + " off",
            EdgeEffect.Conjunction => "and " + (edge.Condition ?? ""),
            EdgeEffect.Disjunction => "or " + (edge.Condition ?? ""),
            _ => edge.Label
        };
    }

    // An engine node's face: the kind, the name, and whether it is per player. Nothing on it is editable.
    private static CanvasNodeViewModel Scaffold(ChainNode node) => new()
    {
        Id = node.Key.Id,
        Ruleset = node.Key.Ruleset,
        Kind = node.Kind switch
        {
            ChainNodeKind.Event => "event",
            ChainNodeKind.Enrichment => "enrich",
            ChainNodeKind.Context => "context",
            ChainNodeKind.Entity => "entity",
            ChainNodeKind.External => "external",
            _ => "engine"
        },
        Chips = node.IsPerPlayer ? ["each player"] : [],
        IsAuthorable = false
    };

    /// <summary>
    ///     The node face from the document: the trigger, gate and scope as chips; the <c>match:</c> block as
    ///     facet rows written the way the author wrote them; the <c>compute:</c>, flag, <c>where:</c> or
    ///     <c>when:</c> text as the expression line.
    /// </summary>
    private static CanvasNodeViewModel Dress(RulesetDocumentNode node, IReadOnlyDictionary<string, RulesetDoc> documents,
        IReadOnlyList<string> nodeDiagnostics)
    {
        List<string> chips = [];
        List<CanvasFacetViewModel> facets = [];
        List<string> available = [];
        Dictionary<string, RulesTypeKind> availableKinds = new(StringComparer.Ordinal);
        List<string> expression = [];
        List<CanvasExpressionViewModel> lines = [];
        string per = "";
        string gate = "";
        string? label = node.Label;
        CanvasNodeViewModel? built = null;
        // Rows write through the node they belong to; the node is attached to the document by the tab.
        void Write(string facet, string? value) => built?.WriteFacet(facet, value);
        void WriteField(string key, string? value) => built?.WriteField(key, value);

        documents.TryGetValue(node.Key.Ruleset, out RulesetDoc? doc);
        if (node.IsHighlight)
        {
            HighlightDef? highlight = doc?.Highlights.FirstOrDefault(h => h.Id == node.Id);
            if (highlight is not null)
            {
                AddScope(chips, highlight.Per);
                expression.Add("when " + highlight.When);
            }
        }
        else
        {
            StatDef? stat = doc?.Stats.FirstOrDefault(s => s.Id == node.Id);
            // The view as the author named it; only a trigger the resolver gave no view shows its wire events.
            if (!string.IsNullOrEmpty(node.View))
            {
                chips.Add("on " + node.View);
            }
            else
            {
                foreach (string eventName in node.Events)
                {
                    chips.Add("on " + eventName);
                }
            }

            if (stat is not null)
            {

                if (!string.IsNullOrWhiteSpace(stat.Trigger?.While))
                {
                    chips.Add("while " + stat.Trigger.While);
                    gate = stat.Trigger.While;
                }

                AddScope(chips, stat.Per);
                per = stat.Per == PerScope.None ? "" : stat.Per.ToString().ToLowerInvariant();
                // The typed form: every facet the view offers, the bound ones as rows, the rest offered by the
                // picker; a key the view does not know is kept as a row the resolver already flagged.
                MatchFacetForm form = MatchFacetForm.For(EmbeddedCatalog.Root, node.View, stat.Trigger?.Match);
                foreach (MatchFacetRow row in form.Rows)
                {
                    if (row.IsSet)
                    {
                        facets.Add(new CanvasFacetViewModel(row.Name, row.Type.Kind, row.Value!.ToMatchValue(), Write));
                    }
                    else
                    {
                        available.Add(row.Name);
                        availableKinds[row.Name] = row.Type.Kind;
                    }
                }

                foreach (MatchBinding unknown in form.Unknown)
                {
                    facets.Add(new CanvasFacetViewModel(unknown.Key, RulesTypeKind.None,
                        MatchFacetValue.From(unknown.Test)?.ToMatchValue() ?? "", Write));
                }

                string kind = stat.Kind.ToString()?.ToLowerInvariant() ?? "";
                if (kind is "compute")
                {
                    if (stat.KindArg is { } formula)
                    {
                        expression.Add("= " + formula);
                    }

                    lines.Add(new CanvasExpressionViewModel("compute", "=", stat.KindArg ?? "", WriteField));
                }
                else if (kind is "flag")
                {
                    if (stat.KindArg is { } condition)
                    {
                        expression.Add("when " + condition);
                    }

                    lines.Add(new CanvasExpressionViewModel("when", "when", stat.KindArg ?? "", WriteField));
                }

                if (!string.IsNullOrWhiteSpace(stat.Trigger?.Where))
                {
                    expression.Add("where " + stat.Trigger.Where);
                }

                if (kind is not "compute")
                {
                    lines.Add(new CanvasExpressionViewModel("where", "where", stat.Trigger?.Where ?? "", WriteField));
                }
            }
        }

        foreach (string read in node.CrossRulesetReads)
        {
            chips.Add("reads " + read);
        }

        if (!node.IsAuthorable)
        {
            chips.Add("from " + node.Key.Ruleset);
        }

        built = new CanvasNodeViewModel
        {
            Id = node.Id,
            Ruleset = node.Key.Ruleset,
            Kind = node.Kind.ToString().ToLowerInvariant(),
            Label = string.Equals(label, node.Id, StringComparison.Ordinal) ? null : label,
            View = node.View,
            Chips = chips,
            Facets = facets,
            AvailableFacets = available,
            AvailableFacetKind = name => availableKinds.GetValueOrDefault(name, RulesTypeKind.None),
            Expression = expression.Count == 0 ? null : string.Join('\n', expression),
            Lines = lines,
            Per = per,
            Gate = gate,
            SourceLine = node.Position.Line,
            IsAuthorable = node.IsAuthorable,
            Diagnostics = nodeDiagnostics
        };
        return built;
    }

    /// <summary>
    ///     Files each composition diagnostic under the node whose entry contains its line: entries are
    ///     contiguous in the file, so the node with the greatest start line at or before the diagnostic's
    ///     line is the one it is about. A diagnostic with no position, or before the first entry, stays
    ///     at the document level.
    /// </summary>
    private static ILookup<RulesetDocumentNodeKey, string> DiagnosticsByNode(RulesetCorpus.Composed composed)
    {
        List<(RulesetDocumentNodeKey Key, string Message)> filed = [];
        foreach ((string rulesetId, int line, string message) in composed.PositionedDiagnostics)
        {
            RulesetDocumentNode? owner = composed.Graph.Nodes
                .Where(n => n.Key.Ruleset == rulesetId && n.Position.Line > 0 && n.Position.Line <= line)
                .OrderByDescending(n => n.Position.Line)
                .FirstOrDefault();
            if (owner is not null)
            {
                filed.Add((owner.Key, message));
            }
        }

        return filed.ToLookup(f => f.Key, f => f.Message);
    }

    private static void AddScope(List<string> chips, PerScope per)
    {
        if (per != PerScope.None)
        {
            chips.Add("per " + per.ToString().ToLowerInvariant());
        }
    }

    private static double WidthOf(CanvasNodeViewModel node)
    {
        int longest = Math.Max(node.Id.Length + (node.Label?.Length ?? 0) + 8,
            node.Expression?.Split('\n').Max(l => l.Length) ?? 0);
        longest = Math.Max(longest, Math.Min(LineWrapColumns, node.Lines.Max(l => (int?)l.Text.Length) ?? 0) + 8);
        longest = Math.Max(longest, (node.Inputs.Max(i => (int?)i.Title.Length) ?? 0) + 14);
        double width = Math.Clamp(BaseWidth + Math.Max(0, longest - 30) * 6, BaseWidth, 520);
        // The picker row and an expression editor are wider than any chip row.
        return node.IsAuthorable && !node.IsHighlight && !node.IsScaffold ? Math.Max(width, EditableWidth) : width;
    }

    private static double HeightOf(CanvasNodeViewModel node)
    {
        double chips = node.Chips.Count == 0 ? 0 : ChipRowHeight * Math.Ceiling(node.Chips.Count / 3.0);
        double rows = Math.Max(node.Facets.Count * FacetRowHeight,
            Math.Max(node.Inputs.Count, node.Outputs.Count) * ConnectorHeight);
        double expression = node.Expression is null ? 0 : ExpressionHeight * node.Expression.Split('\n').Length;
        double diagnostics = node.Diagnostics.Count * DiagnosticHeight;
        // The editors an authorable stat can show: the picker row, the add-facet picker, and one line per
        // expression, each wrapping at the editor's width. Estimated whether or not the node is editable yet.
        double editors = 0;
        if (node.IsAuthorable && !node.IsHighlight && !node.IsScaffold)
        {
            editors = PickerRowHeight + ConnectorHeight
                      + node.Lines.Sum(l => LineHeight + Math.Max(0, (l.Text.Length - 1) / LineWrapColumns) * 16);
        }

        return HeaderHeight + chips + rows + expression + diagnostics + editors + 16;
    }
}
