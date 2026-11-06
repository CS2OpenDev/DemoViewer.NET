#region

using CS2DemoKit.Analysis.Catalog;
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

    // The embedded catalog, read once: 18 views and their typed facets, which is what scopes a node's form.
    private static readonly Lazy<CatalogRoot> _catalog = new(CatalogResource.Load);

    /// <summary>The catalog's view names, in its order.</summary>
    public static IReadOnlyList<string> ViewNames => [.. _catalog.Value.Views.Select(v => v.Name)];

    public static (IReadOnlyList<CanvasNodeViewModel> Nodes, IReadOnlyList<CanvasConnectionViewModel> Connections) Build(
        RulesetCorpus.Composed composed, RulesetNodeLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(composed);
        layout ??= RulesetNodeLayout.Empty;
        ILookup<RulesetDocumentNodeKey, string> diagnostics = DiagnosticsByNode(composed);
        RulesetDocumentGraph graph = composed.Graph;

        Dictionary<RulesetDocumentNodeKey, CanvasNodeViewModel> byKey = new();
        List<CanvasNodeViewModel> nodes = new(graph.Nodes.Count);
        foreach (RulesetDocumentNode node in graph.Nodes)
        {
            CanvasNodeViewModel vm = Dress(node, composed.Documents, [.. diagnostics[node.Key]]);
            byKey[node.Key] = vm;
            nodes.Add(vm);
        }

        // Parallel reads between one pair (a stat and its <id>.count pseudo-member, say) are one wire. The
        // reader's input is labelled with the first reference written; the producer has one output.
        Dictionary<(RulesetDocumentNodeKey, RulesetDocumentNodeKey), CanvasConnectorViewModel> inputs = new();
        Dictionary<RulesetDocumentNodeKey, CanvasConnectorViewModel> outputs = new();
        List<CanvasConnectionViewModel> wires = [];
        foreach (RulesetDocumentEdge edge in graph.Edges)
        {
            if (!byKey.TryGetValue(edge.Source, out CanvasNodeViewModel? source)
                || !byKey.TryGetValue(edge.Target, out CanvasNodeViewModel? target)
                || inputs.ContainsKey((edge.Source, edge.Target)))
            {
                continue;
            }

            if (!outputs.TryGetValue(edge.Source, out CanvasConnectorViewModel? output))
            {
                output = new CanvasConnectorViewModel { Title = "value" };
                outputs[edge.Source] = output;
                source.Outputs.Add(output);
            }

            CanvasConnectorViewModel input = new() { Title = edge.Reference };
            inputs[(edge.Source, edge.Target)] = input;
            target.Inputs.Add(input);
            wires.Add(new CanvasConnectionViewModel(output, input));
        }

        List<CanvasPlacement.Item> items = new(graph.Nodes.Count);
        foreach (RulesetDocumentNode node in graph.Nodes)
        {
            CanvasNodeViewModel vm = byKey[node.Key];
            vm.EstimatedSize = new Avalonia.Size(WidthOf(vm), HeightOf(vm));
            items.Add(new CanvasPlacement.Item(node.Key, vm.EstimatedSize.Width, vm.EstimatedSize.Height));
        }

        IReadOnlyDictionary<RulesetDocumentNodeKey, Avalonia.Point> placed = CanvasPlacement.Place(items, graph.Edges);
        // A saved arrangement wins per node; a node the sidecar does not know keeps its computed place.
        foreach (RulesetDocumentNode node in graph.Nodes)
        {
            if (placed.TryGetValue(node.Key, out Avalonia.Point at))
            {
                byKey[node.Key].Location = layout.PositionOf(node.Key, at);
            }
        }

        return (nodes, wires);
    }

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
        string? label = node.Label;
        CanvasNodeViewModel? built = null;
        // Rows write through the node they belong to; the node is attached to the document by the tab.
        void Write(string facet, string? value) => built?.WriteFacet(facet, value);

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
                }

                AddScope(chips, stat.Per);
                // The typed form: every facet the view offers, the bound ones as rows, the rest offered by the
                // picker; a key the view does not know is kept as a row the resolver already flagged.
                MatchFacetForm form = MatchFacetForm.For(_catalog.Value, node.View, stat.Trigger?.Match);
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
                    facets.Add(new CanvasFacetViewModel(unknown.Key, RulesTypeKind.None, FacetText.Of(unknown.Test), Write));
                }

                string kind = stat.Kind.ToString()?.ToLowerInvariant() ?? "";
                if (kind is "compute" && stat.KindArg is { } formula)
                {
                    expression.Add("= " + formula);
                }
                else if (kind is "flag" && stat.KindArg is { } condition)
                {
                    expression.Add("when " + condition);
                }

                if (!string.IsNullOrWhiteSpace(stat.Trigger?.Where))
                {
                    expression.Add("where " + stat.Trigger.Where);
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
        longest = Math.Max(longest, node.Inputs.Max(i => (int?)i.Title.Length) ?? 0 + 14);
        return Math.Clamp(BaseWidth + Math.Max(0, longest - 30) * 6, BaseWidth, 520);
    }

    private static double HeightOf(CanvasNodeViewModel node)
    {
        double chips = node.Chips.Count == 0 ? 0 : ChipRowHeight * Math.Ceiling(node.Chips.Count / 3.0);
        double rows = Math.Max(node.Facets.Count * FacetRowHeight,
            Math.Max(node.Inputs.Count, node.Outputs.Count) * ConnectorHeight);
        double expression = node.Expression is null ? 0 : ExpressionHeight * node.Expression.Split('\n').Length;
        double diagnostics = node.Diagnostics.Count * DiagnosticHeight;
        return HeaderHeight + chips + rows + expression + diagnostics + 16;
    }
}
