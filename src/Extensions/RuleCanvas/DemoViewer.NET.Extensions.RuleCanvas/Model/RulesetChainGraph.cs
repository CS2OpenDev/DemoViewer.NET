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
public sealed record ChainNode(RulesetDocumentNodeKey Key, ChainNodeKind Kind, string Name, bool IsPerPlayer)
{
    /// <summary>The wire events an event node stands for: one, or the twins merged into it. Empty for every other kind.</summary>
    public IReadOnlyList<string> Events { get; init; } = [];
}

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

    /// <summary>
    ///     The slice around <paramref name="key" />: the node, everything upstream of it through every wire,
    ///     and everything downstream, with the wires among them. A node reached only sideways, an ancestor's
    ///     other reader, is not in it. Empty when the key is not a node of this chain.
    /// </summary>
    public RulesetChainGraph Slice(RulesetDocumentNodeKey key)
    {
        if (Nodes.All(n => n.Key != key))
        {
            return Empty;
        }

        HashSet<RulesetDocumentNodeKey> inSlice = [key];
        Queue<RulesetDocumentNodeKey> pending = new([key]);
        while (pending.Count > 0)
        {
            RulesetDocumentNodeKey at = pending.Dequeue();
            foreach (ChainEdge edge in Edges)
            {
                if (edge.Target == at && inSlice.Add(edge.Source))
                {
                    pending.Enqueue(edge.Source);
                }
            }
        }

        pending.Enqueue(key);
        while (pending.Count > 0)
        {
            RulesetDocumentNodeKey at = pending.Dequeue();
            foreach (ChainEdge edge in Edges)
            {
                if (edge.Source == at && inSlice.Add(edge.Target))
                {
                    pending.Enqueue(edge.Target);
                }
            }
        }

        return new RulesetChainGraph(
            [.. Nodes.Where(n => inSlice.Contains(n.Key))],
            [.. Edges.Where(e => inSlice.Contains(e.Source) && inSlice.Contains(e.Target))]);
    }

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

        // The engine shares one node between entries with the same definition, so a rule node can stand
        // for several declared entries; the canvas draws each entry as its own node with the same wires.
        Dictionary<RuleGraphNode, List<RulesetDocumentNodeKey>> keys = new(ReferenceEqualityComparer.Instance);
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
                // A companion (first-tick, a highlight's counter) shares its owner's name but is not a rule id.
                List<RulesetDocumentNodeKey> owned = [];
                foreach (string owner in node.Owners)
                {
                    if (declared.TryGetValue(owner, out RulesetDocumentNodeKey key) && node.RuleIds.Contains(owner))
                    {
                        owned.Add(key);
                    }
                }

                if (owned.Count > 0)
                {
                    keys[node] = owned;
                    kinds[node] = ChainNodeKind.Declared;
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

            keys[node] = [new RulesetDocumentNodeKey(prefix, id)];
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

        // Event nodes by wire event name, in first-use order; the twins are merged below.
        Dictionary<string, RulesetDocumentNodeKey> events = new(StringComparer.Ordinal);
        List<ChainEdge> edges = [];
        HashSet<ChainEdge> seen = [];
        foreach ((RuleGraphEdge edge, RuleGraphNode? readBy) in taken)
        {
            string label = edge.Descriptor.Label ?? "";
            bool isRead = readBy is not null;
            IReadOnlyList<RulesetDocumentNodeKey> sources;
            if (isRead)
            {
                sources = keys[readBy!];
            }
            else if (ReferenceEquals(edge.Source, root))
            {
                sources = [EventKey(label)];
            }
            else
            {
                sources = keys[edge.Source];
            }

            string text = isRead ? "reads" : label;
            foreach (RulesetDocumentNodeKey source in sources)
            {
                foreach (RulesetDocumentNodeKey target in keys[edge.Destination])
                {
                    if (source != target)
                    {
                        Add(new ChainEdge(source, target, text, edge.Descriptor.ConditionLabel, edge.Descriptor.Effect, isRead));
                    }
                }
            }
        }

        RulesetDocumentNodeKey EventKey(string name)
        {
            if (!events.TryGetValue(name, out RulesetDocumentNodeKey key))
            {
                key = new RulesetDocumentNodeKey("~event", name);
                events[name] = key;
            }

            return key;
        }

        void Add(ChainEdge edge)
        {
            if (seen.Add(edge))
            {
                edges.Add(edge);
            }
        }

        Dictionary<RulesetDocumentNodeKey, ChainNodeKind> kindByKey = new();
        foreach ((RuleGraphNode node, ChainNodeKind kind) in kinds)
        {
            foreach (RulesetDocumentNodeKey key in keys[node])
            {
                kindByKey[key] = kind;
            }
        }

        List<ChainNode> nodes = MergeTwinEvents(events, edges, kindByKey, ViewsByEvents(rulesets));
        HashSet<RulesetDocumentNodeKey> emitted = [];
        foreach (RuleGraphNode node in graph.Nodes)
        {
            if (included.Contains(node))
            {
                foreach (RulesetDocumentNodeKey key in keys[node])
                {
                    if (emitted.Add(key))
                    {
                        string name = kinds[node] == ChainNodeKind.Declared ? key.Id : node.Node.Name;
                        nodes.Add(new ChainNode(key, kinds[node], name, node.Scope == RuleGraphScope.Player));
                    }
                }
            }
        }

        return new RulesetChainGraph(nodes, edges);
    }

    /// <summary>The catalog view the resolver gave each set of concrete events, keyed by the sorted events.</summary>
    private static Dictionary<string, string> ViewsByEvents(IReadOnlyList<CheckedRuleset> rulesets)
    {
        Dictionary<string, string> views = new(StringComparer.Ordinal);
        foreach (CheckedStat stat in rulesets.SelectMany(r => r.Stats))
        {
            if (stat.ResolvedView is { } view && stat.ConcreteEvents.Count > 1)
            {
                views.TryAdd(Signature(stat.ConcreteEvents), view);
            }
        }

        return views;
    }

    private static string Signature(IEnumerable<string> parts) => string.Join("\n", parts.Order(StringComparer.Ordinal));

    /// <summary>
    ///     Folds root-sourced events that write exactly the same declared and enrichment nodes, the same way,
    ///     into one event node: the round-end trio is one <c>round_ended</c>, named by the view the resolver
    ///     gave a stat on those events, else by the events joined. A wire every twin carried becomes one
    ///     wire labelled with the merged name; a wire only some carried keeps its own event's label.
    ///     Rewrites <paramref name="edges" /> in place and returns the event nodes in first-use order.
    /// </summary>
    private static List<ChainNode> MergeTwinEvents(Dictionary<string, RulesetDocumentNodeKey> events, List<ChainEdge> edges,
        Dictionary<RulesetDocumentNodeKey, ChainNodeKind> kindByKey, Dictionary<string, string> views)
    {
        Dictionary<string, List<string>> bySignature = new(StringComparer.Ordinal);
        foreach ((string name, RulesetDocumentNodeKey key) in events)
        {
            // Context targets are left out of the signature on purpose: cs_win_panel_match also ends
            // MatchLive, and that must not keep it apart from the other two round ends.
            string signature = Signature(edges
                .Where(e => e.Source == key && !e.IsRead
                            && kindByKey.GetValueOrDefault(e.Target) is ChainNodeKind.Declared or ChainNodeKind.Enrichment)
                .Select(e => $"{e.Target}|{e.Effect}|{e.Condition}")
                .Distinct(StringComparer.Ordinal));
            if (signature.Length > 0)
            {
                (bySignature.TryGetValue(signature, out List<string>? group) ? group : bySignature[signature] = []).Add(name);
            }
        }

        Dictionary<RulesetDocumentNodeKey, (RulesetDocumentNodeKey Key, string Name, List<string> Events)> merged = new();
        foreach (List<string> group in bySignature.Values.Where(g => g.Count > 1))
        {
            string name = views.GetValueOrDefault(Signature(group)) ?? string.Join(" / ", group);
            RulesetDocumentNodeKey key = new("~event", name);
            for (int n = 2; events.ContainsKey(key.Id); n++)
            {
                key = new RulesetDocumentNodeKey("~event", name + "#" + n);
            }

            foreach (string member in group)
            {
                merged[events[member]] = (key, name, group);
            }
        }

        List<ChainNode> nodes = [];
        HashSet<RulesetDocumentNodeKey> added = [];
        foreach ((string name, RulesetDocumentNodeKey key) in events)
        {
            if (merged.TryGetValue(key, out (RulesetDocumentNodeKey Key, string Name, List<string> Events) twin))
            {
                if (added.Add(twin.Key))
                {
                    nodes.Add(new ChainNode(twin.Key, ChainNodeKind.Event, twin.Name, false) { Events = twin.Events });
                }
            }
            else
            {
                nodes.Add(new ChainNode(key, ChainNodeKind.Event, name, false) { Events = [name] });
            }
        }

        if (merged.Count == 0)
        {
            return nodes;
        }

        // Which twins carry each wire; a wire all of them carry is drawn once under the merged name.
        Dictionary<(RulesetDocumentNodeKey, RulesetDocumentNodeKey, EdgeEffect, string?), HashSet<string>> carriers = new();
        foreach (ChainEdge edge in edges)
        {
            if (merged.TryGetValue(edge.Source, out (RulesetDocumentNodeKey Key, string Name, List<string> Events) twin) && !edge.IsRead)
            {
                (RulesetDocumentNodeKey, RulesetDocumentNodeKey, EdgeEffect, string?) wire = (twin.Key, edge.Target, edge.Effect, edge.Condition);
                (carriers.TryGetValue(wire, out HashSet<string>? set) ? set : carriers[wire] = []).Add(edge.Label);
            }
        }

        List<ChainEdge> rewritten = [];
        HashSet<ChainEdge> seen = [];
        foreach (ChainEdge edge in edges)
        {
            ChainEdge next = edge;
            if (merged.TryGetValue(edge.Source, out (RulesetDocumentNodeKey Key, string Name, List<string> Events) twin))
            {
                bool shared = !edge.IsRead && carriers[(twin.Key, edge.Target, edge.Effect, edge.Condition)].SetEquals(twin.Events);
                next = edge with { Source = twin.Key, Label = shared ? twin.Name : edge.Label };
            }

            if (seen.Add(next))
            {
                rewritten.Add(next);
            }
        }

        edges.Clear();
        edges.AddRange(rewritten);
        return nodes;
    }
}
