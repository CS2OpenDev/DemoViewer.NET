#region

using CS2DemoKit.Analysis.Catalog;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>One gate a ruleset can name in <c>while:</c>, under the engine's name and the ruleset's path.</summary>
/// <param name="EngineName">The context flag as the engine names it (<c>MatchLive</c>), which is the chain node's id.</param>
/// <param name="Path">The v2 path the ruleset writes (<c>match.live</c>).</param>
/// <param name="Hint">One line on when the gate is open, for the gallery.</param>
public sealed record GatePath(string EngineName, string Path, string Hint);

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

    /// <summary>The id a placed gallery item asks for: a prefab or a highlight keeps its name, a kind or a view is <c>new_</c> it.</summary>
    public static string PlacedIdFor(GalleryItem item) =>
        item.Kind is GalleryItemKind.Prefab or GalleryItemKind.Highlight ? item.Name : "new_" + item.Name;

    /// <summary>Adds a count stat on <paramref name="view" /> as <paramref name="id" />, at the end of <c>stats:</c>.</summary>
    public static EditRequest AddStat(string view, string id) =>
        EditRequest.Of(text => RulesetEdits.AddStat(text, $"{id}:\n  count: {view}\n  per: round\n"), "add " + id);

    /// <summary>
    ///     A gallery item dropped on bare canvas: a stat kind, a view, a prefab or a highlight becomes a new entry
    ///     under <paramref name="id" />. A gate has nothing to attach to there and is refused.
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

        string body = string.Join('\n', item.Body.Split('\n').Select(l => "  " + l));
        return EditRequest.Of(text => RulesetEdits.AddStat(text, $"{id}:\n{body}\n"), "add " + id);
    }

    /// <summary>
    ///     A gallery item dropped on a node: a view retargets the trigger (the kind's own argument for a count,
    ///     <c>on:</c> for a stat whose argument is a value), and a gate becomes its <c>while:</c>.
    /// </summary>
    public static EditRequest DropOnNode(GalleryItem item, CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsAuthorable || node.IsHighlight)
        {
            return EditRequest.Refuse($"{item.Name} can only be dropped on a stat of your own ruleset.");
        }

        return item.Kind switch
        {
            GalleryItemKind.Gate => EditRequest.Of(text => RulesetEdits.SetField(text, node.Id, "while", item.Name),
                $"gate {node.Id} on {item.Name}"),
            GalleryItemKind.View when TriggerKeyFor(node) is { } key => Retarget(node, key, item.Name),
            GalleryItemKind.View => EditRequest.Refuse($"A {node.Kind} fires on no view; {item.Name} goes on a stat that has a trigger."),
            _ => EditRequest.Refuse($"Drop {item.Title} on the canvas to add it as a stat.")
        };
    }

    /// <summary>Removes <paramref name="node" />'s entry, whichever section declares it.</summary>
    public static EditRequest Delete(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return EditRequest.Of(text => node.IsHighlight ? RulesetEdits.RemoveHighlight(text, node.Id) : RulesetEdits.RemoveStat(text, node.Id),
            "delete " + node.Id);
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
            return EditRequest.Refuse("A wire can only end on a stat or a highlight of your own ruleset.");
        }

        if (target.IsHighlight)
        {
            return ConnectHighlight(source, target);
        }

        return source.Ruleset switch
        {
            "~event" => ConnectEvent(source.Id, target),
            "~context" => GatePathOf(source.Id) is { } gate
                ? EditRequest.Of(text => RulesetEdits.SetField(text, target.Id, "while", gate), $"gate {target.Id} on {gate}")
                : EditRequest.Refuse($"{source.Id} is not a gate a ruleset can name in while:."),
            "~enrichment" => ConnectEnrichment(source.Id, target),
            _ => ConnectStat(source, target)
        };
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
            return EditRequest.Of(text => RulesetEdits.RemoveFieldIfPresent(text, target.Id, "while"), $"ungate {target.Id}");
        }

        if (source.Ruleset == "~enrichment" && FacetReading(target.View, source.Id)?.Name is { } facet)
        {
            return EditRequest.Of(text => RulesetEdits.RemoveFacet(text, target.Id, facet), $"remove {target.Id}.match.{facet}");
        }

        return EditRequest.Refuse("That wire is part of the stat's own definition; change the stat instead.");
    }

    /// <summary>A facet row's write: a value sets the facet, null removes it.</summary>
    public static EditRequest WriteFacet(CanvasNodeViewModel node, string facet, string? value)
    {
        ArgumentNullException.ThrowIfNull(node);
        return EditRequest.Of(text => value is null
                ? RulesetEdits.RemoveFacet(text, node.Id, facet)
                : RulesetEdits.SetFacet(text, node.Id, facet, value),
            value is null ? $"remove {node.Id}.match.{facet}" : $"set {node.Id}.match.{facet}");
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

        return key switch
        {
            "per" when value is not null => Set(node, "per", value),
            "while" or "where" => SetOrRemove(node, key, value),
            "compute" when value is not null => Set(node, "compute", value),
            "when" when value is not null => EditRequest.Of(text => RulesetEdits.SetFlagWhen(text, node.Id, value), $"set {node.Id}.when"),
            "compute" or "when" => EditRequest.Refuse($"A {key} cannot be empty; delete the stat instead."),
            // The trigger and the kind argument are the stat's definition: set, never emptied.
            "count" or "streak" or "bucket" or "burst" or "on" when value is not null => Retarget(node, key, value),
            "count" or "streak" or "bucket" or "burst" or "on" => EditRequest.Refuse("A stat needs a trigger; pick a view, an event or a define."),
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
        if (node.Face is StatFace face)
        {
            return face.HasTrigger ? StatFace.TriggerKeyOf(face.Kind) : null;
        }

        return node.Kind == "flag" ? null : StatFace.TriggerKeyOf(node.Kind);
    }

    private static EditRequest Set(CanvasNodeViewModel node, string key, string value) =>
        EditRequest.Of(text => RulesetEdits.SetField(text, node.Id, key, value), $"set {node.Id}.{key}");

    private static EditRequest Retarget(CanvasNodeViewModel node, string key, string view) =>
        EditRequest.Of(text => RulesetEdits.SetField(text, node.Id, key, view), $"retarget {node.Id} to {view}");

    // A highlight's when: reads stats; the wired one joins as a conjunct.
    private static EditRequest ConnectHighlight(CanvasNodeViewModel source, CanvasNodeViewModel target)
    {
        if (source.IsScaffold)
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
            return EditRequest.Of(text => RulesetEdits.SetField(text, target.Id, "on", "raw." + eventName), $"retarget {target.Id} to {eventName}");
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

        return EditRequest.Of(text => RulesetEdits.SetFacet(text, target.Id, facet.Name, "true"), $"set {target.Id}.match.{facet.Name}");
    }

    // A stat wired into a flag joins its when: as one more alternative.
    private static EditRequest ConnectStat(CanvasNodeViewModel source, CanvasNodeViewModel target)
    {
        if (target.Kind != "flag" || source.IsScaffold)
        {
            return EditRequest.Refuse($"A stat can be wired into a flag's when:; a {target.Kind} reads its operands in its own text.");
        }

        string read = ReadOf(source, target);
        return EditRequest.Of(text =>
        {
            string? existing = RulesetEdits.Field(text, target.Id, "flag.when") ?? RulesetEdits.Field(text, target.Id, "when");
            string when = string.IsNullOrWhiteSpace(existing) ? $"{read} > 0" : $"{existing} or {read} > 0";
            return RulesetEdits.SetFlagWhen(text, target.Id, when);
        }, $"extend {target.Id} with {read}");
    }

    // A sibling is read by its id; a stat of another ruleset by its qualified path.
    private static string ReadOf(CanvasNodeViewModel source, CanvasNodeViewModel target) =>
        source.Ruleset == target.Ruleset ? source.Id : source.Ruleset + "." + source.Id;
}
