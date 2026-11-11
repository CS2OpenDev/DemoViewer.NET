#region

using System.Text.RegularExpressions;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     The document blocks the engine lowers to nothing of its own but the canvas draws as nodes: the
///     <c>show:</c> tables and scoreboard, wired from the stats they surface, and the <c>params:</c>
///     block, wired to every entry whose text reads a param. Pure: the open document in, chain nodes and
///     edges out, keyed the way the rest of the chain is.
/// </summary>
public static partial class SurfaceNodes
{
    [GeneratedRegex(@"\bparams\.([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ParamReference();

    /// <summary>One node per <c>show:</c> table, then one for the scoreboard when it has entries; in document order.</summary>
    public static IReadOnlyList<ChainNode> ShowNodes(RulesetDoc open)
    {
        ArgumentNullException.ThrowIfNull(open);
        List<ChainNode> nodes = [];
        if (open.Show is not { } show)
        {
            return nodes;
        }

        foreach (TableDef table in show.Tables)
        {
            nodes.Add(new ChainNode(RulesetDocumentNodeKey.Show(open.Id, table.Name), ChainNodeKind.Show, table.Name, false));
        }

        if (show.Scoreboard.Count > 0)
        {
            nodes.Add(new ChainNode(RulesetDocumentNodeKey.Scoreboard(open.Id), ChainNodeKind.Show, RulesetDocumentNodeKey.ScoreboardName, false));
        }

        return nodes;
    }

    /// <summary>
    ///     A read wire from each surfaced entry to the show node that surfaces it, labelled with the column's
    ///     label and carrying the column's index. A column naming an entry outside <paramref name="declared" />
    ///     draws nothing: the Inspector still lists it.
    /// </summary>
    public static IReadOnlyList<ChainEdge> ShowEdges(RulesetDoc open, IReadOnlyCollection<RulesetDocumentNodeKey> declared)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(declared);
        List<ChainEdge> edges = [];
        foreach (ShowTable table in ShowSurface.Of(open).Tables)
        {
            Add(RulesetDocumentNodeKey.Show(open.Id, table.Name), table.Columns);
        }

        Add(RulesetDocumentNodeKey.Scoreboard(open.Id), ShowSurface.Of(open).Scoreboard);
        return edges;

        void Add(RulesetDocumentNodeKey target, IReadOnlyList<ShowReference> columns)
        {
            foreach (ShowReference column in columns)
            {
                RulesetDocumentNodeKey? source = declared.FirstOrDefault(k => k.Ruleset == open.Id && !k.IsDefine && !k.IsShow && !k.IsParams
                                                                              && ShowSurface.Refers(column.Stat, k.Id, open.Id));
                if (source is { } key)
                {
                    edges.Add(new ChainEdge(key, target, column.Label ?? column.Stat, null, EdgeEffect.SetValue, true) { Column = column.Index });
                }
            }
        }
    }

    /// <summary>The one <c>params:</c> node, or null for a document without the block.</summary>
    public static ChainNode? ParamsNode(RulesetDoc open)
    {
        ArgumentNullException.ThrowIfNull(open);
        return open.Params.Count == 0 ? null : new ChainNode(RulesetDocumentNodeKey.Params(open.Id), ChainNodeKind.Params, "params", false);
    }

    /// <summary>
    ///     A read wire from the params node to every declared entry whose texts name a param, labelled with
    ///     the params read. Only entries in <paramref name="declared" /> are wired.
    /// </summary>
    public static IReadOnlyList<ChainEdge> ParamsEdges(RulesetDoc open, IReadOnlyCollection<RulesetDocumentNodeKey> declared)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(declared);
        List<ChainEdge> edges = [];
        if (open.Params.Count == 0)
        {
            return edges;
        }

        RulesetDocumentNodeKey source = RulesetDocumentNodeKey.Params(open.Id);
        foreach (StatDef stat in open.Stats)
        {
            Add(new RulesetDocumentNodeKey(open.Id, stat.Id), StatTexts(stat));
        }

        foreach (HighlightDef highlight in open.Highlights)
        {
            Add(new RulesetDocumentNodeKey(open.Id, highlight.Id), [highlight.When]);
        }

        foreach ((DefineDef define, TriggerDef trigger) in RulesetDocumentGraph.TriggerDefines(open))
        {
            Add(RulesetDocumentNodeKey.Define(open.Id, define.Name), [trigger.Where, trigger.While]);
        }

        return edges;

        void Add(RulesetDocumentNodeKey target, IEnumerable<string?> texts)
        {
            if (!declared.Contains(target))
            {
                return;
            }

            IReadOnlyList<string> names = ParamsRead(texts);
            if (names.Count > 0)
            {
                edges.Add(new ChainEdge(source, target, "reads " + string.Join(", ", names.Select(n => "params." + n)), null, EdgeEffect.SetValue, true));
            }
        }
    }

    /// <summary>The param names <paramref name="texts" /> read, each once, in first-use order.</summary>
    public static IReadOnlyList<string> ParamsRead(IEnumerable<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        List<string> names = [];
        foreach (string text in texts.Where(t => !string.IsNullOrEmpty(t)).Select(t => t!))
        {
            foreach (Match match in ParamReference().Matches(text))
            {
                string name = match.Groups[1].Value;
                if (!names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    // Every text of a stat that may hold a params.<name> read: the kind argument, the trigger's tests, a
    // bucket's key and value, a tally's threshold minimums.
    private static IEnumerable<string?> StatTexts(StatDef stat)
    {
        yield return stat.KindArg;
        yield return stat.Trigger?.Where;
        yield return stat.Trigger?.While;
        yield return stat.OffTrigger?.Where;
        yield return stat.OffTrigger?.While;
        yield return stat.BucketKey;
        yield return stat.BucketValue;
        foreach (string key in stat.BucketKeys ?? [])
        {
            yield return key;
        }

        foreach (TallyThreshold threshold in stat.Thresholds ?? [])
        {
            if (threshold.Min is TallyMinParam param)
            {
                yield return param.RawText;
            }
        }
    }
}
