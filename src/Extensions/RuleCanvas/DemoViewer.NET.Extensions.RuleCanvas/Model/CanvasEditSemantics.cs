#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>One gate a ruleset can name in <c>while:</c>, under the engine's name and the ruleset's path.</summary>
/// <param name="EngineName">The context flag as the engine names it (<c>MatchLive</c>), which is the chain node's id.</param>
/// <param name="Path">The v2 path the ruleset writes (<c>match.live</c>).</param>
/// <param name="Hint">One line on when the gate is open, for the gallery.</param>
public sealed record GatePath(string EngineName, string Path, string Hint);

/// <summary>What still names one entry once it is deleted: the siblings that read it and the <c>show:</c> entries that surface it.</summary>
/// <param name="Id">The entry.</param>
/// <param name="Readers">The ids of the open document's entries whose <c>compute:</c>, <c>when:</c>, <c>where:</c> or <c>while:</c> reads it.</param>
/// <param name="Scoreboard">Whether a <c>show: scoreboard:</c> entry names it.</param>
/// <param name="Tables">The <c>show: tables:</c> with a column that names it.</param>
public sealed record NodeDependants(string Id, IReadOnlyList<string> Readers, bool Scoreboard, IReadOnlyList<string> Tables)
{
    public bool Any => Readers.Count > 0 || Scoreboard || Tables.Count > 0;

    /// <summary>The refusal: <c>round_kills is read by round_kast and shown in table player_round</c>.</summary>
    public string Describe()
    {
        List<string> parts = [];
        if (Readers.Count > 0)
        {
            parts.Add("read by " + string.Join(", ", Readers));
        }

        List<string> shown = [];
        if (Scoreboard)
        {
            shown.Add("on the scoreboard");
        }

        shown.AddRange(Tables.Select(t => "in table " + t));
        if (shown.Count > 0)
        {
            parts.Add("shown " + string.Join(" and ", shown));
        }

        return parts.Count == 0 ? Id + " has no dependants" : Id + " is " + string.Join(" and ", parts);
    }
}

/// <summary>
///     Every write decision the canvas makes, as data: a gesture or a face edit in, an <see cref="EditRequest" />
///     out, which the session applies. Nothing here touches a file or a view; the catalog is read for the
///     view a trigger names and the facet an enrichment is reached through.
/// </summary>
public static class CanvasEditSemantics
{
    /// <summary>The gates, in the order the gallery and the picker offer them.</summary>
    public static IReadOnlyList<GatePath> GatePaths { get; } =
    [
        new("RoundActive", "round.active", "while the round is live, after freeze time and before it is decided"),
        new("MatchLive", "match.live", "while the match is live, between its start and its end"),
        new("Alive", "player.alive", "while this player is alive"),
        new("NoDeathsYet", "round.no_deaths_yet", "until the round's first death"),
        new("BombWasPlanted", "round.bomb.was_planted", "after the bomb is planted this round"),
        new("Traded", "player.traded", "once this player has been traded this round"),
        new("Survived", "player.survived", "once this player has survived the round")
    ];

    /// <summary>The catalog's view names, in its order: what an added count stat fires on.</summary>
    public static IReadOnlyList<string> ViewNames { get; } = [.. EmbeddedCatalog.Root.Views.Select(v => v.Name)];

    /// <summary>The v2 path for a context flag's engine name, or null when a ruleset cannot name it.</summary>
    public static string? GatePathOf(string engineName) => GatePaths.FirstOrDefault(g => g.EngineName == engineName)?.Path;

    /// <summary>The views the catalog fires on <paramref name="eventName" />, in catalog order.</summary>
    public static IReadOnlyList<string> ViewsFiredBy(string eventName) =>
        [.. EmbeddedCatalog.Root.Views.Where(v => v.Event == eventName).Select(v => v.Name)];

    /// <summary>The facet of <paramref name="view" /> that reads <paramref name="enrichment" />, or null.</summary>
    public static CatalogFacet? FacetReading(string? view, string enrichment) =>
        EmbeddedCatalog.Root.Views.FirstOrDefault(v => v.Name == view)?.Facets.FirstOrDefault(f => f.Enrichment == enrichment);

    /// <summary>The face kind for an engine node of the chain: <c>event</c>, <c>enrich</c>, <c>context</c>, <c>entity</c>.</summary>
    public static string ScaffoldKind(ChainNodeKind kind) => kind switch
    {
        ChainNodeKind.Event => "event",
        ChainNodeKind.Enrichment => "enrich",
        ChainNodeKind.Context => "context",
        ChainNodeKind.Entity => "entity",
        ChainNodeKind.External => "external",
        _ => "engine"
    };

    /// <summary>
    ///     <paramref name="wanted" /> when no node has it, else the first <c>wanted_2</c>, <c>wanted_3</c> that is free.
    /// </summary>
    public static string FreshId(string wanted, IEnumerable<string> taken)
    {
        HashSet<string> ids = new(taken, StringComparer.Ordinal);
        string id = wanted;
        for (int n = 2; ids.Contains(id); n++)
        {
            id = $"{wanted}_{n}";
        }

        return id;
    }

    /// <summary>The id a placed gallery item asks for: a prefab or a highlight keeps its name, a kind, a view or the define is <c>new_</c> it.</summary>
    public static string PlacedIdFor(GalleryItem item) =>
        item.Kind is GalleryItemKind.Prefab or GalleryItemKind.Highlight ? item.Name : "new_" + item.Name;

    /// <summary>The node key a placed item will draw under: a define's is prefixed, every other entry's is its id.</summary>
    public static RulesetDocumentNodeKey PlacedKeyFor(GalleryItem item, string ruleset, string id) =>
        item.Kind == GalleryItemKind.Define ? RulesetDocumentNodeKey.Define(ruleset, id) : new RulesetDocumentNodeKey(ruleset, id);

    /// <summary>Adds a count stat on <paramref name="view" /> as <paramref name="id" />, at the end of <c>stats:</c>.</summary>
    public static EditRequest AddStat(string view, string id) =>
        EditRequest.Of(text => RulesetEdits.AddStat(text, $"{id}:\n  count: {view}\n  per: round\n"), "add " + id);

    /// <summary>
    ///     A gallery item dropped on bare canvas: a stat kind, a view, a prefab or a highlight becomes a new entry
    ///     under <paramref name="id" />; the define becomes a <c>define:</c> entry, its block opened above
    ///     <c>stats:</c> when the document has none. A gate has nothing to attach to there and is refused.
    /// </summary>
    public static EditRequest PlaceItem(GalleryItem item, string id)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == GalleryItemKind.Gate || item.Body is null)
        {
            return EditRequest.Refuse($"Drop {item.Name} onto a stat to gate it.");
        }

        if (item.Kind == GalleryItemKind.Highlight)
        {
            return EditRequest.Of(text => RulesetEdits.AddHighlight(text, id, item.Body), "add " + id);
        }

        if (item.Kind == GalleryItemKind.Define)
        {
            return EditRequest.Of(text => RulesetEdits.AddDefine(text, id, item.Body), "add define " + id);
        }

        string body = string.Join('\n', item.Body.Split('\n').Select(l => "  " + l));
        return EditRequest.Of(text => RulesetEdits.AddStat(text, $"{id}:\n{body}\n"), "add " + id);
    }

    /// <summary>
    ///     A gallery item dropped on a node: a view retargets the trigger (the kind's own argument for a count,
    ///     <c>on:</c> for a stat whose argument is a value, <c>on:</c> for a define), a gate becomes its
    ///     <c>while:</c>, and the define puts a new define named <paramref name="freshId" />, firing on the
    ///     stat's own trigger, in front of the stat, which then fires on it.
    /// </summary>
    public static EditRequest DropOnNode(GalleryItem item, CanvasNodeViewModel node, string? freshId = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsAuthorable || node.IsHighlight)
        {
            return EditRequest.Refuse($"{item.Name} can only be dropped on a define or a stat of your own ruleset.");
        }

        return item.Kind switch
        {
            GalleryItemKind.Gate => Gate(node, item.Name),
            GalleryItemKind.View when TriggerKeyFor(node) is { } key => Retarget(node, key, item.Name),
            GalleryItemKind.View => EditRequest.Refuse($"A {node.Kind} fires on no view; {item.Name} goes on a stat that has a trigger."),
            GalleryItemKind.Define when node.IsDefine => EditRequest.Refuse("A define cannot fire on a define; drop it on a stat or on the canvas."),
            GalleryItemKind.Define when freshId is not null && TriggerKeyFor(node) is { } key => InterposeDefine(node, key, freshId),
            GalleryItemKind.Define => EditRequest.Refuse($"A {node.Kind} fires on no view, so no define can stand in front of it."),
            _ => EditRequest.Refuse($"Drop {item.Title} on the canvas to add it as a stat.")
        };
    }

    // The gate goes on the node's own while:. The resolver reads a define's while: and a firing stat's as one
    // compound gate and refuses it, so a gate on either side is refused while the other side has one.
    private static EditRequest Gate(CanvasNodeViewModel node, string gate)
    {
        string id = node.Title;
        if (node.IsDefine)
        {
            return EditRequest.Of(text =>
            {
                IReadOnlyList<string> gated = [.. RulesetEdits.StatsFiringOn(text, id).Where(s => RulesetEdits.Field(text, s, "while") is not null)];
                return gated.Count > 0
                    ? throw new InvalidOperationException($"{string.Join(", ", gated)} already carry a while: of their own, and the engine takes one gate per trigger")
                    : RulesetEdits.SetDefineField(text, id, "while", gate);
            }, $"gate define {id} on {gate}");
        }

        return EditRequest.Of(text =>
        {
            string? define = RulesetEdits.DefineNames(text).FirstOrDefault(d => RulesetEdits.StatsFiringOn(text, d).Contains(id, StringComparer.Ordinal));
            return define is not null && RulesetEdits.DefineField(text, define, "while") is { } own
                ? throw new InvalidOperationException($"{id} fires on {define}, which is already gated on {own}; the engine takes one gate per trigger")
                : RulesetEdits.SetField(text, id, "while", gate);
        }, $"gate {id} on {gate}");
    }

    // A new define takes the stat's trigger spelling as its on:, and the stat fires on the define: nothing changes
    // in what runs, and the condition now has a node to grow on. A stat already firing on a define keeps it.
    private static EditRequest InterposeDefine(CanvasNodeViewModel node, string key, string freshId)
    {
        if (node.Face is StatFace { Define: not null } face)
        {
            return EditRequest.Refuse($"{node.Id} already fires on {face.Define!.Name}; edit that define instead.");
        }

        string spelling = (node.Face as StatFace)?.TriggerSpelling ?? node.View ?? "kill";
        return EditRequest.Of(text =>
        {
            text = RulesetEdits.AddDefine(text, freshId, "on: " + spelling);
            return RulesetEdits.SetField(text, node.Id, key, freshId);
        }, $"put define {freshId} in front of {node.Id}");
    }

    /// <summary>
    ///     What still names <paramref name="node" /> once it is gone: the siblings that read it (the document
    ///     graph's edges out of it, and the open document's own expressions when <paramref name="text" /> is
    ///     given) and the <c>show:</c> entries that surface it. A deleted entry anything names leaves a file
    ///     the host no longer composes, so a delete is refused while this is not empty; a cascade that
    ///     removes these too is the inspector's offer, built from the same answer.
    /// </summary>
    public static NodeDependants Dependants(CanvasNodeViewModel node, RulesetCorpus.Composed composed,
        IReadOnlySet<RulesetDocumentNodeKey>? alsoDeleted = null, string? text = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(composed);
        alsoDeleted ??= new HashSet<RulesetDocumentNodeKey>();
        RulesetDocumentNodeKey key = node.Key;
        List<string> readers = [.. composed.Graph.Edges
            .Where(e => e.Source.Equals(key) && !e.Target.Equals(key) && !alsoDeleted.Contains(e.Target))
            .Select(e => e.Target.Id)
            .Distinct(StringComparer.Ordinal)];
        // The graph is edgeless when the composition failed, which is when the text is the only witness.
        if (text is not null && node.IsAuthorable)
        {
            foreach (string reader in TextReaders(text, node.Id))
            {
                if (reader != node.Id && !readers.Contains(reader) && !alsoDeleted.Contains(new RulesetDocumentNodeKey(node.Ruleset, reader)))
                {
                    readers.Add(reader);
                }
            }
        }

        bool scoreboard = false;
        List<string> tables = [];
        if (composed.Documents.TryGetValue(node.Ruleset, out RulesetDoc? doc) && doc.Show is { } show)
        {
            // A show: reference is the id, or the id and a facet of it (a highlight's .count).
            bool Names(string? reference) =>
                reference is not null && (reference == node.Id || reference.StartsWith(node.Id + ".", StringComparison.Ordinal));
            scoreboard = show.Scoreboard.Any(e => Names(e.Stat));
            tables.AddRange(show.Tables.Where(t => t.Columns.Any(c => Names(c.Stat))).Select(t => t.Name));
        }

        return new NodeDependants(node.Id, readers, scoreboard, tables);
    }

    private static IReadOnlyList<string> TextReaders(string text, string id)
    {
        try
        {
            return RulesetEdits.SiblingReaders(text, id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            return [];
        }
    }

    /// <summary>Removes <paramref name="node" />'s entry, whichever section declares it, unless something still names it.</summary>
    public static EditRequest Delete(CanvasNodeViewModel node, RulesetCorpus.Composed composed, string? text = null) =>
        Delete([node], composed, text);

    /// <summary>
    ///     Removes every entry in <paramref name="nodes" /> in one write, or refuses the whole lot naming what
    ///     still reads or shows any of them. A node another of the same selection reads is not a dependant.
    ///     <paramref name="text" /> is the open document, read for the siblings when the composition failed.
    /// </summary>
    public static EditRequest Delete(IReadOnlyList<CanvasNodeViewModel> nodes, RulesetCorpus.Composed composed, string? text = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(composed);
        List<CanvasNodeViewModel> deleting = [.. nodes.Where(n => n.IsAuthorable && !n.IsScaffold)];
        if (deleting.Count == 0)
        {
            return EditRequest.Ignore;
        }

        HashSet<RulesetDocumentNodeKey> keys = [.. deleting.Select(n => n.Key)];
        List<string> refusals = [];
        foreach (CanvasNodeViewModel node in deleting)
        {
            if (node.IsDefine)
            {
                // A define something fires on leaves those stats with no trigger; there is nothing to retarget them to.
                IReadOnlyList<string> firing = [.. StatsFiringOn(node, composed, text).Where(s => !keys.Contains(new RulesetDocumentNodeKey(node.Ruleset, s)))];
                if (firing.Count > 0)
                {
                    refusals.Add($"{node.Title} is fired on by {string.Join(", ", firing)}; retarget them first");
                }
            }
            else if (Dependants(node, composed, keys, text) is { Any: true } dependants)
            {
                refusals.Add(dependants.Describe());
            }
        }

        if (refusals.Count > 0)
        {
            return EditRequest.Refuse(string.Join("; ", refusals));
        }

        return EditRequest.Of(text =>
            {
                foreach (CanvasNodeViewModel node in deleting)
                {
                    text = node.IsDefine ? RulesetEdits.RemoveDefine(text, node.Title)
                        : node.IsHighlight ? RulesetEdits.RemoveHighlight(text, node.Id)
                        : RulesetEdits.RemoveStat(text, node.Id);
                }

                return text;
            },
            "delete " + string.Join(", ", deleting.Select(n => n.Title)));
    }

    // The stats whose trigger spells the define, from the loaded document, or from the text when the document did not load.
    private static IReadOnlyList<string> StatsFiringOn(CanvasNodeViewModel define, RulesetCorpus.Composed composed, string? text)
    {
        if (composed.Documents.TryGetValue(define.Ruleset, out RulesetDoc? doc))
        {
            return RulesetDocumentGraph.StatsFiringOn(doc, define.Title);
        }

        try
        {
            return text is null ? [] : RulesetEdits.StatsFiringOn(text, define.Title);
        }
        catch (Exception ex) when (ex is InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            return [];
        }
    }

    /// <summary>
    ///     What a wire from <paramref name="source" /> to <paramref name="target" /> stands for: an event or a view
    ///     sets the trigger, a context flag sets <c>while:</c>, an enrichment binds the facet that reads it, a stat
    ///     into a flag extends its <c>when:</c>, and a stat into a highlight extends its <c>when:</c>.
    /// </summary>
    public static EditRequest Connect(CanvasNodeViewModel source, CanvasNodeViewModel target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAuthorable || target.IsScaffold)
        {
            return EditRequest.Refuse("A wire can only end on a stat, a define or a highlight of your own ruleset.");
        }

        if (target.IsHighlight)
        {
            return ConnectHighlight(source, target);
        }

        if (source.IsDefine)
        {
            return ConnectDefine(source, target);
        }

        return source.Ruleset switch
        {
            "~event" => ConnectEvent(source.Id, target),
            "~context" => GatePathOf(source.Id) is { } gate
                ? Gate(target, gate)
                : EditRequest.Refuse($"{source.Id} is not a gate a ruleset can name in while:."),
            "~enrichment" => ConnectEnrichment(source.Id, target),
            _ when target.IsDefine => EditRequest.Refuse("A define fires on an event or a view; wire one of those into it."),
            _ => ConnectStat(source, target)
        };
    }

    // A define wired into a stat is what the stat fires on; the facets scoped to the old view go with the retarget.
    private static EditRequest ConnectDefine(CanvasNodeViewModel define, CanvasNodeViewModel target)
    {
        if (target.IsDefine)
        {
            return EditRequest.Refuse("A define cannot fire on a define; wire it into a stat.");
        }

        if (TriggerKeyFor(target) is not { } key)
        {
            return EditRequest.Refuse($"A {target.Kind} fires on no view; it cannot fire on {define.Title}.");
        }

        return Retarget(target, key, define.Title) with { Description = $"fire {target.Id} on {define.Title}" };
    }

    /// <summary>
    ///     What a wire from <paramref name="source" /> to <paramref name="target" /> would write, in one line
    ///     for the pending wire's preview: <c>sets while: match.live</c>, <c>binds facet enemy</c>,
    ///     <c>joins when: with or round_damage &gt; 0</c>, or the refusal. Nothing is applied; the decision is
    ///     <see cref="Connect" />'s, and the words come from the same facts it decided on.
    /// </summary>
    public static string ConnectPreview(CanvasNodeViewModel source, CanvasNodeViewModel target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        EditRequest request = Connect(source, target);
        if (request.IsRefused)
        {
            return request.Refusal ?? "nothing to write";
        }

        if (target.IsHighlight)
        {
            return $"joins when: with and {ReadOf(source, target)} > 0";
        }

        if (source.IsDefine)
        {
            return $"fires {target.Id} on {source.Title}";
        }

        switch (source.Ruleset)
        {
            case "~event":
            {
                IReadOnlyList<string> views = ViewsFiredBy(source.Id);
                return target.Kind == "count"
                    ? $"sets count: {views[0]}"
                    : $"sets on: {(views.Count == 1 ? views[0] : "raw." + source.Id)}";
            }
            case "~context":
                return $"sets while: {GatePathOf(source.Id)}";
            case "~enrichment":
                return $"binds facet {FacetReading(target.View, source.Id)?.Name}";
            default:
                return $"joins when: with or {ReadOf(source, target)} > 0";
        }
    }

    /// <summary>
    ///     Copies <paramref name="node" />'s entry under <paramref name="id" />, body and comments as written,
    ///     at the end of its section. A node not declared by the open document has no text to copy.
    /// </summary>
    public static EditRequest Duplicate(CanvasNodeViewModel node, string id)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsAuthorable || node.IsScaffold)
        {
            return EditRequest.Refuse("Only a stat, a define or a highlight of your own ruleset can be duplicated.");
        }

        if (node.IsDefine)
        {
            return EditRequest.Of(text => RulesetEdits.AddDefine(text, id, RulesetEntryText.DefineBodyOf(text, node.Title)),
                $"duplicate define {node.Title} as {id}");
        }

        return EditRequest.Of(text =>
        {
            string body = RulesetEntryText.BodyOf(text, node.Id, node.IsHighlight);
            if (node.IsHighlight)
            {
                return RulesetEdits.AddHighlight(text, id, body);
            }

            string indented = string.Join('\n', body.Split('\n').Select(l => l.Length == 0 ? l : "  " + l));
            return RulesetEdits.AddStat(text, $"{id}:\n{indented}\n");
        }, $"duplicate {node.Id} as {id}");
    }

    /// <summary>
    ///     A wire removed: the gate wire takes <c>while:</c> with it, and an enrichment's read takes the facet
    ///     that reads it. Trigger wires and logic inputs are the stat's own definition and stay. A wire whose
    ///     target is not the author's is ignored.
    /// </summary>
    public static EditRequest Disconnect(CanvasConnectionViewModel wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Source.Owner is not { } source || wire.Target.Owner is not { IsAuthorable: true } target)
        {
            return EditRequest.Ignore;
        }

        if (source.Ruleset == "~context" && !wire.IsRead)
        {
            return EditRequest.Of(text => target.IsDefine
                ? RulesetEdits.RemoveDefineFieldIfPresent(text, target.Title, "while")
                : RulesetEdits.RemoveFieldIfPresent(text, target.Id, "while"), $"ungate {target.Title}");
        }

        if (source.Ruleset == "~enrichment" && FacetReading(target.View, source.Id)?.Name is { } facet)
        {
            return WriteFacet(target, facet, null);
        }

        if (source.IsDefine)
        {
            return EditRequest.Refuse($"{target.Id} fires on {source.Title}; pick another trigger on the stat instead.");
        }

        return EditRequest.Refuse("That wire is part of the stat's own definition; change the stat instead.");
    }

    /// <summary>A facet row's write: a value sets the facet, null removes it. A define's go to its own block.</summary>
    public static EditRequest WriteFacet(CanvasNodeViewModel node, string facet, string? value)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsDefine)
        {
            string name = node.Title;
            return EditRequest.Of(text => value is null
                    ? RulesetEdits.RemoveDefineFacet(text, name, facet)
                    : RulesetEdits.SetDefineFacet(text, name, facet, value),
                value is null ? $"remove define {name}.match.{facet}" : $"set define {name}.match.{facet}");
        }

        return EditRequest.Of(text => value is null
                ? RulesetEdits.RemoveFacet(text, node.Id, facet)
                : RulesetEdits.SetFacet(text, node.Id, facet, value),
            value is null ? $"remove {node.Id}.match.{facet}" : $"set {node.Id}.match.{facet}");
    }

    /// <summary>
    ///     A field write from a define face: its trigger (set, never emptied), its gate or its where (either
    ///     removed on null). A define has no scope, label or kind key, so anything else is ignored.
    /// </summary>
    public static EditRequest WriteDefineField(CanvasNodeViewModel node, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(node);
        string name = node.Title;
        return key switch
        {
            "on" when value is not null => Retarget(node, "on", value),
            "on" => EditRequest.Refuse("A define needs a trigger; pick a view or an event."),
            "while" when value is not null => Gate(node, value),
            "while" or "where" => EditRequest.Of(text => value is null
                    ? RulesetEdits.RemoveDefineFieldIfPresent(text, name, key)
                    : RulesetEdits.SetDefineField(text, name, key, value),
                value is null ? $"remove define {name}.{key}" : $"set define {name}.{key}"),
            _ => EditRequest.Ignore
        };
    }

    /// <summary>
    ///     A field write from a node face: the scope, the gate, or one of the expression lines. A removed
    ///     <c>where:</c> or <c>while:</c> comes as null; a formula or a flag's test cannot be empty. A highlight's
    ///     fields go to <see cref="WriteHighlightField" />.
    /// </summary>
    public static EditRequest WriteField(CanvasNodeViewModel node, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsHighlight)
        {
            return WriteHighlightField(node, key, value);
        }

        if (node.IsDefine)
        {
            return WriteDefineField(node, key, value);
        }

        return key switch
        {
            "per" when value is not null => Set(node, "per", value),
            "while" when value is not null => Gate(node, value),
            "while" or "where" => SetOrRemove(node, key, value),
            "compute" when value is not null =>
                EditRequest.Of(text => RulesetEdits.SetComputeFormula(text, node.Id, value), $"set {node.Id}.compute"),
            "live" => EditRequest.Of(text => RulesetEdits.SetComputeLive(text, node.Id, value),
                value is null ? $"remove {node.Id}.compute.live" : $"set {node.Id}.compute.live"),
            "when" when value is not null => EditRequest.Of(text => RulesetEdits.SetFlagWhen(text, node.Id, value), $"set {node.Id}.when"),
            "compute" or "when" => EditRequest.Refuse($"A {key} cannot be empty; delete the stat instead."),
            // The trigger and the kind argument are the stat's definition: set, never emptied.
            "count" or "streak" or "bucket" or "burst" or "on" when value is not null => Retarget(node, key, value),
            "count" or "streak" or "bucket" or "burst" or "on" => EditRequest.Refuse("A stat needs a trigger; pick a view, an event or a define."),
            // A flag's deactivator names its own view; the facets under match: belong to on:, so none are dropped.
            "off" when value is not null => Set(node, "off", value),
            "off" => EditRequest.Refuse("A flag with off: needs a deactivator; remove the key in the source pane to drop it."),
            "sum" or "capture" or "tally" when value is not null => Set(node, key, value),
            "sum" or "capture" or "tally" => EditRequest.Refuse($"A {key} needs a value; delete the stat instead."),
            // The optional keys: a value sets, nothing removes, and the engine's default takes over.
            "keep" or "format" or "label" or "window" or "min_streak" or "key" or "value" or "reduce" => SetOrRemove(node, key, value),
            "rate.of" or "rate.per" when value is not null =>
                EditRequest.Of(text => RulesetEdits.SetNestedField(text, node.Id, "rate", key[5..], value), $"set {node.Id}.{key}"),
            "rate.of" or "rate.per" => EditRequest.Refuse("A rate needs both buckets; delete the stat instead."),
            "thresholds" => WriteThresholds(node, value),
            _ => EditRequest.Ignore
        };
    }

    /// <summary>
    ///     A highlight's fields: <c>when:</c> and <c>per:</c> cannot be empty; <c>title:</c>, <c>score:</c>,
    ///     <c>kind:</c> and <c>group:</c> can go, and the engine's default takes over.
    /// </summary>
    public static EditRequest WriteHighlightField(CanvasNodeViewModel node, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(node);
        return key switch
        {
            "when" or "per" when value is not null =>
                EditRequest.Of(text => RulesetEdits.SetHighlightField(text, node.Id, key, value), $"set {node.Id}.{key}"),
            "when" or "per" => EditRequest.Refuse($"A highlight's {key} cannot be empty; delete the highlight instead."),
            "title" or "score" or "kind" or "group" => EditRequest.Of(text => value is null
                    ? RulesetEdits.RemoveHighlightFieldIfPresent(text, node.Id, key)
                    : RulesetEdits.SetHighlightField(text, node.Id, key, value),
                value is null ? $"remove {node.Id}.{key}" : $"set {node.Id}.{key}"),
            _ => EditRequest.Ignore
        };
    }

    // A tally's table comes as one value, a row per line; see CanvasThresholdsViewModel.Serialize.
    private static EditRequest WriteThresholds(CanvasNodeViewModel node, string? value)
    {
        IReadOnlyList<(string Min, string Target)> rows = value is null ? [] : CanvasThresholdsViewModel.Parse(value);
        if (rows.Count == 0)
        {
            return EditRequest.Refuse("A tally needs at least one threshold with a minimum and a target.");
        }

        if (rows.Any(r => r.Min.Length == 0 || r.Target.Length == 0))
        {
            return EditRequest.Refuse("Every threshold needs a minimum and a target id.");
        }

        return EditRequest.Of(text => RulesetEdits.SetThresholds(text, node.Id, rows), $"set {node.Id}.thresholds");
    }

    private static EditRequest SetOrRemove(CanvasNodeViewModel node, string key, string? value) => value is null
        ? EditRequest.Of(text => RulesetEdits.RemoveFieldIfPresent(text, node.Id, key), $"remove {node.Id}.{key}")
        : Set(node, key, value);

    /// <summary>
    ///     The key a trigger write on <paramref name="node" /> goes to, or null when it fires on nothing. A flag
    ///     takes <c>on:</c> only when its face says it is not its own condition; a node with no face is read by
    ///     kind alone, and a flag then stays refused.
    /// </summary>
    public static string? TriggerKeyFor(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsDefine)
        {
            return "on";
        }

        if (node.Face is StatFace face)
        {
            return face.HasTrigger ? StatFace.TriggerKeyOf(face.Kind) : null;
        }

        return node.Kind == "flag" ? null : StatFace.TriggerKeyOf(node.Kind);
    }

    private static EditRequest Set(CanvasNodeViewModel node, string key, string value) =>
        EditRequest.Of(text => RulesetEdits.SetField(text, node.Id, key, value), $"set {node.Id}.{key}");

    // The facets under match: are scoped to the old view. One the new view lacks makes the resolver drop the
    // whole composition, so it goes in the same write. A target the catalog does not know (a define, a raw
    // event) cannot be checked and keeps them. A define's trigger and facets live under define: instead.
    private static EditRequest Retarget(CanvasNodeViewModel node, string key, string view)
    {
        CatalogView? target = FaceVocabulary.ViewOf(view);
        bool define = node.IsDefine;
        string id = node.Title;
        bool Stale(string facet) => facet != "actor" && target!.Facets.All(f => f.Name != facet);
        IReadOnlyList<string> dropped = target is null ? [] : [.. node.Facets.Select(f => f.Name).Where(Stale)];
        string description = dropped.Count == 0
            ? $"retarget {id} to {view}"
            : $"retarget {id} to {view}, dropping {string.Join(", ", dropped)}";
        return EditRequest.Of(text =>
        {
            text = define ? RulesetEdits.SetDefineField(text, id, key, view) : RulesetEdits.SetField(text, id, key, view);
            if (target is not null)
            {
                IReadOnlyList<string> keys = define ? RulesetEdits.DefineFacetKeys(text, id) : RulesetEdits.FacetKeys(text, id);
                foreach (string facet in keys.Where(Stale))
                {
                    text = define ? RulesetEdits.RemoveDefineFacet(text, id, facet) : RulesetEdits.RemoveFacet(text, id, facet);
                }
            }

            return text;
        }, description);
    }

    // A highlight's when: reads stats; the wired one joins as a conjunct. A define is a trigger, not a value.
    private static EditRequest ConnectHighlight(CanvasNodeViewModel source, CanvasNodeViewModel target)
    {
        if (source.IsScaffold || source.IsDefine)
        {
            return EditRequest.Refuse("A highlight's when: reads stats; wire a stat into it.");
        }

        string read = ReadOf(source, target);
        return EditRequest.Of(text =>
        {
            string? existing = RulesetEdits.HighlightField(text, target.Id, "when");
            string when = string.IsNullOrWhiteSpace(existing) ? $"{read} > 0" : $"{existing} and {read} > 0";
            return RulesetEdits.SetHighlightField(text, target.Id, "when", when);
        }, $"extend {target.Id} with {read}");
    }

    // An event fires one or several views. One view names the trigger outright; several leave the choice to
    // the author; none means the raw event, which a stat whose argument is a value can fire on.
    private static EditRequest ConnectEvent(string eventName, CanvasNodeViewModel target)
    {
        if (TriggerKeyFor(target) is not { } key)
        {
            return EditRequest.Refuse($"A {target.Kind} fires on no view; it cannot fire on {eventName}.");
        }

        IReadOnlyList<string> views = ViewsFiredBy(eventName);
        if (views.Count == 1)
        {
            return Retarget(target, key, views[0]);
        }

        if (views.Count == 0 && key == "on")
        {
            string raw = "raw." + eventName;
            return EditRequest.Of(text => target.IsDefine
                ? RulesetEdits.SetDefineField(text, target.Title, "on", raw)
                : RulesetEdits.SetField(text, target.Id, "on", raw), $"retarget {target.Title} to {eventName}");
        }

        return views.Count > 1
            ? EditRequest.Refuse($"{eventName} fires {string.Join(", ", views)}; drop the view you mean from the gallery.")
            : EditRequest.Refuse($"A {target.Kind} cannot fire on {eventName}.");
    }

    // An enrichment is reached through the facet of the target's view that reads it.
    private static EditRequest ConnectEnrichment(string enrichment, CanvasNodeViewModel target)
    {
        CatalogFacet? facet = FacetReading(target.View, enrichment);
        if (facet is null)
        {
            return EditRequest.Refuse($"No facet of {target.View ?? target.Kind} reads {enrichment}; test it in where: instead.");
        }

        if (!string.Equals(facet.Type, "bool", StringComparison.OrdinalIgnoreCase))
        {
            return EditRequest.Refuse($"{facet.Name} is a {facet.Type} facet; set its value on the node.");
        }

        return WriteFacet(target, facet.Name, "true");
    }

    // A stat wired into an expression flag joins its when: as one more alternative. A trigger flag
    // (flag: true with on:) has no when:, and the loader takes none beside it.
    private static EditRequest ConnectStat(CanvasNodeViewModel source, CanvasNodeViewModel target)
    {
        if (target.Kind != "flag" || source.IsScaffold || source.IsDefine)
        {
            return EditRequest.Refuse($"A stat can be wired into a flag's when:; a {target.Kind} reads its operands in its own text.");
        }

        if (target.Face is FlagFace { Trigger.On: not null })
        {
            return EditRequest.Refuse($"{target.Id} fires on its trigger and has no when: to extend; test {source.Id} in its where: instead.");
        }

        string read = ReadOf(source, target);
        return EditRequest.Of(text =>
        {
            string? existing = RulesetEdits.FlagWhen(text, target.Id);
            string when = string.IsNullOrWhiteSpace(existing) ? $"{read} > 0" : $"{existing} or {read} > 0";
            return RulesetEdits.SetFlagWhen(text, target.Id, when);
        }, $"extend {target.Id} with {read}");
    }

    // A sibling is read by its id; a stat of another ruleset by its qualified path.
    private static string ReadOf(CanvasNodeViewModel source, CanvasNodeViewModel target) =>
        source.Ruleset == target.Ruleset ? source.Id : source.Ruleset + "." + source.Id;
}
