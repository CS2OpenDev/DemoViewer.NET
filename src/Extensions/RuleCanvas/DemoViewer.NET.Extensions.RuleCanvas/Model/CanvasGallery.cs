#region

using CS2DemoKit.Analysis.Catalog;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>What a gallery item does when it lands on the canvas or on a node.</summary>
public enum GalleryItemKind
{
    /// <summary>A stat of one kind with a starter body; placed on the canvas as a new entry.</summary>
    Stat,

    /// <summary>A view the catalog offers; placed as a new count on it, or dropped on a stat to retarget it.</summary>
    View,

    /// <summary>A gate; dropped on a stat to become its <c>while:</c>.</summary>
    Gate,

    /// <summary>A complete, common stat; placed on the canvas as a new entry.</summary>
    Prefab,

    /// <summary>A highlight with a starter <c>when:</c>; placed on the canvas under <c>highlights:</c>.</summary>
    Highlight,

    /// <summary>
    ///     A named trigger; placed on the canvas as a new <c>define:</c> entry, or dropped on a stat to put a new
    ///     define, firing on the stat's own trigger, in front of it.
    /// </summary>
    Define,

    /// <summary>A <c>show:</c> table; placed on the canvas as an empty table on the ruleset's grain, to wire stats into.</summary>
    Table
}

/// <summary>One thing the gallery offers to drag.</summary>
/// <param name="Kind">What dropping it does.</param>
/// <param name="Name">The catalog name or the gate path, which is what the YAML gets.</param>
/// <param name="Title">What the gallery shows.</param>
/// <param name="Hint">One line on what it is, shown under the title.</param>
/// <param name="Body">For a stat or a prefab, the entry's body at one indentation, without its id line.</param>
/// <param name="CopiesName">Placed under its own name (a copy of the user's entry) rather than a fresh <c>new_</c> id.</param>
public sealed record GalleryItem(GalleryItemKind Kind, string Name, string Title, string Hint, string? Body = null, bool CopiesName = false)
{
    public string KindLabel => Kind.ToString().ToUpperInvariant();

    /// <summary>The node kind the item draws as once placed, which picks its family colour.</summary>
    public string NodeKind => Kind switch
    {
        GalleryItemKind.Stat => Name,
        GalleryItemKind.Prefab => Body?.Split('\n')[0].Split(':')[0].Trim() is { Length: > 0 } key ? key : "count",
        GalleryItemKind.Gate => Name.Contains('.', StringComparison.Ordinal) ? "context" : "flag",
        GalleryItemKind.Highlight => "highlight",
        GalleryItemKind.Define => "define",
        GalleryItemKind.Table => "show",
        _ => "count"
    };

    /// <summary>Whether <paramref name="query" /> matches the title, the name or the hint, ignoring case.</summary>
    public bool Matches(string query) =>
        Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Hint.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A titled run of items inside a group: the views of one event, or the user's own flags among the gates.</summary>
/// <param name="Title">The sub-heading, or null for the group's unnamed run.</param>
/// <param name="Items">The items, in the order shown.</param>
public sealed record GallerySection(string? Title, IReadOnlyList<GalleryItem> Items);

/// <summary>One group of the gallery: an expander whose body is its sections.</summary>
/// <param name="Title">The expander's heading.</param>
/// <param name="Items">Every item of the group, flat, for a drop or a search.</param>
/// <param name="Sections">The items as shown, in sub-headed runs; one unnamed run when the group has no sub-headings.</param>
/// <param name="ExpandedByDefault">Whether the expander opens on first show.</param>
public sealed record GalleryGroup(string Title, IReadOnlyList<GalleryItem> Items, IReadOnlyList<GallerySection>? Sections = null,
    bool ExpandedByDefault = true)
{
    /// <summary>The sections as shown; the flat items as one unnamed section when none were given.</summary>
    public IReadOnlyList<GallerySection> Sections { get; init; } = Sections ?? [new GallerySection(null, Items)];

    /// <summary>The expander's state. The view writes it; a search opens every group it shows.</summary>
    public bool IsExpanded { get; set; } = ExpandedByDefault;

    public int Count => Items.Count;
}

/// <summary>
///     The gallery a user drags from, in task order: prefabs for the stats most rulesets start with, the
///     highlights, the stat kinds and the define, the gates (the engine's and the user's own flags), and the catalog's views
///     grouped by the event that fires them. Views come from the catalog; the engine gates from the one
///     table in <see cref="CanvasEditSemantics.GatePaths" />.
/// </summary>
public static class CanvasGallery
{
    /// <summary>The gate paths a ruleset writes in <c>while:</c>, in the order the gallery offers them.</summary>
    public static IReadOnlyList<string> GatePaths { get; } = [.. CanvasEditSemantics.GatePaths.Select(g => g.Path)];

    /// <summary>The gallery without a ruleset open: no flags of the user's own among the gates.</summary>
    public static IReadOnlyList<GalleryGroup> Build(CatalogRoot catalog) => Build(catalog, []);

    /// <param name="catalog">The catalog whose views the Views group lists.</param>
    /// <param name="flags">The flag stat ids of the open ruleset, each offered as a gate under "Your flags".</param>
    public static IReadOnlyList<GalleryGroup> Build(CatalogRoot catalog, IEnumerable<string> flags) => Build(catalog, flags, []);

    /// <param name="catalog">The catalog whose views the Views group lists.</param>
    /// <param name="flags">The flag stat ids of the open ruleset, each offered as a gate under "Your flags".</param>
    /// <param name="yourDefines">
    ///     The trigger defines of the user's other rulesets, each a copy into the open one; the engine scopes
    ///     a define to its document, so sharing one is copying it. No group when there are none.
    /// </param>
    public static IReadOnlyList<GalleryGroup> Build(CatalogRoot catalog, IEnumerable<string> flags, IEnumerable<GalleryItem> yourDefines)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(yourDefines);
        List<GalleryItem> shelf = [.. yourDefines];
        List<GalleryItem> engineGates = [.. CanvasEditSemantics.GatePaths.Select(g => Gate(g.Path, g.Hint))];
        List<GalleryItem> ownFlags = [.. flags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(id => Gate(id, $"while your flag {id} holds"))];
        List<GallerySection> gateSections = [new GallerySection(null, engineGates)];
        if (ownFlags.Count > 0)
        {
            gateSections.Add(new GallerySection("Your flags", ownFlags));
        }

        List<GalleryItem> views = [.. catalog.Views.OrderBy(v => v.Name, StringComparer.Ordinal).Select(ViewItem)];
        List<GallerySection> viewSections =
        [
            .. catalog.Views.GroupBy(v => v.Event, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new GallerySection(g.Key, [.. g.OrderBy(v => v.Name, StringComparer.Ordinal).Select(ViewItem)]))
        ];

        List<GalleryGroup> groups =
        [
            new GalleryGroup("Prefabs",
            [
                Prefab("enemy_kills", "Enemy kills", "Kills on enemies, per round.",
                    "count: kill\nmatch: { enemy: true }\nper: round"),
                Prefab("deaths", "Deaths", "Deaths while the match is live, per round.",
                    "count: death\nwhile: match.live\nper: round"),
                Prefab("headshot_kills", "Headshot kills", "Enemy kills that were headshots, per match.",
                    "count: kill\nmatch: { enemy: true, headshot: true }\nper: match"),
                Prefab("damage_dealt", "Damage dealt", "Capped damage to enemies, per round.",
                    "sum: enrich.hurt.capped_damage\non: damage_dealt\nmatch: { enemy: true }\nper: round"),
                Prefab("rounds_survived", "Rounds survived", "Rounds this player was alive at the end of.",
                    "count: round_ended\nwhile: player.alive\nper: match"),
                Prefab("opening_kills", "Opening kills", "Enemy kills before the round's first death.",
                    "count: kill\nmatch: { enemy: true }\nwhile: round.no_deaths_yet\nper: match"),
                Prefab("side", "Side", "The player's team at freeze end, per round.",
                    "capture: player.team\non: round_freeze_end\nper: round"),
            ]),
            new GalleryGroup("Highlights",
            [
                Highlight("multi_kill", "Multi-kill", "Fires when a player's enemy kills in a round reach three; reads the Enemy kills prefab.",
                    "when: enemy_kills >= 3\nper: round\nscore: 80\ntitle: \"{player.name} multi-kill (round {round.number})\""),
                Highlight("opening_kill", "Opening kill", "Fires on a player's first kill before any death in the round; reads the Opening kills prefab.",
                    "when: opening_kills >= 1\nper: round\nscore: 60\ntitle: \"{player.name} opening kill (round {round.number})\""),
                Highlight("highlight", "Blank highlight", "A when: to fill in; wire a stat into it or type the test. Starts on the survived flag, which every ruleset can read.",
                    "when: player.survived\nper: round\nscore: 50\ntitle: \"{player.name} (round {round.number})\""),
            ]),
            new GalleryGroup("Stats",
            [
                new GalleryItem(GalleryItemKind.Stat, "count", "count", "Counts a view's events per scope.",
                    "count: kill\nmatch: { enemy: true }\nper: round"),
                new GalleryItem(GalleryItemKind.Stat, "sum", "sum", "Adds an event value per scope.",
                    "sum: enrich.hurt.capped_damage\non: damage_dealt\nmatch: { enemy: true }\nper: round"),
                new GalleryItem(GalleryItemKind.Stat, "capture", "capture", "Keeps a value as it is when the event fires.",
                    "capture: player.team\non: round_freeze_end\nper: round"),
                new GalleryItem(GalleryItemKind.Stat, "flag", "flag", "True when its expression holds.",
                    "flag:\n  when: \"round_kills > 0\"\nper: round"),
                new GalleryItem(GalleryItemKind.Stat, "compute", "compute", "A formula over sibling stats, at the scope's end.",
                    "compute: \"round_kills / max(round_deaths, 1)\"\nper: match"),
                // The loader takes thresholds only as { min, target } maps; a bare number list is dropped with the stat.
                new GalleryItem(GalleryItemKind.Stat, "tally", "tally", "Counts the scopes in which a sibling reached a threshold.",
                    "tally: round_kills\nthresholds:\n  - { min: 3, target: rounds_3k }\nper: match"),
                new GalleryItem(GalleryItemKind.Stat, "streak", "streak", "The longest run of events closer together than the window, in ticks.",
                    "streak: kill\nmatch: { enemy: true }\nwindow: 640\nmin_streak: 2\nper: match"),
                new GalleryItem(GalleryItemKind.Stat, "bucket", "bucket", "Counts, or sums a value, per key such as the weapon.",
                    "bucket: kill\nmatch: { enemy: true }\nkey: event.Weapon\nper: match"),
                new GalleryItem(GalleryItemKind.Stat, "rate", "rate", "A per-key ratio of two sibling buckets on the same key.",
                    "rate: { of: kills_by_weapon, per: shots_by_weapon }\nper: match"),
                new GalleryItem(GalleryItemKind.Stat, "burst", "burst", "A pulse when enough events land inside a window, for multi-kill highlights.",
                    "burst: kill\nmatch: { enemy: true }\nwindow: \"3s\"\nmin_streak: 2"),
                new GalleryItem(GalleryItemKind.Define, "define", "define",
                    "A named trigger: a view with its own match, while and where, that stats fire on.", "on: kill"),
            ]),
            new GalleryGroup("Surfacing",
            [
                new GalleryItem(GalleryItemKind.Table, "table", "Table",
                    "An export table on the ruleset's grain, with no columns yet; wire a stat into it to add one.", "per: player_round\ncolumns: []")
            ]),
            new GalleryGroup("Gates", [.. engineGates, .. ownFlags], gateSections),
            new GalleryGroup("Views", views, viewSections, ExpandedByDefault: false)
        ];
        if (shelf.Count > 0)
        {
            groups.Insert(2, new GalleryGroup("Your defines", shelf));
        }

        return groups;
    }

    /// <summary>
    ///     The trigger defines of one of the user's other rulesets as shelf items: the body as the file holds
    ///     it, placed under the define's own name. A define that is not a trigger (a list, a macro, a map) is skipped.
    /// </summary>
    /// <param name="rulesetId">The ruleset the text belongs to, named in the hint.</param>
    /// <param name="text">That ruleset's text.</param>
    public static IReadOnlyList<GalleryItem> DefinesOf(string rulesetId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<GalleryItem> items = [];
        foreach (string name in RulesetEdits.DefineNames(text))
        {
            string body;
            try
            {
                body = RulesetEntryText.DefineBodyOf(text, name);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (!body.Split('\n').Any(l => l.TrimStart().StartsWith("on:", StringComparison.Ordinal)))
            {
                continue;
            }

            string first = body.Split('\n')[0].Trim();
            items.Add(new GalleryItem(GalleryItemKind.Define, name, name, $"from {rulesetId}: {first}; dropped here as a copy", body, CopiesName: true));
        }

        return items;
    }

    /// <summary>
    ///     The groups narrowed to the items matching <paramref name="query" /> by title, name or hint, every
    ///     shown group opened; a group with no match is left out. An empty query is the gallery as built.
    /// </summary>
    public static IReadOnlyList<GalleryGroup> Filter(IReadOnlyList<GalleryGroup> groups, string? query)
    {
        ArgumentNullException.ThrowIfNull(groups);
        string trimmed = query?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return groups;
        }

        List<GalleryGroup> shown = [];
        foreach (GalleryGroup group in groups)
        {
            List<GallerySection> sections = [];
            foreach (GallerySection section in group.Sections)
            {
                List<GalleryItem> items = [.. section.Items.Where(i => i.Matches(trimmed))];
                if (items.Count > 0)
                {
                    sections.Add(new GallerySection(section.Title, items));
                }
            }

            if (sections.Count > 0)
            {
                shown.Add(new GalleryGroup(group.Title, [.. sections.SelectMany(s => s.Items)], sections) { IsExpanded = true });
            }
        }

        return shown;
    }

    private static GalleryItem ViewItem(CatalogView view)
    {
        string facets = view.Facets.Count == 0 ? "no facets" : "facets: " + string.Join(", ", view.Facets.Select(f => f.Name));
        string hint = string.IsNullOrWhiteSpace(view.Note) ? $"fires on {view.Event}; {facets}" : view.Note;
        return new GalleryItem(GalleryItemKind.View, view.Name, view.Name, hint, $"count: {view.Name}\nper: round");
    }

    private static GalleryItem Gate(string path, string hint) => new(GalleryItemKind.Gate, path, path, hint);

    private static GalleryItem Prefab(string id, string title, string hint, string body) =>
        new(GalleryItemKind.Prefab, id, title, hint, body);

    private static GalleryItem Highlight(string id, string title, string hint, string body) =>
        new(GalleryItemKind.Highlight, id, title, hint, body);
}
