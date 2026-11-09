#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

/// <summary>
///     Reduces an <see cref="AnalysisRun" /> to a <see cref="LiveValuesRun" />: O(nodes + edges + fires),
///     no snapshot row materialised. Declared entries are joined to engine nodes the way the chain graph
///     joins them, by the <c>{ruleset}.{id}</c> spelling in <see cref="RuleGraphNode.RuleIds" />, so a
///     node shared by two identical entries gives both the same track. Wires are joined through the chain
///     graph built over the run's own graph, so each drawn wire reads the engine edge it was cut from. Pure.
/// </summary>
public static class LiveValuesReducer
{
    /// <param name="run">The evaluation, with snapshots on.</param>
    /// <param name="rulesets">The checked rulesets the run was built from.</param>
    /// <param name="demoPath">The demo evaluated.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    /// <param name="buildTime">Time the build took.</param>
    /// <param name="evaluateTime">Time the evaluation took.</param>
    /// <param name="peakBytes">Working set growth so far.</param>
    /// <param name="open">The open document, whose defines get tracks of their own; null folds them into their stats.</param>
    public static LiveValuesRun Reduce(AnalysisRun run, IReadOnlyList<CheckedRuleset> rulesets, string demoPath, int tickRate,
        TimeSpan buildTime, TimeSpan evaluateTime, long peakBytes, RulesetDoc? open = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(rulesets);
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        RuleGraph graph = RuleGraph.FromRun(run);
        EvaluationResult? snapshots = run.Snapshots;
        IReadOnlyDictionary<StateEdge, List<int>> applied = snapshots?.AppliedMessagesByEdge ?? new Dictionary<StateEdge, List<int>>();
        IReadOnlyList<MessageRef> messages = snapshots?.Messages ?? [];
        Dictionary<StateNode, int> columns = new(ReferenceEqualityComparer.Instance);
        IReadOnlyList<StateNode> tracked = snapshots?.FinalTrackedNodes ?? [];
        for (int i = 0; i < tracked.Count; i++)
        {
            columns.TryAdd(tracked[i], i);
        }

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

        List<LiveInstance> instances = [new LiveInstance(0, null, null)];
        IReadOnlyList<PerPlayerNodeTemplate.MaterializedPlayer> players = run.MaterializedPlayers;
        for (int i = 0; i < players.Count; i++)
        {
            instances.Add(new LiveInstance(i + 1, players[i].PlayerSlot, players[i].PlayerName));
        }

        // Edges by destination, once; a template edge carries one engine edge per player in slot order.
        Dictionary<RuleGraphNode, List<RuleGraphEdge>> into = new(ReferenceEqualityComparer.Instance);
        foreach (RuleGraphEdge edge in graph.Edges)
        {
            if (!into.TryGetValue(edge.Destination, out List<RuleGraphEdge>? list))
            {
                into[edge.Destination] = list = [];
            }

            list.Add(edge);
        }

        Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> tracks = [];
        HashSet<RulesetDocumentNodeKey> bound = [];
        List<int> indices = [];
        foreach (RuleGraphNode node in graph.Nodes)
        {
            List<RulesetDocumentNodeKey> keys = [.. node.RuleIds.Concat(node.Owners).Distinct(StringComparer.Ordinal)
                .Select(id => declared.GetValueOrDefault(id)).OfType<RulesetDocumentNodeKey>().Distinct()];
            if (keys.Count == 0)
            {
                continue;
            }

            bool perPlayer = node.Template is not null && node.Instances.Count > 1 || node.PlayerSlot is not null;
            int copies = Math.Max(1, node.Instances.Count);
            for (int copy = 0; copy < copies; copy++)
            {
                StateNode state = node.Instances.Count > copy ? node.Instances[copy] : node.Node;
                int instance = perPlayer ? InstanceOf(instances, node, copy, players) : 0;
                int fires = 0;
                indices.Clear();
                foreach (RuleGraphEdge edge in into.GetValueOrDefault(node) ?? [])
                {
                    GraphEdgeDescriptor descriptor = edge.Instances.Count > copy ? edge.Instances[copy] : edge.Descriptor;
                    if (descriptor.Edge is not { } engineEdge)
                    {
                        continue;
                    }

                    fires += engineEdge.FireCount;
                    if (applied.TryGetValue(engineEdge, out List<int>? hits))
                    {
                        indices.AddRange(hits);
                    }
                }

                indices.Sort();
                ValueSeries series = SeriesOf(indices, messages, snapshots, columns.TryGetValue(state, out int column) ? column : -1);
                (int on, int off) = Transitions(series);
                NodeTrack track = new(fires, series.Count > 0 ? series.Ticks[0] : null, series.Count > 0 ? series.Ticks[^1] : null,
                    state.GetDisplayValue(), state.GetNumericValue(), state.IsActive) { Series = series, OnCount = on, OffCount = off };
                foreach (RulesetDocumentNodeKey key in keys)
                {
                    bound.Add(key);
                    tracks[(key, instance)] = track;
                }
            }
        }

        if (open is not null)
        {
            AddDefineTracks(open, instances.Count, tracks);
        }

        int firstTick = messages.Count > 0 ? messages[0].Tick : 0;
        int lastTick = messages.Count > 0 ? messages[^1].Tick : 0;
        List<RulesetDocumentNodeKey> unbound = [.. declared.Values.Distinct().Where(k => !bound.Contains(k)).OrderBy(k => k.ToString(), StringComparer.Ordinal)];
        return new LiveValuesRun(demoPath, run.Build.Profile.GetType().Name, tickRate, firstTick, lastTick, instances, tracks, unbound,
            buildTime, evaluateTime, clock.Elapsed, peakBytes) { Wires = WireTracks(graph, rulesets, open, instances, applied, messages) };
    }

    /// <summary>The value after each applied message, in index order, from the node's snapshot column; ticks alone without a column.</summary>
    public static ValueSeries SeriesOf(IReadOnlyList<int> sortedIndices, IReadOnlyList<MessageRef> messages, EvaluationResult? snapshots, int column)
    {
        ArgumentNullException.ThrowIfNull(sortedIndices);
        ArgumentNullException.ThrowIfNull(messages);
        if (sortedIndices.Count == 0 || messages.Count == 0)
        {
            return ValueSeries.Empty;
        }

        List<ValuePoint> points = new(Math.Min(sortedIndices.Count, ValueSeries.Cap * 2));
        // Decimation keeps indices, not values, so only the kept rows are read from the table.
        IReadOnlyList<int> keep = LiveLookup.Decimate(sortedIndices.Count, ValueSeries.Cap);
        int last = -1;
        foreach (int k in keep)
        {
            int index = sortedIndices[k];
            if (index == last || index < 0 || index >= messages.Count)
            {
                continue;
            }

            last = index;
            int tick = messages[index].Tick;
            if (snapshots is not null && column >= 0)
            {
                NodeSnapshot snapshot = snapshots.MessageSnapshots[index, column];
                points.Add(new ValuePoint(tick, snapshot.IsActive, snapshot.NumericValue, snapshot.DisplayValue));
            }
            else
            {
                points.Add(new ValuePoint(tick, false, null, null));
            }
        }

        return keep.Count < sortedIndices.Count ? ValueSeries.Sampled(points) : ValueSeries.From(points);
    }

    /// <summary>How many points turned the node on and off, from inactive at the start.</summary>
    public static (int On, int Off) Transitions(ValueSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        int on = 0, off = 0;
        bool was = false;
        foreach (ValuePoint point in series.Points)
        {
            if (point.Active != was)
            {
                if (point.Active)
                {
                    on++;
                }
                else
                {
                    off++;
                }

                was = point.Active;
            }
        }

        return (on, off);
    }

    // A define has no engine node: its track sums the stats that fire on it, per instance, and its series
    // is the running fire count over their merged ticks.
    private static void AddDefineTracks(RulesetDoc open, int instanceCount, Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> tracks)
    {
        foreach ((DefineDef define, TriggerDef _) in RulesetDocumentGraph.TriggerDefines(open))
        {
            IReadOnlyList<string> firing = RulesetDocumentGraph.StatsFiringOn(open, define.Name);
            if (firing.Count == 0)
            {
                continue;
            }

            RulesetDocumentNodeKey key = RulesetDocumentNodeKey.Define(open.Id, define.Name);
            for (int instance = 0; instance < instanceCount; instance++)
            {
                List<NodeTrack> parts = [];
                foreach (string stat in firing)
                {
                    if (tracks.TryGetValue((new RulesetDocumentNodeKey(open.Id, stat), instance), out NodeTrack? part))
                    {
                        parts.Add(part);
                    }
                }

                if (parts.Count == 0)
                {
                    continue;
                }

                int fires = parts.Sum(p => p.FireCount);
                List<int> ticks = [.. parts.SelectMany(p => p.Series.Ticks).Order()];
                List<ValuePoint> points = new(ticks.Count);
                for (int i = 0; i < ticks.Count; i++)
                {
                    points.Add(new ValuePoint(ticks[i], true, i + 1, null));
                }

                ValueSeries series = parts.Any(p => p.Series.IsSampled) ? ValueSeries.Sampled(points) : ValueSeries.From(points);
                tracks[(key, instance)] = new NodeTrack(fires, parts.Min(p => p.FirstTick), parts.Max(p => p.LastTick), null, fires, fires > 0)
                {
                    Series = series, ViaStats = firing.Count
                };
            }
        }
    }

    // The chain graph over the run's graph, players collapsed, names the engine edge(s) behind every drawn
    // wire; each copy of an edge is one player's, told apart by the node it writes.
    private static Dictionary<(LiveWireKey, int), WireTrack> WireTracks(RuleGraph graph, IReadOnlyList<CheckedRuleset> rulesets,
        RulesetDoc? open, IReadOnlyList<LiveInstance> instances, IReadOnlyDictionary<StateEdge, List<int>> applied, IReadOnlyList<MessageRef> messages)
    {
        Dictionary<(LiveWireKey, int), WireTrack> wires = [];
        RulesetChainGraph chain;
        try
        {
            chain = RulesetChainGraph.Build(graph.CollapsePlayers(), rulesets, open);
        }
        catch (InvalidOperationException)
        {
            // A template that is not slot-independent cannot collapse; the nodes keep their tracks, the wires go without.
            return wires;
        }

        Dictionary<string, int> eventCounts = new(StringComparer.Ordinal);
        foreach (MessageRef message in messages)
        {
            if (message.Message is GameEventMessage game)
            {
                eventCounts[game.DecodedEvent.Name] = eventCounts.GetValueOrDefault(game.DecodedEvent.Name) + 1;
            }
        }

        Dictionary<(LiveWireKey, int), (HashSet<StateEdge> Edges, List<int> Indices, string? Label)> gathered = [];
        foreach (ChainEdge edge in chain.Edges)
        {
            if (!chain.EngineEdges.TryGetValue(edge, out IReadOnlyList<RuleGraphEdge>? origins))
            {
                continue;
            }

            LiveWireKey key = new(edge.Source, edge.Target, KindOf(edge));
            foreach (RuleGraphEdge origin in origins)
            {
                foreach (GraphEdgeDescriptor descriptor in origin.Instances)
                {
                    if (descriptor.Edge is not { } engineEdge)
                    {
                        continue;
                    }

                    int instance = graph.TryGetNode(descriptor.Destination, out RuleGraphNode? written) && written.PlayerSlot is { } slot
                        ? instances.FirstOrDefault(i => i.Slot == slot)?.Index ?? 0
                        : 0;
                    if (!gathered.TryGetValue((key, instance), out (HashSet<StateEdge> Edges, List<int> Indices, string? Label) acc))
                    {
                        gathered[(key, instance)] = acc = (new HashSet<StateEdge>(ReferenceEqualityComparer.Instance), [], descriptor.Label);
                    }

                    if (acc.Edges.Add(engineEdge) && applied.TryGetValue(engineEdge, out List<int>? hits))
                    {
                        acc.Indices.AddRange(hits);
                    }
                }
            }
        }

        foreach (((LiveWireKey key, int instance), (HashSet<StateEdge> edges, List<int> indices, string? label)) in gathered)
        {
            indices.Sort();
            List<int> ticks = new(indices.Count);
            int last = -1;
            foreach (int index in indices)
            {
                if (index != last && index >= 0 && index < messages.Count)
                {
                    ticks.Add(messages[index].Tick);
                    last = index;
                }
            }

            int fires = edges.Sum(e => e.FireCount);
            wires[(key, instance)] = new WireTrack(fires, ticks.Count > 0 ? ticks[0] : null, ticks.Count > 0 ? ticks[^1] : null, TickList.From(ticks))
            {
                TriggerCount = key.Kind == LiveWireKind.Gate && label is { Length: > 0 } && eventCounts.TryGetValue(label, out int count) ? count : null
            };
        }

        return wires;
    }

    /// <summary>What a chain wire carries, as the canvas types its target port.</summary>
    public static LiveWireKind KindOf(ChainEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (edge.IsGate)
        {
            return LiveWireKind.Gate;
        }

        if (edge.IsRead)
        {
            return LiveWireKind.Read;
        }

        return edge.Effect is EdgeEffect.Conjunction or EdgeEffect.Disjunction ? LiveWireKind.Logic : LiveWireKind.Trigger;
    }

    // A template node's copies come in the order the players materialised, which is the run's player order.
    private static int InstanceOf(IReadOnlyList<LiveInstance> instances, RuleGraphNode node, int copy,
        IReadOnlyList<PerPlayerNodeTemplate.MaterializedPlayer> players)
    {
        if (node.PlayerSlot is { } slot)
        {
            return instances.FirstOrDefault(i => i.Slot == slot)?.Index ?? 0;
        }

        return copy < players.Count ? copy + 1 : 0;
    }
}
