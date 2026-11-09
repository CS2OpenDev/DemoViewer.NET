#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     Entries the author has not finished: a stat with no trigger, an empty <c>when:</c>, a highlight with its
///     <c>when:</c> blank, a table with no columns, a define nothing fires on. The resolver drops the first two
///     without a word and refuses a blank <c>when:</c> outright, which blanked the whole canvas; here each one
///     is a warning at its entry saying what is missing and the fix, and the entries the resolver cannot take
///     are kept out of the composition so the rest still lowers. The open document only: a dependency's
///     entries are its author's to finish.
/// </summary>
public static class Incompleteness
{
    /// <summary>One warning per unfinished entry of <paramref name="doc" />, at the entry's line, in document order.</summary>
    public static IReadOnlyList<RuleDiagnostic> Of(RulesetDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        List<RuleDiagnostic> found = [];
        foreach (StatDef stat in doc.Stats)
        {
            if (StatMessage(stat) is { } message)
            {
                found.Add(At(doc, new RulesetDocumentNodeKey(doc.Id, stat.Id), stat.Position.Line, message));
            }
        }

        foreach (HighlightDef highlight in doc.Highlights)
        {
            if (string.IsNullOrWhiteSpace(highlight.When))
            {
                found.Add(At(doc, new RulesetDocumentNodeKey(doc.Id, highlight.Id), highlight.Position.Line,
                    $"{highlight.Id} has a blank when: type the test, or wire a stat into its when port"));
            }
        }

        foreach (TableDef table in doc.Show?.Tables ?? [])
        {
            if (table.Columns.Count == 0)
            {
                found.Add(At(doc, RulesetDocumentNodeKey.Show(doc.Id, table.Name), table.Position.Line,
                    $"table {table.Name} has no columns: wire a stat into it, or add one in the Inspector"));
            }
        }

        foreach ((DefineDef define, TriggerDef _) in RulesetDocumentGraph.TriggerDefines(doc))
        {
            if (RulesetDocumentGraph.StatsFiringOn(doc, define.Name).Count == 0)
            {
                found.Add(At(doc, RulesetDocumentNodeKey.Define(doc.Id, define.Name), define.Position.Line,
                    $"nothing fires on define {define.Name}: set a stat's trigger to it, or drop it on a trigger wire"));
            }
        }

        return found;
    }

    /// <summary>
    ///     <paramref name="doc" /> without the stats and highlights the resolver cannot take or would drop in
    ///     silence; the same document when every entry is whole. The entries left out are still drawn, from
    ///     the document, with their warning.
    /// </summary>
    public static RulesetDoc Stripped(RulesetDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        List<StatDef> stats = [.. doc.Stats.Where(s => StatMessage(s) is null)];
        List<HighlightDef> highlights = [.. doc.Highlights.Where(h => !string.IsNullOrWhiteSpace(h.When))];
        return stats.Count == doc.Stats.Count && highlights.Count == doc.Highlights.Count
            ? doc
            : doc with { Stats = stats, Highlights = highlights };
    }

    /// <summary>The keys of the stats and highlights <see cref="Stripped" /> leaves out of <paramref name="doc" />.</summary>
    public static IReadOnlyList<RulesetDocumentNodeKey> StrippedKeys(RulesetDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return
        [
            .. doc.Stats.Where(s => StatMessage(s) is not null).Select(s => new RulesetDocumentNodeKey(doc.Id, s.Id)),
            .. doc.Highlights.Where(h => string.IsNullOrWhiteSpace(h.When)).Select(h => new RulesetDocumentNodeKey(doc.Id, h.Id))
        ];
    }

    // What a stat lacks, in the words of the fix, or null for a whole one. The kinds that name their view in
    // their own key need that view; a sum or capture of an event value needs on: (a round fact or a context
    // value is read without one); a flag or a compute needs its text.
    private static string? StatMessage(StatDef stat)
    {
        bool blankArg = string.IsNullOrWhiteSpace(stat.KindArg);
        bool noOn = stat.Trigger?.On is null;
        bool eventValue = blankArg || stat.KindArg!.TrimStart().StartsWith("event.", StringComparison.Ordinal);
        return stat.Kind switch
        {
            // The resolver drops a kind-less stat without a word, so it is kept out here and drawn as a draft.
            StatKind.None => $"{stat.Id} is unfinished: pick a kind",
            StatKind.Count or StatKind.Streak or StatKind.Bucket or StatKind.Burst when blankArg =>
                $"{stat.Id} has no trigger: give {stat.Kind.ToString().ToLowerInvariant()}: a view, such as kill",
            StatKind.Sum or StatKind.Capture when noOn && eventValue =>
                $"{stat.Id} has no trigger: add on: with a view, such as damage_dealt",
            StatKind.Flag when noOn && blankArg =>
                $"{stat.Id} has an empty when: type the test, or wire a stat into its when port",
            StatKind.Compute when blankArg =>
                $"{stat.Id} has no formula: type one under compute:, or wire a stat into its reads port",
            _ => null
        };
    }

    private static RuleDiagnostic At(RulesetDoc doc, RulesetDocumentNodeKey key, int line, string message) =>
        new($"{Path.GetFileName(doc.Position.File ?? doc.Id + ".rules.yaml")}({line}): {message}", message, DiagnosticWeight.Warning, doc.Id, line)
        {
            Node = key
        };
}
