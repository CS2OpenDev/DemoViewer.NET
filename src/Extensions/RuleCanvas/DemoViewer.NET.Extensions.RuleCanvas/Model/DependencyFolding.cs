#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     Folds each <c>use:</c> dependency of a composition into one node: the dependency's entries and the
///     engine nodes only they reach go, and every wire a dependency entry fed into the open document's nodes
///     runs from the dependency node instead, out of an output named for the entry. A dependency the user
///     expanded is left as it was drawn. Pure: a composition in, a composition out.
/// </summary>
public static class DependencyFolding
{
    /// <summary>The composition with every dependency not in <paramref name="expanded" /> folded to one node.</summary>
    public static RulesetCorpus.Composed Fold(RulesetCorpus.Composed composed, IReadOnlySet<string> expanded)
    {
        ArgumentNullException.ThrowIfNull(composed);
        ArgumentNullException.ThrowIfNull(expanded);
        if (composed.OpenId is not { } openId)
        {
            return composed;
        }

        List<string> folding = [.. composed.Documents.Keys.Where(id => id != openId && !expanded.Contains(id)).Order(StringComparer.Ordinal)];
        if (folding.Count == 0)
        {
            return composed;
        }

        HashSet<string> folded = new(folding, StringComparer.Ordinal);
        RulesetDocumentGraph graph = new(
            [.. composed.Graph.Nodes.Where(n => !folded.Contains(n.Key.Ruleset))],
            [.. composed.Graph.Edges.Where(e => !folded.Contains(e.Source.Ruleset) && !folded.Contains(e.Target.Ruleset))]);
        return composed with { Graph = graph, Chain = FoldChain(composed, folded, folding) };
    }

    // The kept part of the chain is everything the open document's nodes reach upstream without passing
    // through a folded entry; a scaffold only the dependency reaches goes with it.
    private static RulesetChainGraph FoldChain(RulesetCorpus.Composed composed, HashSet<string> folded, List<string> folding)
    {
        RulesetChainGraph chain = composed.Chain;
        string openId = composed.OpenId!;
        Dictionary<RulesetDocumentNodeKey, ChainNode> byKey = chain.Nodes.ToDictionary(n => n.Key);
        bool IsFoldedEntry(RulesetDocumentNodeKey key) => folded.Contains(key.Ruleset);

        HashSet<RulesetDocumentNodeKey> kept = [.. chain.Nodes.Where(n => n.Key.Ruleset == openId).Select(n => n.Key)];
        Queue<RulesetDocumentNodeKey> pending = new(kept);
        ILookup<RulesetDocumentNodeKey, ChainEdge> incoming = chain.Edges.ToLookup(e => e.Target);
        while (pending.Count > 0)
        {
            RulesetDocumentNodeKey at = pending.Dequeue();
            foreach (ChainEdge edge in incoming[at])
            {
                if (!IsFoldedEntry(edge.Source) && byKey.ContainsKey(edge.Source) && kept.Add(edge.Source))
                {
                    pending.Enqueue(edge.Source);
                }
            }
        }

        // Each dependency's node, with the entries the open document reads as its outputs in first-use order.
        Dictionary<string, List<string>> outputs = folding.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        List<ChainEdge> edges = [];
        HashSet<ChainEdge> seen = [];
        Dictionary<ChainEdge, IReadOnlyList<RuleGraphEdge>> origins = new();
        foreach (ChainEdge edge in chain.Edges)
        {
            if (!kept.Contains(edge.Target))
            {
                continue;
            }

            ChainEdge next = edge;
            if (IsFoldedEntry(edge.Source))
            {
                string entry = edge.Source.Id;
                List<string> list = outputs[edge.Source.Ruleset];
                if (!list.Contains(entry, StringComparer.Ordinal))
                {
                    list.Add(entry);
                }

                next = edge with
                {
                    Source = RulesetDocumentNodeKey.Dependency(edge.Source.Ruleset),
                    Label = OutputLabel(entry, edge.Label),
                    Output = entry
                };
            }
            else if (!kept.Contains(edge.Source))
            {
                continue;
            }

            if (seen.Add(next))
            {
                edges.Add(next);
                if (chain.EngineEdges.TryGetValue(edge, out IReadOnlyList<RuleGraphEdge>? engine))
                {
                    origins[next] = engine;
                }
            }
        }

        // With nothing lowered the chain has no read wires, so the document's own cross-ruleset reads stand in.
        if (!chain.IsLowered)
        {
            foreach (RulesetDocumentNode node in composed.Graph.Nodes.Where(n => n.Key.Ruleset == openId))
            {
                foreach (string read in node.CrossRulesetReads)
                {
                    int dot = read.IndexOf('.', StringComparison.Ordinal);
                    if (dot <= 0 || !folded.Contains(read[..dot]))
                    {
                        continue;
                    }

                    string ruleset = read[..dot];
                    string entry = read[(dot + 1)..];
                    List<string> list = outputs[ruleset];
                    if (!list.Contains(entry, StringComparer.Ordinal))
                    {
                        list.Add(entry);
                    }

                    ChainEdge next = new(RulesetDocumentNodeKey.Dependency(ruleset), node.Key, entry, null, EdgeEffect.SetValue, true) { Output = entry };
                    if (seen.Add(next))
                    {
                        edges.Add(next);
                    }
                }
            }
        }

        List<ChainNode> nodes = [.. chain.Nodes.Where(n => kept.Contains(n.Key))];
        int insertAt = Math.Max(0, nodes.FindIndex(n => n.Kind is ChainNodeKind.Declared or ChainNodeKind.Show or ChainNodeKind.Params));
        foreach (string id in folding)
        {
            RulesetDoc doc = composed.Documents[id];
            nodes.Insert(insertAt++, new ChainNode(RulesetDocumentNodeKey.Dependency(id), ChainNodeKind.Dependency, id, false)
            {
                Outputs = outputs[id],
                EntryCount = doc.Stats.Count + doc.Highlights.Count
            });
        }

        return new RulesetChainGraph(nodes, edges) { Diagnostics = chain.Diagnostics, EngineEdges = origins };
    }

    // The wire names the entry it leaves from; a companion read keeps its suffix ("kast.count"), a live read its mark.
    private static string OutputLabel(string entry, string label)
    {
        if (label.StartsWith("reads ", StringComparison.Ordinal))
        {
            string rest = label["reads ".Length..];
            return rest.StartsWith('.') ? entry + rest : entry + " " + rest;
        }

        return label.Length == 0 || label == "reads" ? entry : label;
    }
}
