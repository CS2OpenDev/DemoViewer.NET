#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

/// <summary>
///     Reduces an <see cref="AnalysisRun" /> to a <see cref="LiveValuesRun" />: O(nodes + edges + fires),
///     no snapshot row materialised. Declared entries are joined to engine nodes the way the chain graph
///     joins them, by the <c>{ruleset}.{id}</c> spelling in <see cref="RuleGraphNode.RuleIds" />, so a
///     node shared by two identical entries gives both the same track. Pure.
/// </summary>
public static class LiveValuesReducer
{
    public static LiveValuesRun Reduce(AnalysisRun run, IReadOnlyList<CheckedRuleset> rulesets, string demoPath, int tickRate,
        TimeSpan buildTime, TimeSpan evaluateTime, long peakBytes)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(rulesets);
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        RuleGraph graph = RuleGraph.FromRun(run);
        EvaluationResult? snapshots = run.Snapshots;
        IReadOnlyDictionary<StateEdge, List<int>> applied = snapshots?.AppliedMessagesByEdge ?? new Dictionary<StateEdge, List<int>>();
        IReadOnlyList<MessageRef> messages = snapshots?.Messages ?? [];

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
                int? first = null, last = null;
                foreach (RuleGraphEdge edge in into.GetValueOrDefault(node) ?? [])
                {
                    GraphEdgeDescriptor descriptor = edge.Instances.Count > copy ? edge.Instances[copy] : edge.Descriptor;
                    if (descriptor.Edge is not { } engineEdge)
                    {
                        continue;
                    }

                    fires += engineEdge.FireCount;
                    if (applied.TryGetValue(engineEdge, out List<int>? indices) && indices.Count > 0 && messages.Count > 0)
                    {
                        int a = messages[indices[0]].Tick;
                        int b = messages[indices[^1]].Tick;
                        first = first is null ? a : Math.Min(first.Value, a);
                        last = last is null ? b : Math.Max(last.Value, b);
                    }
                }

                NodeTrack track = new(fires, first, last, state.GetDisplayValue(), state.GetNumericValue(), state.IsActive);
                foreach (RulesetDocumentNodeKey key in keys)
                {
                    bound.Add(key);
                    tracks[(key, instance)] = track;
                }
            }
        }

        int firstTick = messages.Count > 0 ? messages[0].Tick : 0;
        int lastTick = messages.Count > 0 ? messages[^1].Tick : 0;
        List<RulesetDocumentNodeKey> unbound = [.. declared.Values.Distinct().Where(k => !bound.Contains(k)).OrderBy(k => k.ToString(), StringComparer.Ordinal)];
        return new LiveValuesRun(demoPath, run.Build.Profile.GetType().Name, tickRate, firstTick, lastTick, instances, tracks, unbound,
            buildTime, evaluateTime, clock.Elapsed, peakBytes);
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
