#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     The identity of a document-derived node: the ruleset that declares it and the id it is
///     declared under.
///     <para>
///         An id is unique inside one ruleset's shared stat/highlight namespace but NOT across
///         rulesets: the shipped corpus declares <c>enemy_kills_round</c> in
///         <c>highlights_multikill</c> and again in <c>kast</c>. The ruleset is therefore part of
///         the key rather than a decoration on it, which is the same lesson the Analysis graph's
///         node key records.
///     </para>
/// </summary>
/// <param name="Ruleset">The declaring ruleset's id (the <c>ruleset:</c> key).</param>
/// <param name="Id">The <c>stats:</c> or <c>highlights:</c> key.</param>
public readonly record struct RulesetDocumentNodeKey(string Ruleset, string Id)
{
    /// <summary>What a define node's id starts with; a stat id cannot hold a dot, so the two never collide.</summary>
    public const string DefinePrefix = "define.";

    /// <summary>What a <c>show:</c> node's id starts with: a table's name, or <c>scoreboard</c>.</summary>
    public const string ShowPrefix = "show.";

    /// <summary>The id of the one node that stands for the document's <c>params:</c> block.</summary>
    public const string ParamsId = "params.";

    /// <summary>The show node id of the scoreboard; a table is keyed by its own name.</summary>
    public const string ScoreboardName = "scoreboard";

    /// <summary>Whether this key names a trigger-bodied <c>define:</c> rather than a stat or a highlight.</summary>
    public bool IsDefine => Id.StartsWith(DefinePrefix, StringComparison.Ordinal);

    /// <summary>Whether this key names a <c>show:</c> table or the scoreboard.</summary>
    public bool IsShow => Id.StartsWith(ShowPrefix, StringComparison.Ordinal);

    /// <summary>Whether this key names the document's <c>params:</c> block.</summary>
    public bool IsParams => Id == ParamsId;

    /// <summary>Whether this key is a show node for the scoreboard rather than a table.</summary>
    public bool IsScoreboard => IsShow && Name == ScoreboardName;

    /// <summary>The define's name, the table's name or <c>params</c> for those keys; the id itself otherwise.</summary>
    public string Name => IsDefine ? Id[DefinePrefix.Length..] : IsShow ? Id[ShowPrefix.Length..] : IsParams ? "params" : Id;

    /// <summary>The key of the define <paramref name="name" /> declared by <paramref name="ruleset" />.</summary>
    public static RulesetDocumentNodeKey Define(string ruleset, string name) => new(ruleset, DefinePrefix + name);

    /// <summary>The key of the <c>show:</c> table <paramref name="name" /> of <paramref name="ruleset" />.</summary>
    public static RulesetDocumentNodeKey Show(string ruleset, string name) => new(ruleset, ShowPrefix + name);

    /// <summary>The key of the scoreboard node of <paramref name="ruleset" />.</summary>
    public static RulesetDocumentNodeKey Scoreboard(string ruleset) => Show(ruleset, ScoreboardName);

    /// <summary>The key of the <c>params:</c> node of <paramref name="ruleset" />.</summary>
    public static RulesetDocumentNodeKey Params(string ruleset) => new(ruleset, ParamsId);

    /// <summary>Formats as the qualified read an author would write from another ruleset.</summary>
    /// <returns>The <c>ruleset.id</c> form.</returns>
    public override string ToString() => Ruleset + "." + Id;
}

/// <summary>
///     One node of the document-derived graph: exactly one <c>stats:</c> or <c>highlights:</c> entry,
///     with the back-reference to the YAML that <c>AuthoringGraphNode</c> does not carry.
///     <para>
///         <b>Every node here was written by a human in a file.</b> That is the whole difference from
///         the authoring graph, which draws the engine's scaffolding (the root, <c>MatchLive</c>,
///         <c>RoundActive</c>, every <c>enrich.*</c>, a <c>tally:</c>'s threshold counters) and has
///         nowhere to send an edit for any of it. There is no projected node to hide, so
///         design.md §6.7's authorable-versus-projected problem does not arise;
///         <see cref="IsAuthorable" /> only separates the open document from the rulesets it
///         <c>use:</c>s.
///     </para>
/// </summary>
/// <param name="Key">Identity, stable across renders and across a re-parse of the same document.</param>
/// <param name="Kind">
///     The engine's own node kind. A highlight is <see cref="RuleNodeKind.Highlight" />; a stat
///     carries whichever of <c>flag</c>, <c>count</c>, <c>sum</c>, <c>capture</c>, <c>compute</c>,
///     <c>tally</c>, <c>streak</c>, <c>bucket</c> or <c>rate</c> it resolved to.
/// </param>
/// <param name="ValueType">The stat's value type, or <c>bool</c> for a highlight's <c>when:</c>.</param>
/// <param name="Position">
///     Where the entry starts in its file, which is what a node-to-caret jump moves to.
///     <b>Never <see cref="SourcePosition.None" /> for a node built from a loaded document</b>: the
///     resolver copies the position off the model record, and the model record takes it from the
///     YAML node.
/// </param>
/// <param name="Label">The author's display name (<c>label:</c>, or a highlight's <c>title:</c>), or <c>null</c>.</param>
/// <param name="Events">
///     The trigger's concrete wire events, for the header chips design.md §6.7 puts <c>on:</c> and
///     the gates in. Empty for a <c>compute:</c>, an expression <c>flag:</c>, and a count that rides
///     a sibling flag's rising edge, none of which fire on an event of their own.
/// </param>
/// <param name="View">
///     The catalog view the trigger resolved to, as the author named it (<c>kill</c>, not
///     <c>player_death</c>), or null for a node that fires on no view.
/// </param>
/// <param name="CrossRulesetReads">
///     Stat references that resolve outside this node's ruleset, as written. They are NOT edges,
///     because the node they name belongs to another document, and they are the one thing about a
///     node the canvas would otherwise lose without trace.
/// </param>
/// <param name="IsAuthorable">
///     Whether the open document declares this node, and therefore whether an edit can be addressed
///     to it. False for a node contributed by a <c>use:</c> dependency, which is drawn and is read-only.
/// </param>
public sealed record RulesetDocumentNode(
    RulesetDocumentNodeKey Key,
    RuleNodeKind Kind,
    RulesType ValueType,
    SourcePosition Position,
    string? Label,
    IReadOnlyList<string> Events,
    string? View,
    IReadOnlyList<string> CrossRulesetReads,
    bool IsAuthorable)
{
    /// <summary>The <c>stats:</c> or <c>highlights:</c> key, without its ruleset.</summary>
    public string Id => Key.Id;

    /// <summary>Which of the two document sections declares this node.</summary>
    public bool IsHighlight => Kind == RuleNodeKind.Highlight;

    /// <summary>A trigger-bodied <c>define:</c> of the open document, drawn as the node its stats fire through.</summary>
    public bool IsDefine => Key.IsDefine;
}

/// <summary>
///     One value reference between two nodes of the same ruleset, running the way the value does:
///     from the node that is read to the node that reads it.
///     <para>
///         These are the wires design.md §6.7 argues the canvas should draw and does not. They come
///         from <see cref="CheckedStat.DeclaredReads" />, which the resolver precomputes from every
///         reference in the node's checked ASTs, so a <c>compute:</c> formula's operands are edges
///         here although the engine emits no <c>GraphEdgeDescriptor</c> for them and the authoring
///         graph therefore draws all 18 of the corpus's computes with no edge at all.
///     </para>
/// </summary>
/// <param name="Source">The node that is read.</param>
/// <param name="Target">The node whose <c>compute:</c>, <c>when:</c>, <c>where:</c> or <c>while:</c> reads it.</param>
/// <param name="Reference">The read path as the author wrote it, which is the edge's label.</param>
public sealed record RulesetDocumentEdge(
    RulesetDocumentNodeKey Source,
    RulesetDocumentNodeKey Target,
    string Reference);

/// <summary>
///     The node/edge model the Rule Workbench canvas is built from: a pure, static projection of the
///     composed <see cref="CheckedRuleset" /> set, with no layout, no view models and no Avalonia.
///     <para>
///         <b>This replaces <c>AuthoringGraph</c> as the canvas's source, and the reason is
///         measured.</b> Over <c>rules/</c> the authoring graph puts 137 of 240 edges on four
///         lifecycle hub nodes, leaves 108 of 230 nodes with out-degree 0, and draws every
///         <c>compute:</c> stat unconnected, which in <c>aim_rating</c> means the 17 isolated nodes
///         ARE the 17 board columns the file exists to produce. The checked model carries the
///         author's own relationships instead, and a <see cref="SourcePosition" /> on every node.
///     </para>
///     <para>
///         <b>One dependency the checked model cannot express.</b> A <c>count:</c> that triggers on a
///         sibling <c>flag:</c> resolves to a flag source the resolver keeps to itself: the stat
///         comes back with empty <see cref="CheckedStat.ConcreteEvents" /> and nothing naming the
///         flag, so that wire is not drawn. Recovering it needs an engine change, not a parse of the
///         YAML behind the model's back.
///     </para>
/// </summary>
/// <param name="Nodes">Every declared stat and highlight of every composed ruleset, in document order.</param>
/// <param name="Edges">Every resolving sibling read, in the order the nodes declare them.</param>
public sealed record RulesetDocumentGraph(
    IReadOnlyList<RulesetDocumentNode> Nodes,
    IReadOnlyList<RulesetDocumentEdge> Edges)
{
    /// <summary>The answer for no documents at all.</summary>
    public static RulesetDocumentGraph Empty { get; } = new([], []);

    /// <summary>
    ///     The declared entries of <paramref name="documents" /> as nodes with no edges: what the canvas draws
    ///     when the composition failed and there is no checked model to project. Each node carries what the
    ///     document itself says: the kind, the position, the label, the trigger as written, and the view when
    ///     the catalog names one by that spelling. The value type is unknown, and a read is not resolved.
    /// </summary>
    public static RulesetDocumentGraph FromDocuments(IReadOnlyList<RulesetDoc> documents, string? openRulesetId)
    {
        ArgumentNullException.ThrowIfNull(documents);
        HashSet<string> views = [.. EmbeddedCatalog.Root.Views.Select(v => v.Name)];
        List<RulesetDocumentNode> nodes = [];
        List<RulesetDocumentEdge> edges = [];
        foreach (RulesetDoc doc in documents)
        {
            bool authorable = string.Equals(doc.Id, openRulesetId, StringComparison.Ordinal);
            if (authorable)
            {
                AddDefines(doc, nodes, edges, (_, spelling) => (views.Contains(spelling) ? spelling : null, [spelling]));
            }

            foreach (StatDef stat in doc.Stats)
            {
                string? written = TriggerSpelling(stat);
                RuleNodeKind kind = Enum.TryParse(stat.Kind.ToString(), out RuleNodeKind parsed) ? parsed : RuleNodeKind.None;
                nodes.Add(new RulesetDocumentNode(new RulesetDocumentNodeKey(doc.Id, stat.Id), kind, default,
                    stat.Position, stat.Label, written is null ? [] : [written],
                    written is not null && views.Contains(written) ? written : null, [], authorable));
            }

            foreach (HighlightDef highlight in doc.Highlights)
            {
                nodes.Add(new RulesetDocumentNode(new RulesetDocumentNodeKey(doc.Id, highlight.Id), RuleNodeKind.Highlight,
                    RulesType.Bool, highlight.Position, highlight.Title, [], null, [], authorable));
            }
        }

        return new RulesetDocumentGraph(nodes, edges);
    }

    /// <summary>
    ///     The trigger of <paramref name="stat" /> as the author spelled it: the <c>on:</c> reference, or the kind
    ///     argument of a kind that names its event there; null for a kind that fires on nothing.
    /// </summary>
    public static string? TriggerSpelling(StatDef stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        // Only these kinds name their event in the kind argument; the others carry it under on:.
        return stat.Trigger?.On is { } on
            ? Spell(on)
            : stat.Kind is StatKind.Count or StatKind.Streak or StatKind.Bucket or StatKind.Burst
                ? stat.KindArg?.Trim()
                : null;
    }

    /// <summary>The trigger-bodied defines of <paramref name="doc" />, in document order; the other bodies are not nodes.</summary>
    public static IEnumerable<(DefineDef Define, TriggerDef Trigger)> TriggerDefines(RulesetDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        foreach (DefineDef define in doc.Defines)
        {
            if (define.Body is TriggerDefineBody body)
            {
                yield return (define, body.Trigger);
            }
        }
    }

    /// <summary>The trigger-bodied define of <paramref name="doc" /> named <paramref name="name" />, or null.</summary>
    public static (DefineDef Define, TriggerDef Trigger)? TriggerDefine(RulesetDoc doc, string name)
    {
        foreach ((DefineDef define, TriggerDef trigger) in TriggerDefines(doc))
        {
            if (string.Equals(define.Name, name, StringComparison.Ordinal))
            {
                return (define, trigger);
            }
        }

        return null;
    }

    /// <summary>The ids of the stats of <paramref name="doc" /> whose trigger names the define <paramref name="name" />, in document order.</summary>
    public static IReadOnlyList<string> StatsFiringOn(RulesetDoc doc, string name)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return [.. doc.Stats.Where(s => string.Equals(TriggerSpelling(s), name, StringComparison.Ordinal)).Select(s => s.Id)];
    }

    // One node per trigger-bodied define of the open document, keyed define.<name>, wired to every stat that
    // fires on it, ahead of the stats as the document orders them. The resolver scopes defines to their own
    // document, so a dependency's are not drawn.
    private static void AddDefines(RulesetDoc doc, List<RulesetDocumentNode> nodes, List<RulesetDocumentEdge> edges,
        Func<string, string, (string? View, IReadOnlyList<string> Events)> resolve)
    {
        foreach ((DefineDef define, TriggerDef trigger) in TriggerDefines(doc))
        {
            RulesetDocumentNodeKey key = RulesetDocumentNodeKey.Define(doc.Id, define.Name);
            string? spelling = trigger.On is { } on ? Spell(on) : null;
            (string? view, IReadOnlyList<string> events) = spelling is null ? (null, []) : resolve(define.Name, spelling);
            nodes.Add(new RulesetDocumentNode(key, RuleNodeKind.None, default, define.Position, null, events, view, [], true));
            foreach (string stat in StatsFiringOn(doc, define.Name))
            {
                edges.Add(new RulesetDocumentEdge(key, new RulesetDocumentNodeKey(doc.Id, stat), "fires"));
            }
        }
    }

    /// <summary>
    ///     Projects the composed rulesets into nodes and edges.
    /// </summary>
    /// <param name="rulesets">
    ///     The composition's <c>Rulesets</c>, which is the open document plus its transitive
    ///     <c>use:</c> dependencies as the Workbench composes them.
    /// </param>
    /// <param name="openRulesetId">
    ///     The id of the document being edited, whose nodes are authorable. <c>null</c> marks nothing
    ///     authorable, which is the honest answer when no file is open.
    /// </param>
    /// <param name="documents">
    ///     The documents composed, for the open one's trigger-bodied <c>define:</c> entries, which the checked
    ///     model folds into the stats that fire on them; null draws no define nodes.
    /// </param>
    /// <returns>The model.</returns>
    public static RulesetDocumentGraph Build(IReadOnlyList<CheckedRuleset> rulesets, string? openRulesetId,
        IReadOnlyList<RulesetDoc>? documents = null)
    {
        ArgumentNullException.ThrowIfNull(rulesets);

        List<RulesetDocumentNode> nodes = [];
        List<RulesetDocumentEdge> edges = [];

        foreach (CheckedRuleset ruleset in rulesets)
        {
            string rulesetId = ruleset.Id.Id;
            bool authorable = string.Equals(rulesetId, openRulesetId, StringComparison.Ordinal);
            Dictionary<string, RulesetDocumentNodeKey> index = IndexOf(ruleset);
            if (authorable && documents?.FirstOrDefault(d => d.Id == rulesetId) is { } doc)
            {
                // The define's view and events are what the resolver gave a stat firing on it; the catalog
                // answers for a define nothing fires on yet.
                AddDefines(doc, nodes, edges, (name, spelling) =>
                {
                    CheckedStat? firing = StatsFiringOn(doc, name)
                        .Select(id => ruleset.Stats.FirstOrDefault(s => s.StatId == id))
                        .FirstOrDefault(s => s is not null);
                    if (firing is not null)
                    {
                        return (firing.ResolvedView, firing.ConcreteEvents);
                    }

                    CatalogView? view = EmbeddedCatalog.Root.Views.FirstOrDefault(v => v.Name == spelling);
                    return (view?.Name, view is null ? [] : [view.Event]);
                });
            }

            foreach (CheckedStat stat in ruleset.Stats)
            {
                RulesetDocumentNodeKey key = new(rulesetId, stat.StatId);
                List<string> foreign = [];
                Collect(key, stat.DeclaredReads, index,
                    [stat.TriggerCondition, stat.ValueSelector, stat.WhileGate], edges, foreign);

                nodes.Add(new RulesetDocumentNode(key, stat.Kind, stat.ValueType, stat.Position, stat.Label,
                    stat.ConcreteEvents, stat.ResolvedView, foreign, authorable));
            }

            foreach (CheckedHighlight highlight in ruleset.Highlights)
            {
                RulesetDocumentNodeKey key = new(rulesetId, highlight.HighlightId);
                List<string> foreign = [];
                Collect(key, highlight.DeclaredReads, index, [highlight.When], edges, foreign);

                // A highlight fires on its when: conjunction rather than on a trigger of its own, so
                // it has no events to chip. Its title: is the closest thing it has to a label.
                nodes.Add(new RulesetDocumentNode(key, RuleNodeKind.Highlight, highlight.When.ResultType,
                    highlight.Position, highlight.Title, [], null, foreign, authorable));
            }
        }

        return new RulesetDocumentGraph(nodes, edges);
    }

    /// <summary>The <c>on:</c> reference as the author spelled it, sigil and all.</summary>
    private static string Spell(TriggerRef on) => on.Kind switch
    {
        TriggerRefKind.Raw => "raw." + on.Name,
        TriggerRefKind.Net => "net." + on.Name,
        TriggerRefKind.Cadence => "cadence." + on.Name,
        TriggerRefKind.This => "this",
        _ => on.Name
    };

    /// <summary>
    ///     The ruleset's own id namespace, as <c>StatReferenceCycleDetector</c> builds it: every stat
    ///     id, every highlight id, and every highlight's automatic <c>&lt;id&gt;.count</c>, which
    ///     resolves to the highlight because the canvas draws no separate node for it.
    /// </summary>
    private static Dictionary<string, RulesetDocumentNodeKey> IndexOf(CheckedRuleset ruleset)
    {
        Dictionary<string, RulesetDocumentNodeKey> index = new(StringComparer.Ordinal);

        foreach (CheckedStat stat in ruleset.Stats)
        {
            index.TryAdd(stat.StatId, new RulesetDocumentNodeKey(ruleset.Id.Id, stat.StatId));
        }

        foreach (CheckedHighlight highlight in ruleset.Highlights)
        {
            RulesetDocumentNodeKey key = new(ruleset.Id.Id, highlight.HighlightId);
            index.TryAdd(highlight.HighlightId, key);
            index.TryAdd(highlight.CountNodeId, key);
        }

        return index;
    }

    /// <summary>
    ///     Turns one node's declared reads into its edges and its cross-ruleset marks, classifying
    ///     each read against the checked expressions it came from.
    /// </summary>
    private static void Collect(RulesetDocumentNodeKey owner, IReadOnlyList<string> declaredReads,
        Dictionary<string, RulesetDocumentNodeKey> index, ReadOnlySpan<CheckedExpression?> expressions,
        List<RulesetDocumentEdge> edges, List<string> crossRuleset)
    {
        Dictionary<string, ResolvedReference> resolutions = new(StringComparer.Ordinal);
        foreach (CheckedExpression? expression in expressions)
        {
            if (expression is null)
            {
                continue;
            }

            foreach (ResolvedReference reference in expression.References)
            {
                resolutions.TryAdd(reference.Path, reference);
            }
        }

        foreach (string read in declaredReads)
        {
            if (resolutions.TryGetValue(read, out ResolvedReference? resolution))
            {
                // The engine already decided what this path names, so nothing is re-derived from the
                // text. A non-stat read is a catalog, event or param read, which design.md §6.7
                // renders as a header chip rather than as a wire.
                if (resolution is not { IsStatReference: true, StatPath: { } statPath })
                {
                    continue;
                }

                if (TryResolve(statPath, owner.Ruleset, index, out RulesetDocumentNodeKey target))
                {
                    Connect(owner, target, read, edges);
                }
                else if (!crossRuleset.Contains(statPath, StringComparer.Ordinal))
                {
                    crossRuleset.Add(statPath);
                }

                continue;
            }

            // A read the three ASTs do not carry: a bucket: key expression is collected into
            // DeclaredReads and then kept only as its rendered key part. Resolve it structurally
            // against the id namespace, and stay silent when it does not hit, since a miss here is
            // an ordinary catalog read and not a reference to another document.
            if (TryResolvePrefix(read, owner.Ruleset, index, out RulesetDocumentNodeKey inferred))
            {
                Connect(owner, inferred, read, edges);
            }
        }
    }

    /// <summary>Adds the edge unless it would be a self-loop, which a canvas draws through its own box.</summary>
    private static void Connect(RulesetDocumentNodeKey owner, RulesetDocumentNodeKey target, string read,
        List<RulesetDocumentEdge> edges)
    {
        if (!target.Equals(owner))
        {
            edges.Add(new RulesetDocumentEdge(target, owner, read));
        }
    }

    /// <summary>
    ///     Resolves a stat path against one ruleset's id namespace, accepting the self-qualified
    ///     <c>ruleset.id</c> spelling an author may write for a sibling.
    /// </summary>
    private static bool TryResolve(string statPath, string rulesetId,
        Dictionary<string, RulesetDocumentNodeKey> index, out RulesetDocumentNodeKey key)
    {
        if (index.TryGetValue(statPath, out key))
        {
            return true;
        }

        string qualifier = rulesetId + ".";
        return statPath.StartsWith(qualifier, StringComparison.Ordinal)
               && index.TryGetValue(statPath[qualifier.Length..], out key);
    }

    /// <summary>
    ///     Resolves the longest dotted prefix of a read that names a local node, which is how a
    ///     pseudo-member such as <c>&lt;id&gt;.count</c> or <c>&lt;id&gt;.set</c> hangs off its stat.
    /// </summary>
    private static bool TryResolvePrefix(string read, string rulesetId,
        Dictionary<string, RulesetDocumentNodeKey> index, out RulesetDocumentNodeKey key)
    {
        for (int end = read.Length; end > 0; end = read.LastIndexOf('.', end - 1))
        {
            if (TryResolve(read[..end], rulesetId, index, out key))
            {
                return true;
            }
        }

        key = default;
        return false;
    }
}
