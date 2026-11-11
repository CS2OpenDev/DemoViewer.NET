#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
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
    Other,

    /// <summary>A <c>show:</c> table or the scoreboard of the open document, read from the entries it surfaces.</summary>
    Show,

    /// <summary>The open document's <c>params:</c> block, read by every entry whose text names a param.</summary>
    Params,

    /// <summary>A <c>use:</c> dependency folded to one node, with an output per entry the open document reads.</summary>
    Dependency
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

    /// <summary>Whether it materializes once per side, for a <c>for: each_team</c> ruleset's nodes and the team aggregates.</summary>
    public bool IsPerTeam { get; init; }

    /// <summary>A plant-site round fact (<c>round.bomb.site</c>), read once at the plant rather than driven by a trigger.</summary>
    public bool IsRoundFact { get; init; }

    /// <summary>
    ///     The companions a declared node owns that a sibling can read, as the author spells them: a tally's
    ///     target counters in threshold order, a highlight's <c>&lt;id&gt;.count</c>. Empty otherwise.
    /// </summary>
    public IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>For a dependency node, how many entries the folded ruleset declares; zero elsewhere.</summary>
    public int EntryCount { get; init; }
}

/// <summary>One wire of the lowered chain, from the node that fires or is read to the node written.</summary>
/// <param name="Source">The firing, gating or read node.</param>
/// <param name="Target">The node written.</param>
/// <param name="Label">The event on the wire, or <c>reads</c>.</param>
/// <param name="Condition">The lowered condition, or null.</param>
/// <param name="Effect">What the edge does to its target.</param>
/// <param name="IsRead">A value read rather than a write.</param>
public sealed record ChainEdge(RulesetDocumentNodeKey Source, RulesetDocumentNodeKey Target, string Label,
    string? Condition, EdgeEffect Effect, bool IsRead)
{
    /// <summary>The context guard a <c>while:</c> puts in front of this write, or null. The gate itself is a separate wire.</summary>
    public RulesetDocumentNodeKey? Guard { get; init; }

    /// <summary>A guard holding a write open rather than a write: drawn from the guard to the gated node, dashed.</summary>
    public bool IsGate { get; init; }

    /// <summary>For a read into a show node, the index of the column the wire stands for; null elsewhere.</summary>
    public int? Column { get; init; }

    /// <summary>
    ///     For a logic input, the operand row as the target's <c>when:</c> spells it (<c>round_kills &gt; 2</c>);
    ///     null when the row was not found or the target is not in the open document. The label reads this
    ///     over the engine's lowering (<c>value &gt; 2</c>), which <see cref="Condition" /> keeps for the hover.
    /// </summary>
    public string? Authored { get; init; }

    /// <summary>For a wire out of a dependency node, the entry of the folded ruleset it leaves from; null elsewhere.</summary>
    public string? Output { get; init; }
}

/// <summary>
///     The engine's lowered chain for a composition, reduced to what the open rulesets reach: every declared
///     stat and highlight, everything upstream of them (gates, enrichments, context values), the events that
///     fire them, and every wire between those. Companions the builder makes for itself (first-tick nodes,
///     a highlight's counter) are folded into the entry that owns them.
/// </summary>
public sealed record RulesetChainGraph(IReadOnlyList<ChainNode> Nodes, IReadOnlyList<ChainEdge> Edges)
{
    public static RulesetChainGraph Empty { get; } = new([], []);

    /// <summary>What the engine's graph view could not draw as the builder describes it; empty when it drew everything.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>
    ///     The engine edges each wire stands for, for a graph built from a run: a live run reads their fire
    ///     counts and applied messages. A wire the splitting made (a guard's gate, a define's two halves)
    ///     names the edge it was cut from. Empty for a slice and for <see cref="Empty" />.
    /// </summary>
    public IReadOnlyDictionary<ChainEdge, IReadOnlyList<RuleGraphEdge>> EngineEdges { get; init; } =
        new Dictionary<ChainEdge, IReadOnlyList<RuleGraphEdge>>();

    /// <summary>
    ///     Whether the engine lowered anything here. A chain of show and params nodes alone is drawn from the
    ///     document after a failed build, and nothing in it can run.
    /// </summary>
    public bool IsLowered => Nodes.Any(n => n.Kind is not (ChainNodeKind.Show or ChainNodeKind.Params or ChainNodeKind.Dependency));

    private const string TemplatePlayer = "each player";

    /// <summary>The engine rule id of each context value by the v2 path a <c>while:</c> writes (<c>round.number</c> to <c>round_number</c>).</summary>
    private static Dictionary<string, string> ContextRuleIds { get; } = BuildContextRuleIds();

    private static Dictionary<string, string> BuildContextRuleIds()
    {
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        foreach (CatalogContextRule context in EmbeddedCatalog.Root.Contexts)
        {
            if (context.V2Name is { } path)
            {
                ids.TryAdd(path, context.RuleId);
            }
        }

        return ids;
    }

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

    /// <param name="build">The lowered chain.</param>
    /// <param name="rulesets">The checked rulesets it was built from.</param>
    /// <param name="open">
    ///     The open document, whose trigger-bodied defines are drawn as nodes between the event and the stats
    ///     that fire on them; null folds every define into its stats, as the engine does.
    /// </param>
    public static RulesetChainGraph Build(BuildResult build, IReadOnlyList<CheckedRuleset> rulesets, RulesetDoc? open = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        return Build(RuleGraph.FromBuild(build, true, TemplatePlayer), rulesets, open);
    }

    /// <param name="graph">
    ///     The engine's view of the chain: the build's preview, or a run's graph with its players collapsed,
    ///     whose edges then carry the fire counts behind <see cref="EngineEdges" />.
    /// </param>
    /// <param name="rulesets">The checked rulesets it was built from.</param>
    /// <param name="open">The open document, as for <see cref="Build(BuildResult, IReadOnlyList{CheckedRuleset}, RulesetDoc?)" />.</param>
    public static RulesetChainGraph Build(RuleGraph graph, IReadOnlyList<CheckedRuleset> rulesets, RulesetDoc? open = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(rulesets);
        EdgeOrigins origins = new();

        // Declared entries, by the owner spelling the graph uses, and what each one's companions are called.
        Dictionary<string, RulesetDocumentNodeKey> declared = new(StringComparer.Ordinal);
        Dictionary<RulesetDocumentNodeKey, IReadOnlyList<string>> outputs = new();
        Dictionary<RulesetDocumentNodeKey, CheckedStat> stats = new();
        // What each entry's own text names, as the resolver spelled it: its reads and a rate's buckets.
        Dictionary<RulesetDocumentNodeKey, IReadOnlyList<string>> references = new();
        foreach (CheckedRuleset ruleset in rulesets)
        {
            foreach (CheckedStat stat in ruleset.Stats)
            {
                RulesetDocumentNodeKey key = new(ruleset.Id.Id, stat.StatId);
                declared[ruleset.Id.Id + "." + stat.StatId] = key;
                stats[key] = stat;
                references[key] = [.. stat.DeclaredReads, .. new[] { stat.RateOf, stat.RatePer }.OfType<string>()];
                if (stat.TallyThresholds is { Count: > 0 } thresholds)
                {
                    outputs[key] = [.. thresholds.Select(t => t.Target)];
                }
            }

            foreach (CheckedHighlight highlight in ruleset.Highlights)
            {
                RulesetDocumentNodeKey key = new(ruleset.Id.Id, highlight.HighlightId);
                declared[ruleset.Id.Id + "." + highlight.HighlightId] = key;
                references[key] = highlight.DeclaredReads;
                outputs[key] = [highlight.CountNodeId];
            }
        }

        // The engine shares one node between entries with the same definition, so a rule node can stand
        // for several declared entries; the canvas draws each entry as its own node with the same wires.
        Dictionary<RuleGraphNode, List<RulesetDocumentNodeKey>> keys = new(ReferenceEqualityComparer.Instance);
        Dictionary<RuleGraphNode, ChainNodeKind> kinds = new(ReferenceEqualityComparer.Instance);
        // A companion folded into its owner, by the name a sibling reads it under (.count, rounds_5k).
        Dictionary<RuleGraphNode, string> companions = new(ReferenceEqualityComparer.Instance);
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
                // A companion (first-tick, a tally's counters, a highlight's .count) shares its owner's name
                // but is not a rule id of its own; it is drawn as the owner.
                List<RulesetDocumentNodeKey> owned = [];
                List<RulesetDocumentNodeKey> owners = [];
                foreach (string owner in node.Owners)
                {
                    if (declared.TryGetValue(owner, out RulesetDocumentNodeKey key))
                    {
                        (node.RuleIds.Contains(owner) ? owned : owners).Add(key);
                    }
                }

                if (owned.Count > 0)
                {
                    keys[node] = owned;
                    kinds[node] = ChainNodeKind.Declared;
                }
                else if (owners.Count > 0)
                {
                    keys[node] = owners;
                    kinds[node] = ChainNodeKind.Declared;
                    string ownerPrefix = owners[0].Id + ".";
                    companions[node] = node.Node.Name.StartsWith(ownerPrefix, StringComparison.Ordinal)
                        ? node.Node.Name[(ownerPrefix.Length - 1)..]
                        : node.Node.Name;
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
            // A live compute's reads arrive as their own unlabelled edges from the node read, not through Reads.
            bool isLive = edge.Descriptor.Kind == GraphEdgeKind.LiveComputeRead;
            RuleGraphNode? reader = readBy ?? (isLive ? edge.Source : null);
            bool isRead = reader is not null;
            IReadOnlyList<RulesetDocumentNodeKey> sources;
            IReadOnlyList<RulesetDocumentNodeKey>? guards = null;
            if (isRead)
            {
                sources = keys[reader!];
            }
            else if (ReferenceEquals(edge.Source, root))
            {
                sources = [EventKey(label)];
            }
            else if (edge.Descriptor.Kind == GraphEdgeKind.Trigger && label.Length > 0
                     && kinds[edge.Source] is ChainNodeKind.Context or ChainNodeKind.Declared)
            {
                // The engine wires a gated trigger from its guard, a context flag or a sibling flag alike;
                // the event still fires it, so the wire runs from the event and the guard becomes a gate.
                sources = [EventKey(label)];
                guards = keys[edge.Source];
            }
            else
            {
                sources = keys[edge.Source];
            }

            string text = label;
            if (isRead)
            {
                text = companions.TryGetValue(reader!, out string? companion) ? "reads " + companion : "reads";
                if (isLive)
                {
                    text += " (live)";
                }
            }

            foreach (RulesetDocumentNodeKey target in keys[edge.Destination])
            {
                RulesetDocumentNodeKey? guard = guards is null ? null : OwnersNamedBy(guards, target)[0];
                foreach (RulesetDocumentNodeKey source in OwnersNamedBy(sources, target))
                {
                    if (source != target)
                    {
                        // A comparison gate (while: round.number > 0) has no guard node; the engine reads its
                        // operands on the trigger edge. That read is the gate, and is drawn as one.
                        if (readBy is not null && edge.Descriptor.Kind == GraphEdgeKind.Trigger && GateText(target, readBy) is { } gateText)
                        {
                            Add(new ChainEdge(source, target, gateText, null, edge.Descriptor.Effect, false) { IsGate = true }, edge);
                            continue;
                        }

                        string? authored = edge.Descriptor.Effect is EdgeEffect.Conjunction or EdgeEffect.Disjunction
                            ? AuthoredOperand(open, source, target, edge.Descriptor.ConditionLabel)
                            : null;
                        Add(new ChainEdge(source, target, text, edge.Descriptor.ConditionLabel, edge.Descriptor.Effect, isRead) { Guard = guard, Authored = authored }, edge);
                        if (guard is { } gate && gate != target)
                        {
                            Add(new ChainEdge(gate, target, "while", null, edge.Descriptor.Effect, false) { IsGate = true }, edge);
                        }
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

        void Add(ChainEdge chainEdge, RuleGraphEdge origin)
        {
            origins.Record(chainEdge, origin);
            if (seen.Add(chainEdge))
            {
                edges.Add(chainEdge);
            }
        }

        // A shared engine node stands for every entry with its definition. A wire into target runs from
        // the owners target's own text names, and from all of them only when it names none.
        IReadOnlyList<RulesetDocumentNodeKey> OwnersNamedBy(IReadOnlyList<RulesetDocumentNodeKey> owners, RulesetDocumentNodeKey target)
        {
            if (owners.Count < 2 || !references.TryGetValue(target, out IReadOnlyList<string>? paths))
            {
                return owners;
            }

            List<RulesetDocumentNodeKey> named = [.. owners.Where(owner => paths.Any(path => Names(owner, path, target.Ruleset)))];
            return named.Count > 0 ? named : owners;
        }

        bool Names(RulesetDocumentNodeKey owner, string path, string from)
        {
            string qualified = owner.Ruleset + "." + owner.Id;
            if (path == qualified || path.StartsWith(qualified + ".", StringComparison.Ordinal))
            {
                return true;
            }

            if (from == owner.Ruleset && (path == owner.Id || path.StartsWith(owner.Id + ".", StringComparison.Ordinal)))
            {
                return true;
            }

            return outputs.TryGetValue(owner, out IReadOnlyList<string>? companions)
                   && companions.Any(c => path == c || path == owner.Ruleset + "." + c);
        }

        // "while <gate>" when the stat written at target has a comparison gate that reads the reader's node;
        // a bare-reference gate is the guard split above, and a stat with no gate has nothing to say.
        string? GateText(RulesetDocumentNodeKey target, RuleGraphNode reader)
        {
            if (!stats.TryGetValue(target, out CheckedStat? stat) || stat.WhileGate is not { Root: not ReferenceNode } gate)
            {
                return null;
            }

            foreach (ResolvedReference reference in gate.References)
            {
                string ruleId = ContextRuleIds.GetValueOrDefault(reference.Path) ?? reference.Path;
                if (reader.RuleIds.Contains(ruleId, StringComparer.Ordinal)
                    || reader.RuleIds.Contains(target.Ruleset + "." + reference.Path, StringComparer.Ordinal)
                    || keys[reader].Any(k => k.Ruleset == target.Ruleset && k.Id == reference.Path))
                {
                    // The infix writer parenthesises a binary root; the gate is one comparison, so the pair goes.
                    string spelled = V1ExpressionWriter.Write(gate.Root);
                    return "while " + (gate.Root is BinaryNode && spelled.Length > 1 ? spelled[1..^1] : spelled);
                }
            }

            return null;
        }

        Dictionary<RulesetDocumentNodeKey, ChainNodeKind> kindByKey = new();
        foreach ((RuleGraphNode node, ChainNodeKind kind) in kinds)
        {
            foreach (RulesetDocumentNodeKey key in keys[node])
            {
                kindByKey[key] = kind;
            }
        }

        List<ChainNode> nodes = MergeTwinEvents(events, edges, kindByKey, ViewsByEvents(rulesets), origins);
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
                        nodes.Add(new ChainNode(key, kinds[node], name, node.Scope == RuleGraphScope.Player)
                        {
                            Outputs = outputs.GetValueOrDefault(key) ?? [],
                            IsPerTeam = node.Scope == RuleGraphScope.Team,
                            IsRoundFact = node.Origin == RuleGraphNodeOrigin.RoundFact
                        });
                    }
                }
            }
        }

        if (open is not null)
        {
            SpliceDefines(nodes, edges, open, stats, origins);
            SpliceSurfaces(nodes, edges, open);
        }

        return new RulesetChainGraph(nodes, edges) { Diagnostics = graph.Diagnostics, EngineEdges = origins.Freeze(edges) };
    }

    /// <summary>
    ///     The document blocks the engine has no node for: the params node ahead of the declared entries
    ///     that read it, the show nodes after the entries they surface. Wires only to nodes already drawn.
    /// </summary>
    // The operand row of the target's when: that this logic wire stands for, as written. Only the open
    // document's text is at hand; a dependency's wire keeps the engine's label.
    private static string? AuthoredOperand(RulesetDoc? open, RulesetDocumentNodeKey source, RulesetDocumentNodeKey target, string? lowered)
    {
        if (open is null || target.Ruleset != open.Id)
        {
            return null;
        }

        string? when = open.Highlights.FirstOrDefault(h => h.Id == target.Id)?.When
                       ?? open.Stats.FirstOrDefault(s => s.Id == target.Id && s.Kind == StatKind.Flag)?.KindArg;
        if (string.IsNullOrWhiteSpace(when))
        {
            return null;
        }

        string read = source.Ruleset == "~context"
            ? CanvasEditSemantics.GatePathOf(source.Id) ?? source.Id
            : source.Ruleset == target.Ruleset ? source.Id : source.Ruleset + "." + source.Id;
        return ChainWiring.AuthoredRow(when, read, lowered)?.Text;
    }

    private static void SpliceSurfaces(List<ChainNode> nodes, List<ChainEdge> edges, RulesetDoc open)
    {
        HashSet<RulesetDocumentNodeKey> declared = [.. nodes.Where(n => n.Kind == ChainNodeKind.Declared).Select(n => n.Key)];
        if (SurfaceNodes.ParamsNode(open) is { } parameters)
        {
            nodes.Insert(0, parameters);
            edges.AddRange(SurfaceNodes.ParamsEdges(open, declared));
        }

        nodes.AddRange(SurfaceNodes.ShowNodes(open));
        edges.AddRange(SurfaceNodes.ShowEdges(open, declared));
    }

    /// <summary>
    ///     The lowered graph has no node for a define: its body is folded into the trigger wire of every stat
    ///     that fires on it. The canvas draws the define once, so each such stat's trigger wire is cut in two
    ///     at a define node: the event fires the define, carrying the define's condition and gate, and the
    ///     define fires the stat. What the define's body reads (a facet's enrichment, a context in its where:
    ///     or while:) moves with it; what the stat's own body reads stays on the stat.
    /// </summary>
    private static void SpliceDefines(List<ChainNode> nodes, List<ChainEdge> edges, RulesetDoc open,
        Dictionary<RulesetDocumentNodeKey, CheckedStat> stats, EdgeOrigins origins)
    {
        Dictionary<RulesetDocumentNodeKey, ChainNode> byKey = nodes.ToDictionary(n => n.Key);
        foreach ((DefineDef define, TriggerDef trigger) in RulesetDocumentGraph.TriggerDefines(open))
        {
            RulesetDocumentNodeKey key = RulesetDocumentNodeKey.Define(open.Id, define.Name);
            List<StatDef> firing = [.. open.Stats.Where(s => RulesetDocumentGraph.TriggerSpelling(s) == define.Name
                                                             && byKey.ContainsKey(new RulesetDocumentNodeKey(open.Id, s.Id)))];
            if (firing.Count == 0)
            {
                continue;
            }

            string? view = firing.Select(s => stats.GetValueOrDefault(new RulesetDocumentNodeKey(open.Id, s.Id))?.ResolvedView).FirstOrDefault(v => v is not null);
            ChainNode first = byKey[new RulesetDocumentNodeKey(open.Id, firing[0].Id)];
            ChainNode node = new(key, ChainNodeKind.Declared, define.Name, first.IsPerPlayer) { IsPerTeam = first.IsPerTeam };
            nodes.Insert(nodes.IndexOf(first), node);
            byKey[key] = node;

            List<ChainEdge> rewritten = [];
            HashSet<ChainEdge> seen = [];
            // Both halves of a cut wire stand for the engine edge the whole did.
            void Add(ChainEdge next, ChainEdge from)
            {
                origins.Inherit(from, next);
                if (seen.Add(next))
                {
                    rewritten.Add(next);
                }
            }

            HashSet<RulesetDocumentNodeKey> targets = [.. firing.Select(s => new RulesetDocumentNodeKey(open.Id, s.Id))];
            // The lowered condition is the define's alone on a stat that adds nothing of its own; when every
            // firing stat refines it, the define's part is spelled from its body instead.
            HashSet<RulesetDocumentNodeKey> unrefined = [.. firing.Where(s => !HasRefinements(s)).Select(s => new RulesetDocumentNodeKey(open.Id, s.Id))];
            string? defineCondition = edges
                .Where(e => unrefined.Contains(e.Target) && !e.IsRead && !e.IsGate && byKey[e.Source].Kind == ChainNodeKind.Event)
                .Select(e => e.Condition)
                .FirstOrDefault() ?? Spell(trigger);
            foreach (ChainEdge edge in edges)
            {
                if (!targets.Contains(edge.Target))
                {
                    Add(edge, edge);
                    continue;
                }

                StatDef stat = firing.Single(s => s.Id == edge.Target.Id);
                ChainNode source = byKey[edge.Source];
                if (edge.IsGate)
                {
                    Add(GateIsDefines(edge.Source, edge.Label, trigger, stat, open.Id) ? edge with { Target = key } : edge, edge);
                    continue;
                }

                if (edge.IsRead)
                {
                    bool defines = BodyReads(source, trigger.Match, [trigger.Where, trigger.While], view);
                    if (defines)
                    {
                        Add(edge with { Target = key }, edge);
                    }

                    if (!defines || BodyReads(source, stat.Trigger?.Match ?? [], OwnTexts(stat), view))
                    {
                        Add(edge, edge);
                    }

                    continue;
                }

                if (source.Kind != ChainNodeKind.Event)
                {
                    Add(edge, edge);
                    continue;
                }

                RulesetDocumentNodeKey? guard = edge.Guard is { } g && GateIsDefines(g, "while", trigger, stat, open.Id) ? g : null;
                Add(edge with { Target = key, Condition = defineCondition, Guard = guard }, edge);
                Add(new ChainEdge(key, edge.Target, "fires", HasRefinements(stat) ? Spell(stat.Trigger!) : null, edge.Effect, false)
                {
                    Guard = guard is null ? edge.Guard : null
                }, edge);
            }

            edges.Clear();
            edges.AddRange(rewritten);
        }
    }

    // Whether a gate wire or a guard from `source` is the define's while: rather than the stat's own. A stat
    // with no while: of its own is gated by the define alone.
    private static bool GateIsDefines(RulesetDocumentNodeKey source, string label, TriggerDef trigger, StatDef stat, string rulesetId)
    {
        if (string.IsNullOrWhiteSpace(stat.Trigger?.While))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(trigger.While))
        {
            return false;
        }

        string gate = trigger.While.Trim();
        return source.Id == ContextRuleIds.GetValueOrDefault(gate)
               || source == new RulesetDocumentNodeKey(rulesetId, gate)
               || label == "while " + gate;
    }

    // Whether a body (its match: facets and its texts) reads what `source` stands for: the enrichment one of
    // its facets is reached through, or a context or enrichment path named in its where: or while:.
    private static bool BodyReads(ChainNode source, IReadOnlyList<MatchBinding> match, IEnumerable<string?> texts, string? view)
    {
        if (source.Kind == ChainNodeKind.Enrichment
            && match.Any(b => CanvasEditSemantics.FacetReading(view, source.Name)?.Name == b.Key))
        {
            return true;
        }

        // A path names the node by the catalog's rule id, by its own spelling, or by the engine's built-in
        // player.team to player_team form, which the catalog does not list.
        foreach (string path in texts.Where(t => !string.IsNullOrWhiteSpace(t)).SelectMany(t => Paths(t!)))
        {
            if (path == source.Name || path.Replace('.', '_') == source.Name || ContextRuleIds.GetValueOrDefault(path) == source.Name)
            {
                return true;
            }
        }

        return false;
    }

    // The dotted identifiers of an expression text, quoted literals left out.
    private static IEnumerable<string> Paths(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                int close = text.IndexOf(c, i + 1);
                i = close < 0 ? text.Length : close + 1;
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                {
                    i++;
                }

                yield return text[start..i].TrimEnd('.');
                continue;
            }

            i++;
        }
    }

    // The texts of a stat's own body that may read a node: its where:, its while:, and its value argument.
    private static IEnumerable<string?> OwnTexts(StatDef stat)
    {
        yield return stat.Trigger?.Where;
        yield return stat.Trigger?.While;
        if (stat.Kind is not (StatKind.Count or StatKind.Streak or StatKind.Bucket or StatKind.Burst))
        {
            yield return stat.KindArg;
        }

        yield return stat.BucketKey;
        yield return stat.BucketValue;
        foreach (string key in stat.BucketKeys ?? [])
        {
            yield return key;
        }
    }

    // Whether the stat adds a match: facet or a where: of its own beside the define's; actor: is a binding, not a condition.
    private static bool HasRefinements(StatDef stat) =>
        stat.Trigger is { } trigger
        && (trigger.Match.Any(b => !string.Equals(b.Key, "actor", StringComparison.Ordinal)) || !string.IsNullOrWhiteSpace(trigger.Where));

    // A trigger body as a condition the hover can show: its facets as written, then its where:.
    private static string? Spell(TriggerDef trigger)
    {
        List<string> parts = [];
        foreach (MatchBinding binding in trigger.Match)
        {
            if (!string.Equals(binding.Key, "actor", StringComparison.Ordinal))
            {
                parts.Add($"{binding.Key}: {MatchFacetValue.From(binding.Test)?.ToMatchValue() ?? ""}");
            }
        }

        if (!string.IsNullOrWhiteSpace(trigger.Where))
        {
            parts.Add("where " + trigger.Where.Trim());
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>
    ///     The view name for each set of concrete events, keyed by the sorted events: the catalog's plainest
    ///     view on that set under the composing profile (<c>round_ended</c>, not <c>round_won</c>), then any
    ///     view the resolver gave a stat on a set the catalog does not list.
    /// </summary>
    private static Dictionary<string, string> ViewsByEvents(IReadOnlyList<CheckedRuleset> rulesets)
    {
        Dictionary<string, string> views = new(StringComparer.Ordinal);
        foreach (CatalogView view in EmbeddedCatalog.Root.Views.OrderBy(v => string.IsNullOrEmpty(v.Result) && string.IsNullOrEmpty(v.Actor) ? 0 : 1))
        {
            CatalogViewProfile? profile = view.Profiles.FirstOrDefault(p => p.Profile == RulesetCorpus.ProfileName);
            if (profile is { ConcreteEvents.Count: > 1 })
            {
                views.TryAdd(Signature(profile.ConcreteEvents), view.Name);
            }
        }

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
    ///     into one event node: the round-end trio is one <c>round_ended</c>, named by the catalog view on
    ///     those events, else by the events joined. A wire every twin carried becomes one
    ///     wire labelled with the merged name; a wire only some carried keeps its own event's label.
    ///     Rewrites <paramref name="edges" /> in place and returns the event nodes in first-use order.
    /// </summary>
    private static List<ChainNode> MergeTwinEvents(Dictionary<string, RulesetDocumentNodeKey> events, List<ChainEdge> edges,
        Dictionary<RulesetDocumentNodeKey, ChainNodeKind> kindByKey, Dictionary<string, string> views, EdgeOrigins origins)
    {
        Dictionary<string, List<string>> bySignature = new(StringComparer.Ordinal);
        foreach ((string name, RulesetDocumentNodeKey key) in events)
        {
            // Context targets are left out of the signature on purpose: cs_win_panel_match also ends
            // MatchLive, and that must not keep it apart from the other two round ends.
            string signature = Signature(edges
                .Where(e => e.Source == key && !e.IsRead && !e.IsGate
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
        Dictionary<(RulesetDocumentNodeKey, RulesetDocumentNodeKey, EdgeEffect, string?, RulesetDocumentNodeKey?), HashSet<string>> carriers = new();
        foreach (ChainEdge edge in edges)
        {
            if (merged.TryGetValue(edge.Source, out (RulesetDocumentNodeKey Key, string Name, List<string> Events) twin) && !edge.IsRead)
            {
                (RulesetDocumentNodeKey, RulesetDocumentNodeKey, EdgeEffect, string?, RulesetDocumentNodeKey?) wire = (twin.Key, edge.Target, edge.Effect, edge.Condition, edge.Guard);
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
                bool shared = !edge.IsRead && carriers[(twin.Key, edge.Target, edge.Effect, edge.Condition, edge.Guard)].SetEquals(twin.Events);
                next = edge with { Source = twin.Key, Label = shared ? twin.Name : edge.Label };
                origins.Inherit(edge, next);
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

    /// <summary>
    ///     The engine edges behind each wire while the chain is built. Keyed by the wire's value, so a wire
    ///     rewritten with <c>with</c> is a new key that inherits from the old one; one engine edge reached
    ///     through several owners is recorded once.
    /// </summary>
    private sealed class EdgeOrigins
    {
        private readonly Dictionary<ChainEdge, HashSet<RuleGraphEdge>> _byEdge = [];

        public void Record(ChainEdge edge, RuleGraphEdge origin) => Set(edge).Add(origin);

        public void Inherit(ChainEdge from, ChainEdge to)
        {
            if (from != to && _byEdge.TryGetValue(from, out HashSet<RuleGraphEdge>? origins))
            {
                Set(to).UnionWith(origins);
            }
        }

        /// <summary>The map for the wires that survived, in a stable order per wire.</summary>
        public Dictionary<ChainEdge, IReadOnlyList<RuleGraphEdge>> Freeze(IReadOnlyList<ChainEdge> edges)
        {
            Dictionary<ChainEdge, IReadOnlyList<RuleGraphEdge>> frozen = [];
            foreach (ChainEdge edge in edges)
            {
                if (_byEdge.TryGetValue(edge, out HashSet<RuleGraphEdge>? origins))
                {
                    frozen[edge] = [.. origins.OrderBy(o => o.Key, StringComparer.Ordinal)];
                }
            }

            return frozen;
        }

        private HashSet<RuleGraphEdge> Set(ChainEdge edge) =>
            _byEdge.TryGetValue(edge, out HashSet<RuleGraphEdge>? set) ? set : _byEdge[edge] = new HashSet<RuleGraphEdge>(ReferenceEqualityComparer.Instance);
    }
}
