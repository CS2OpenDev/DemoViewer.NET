#region

using System.Globalization;
using System.Text.RegularExpressions;
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
///     What editing a wire's condition edits: the node written, the key under it, and for a logic wire into a
///     flat <c>when:</c> the operand row the wire stands for. <see cref="Row" /> is null when the whole key is
///     edited as text (a gate expression, a nested <c>when:</c>); <see cref="Reason" /> says why nothing can be
///     edited through this wire.
/// </summary>
/// <param name="Target">The node the key is written on.</param>
/// <param name="Key"><c>when</c> for a logic wire, <c>while</c> for a comparison gate.</param>
/// <param name="Row">The operand row of a flat <c>when:</c>, or null for the whole text.</param>
/// <param name="Reason">Why the wire has no editable condition, or null.</param>
public sealed record WireEdit(CanvasNodeViewModel Target, string Key, int? Row, string? Reason)
{
    public bool IsEnabled => Reason is null;

    public bool EditsRow => Row is not null;
}

/// <summary>
///     Every write decision the canvas makes, as data: a gesture or a face edit in, an <see cref="EditRequest" />
///     out, which the session applies. Nothing here touches a file or a view; the catalog is read for the
///     view a trigger names and the facet an enrichment is reached through.
/// </summary>
public static partial class CanvasEditSemantics
{
    /// <summary>
    ///     What a wire's condition editor edits. A logic wire into a flag or a highlight edits the one operand
    ///     row of its <c>when:</c> that the wire stands for, found by the lowered condition's text and then by
    ///     the path the source is read under; a <c>when:</c> that is not flat, or whose rows do not name the
    ///     wire, is edited whole as text. A comparison gate edits the target's <c>while:</c> as text. A read,
    ///     a plain guard, a trigger and a define's fires wire carry no condition and say why.
    /// </summary>
    /// <param name="wire">The wire.</param>
    /// <param name="whenText">The target's <c>when:</c> as written; read from its when line when null.</param>
    public static WireEdit? WireEditOf(CanvasConnectionViewModel wire, string? whenText = null)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Target.Owner is not { } target)
        {
            return null;
        }

        if (!target.IsAuthorable || target.IsScaffold)
        {
            return new WireEdit(target, "", null, "only a wire into a stat, a define or a highlight of your own ruleset can be edited");
        }

        if (wire.IsRead)
        {
            return new WireEdit(target, "", null, "a read has nothing to edit");
        }

        if (wire.IsGate)
        {
            return wire.Text == "while"
                ? new WireEdit(target, "while", null, $"this gate is a flag; edit the flag, or pick another gate on {target.Title}")
                : new WireEdit(target, "while", null, null);
        }

        if (wire.IsAnd || wire.IsOr)
        {
            whenText ??= target.Lines.FirstOrDefault(l => l.Key == "when")?.Text ?? "";
            return new WireEdit(target, "when", WhenRowOf(wire, target, whenText), null);
        }

        if (wire.Source.Owner is { IsDefine: true } define)
        {
            return new WireEdit(target, "", null, $"{define.Title} is what {target.Title} fires on; pick another trigger on {target.Title}");
        }

        return new WireEdit(target, "", null, $"a trigger wire has no condition; add a where row on {target.Title}");
    }

    // The engine lowers an operand relative to the value read ("value > 2", "active"), so the row is found by
    // the path the source is read under, and among several rows on one path by the comparison. A condition
    // the engine spelled with the path itself matches by text first.
    // The same lookup the chain uses for the wire's label, so the editor opens the row the label shows.
    private static int? WhenRowOf(CanvasConnectionViewModel wire, CanvasNodeViewModel target, string whenText)
    {
        if (wire.Source.Owner is not { } source)
        {
            return null;
        }

        string read = source.IsScaffold ? source.Ruleset == "~context" ? GatePathOf(source.Id) ?? source.Id : source.Id : ReadOf(source, target);
        ConditionRow? row = ChainWiring.AuthoredRow(whenText, read, wire.Condition);
        if (row is null)
        {
            return null;
        }

        int index = ConditionExpression.Parse(whenText).Rows.ToList().IndexOf(row);
        return index < 0 ? null : index;
    }

    /// <summary>
    ///     A gallery item dropped on a wire: the define goes in front of the target of a trigger wire, taking
    ///     the trigger with it; a gate becomes the target's <c>while:</c>; a view retargets a trigger wire's
    ///     target. Null when the item has no meaning on a wire and is placed on the canvas as before.
    /// </summary>
    public static EditRequest? DropOnWire(GalleryItem item, CanvasConnectionViewModel wire, string? freshId = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Target.Owner is not { } target)
        {
            return null;
        }

        bool trigger = IsTriggerWire(wire);
        return item.Kind switch
        {
            GalleryItemKind.Gate => DropOnNode(item, target, freshId),
            GalleryItemKind.Define when trigger => DropOnNode(item, target, freshId),
            GalleryItemKind.Define => EditRequest.Refuse($"A define goes in front of a trigger; drop it on the wire that fires {target.Title}, or on the stat."),
            GalleryItemKind.View when trigger => DropOnNode(item, target, freshId),
            GalleryItemKind.View => EditRequest.Refuse($"A view retargets a trigger; drop {item.Name} on the wire that fires {target.Title}, or on the stat."),
            _ => null
        };
    }

    /// <summary>An event's or a define's wire into what fires on it: not a read, a gate or a logic input.</summary>
    public static bool IsTriggerWire(CanvasConnectionViewModel wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return !wire.IsRead && !wire.IsGate && !wire.IsAnd && !wire.IsOr
               && wire.Source.Owner is { } source && (source.IsDefine || source.Ruleset == "~event");
    }

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
        item.Kind is GalleryItemKind.Prefab or GalleryItemKind.Highlight || item.CopiesName ? item.Name : "new_" + item.Name;

    /// <summary>
    ///     The nodes a wire from <paramref name="source" /> can start when it is dropped on empty canvas, as
    ///     placeable items whose bodies already name the source: from a stat, a highlight, a flag or a compute
    ///     that reads it; from an event, a count or a define on each view it fires; from a define, a count on
    ///     it; from a gate, a define held by it. Empty when nothing can start there.
    /// </summary>
    public static IReadOnlyList<GalleryItem> WireDropOptions(CanvasNodeViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.IsScaffold)
        {
            return source.Ruleset switch
            {
                "~event" => [.. ViewsFiredBy(source.Id).SelectMany(view => new[]
                {
                    new GalleryItem(GalleryItemKind.Prefab, view + "_count", "New count on " + view, "Counts " + view + " per round.", $"count: {view}\nper: round"),
                    new GalleryItem(GalleryItemKind.Define, view + "_define", "New define on " + view, "A named trigger on " + view + " to put conditions on.", $"on: {view}")
                })],
                "~context" when GatePathOf(source.Id) is { } gate =>
                [
                    new GalleryItem(GalleryItemKind.Define, "gated_kill", "New define on kill while " + gate, "A kill trigger held by " + gate + ".", $"on: kill\nwhile: {gate}")
                ],
                _ => []
            };
        }

        if (source.IsDefine)
        {
            return [new GalleryItem(GalleryItemKind.Prefab, source.Id + "_count", "New count on " + source.Id, "Counts the define's firings per round.", $"count: {source.Id}\nper: round")];
        }

        if (source.IsHighlight || !source.IsAuthorable || source.IsShow || source.IsParams)
        {
            return [];
        }

        string read = source.Id;
        if (source.ValueType == "bool")
        {
            return
            [
                new GalleryItem(GalleryItemKind.Highlight, read + "_highlight", "New highlight when " + read, "A highlight that fires when the flag holds in the scope.",
                    $"when: {read}\nper: round\nscore: 50\ntitle: \"{{player.name}} {read} (round {{round.number}})\""),
                new GalleryItem(GalleryItemKind.Prefab, read + "_gated", "New count while " + read, "A kill count held open by the flag.",
                    $"count: kill\nmatch: {{ enemy: true }}\nwhile: {read}\nper: round"),
                new GalleryItem(GalleryItemKind.Prefab, read + "_compute", "New compute from " + read, "A formula over the flag, to edit on the face.",
                    $"compute: \"{read}\"\nper: round")
            ];
        }

        return
        [
            new GalleryItem(GalleryItemKind.Highlight, read + "_highlight", "New highlight when " + read + " > 0", "A highlight that fires when the stat is above zero in the scope.",
                $"when: {read} > 0\nper: round\nscore: 50\ntitle: \"{{player.name}} {read} (round {{round.number}})\""),
            new GalleryItem(GalleryItemKind.Prefab, read + "_flag", "New flag when " + read + " > 0", "True in a scope where the stat is above zero.",
                $"flag:\n  when: \"{read} > 0\"\nper: round"),
            new GalleryItem(GalleryItemKind.Prefab, read + "_compute", "New compute from " + read, "A formula over the stat, to edit on the face.",
                $"compute: \"{read}\"\nper: round")
        ];
    }

    /// <summary>The node key a placed item will draw under: a define's and a table's are prefixed, every other entry's is its id.</summary>
    public static RulesetDocumentNodeKey PlacedKeyFor(GalleryItem item, string ruleset, string id) => item.Kind switch
    {
        GalleryItemKind.Define => RulesetDocumentNodeKey.Define(ruleset, id),
        GalleryItemKind.Table => RulesetDocumentNodeKey.Show(ruleset, id),
        _ => new RulesetDocumentNodeKey(ruleset, id)
    };

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

        if (item.Kind == GalleryItemKind.Table)
        {
            // The grain follows the ruleset's for:, which the registry requires it to match.
            return EditRequest.Of(text => RulesetEdits.AddTable(text, id, RulesetEdits.TableGrainsFor(RulesetEdits.DocumentField(text, "for"))[0]),
                "add table " + id);
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
        if (item.Kind == GalleryItemKind.Table)
        {
            return EditRequest.Refuse("Drop a table on the canvas; wire stats into it to add columns.");
        }

        if (!node.IsAuthorable || node.IsHighlight || node.IsShow || node.IsParams)
        {
            return EditRequest.Refuse($"{item.Name} can only be dropped on a define or a stat of your own ruleset.");
        }

        return item.Kind switch
        {
            GalleryItemKind.Gate => Gate(node, item.Name),
            GalleryItemKind.View when TriggerKeyFor(node) is { } key => Retarget(node, key, item.Name),
            GalleryItemKind.View => EditRequest.Refuse($"A {node.Kind} fires on no view; {item.Name} goes on a stat that has a trigger."),
            GalleryItemKind.Define when node.IsDefine => EditRequest.Refuse("A define cannot fire on a define; drop it on a stat or on the canvas."),
            GalleryItemKind.Define when item is { CopiesName: true, Body: not null } && freshId is not null && TriggerKeyFor(node) is { } key =>
                CopyDefineInFront(item, node, key, freshId),
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
    // A define from another ruleset, copied in under a free name, with the stat retargeted onto the copy.
    private static EditRequest CopyDefineInFront(GalleryItem item, CanvasNodeViewModel node, string key, string freshId)
    {
        EditRequest retarget = Retarget(node, key, freshId);
        return EditRequest.Of(text => retarget.Transform!(RulesetEdits.AddDefine(text, freshId, item.Body!)),
            $"copy define {item.Name} in front of {node.Id}");
    }

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
            if (node.IsParams)
            {
                refusals.Add("params: is removed in the YAML tab, with the reads that name it");
            }
            else if (node.IsShow)
            {
                // A table goes with its columns; nothing reads a table.
            }
            else if (node.IsDefine)
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
                    text = node.IsShow ? RemoveShow(text, node)
                        : node.IsDefine ? RulesetEdits.RemoveDefine(text, node.Title)
                        : node.IsHighlight ? RulesetEdits.RemoveHighlight(text, node.Id)
                        : RulesetEdits.RemoveStat(text, node.Id);
                }

                return text;
            },
            "delete " + string.Join(", ", deleting.Select(n => n.Title)));
    }

    /// <summary>Removes a show node's block: the table whole, or the scoreboard list.</summary>
    public static string RemoveShow(string text, CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Key.IsScoreboard ? RulesetEdits.RemoveScoreboard(text) : RulesetEdits.RemoveTable(text, node.Title);
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
    /// <summary>
    ///     A wire dropped on a typed input: the port decides what the wire must be. A trigger port takes what
    ///     fires; a gate port takes a context flag or a sibling bool stat, written as <c>while:</c>; a read or
    ///     logic port takes a value and falls through to the body rules. A wire into the node body, with no
    ///     port, is read from the source as before.
    /// </summary>
    public static EditRequest Connect(CanvasNodeViewModel source, CanvasNodeViewModel target, PortKind? port) =>
        Connect(source, target, port, null);

    /// <summary>
    ///     A wire from <paramref name="output" />: a folded dependency's output names the entry it stands for, so
    ///     the wire reads <c>&lt;ruleset&gt;.&lt;entry&gt;</c> exactly as one from that entry drawn expanded would.
    ///     Null, or a dependency port that names no entry, is a wire from the node itself.
    /// </summary>
    public static EditRequest Connect(CanvasNodeViewModel source, CanvasNodeViewModel target, PortKind? port, CanvasConnectorViewModel? output)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        source = EntryBehind(source, output);
        // A show node's inputs are its columns, and a wire on any of them appends one.
        if (port is null || !target.IsAuthorable || target.IsScaffold || target.IsShow)
        {
            return Connect(source, target);
        }

        PortKind gives = CanvasGraphBuilder.OutputKindOf(source);
        switch (port)
        {
            case PortKind.Trigger when gives != PortKind.Fire:
                return EditRequest.Refuse($"The on port of {target.Title} takes an event, a view or a define; {source.Title} is a {Noun(gives)}.");
            case PortKind.Gate when gives == PortKind.Flag:
                return GatePathOf(source.Id) is { } path ? Gate(target, path) : EditRequest.Refuse($"{source.Id} is not a gate a ruleset can name in while:.");
            case PortKind.Gate when gives == PortKind.Value && source is { ValueType: "bool", IsScaffold: false } && source.Ruleset == target.Ruleset:
                return Gate(target, source.Id);
            case PortKind.Gate:
                return EditRequest.Refuse($"The while port of {target.Title} takes a context flag or one of your bool stats; {source.Title} is a {Noun(gives)}.");
            case PortKind.Read or PortKind.Logic when gives == PortKind.Fire:
                return EditRequest.Refuse($"The {CanvasConnectorViewModel.TitleOf(port.Value)} port of {target.Title} takes a value; {source.Title} fires.");
            default:
                return Connect(source, target);
        }
    }

    // The dependency's entry as a read-only node of its ruleset: the same node an expansion would draw, for the rules below.
    private static CanvasNodeViewModel EntryBehind(CanvasNodeViewModel source, CanvasConnectorViewModel? output) =>
        source.IsDependency && output is { Entry: { Length: > 0 } entry } && ReferenceEquals(output.Owner, source)
            ? new CanvasNodeViewModel { Id = entry, Ruleset = source.Id, Kind = "stat", IsAuthorable = false }
            : source;

    private static string Noun(PortKind kind) => kind switch
    {
        PortKind.Fire => "trigger",
        PortKind.Flag => "flag",
        _ => "value"
    };

    public static EditRequest Connect(CanvasNodeViewModel source, CanvasNodeViewModel target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAuthorable || target.IsScaffold || target.IsParams)
        {
            return EditRequest.Refuse("A wire can only end on a stat, a define, a highlight or a table of your own ruleset.");
        }

        if (source.IsShow)
        {
            return EditRequest.Refuse("A table surfaces values; nothing reads a table.");
        }

        if (source.IsParams)
        {
            return EditRequest.Refuse($"A param is read by name: write params.<name> in {target.Title}'s text.");
        }

        // A folded dependency has no entry of its own to read; its entries do, once drawn.
        if (source.IsDependency)
        {
            return EditRequest.Refuse($"Expand {source.Id} and wire from the entry itself; a read of another ruleset is written as {source.Id}.<entry>.");
        }

        if (target.IsShow)
        {
            return ConnectShow(source, target);
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

    /// <summary>The caption for a wire held over a typed port: the port's refusal or write, else the body's.</summary>
    public static string ConnectPreview(CanvasNodeViewModel source, CanvasNodeViewModel target, PortKind? port) =>
        ConnectPreview(source, target, port, null);

    /// <summary>The caption for a wire held from <paramref name="output" />, which names a folded dependency's entry.</summary>
    public static string ConnectPreview(CanvasNodeViewModel source, CanvasNodeViewModel target, PortKind? port, CanvasConnectorViewModel? output)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        source = EntryBehind(source, output);
        if (port is null)
        {
            return ConnectPreview(source, target);
        }

        EditRequest request = Connect(source, target, port);
        if (request.IsRefused)
        {
            return request.Refusal ?? "";
        }

        // The one write a port adds over the body rules: a sibling bool stat into while.
        return port == PortKind.Gate && !source.IsScaffold && source is { ValueType: "bool" }
            ? $"sets while: {source.Id}"
            : ConnectPreview(source, target);
    }


    /// <summary>
    ///     What a wire from <paramref name="source" /> to <paramref name="target" /> would write, in one line
    ///     for the pending wire's preview: <c>sets while: match.live</c>, <c>binds facet enemy</c>,
    ///     <c>joins when: with or round_damage &gt; 0</c>, or the refusal. Nothing is applied; the decision is
    ///     <see cref="Connect(CanvasNodeViewModel, CanvasNodeViewModel)" />'s, and the words come from the same facts it decided on.
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

        if (target.IsShow)
        {
            return $"adds column {source.Label ?? source.Id} to {target.Title}";
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
        if (!node.IsEntry)
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

        if (target.IsShow)
        {
            // The column is the wire: removing it takes the column out and keeps the table.
            if (target.ColumnOf(wire.Target) is not { } column)
            {
                return EditRequest.Ignore;
            }

            string? table = target.Key.IsScoreboard ? null : target.Title;
            return EditRequest.Of(text => RulesetEdits.RemoveShowItem(text, table, column.Reference.Index),
                $"stop showing {column.Stat} on {target.Title}");
        }

        if (source.IsParams)
        {
            return EditRequest.Refuse($"{target.Title} reads a param in its text; edit the text to stop.");
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
        if (node.IsShow)
        {
            return WriteShowField(node, key, value);
        }

        if (node.IsParams)
        {
            return WriteParamField(node, key, value);
        }

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

    /// <summary>A table's one field: its <c>per:</c> grain, which cannot be emptied. The scoreboard has none.</summary>
    public static EditRequest WriteShowField(CanvasNodeViewModel node, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (key != "per" || node.Key.IsScoreboard)
        {
            return EditRequest.Ignore;
        }

        return value is null
            ? EditRequest.Refuse("A table needs a per: grain; pick the one that matches the ruleset's for:.")
            : EditRequest.Of(text => RulesetEdits.SetTablePer(text, node.Title, value), $"set table {node.Title}.per");
    }

    /// <summary>The types a param can declare, as the schema spells them.</summary>
    public static IReadOnlyList<string> ParamTypeOptions { get; } = ["int", "float", "bool", "string", "duration"];

    /// <summary>
    ///     A params write: <c>param.&lt;name&gt;</c> sets that param's default, <c>param.&lt;name&gt;.type</c>,
    ///     <c>.min</c> or <c>.max</c> the key named, each checked against the declared type and the other
    ///     keys before anything is written. A default and a type cannot be emptied here; a bound can.
    /// </summary>
    public static EditRequest WriteParamField(CanvasNodeViewModel node, string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!key.StartsWith("param.", StringComparison.Ordinal))
        {
            return EditRequest.Ignore;
        }

        string rest = key["param.".Length..];
        int dot = rest.IndexOf('.', StringComparison.Ordinal);
        string name = dot < 0 ? rest : rest[..dot];
        string property = dot < 0 ? "default" : rest[(dot + 1)..];
        ParamDef? param = (node.Face as ParamsFace)?.Params.FirstOrDefault(p => p.Name == name);
        if (property is "min" or "max" && value is null)
        {
            return EditRequest.Of(text => RulesetEdits.RemoveParamField(text, name, property), $"remove params.{name}.{property}");
        }

        if (value is null)
        {
            return EditRequest.Refuse(property == "type"
                ? $"{name} needs a type; pick one of {string.Join(", ", ParamTypeOptions)}."
                : $"{name} needs a default; remove the param in the YAML tab instead.");
        }

        string trimmed = value.Trim();
        string? refusal = property switch
        {
            "default" => param is null ? null : ParamRefusal(param, trimmed),
            "type" => ParamTypeRefusal(param, name, trimmed),
            "min" or "max" => param is null ? null : ParamBoundRefusal(param, property, trimmed),
            _ => $"a param has no {property} key"
        };
        if (refusal is not null)
        {
            return EditRequest.Refuse(refusal);
        }

        return EditRequest.Of(text => RulesetEdits.SetParamField(text, name, property, trimmed), $"set params.{name}.{property}");
    }

    /// <summary>
    ///     Why <paramref name="type" /> cannot be <paramref name="param" />'s type: not a type the language has,
    ///     a default the new type does not take, or bounds a bool or a string cannot carry. Null when it can.
    /// </summary>
    public static string? ParamTypeRefusal(ParamDef? param, string name, string type)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(type);
        if (!ParamTypeOptions.Contains(type, StringComparer.Ordinal))
        {
            return $"{name} takes a type of {string.Join(", ", ParamTypeOptions)}; not '{type}'.";
        }

        if (param is null)
        {
            return null;
        }

        ParamType wanted = Enum.Parse<ParamType>(type, ignoreCase: true);
        if (wanted is ParamType.Bool or ParamType.String && (param.Min is not null || param.Max is not null))
        {
            return $"{name} has min or max, which a {type} cannot carry; remove them first.";
        }

        string? current = CanvasNodeFactory.ParamText(param.Default);
        return current is null ? null : ParamRefusal(new ParamDef(name, wanted, param.Default, param.Min, param.Max, param.Position), current);
    }

    /// <summary>
    ///     Why <paramref name="value" /> cannot be <paramref name="param" />'s <paramref name="bound" /> (<c>min</c>
    ///     or <c>max</c>): the type has no bounds, the value is not a number the type takes, the bounds would
    ///     cross, or the default would fall outside them. Null when it can.
    /// </summary>
    public static string? ParamBoundRefusal(ParamDef param, string bound, string value)
    {
        ArgumentNullException.ThrowIfNull(param);
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(value);
        if (param.Type is ParamType.Bool or ParamType.String)
        {
            return $"{param.Name} is a {param.Type.ToString().ToLowerInvariant()}; it has no {bound}.";
        }

        double number;
        if (param.Type == ParamType.Int)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
            {
                return $"{param.Name} is an int; {value} is not a whole number.";
            }

            number = whole;
        }
        else if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return $"{param.Name}'s {bound} must be a number; {value} is not one.";
        }

        bool isMin = bound == "min";
        if (isMin && param.Max is { } max && number > max)
        {
            return $"min {value} is above max {max.ToString(CultureInfo.InvariantCulture)}.";
        }

        if (!isMin && param.Min is { } min && number < min)
        {
            return $"max {value} is below min {min.ToString(CultureInfo.InvariantCulture)}.";
        }

        double? current = param.Default switch
        {
            long l => l,
            double d => d,
            _ => null
        };
        if (current is { } d2 && (isMin ? d2 < number : d2 > number))
        {
            return $"{bound} {value} would leave the default {d2.ToString(CultureInfo.InvariantCulture)} outside the bounds.";
        }

        return null;
    }

    /// <summary>Why <paramref name="value" /> is not a default <paramref name="param" /> takes, or null when it is.</summary>
    public static string? ParamRefusal(ParamDef param, string value)
    {
        ArgumentNullException.ThrowIfNull(param);
        ArgumentNullException.ThrowIfNull(value);
        double? number = null;
        switch (param.Type)
        {
            case ParamType.Int:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
                {
                    return $"{param.Name} is an int; {value} is not a whole number.";
                }

                number = whole;
                break;
            case ParamType.Float:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
                {
                    return $"{param.Name} is a float; {value} is not a number.";
                }

                number = real;
                break;
            case ParamType.Bool:
                if (value is not ("true" or "false"))
                {
                    return $"{param.Name} is a bool; write true or false.";
                }

                break;
            case ParamType.Duration:
                if (!DurationLiteral().IsMatch(value))
                {
                    return $"{param.Name} is a duration; write it as 10s, 0.5s or 500ms.";
                }

                break;
        }

        if (number is { } n)
        {
            if (param.Min is { } min && n < min)
            {
                return $"{param.Name} is at least {min.ToString(CultureInfo.InvariantCulture)}.";
            }

            if (param.Max is { } max && n > max)
            {
                return $"{param.Name} is at most {max.ToString(CultureInfo.InvariantCulture)}.";
            }
        }

        return null;
    }

    [GeneratedRegex(@"^\d+(\.\d+)?(s|ms)$")]
    private static partial Regex DurationLiteral();

    // A stat or a highlight wired into a table becomes a column labelled by its label or id; a highlight is
    // surfaced by its count, as the corpus writes it. An entry already a column is refused by name.
    private static EditRequest ConnectShow(CanvasNodeViewModel source, CanvasNodeViewModel target)
    {
        if (source.IsScaffold || source.IsDefine || !source.IsAuthorable || source.Ruleset != target.Ruleset)
        {
            return EditRequest.Refuse($"{target.Title} shows stats and highlights of this ruleset; wire one of those into it.");
        }

        string reference = source.IsHighlight ? source.Id + ".count" : source.Id;
        string label = source.Label ?? source.Id;
        if (target.Columns.Any(c => ShowSurface.Refers(c.Stat, source.Id, source.Ruleset)))
        {
            return EditRequest.Refuse($"{source.Id} is already a column of {target.Title}.");
        }

        string? table = target.Key.IsScoreboard ? null : target.Title;
        return EditRequest.Of(text => table is null
                ? RulesetEdits.AddToScoreboard(text, reference, label)
                : RulesetEdits.AddToTable(text, table, reference, label),
            $"show {source.Id} on {target.Title}");
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

        if (CrossRulesetWhen(source, target) is { } refusal)
        {
            return refusal;
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

        if (CrossRulesetWhen(source, target) is { } refusal)
        {
            return refusal;
        }

        string read = ReadOf(source, target);
        return EditRequest.Of(text =>
        {
            string? existing = RulesetEdits.FlagWhen(text, target.Id);
            string when = string.IsNullOrWhiteSpace(existing) ? $"{read} > 0" : $"{existing} or {read} > 0";
            return RulesetEdits.SetFlagWhen(text, target.Id, when);
        }, $"extend {target.Id} with {read}");
    }

    // The planner gates a when: on siblings and context only; a ruleset whose when: reads another ruleset is left out of the chain.
    private static EditRequest? CrossRulesetWhen(CanvasNodeViewModel source, CanvasNodeViewModel target) =>
        source.Ruleset == target.Ruleset ? null
            : EditRequest.Refuse($"{ReadOf(source, target)} is another ruleset's stat, and a when: gates only on this ruleset's own; "
                                 + $"read it in a compute of yours (write {ReadOf(source, target)} in its text) and wire that into {target.Title}.");

    // A sibling is read by its id; a stat of another ruleset by its qualified path.
    private static string ReadOf(CanvasNodeViewModel source, CanvasNodeViewModel target) =>
        source.Ruleset == target.Ruleset ? source.Id : source.Ruleset + "." + source.Id;
}
