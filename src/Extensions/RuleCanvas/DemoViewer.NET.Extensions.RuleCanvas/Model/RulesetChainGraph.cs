#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>What part of the engine a chain node is, which decides its face and whether an edit can reach it.</summary>
public enum ChainNodeKind
{
    /// <summary>A declared stat or highlight; keyed by its document entry.</summary>
    Declared,

    /// <summary>One wire event, split out of the engine's root so no single node fans out to everything.</summary>
    Event,

    /// <summary>A built-in event enrichment (<c>enrich.kill.was_enemy_kill</c>).</summary>
    Enrichment,

    /// <summary>A built-in context flag or value (<c>MatchLive</c>, <c>Alive</c>, <c>player_team</c>).</summary>
    Context,

    /// <summary>An entity value pull.</summary>
    Entity,

    /// <summary>External state the engine consults.</summary>
    External,

    /// <summary>Anything else the builder made.</summary>
    Other
}

/// <summary>One node of the lowered chain.</summary>
/// <param name="Key">Identity for layout and selection: the document key for a declared node, <c>~kind</c> plus the name otherwise.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Name">The engine's name, or the event name.</param>
/// <param name="IsPerPlayer">Whether it materializes once per player.</param>
public sealed record ChainNode(RulesetDocumentNodeKey Key, ChainNodeKind Kind, string Name, bool IsPerPlayer);

/// <summary>One wire of the lowered chain, from the node that fires or is read to the node written.</summary>
/// <param name="Source">The firing, gating or read node.</param>
/// <param name="Target">The node written.</param>
/// <param name="Label">The event on the wire, or <c>reads</c>.</param>
/// <param name="Condition">The lowered condition, or null.</param>
/// <param name="Effect">What the edge does to its target.</param>
/// <param name="IsRead">A value read rather than a write.</param>
public sealed record ChainEdge(RulesetDocumentNodeKey Source, RulesetDocumentNodeKey Target, string Label,
    string? Condition, EdgeEffect Effect, bool IsRead);

/// <summary>
///     The engine's lowered chain for a composition, reduced to what the open rulesets reach: every declared
///     stat and highlight, everything upstream of them (gates, enrichments, context values), the events that
///     fire them, and every wire between those. Companions the builder makes for itself (first-tick nodes,
///     a highlight's counter) are folded into the entry that owns them.
/// </summary>
public sealed record RulesetChainGraph(IReadOnlyList<ChainNode> Nodes, IReadOnlyList<ChainEdge> Edges)
{
    public static RulesetChainGraph Empty { get; } = new([], []);

    private const string TemplatePlayer = "each player";

    public static RulesetChainGraph Build(BuildResult build, IReadOnlyList<CheckedRuleset> rulesets)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(rulesets);
        RuleGraph graph = RuleGraph.FromBuild(build, true, TemplatePlayer);

        // Declared entries, by the owner spelling the graph uses; a companion shares its owner but is not
        // named by it, and is dropped.
        Dictionary<string, RulesetDocumentNodeKey> declared = new(StringComparer.Ordinal);
        foreach (CheckedRuleset ruleset in rulesets)
        {
            foreach (CheckedStat stat in ruleset.Stats)
            {
                declared[ruleset.Id.Id + "." + stat.StatId] = new RulesetDocumentNodeKey(ruleset.Id.Id, stat.StatId);
            }

            foreach (CheckedHighlight highlight in ruleset.Highlights)
            {
                declared[ruleset.Id.Id + "." + highlight.HighlightId] = new RulesetDocumentNodeKey(ruleset.Id.Id, highlight.HighlightId);
            }
        }

        Dictionary<RuleGraphNode, RulesetDocumentNodeKey> keys = new(ReferenceEqualityComparer.Instance);
        Dictionary<RuleGraphNode, ChainNodeKind> kinds = new(ReferenceEqualityComparer.Instance);
        HashSet<string> usedScaffoldKeys = new(StringComparer.Ordinal);
        RuleGraphNode? root = null;
        foreach (RuleGraphNode node in graph.Nodes)
        {
            if (node.Origin == RuleGraphNodeOrigin.Root)
            {
                root = node;
                continue;
            }

            if (node.Origin == RuleGraphNodeOrigin.Rule)
            {
                foreach (string owner in node.Owners)
                {
                    if (declared.TryGetValue(owner, out RulesetDocumentNodeKey key)
                        && string.Equals(node.Node.Name, key.Id, StringComparison.Ordinal))
                    {
                        keys[node] = key;
                        kinds[node] = ChainNodeKind.Declared;
                        break;
                    }
                }

                continue;
            }

            ChainNodeKind kind = node.Origin switch
            {
                RuleGraphNodeOrigin.Enrichment => ChainNodeKind.Enrichment,
                RuleGraphNodeOrigin.Context or RuleGraphNodeOrigin.RoundFact => ChainNodeKind.Context,
                RuleGraphNodeOrigin.EntityValue => ChainNodeKind.Entity,
                RuleGraphNodeOrigin.External => ChainNodeKind.External,
                _ => ChainNodeKind.Other
            };
            string prefix = "~" + kind.ToString().ToLowerInvariant();
            string id = node.Node.Name;
            for (int n = 2; !usedScaffoldKeys.Add(prefix + "." + id); n++)
            {
                id = node.Node.Name + "#" + n;
            }

            keys[node] = new RulesetDocumentNodeKey(prefix, id);
            kinds[node] = kind;
        }

        // Upstream closure from the declared nodes, through the edges and through what each edge reads.
        HashSet<RuleGraphNode> included = new(ReferenceEqualityComparer.Instance);
        Queue<RuleGraphNode> pending = new();
        foreach ((RuleGraphNode node, ChainNodeKind kind) in kinds)
        {
            if (kind == ChainNodeKind.Declared)
            {
                included.Add(node);
                pending.Enqueue(node);
            }
        }

        List<(RuleGraphEdge Edge, RuleGraphNode? ReadBy)> taken = [];
        ILookup<RuleGraphNode, RuleGraphEdge> incoming = graph.Edges.ToLookup(e => e.Destination);
        while (pending.Count > 0)
        {
            RuleGraphNode target = pending.Dequeue();
            foreach (RuleGraphEdge edge in incoming[target])
            {
                if (edge.Descriptor.Kind is GraphEdgeKind.FirstTick or GraphEdgeKind.RoundReset
                    || ReferenceEquals(edge.Source, edge.Destination))
                {
                    continue;
                }

                // External state is where the walk stops: everything writes it, and none of that is the
                // ruleset's chain.
                RuleGraphNode source = edge.Source;
                if (!ReferenceEquals(source, root) && (!keys.ContainsKey(source) || kinds[source] == ChainNodeKind.External))
                {
                    continue;
                }

                taken.Add((edge, null));
                if (!ReferenceEquals(source, root) && included.Add(source))
                {
                    pending.Enqueue(source);
                }

                foreach (StateNode read in edge.Descriptor.Reads ?? [])
                {
                    if (graph.TryGetNode(read, out RuleGraphNode? reader) && keys.ContainsKey(reader)
                        && kinds[reader] != ChainNodeKind.External
                        && !ReferenceEquals(reader, target) && !ReferenceEquals(reader, source))
                    {
                        taken.Add((edge, reader));
                        if (included.Add(reader))
                        {
                            pending.Enqueue(reader);
                        }
                    }
                }
            }
        }

        List<ChainNode> nodes = [];
        Dictionary<string, RulesetDocumentNodeKey> events = new(StringComparer.Ordinal);
        List<ChainEdge> edges = [];
        HashSet<(RulesetDocumentNodeKey, RulesetDocumentNodeKey, string, EdgeEffect, bool)> seen = [];
        foreach ((RuleGraphEdge edge, RuleGraphNode? readBy) in taken)
        {
            RulesetDocumentNodeKey target = keys[edge.Destination];
            string label = edge.Descriptor.Label ?? "";
            RulesetDocumentNodeKey source;
            bool isRead = readBy is not null;
            if (isRead)
            {
                source = keys[readBy!];
            }
            else if (ReferenceEquals(edge.Source, root))
            {
                if (!events.TryGetValue(label, out source))
                {
                    source = new RulesetDocumentNodeKey("~event", label);
                    events[label] = source;
                    nodes.Add(new ChainNode(source, ChainNodeKind.Event, label, false));
                }
            }
            else
            {
                source = keys[edge.Source];
            }

            string text = isRead ? "reads" : label;
            if (seen.Add((source, target, text, edge.Descriptor.Effect, isRead)))
            {
                edges.Add(new ChainEdge(source, target, text, edge.Descriptor.ConditionLabel, edge.Descriptor.Effect, isRead));
            }
        }

        foreach (RuleGraphNode node in graph.Nodes)
        {
            if (included.Contains(node))
            {
                nodes.Add(new ChainNode(keys[node], kinds[node], node.Node.Name, node.Scope == RuleGraphScope.Player));
            }
        }

        return new RulesetChainGraph(nodes, edges);
    }
}
