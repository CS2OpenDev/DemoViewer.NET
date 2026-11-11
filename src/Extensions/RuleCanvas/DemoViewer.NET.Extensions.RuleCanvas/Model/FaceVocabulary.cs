#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>One thing a trigger picker offers: what the YAML gets, and how the picker shows it.</summary>
/// <param name="Written">The reference as the file writes it: <c>kill</c>, <c>raw.player_death</c>, a define's name.</param>
/// <param name="Group">Where it comes from: <c>view</c>, <c>raw</c> or <c>define</c>.</param>
/// <param name="Events">The wire events it fires on, for the tooltip; empty for a define.</param>
public sealed record TriggerOption(string Written, string Group, IReadOnlyList<string> Events)
{
    /// <summary>What the picker shows: a view beside its event, a raw event or a define as written.</summary>
    public string Display => Group == "view" && Events.Count > 0 ? $"{Written}  ({Events[0]})" : Written;

    public string Tip => Group switch
    {
        "view" => $"view {Written}, fires on {string.Join(", ", Events)}",
        "raw" => $"the wire event {(Events.Count > 0 ? Events[0] : Written)}, with no actor binding",
        _ => $"define {Written} of this ruleset"
    };
}

/// <summary>
///     What the faces of one composition can name besides their own fields: the open document's defines,
///     the flags a gate can name, the stats a tally can count, the buckets a rate can divide, and the groups
///     highlights already use. Built once per composition and shared by every face of it.
/// </summary>
/// <param name="Defines">The open document's trigger-bodied <c>define:</c> names, in document order.</param>
/// <param name="Flags">Every flag id a <c>while:</c> can name: the open ruleset's bare, a dependency's qualified.</param>
/// <param name="Stats">The open ruleset's stat ids, in document order.</param>
/// <param name="Buckets">The open ruleset's bucket ids, in document order.</param>
/// <param name="Groups">The <c>group:</c> names the open ruleset's highlights use, each once.</param>
public sealed record FaceVocabulary(
    IReadOnlyList<string> Defines,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> Stats,
    IReadOnlyList<string> Buckets,
    IReadOnlyList<string> Groups)
{
    /// <summary>Nothing to offer: a face built outside a composition.</summary>
    public static FaceVocabulary Empty { get; } = new([], [], [], [], []);

    /// <summary>The expression that bakes the suicide out of a view, as the catalog spells it.</summary>
    private const string NotASuicide = "event.Attacker != event.UserId";

    /// <summary>
    ///     The catalog's triggers, in the order a picker lists them: the views by their event so the views of
    ///     one event sit together, then every event under <c>raw.</c>.
    /// </summary>
    public static IReadOnlyList<TriggerOption> CatalogTriggers { get; } = BuildCatalogTriggers();

    /// <summary>The <c>player.*</c> values the catalog names, context flags and entity reads alike, which a sum or a capture can take.</summary>
    public static IReadOnlyList<string> PlayerContextPaths { get; } =
    [
        .. EmbeddedCatalog.Root.Contexts.Select(c => c.V2Name)
            .Concat(EmbeddedCatalog.Root.Providers.Select(p => p.V2Name))
            .Where(n => n is not null && n.StartsWith("player.", StringComparison.Ordinal))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>The catalog view named <paramref name="view" />, or null.</summary>
    public static CatalogView? ViewOf(string? view) =>
        view is null ? null : EmbeddedCatalog.Root.Views.FirstOrDefault(v => string.Equals(v.Name, view, StringComparison.Ordinal));

    /// <summary>
    ///     The values a stat on <paramref name="view" /> can sum or capture, as paths: the enrichments its
    ///     facets read, then its event fields, each once.
    /// </summary>
    public static IReadOnlyList<string> ValuePathsFor(string? view)
    {
        CatalogView? scope = ViewOf(view);
        if (scope is null)
        {
            return [];
        }

        List<string> paths = [];
        foreach (CatalogFacet facet in scope.Facets)
        {
            if (!string.IsNullOrEmpty(facet.Enrichment) && !paths.Contains(facet.Enrichment, StringComparer.Ordinal))
            {
                paths.Add(facet.Enrichment);
            }
        }

        foreach (CatalogFacet facet in scope.Facets)
        {
            if (!string.IsNullOrEmpty(facet.Field))
            {
                string path = "event." + facet.Field;
                if (!paths.Contains(path, StringComparer.Ordinal))
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    /// <summary>Whether <paramref name="view" /> bakes the suicide out, so its face can say the engine does.</summary>
    public static bool BakesOutSuicide(string? view) =>
        ViewOf(view)?.Baked.Any(b => string.Equals(b.Trim(), NotASuicide, StringComparison.Ordinal)) == true;

    /// <summary>The vocabulary of a composition, read from its documents and its graph.</summary>
    /// <param name="composed">The composition.</param>
    /// <param name="openRulesetId">The ruleset being edited; null makes every list empty but the flags.</param>
    public static FaceVocabulary Of(RulesetCorpus.Composed composed, string? openRulesetId)
    {
        ArgumentNullException.ThrowIfNull(composed);
        List<string> defines = [];
        List<string> groups = [];
        if (openRulesetId is not null && composed.Documents.TryGetValue(openRulesetId, out RulesetDoc? doc))
        {
            defines.AddRange(doc.Defines.Where(d => d.Body is TriggerDefineBody).Select(d => d.Name));
            foreach (HighlightDef highlight in doc.Highlights)
            {
                if (!string.IsNullOrWhiteSpace(highlight.Group) && !groups.Contains(highlight.Group, StringComparer.Ordinal))
                {
                    groups.Add(highlight.Group);
                }
            }
        }

        List<string> flags = [];
        List<string> stats = [];
        List<string> buckets = [];
        foreach (RulesetDocumentNode node in composed.Graph.Nodes)
        {
            bool own = string.Equals(node.Key.Ruleset, openRulesetId, StringComparison.Ordinal);
            if (node.Kind == RuleNodeKind.Flag)
            {
                flags.Add(own ? node.Id : node.Key.ToString());
            }

            if (!own || node.IsHighlight || node.IsDefine)
            {
                continue;
            }

            stats.Add(node.Id);
            if (node.Kind == RuleNodeKind.Bucket)
            {
                buckets.Add(node.Id);
            }
        }

        return new FaceVocabulary(defines, flags, stats, buckets, groups);
    }

    /// <summary>
    ///     The triggers a stat of this ruleset can fire on: the catalog's, then the document's defines. A
    ///     current spelling none of them has (a cadence, a dependency's define) is listed first so it stays
    ///     visible and selected.
    /// </summary>
    public IReadOnlyList<TriggerOption> TriggersFor(string? current)
    {
        List<TriggerOption> options = [];
        if (!string.IsNullOrEmpty(current) && CatalogTriggers.All(t => t.Written != current) && !Defines.Contains(current, StringComparer.Ordinal))
        {
            options.Add(new TriggerOption(current, "written", []));
        }

        options.AddRange(CatalogTriggers);
        options.AddRange(Defines.Select(d => new TriggerOption(d, "define", [])));
        return options;
    }

    /// <summary>
    ///     The triggers a define can fire on: the catalog's alone, since the resolver reads a define's <c>on:</c>
    ///     as a view, a raw or a cadence, never as another define. A current spelling outside the catalog is
    ///     listed first so it stays visible and selected.
    /// </summary>
    public static IReadOnlyList<TriggerOption> TriggersForDefine(string? current)
    {
        List<TriggerOption> options = [];
        if (!string.IsNullOrEmpty(current) && CatalogTriggers.All(t => t.Written != current))
        {
            options.Add(new TriggerOption(current, "written", []));
        }

        options.AddRange(CatalogTriggers);
        return options;
    }

    private static List<TriggerOption> BuildCatalogTriggers()
    {
        List<TriggerOption> options = [];
        IReadOnlyList<CatalogView> views = EmbeddedCatalog.Root.Views;
        foreach (IGrouping<string, CatalogView> byEvent in views.GroupBy(v => v.Event, StringComparer.Ordinal))
        {
            foreach (CatalogView view in byEvent)
            {
                options.Add(new TriggerOption(view.Name, "view", [byEvent.Key]));
            }
        }

        foreach (CatalogEvent eventEntry in EmbeddedCatalog.Root.Events)
        {
            options.Add(new TriggerOption("raw." + eventEntry.Name, "raw", [eventEntry.Name]));
        }

        return options;
    }
}
