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
    Prefab
}

/// <summary>One thing the gallery offers to drag.</summary>
/// <param name="Kind">What dropping it does.</param>
/// <param name="Name">The catalog name or the gate path, which is what the YAML gets.</param>
/// <param name="Title">What the gallery shows.</param>
/// <param name="Hint">One line on what it is, for the tooltip.</param>
/// <param name="Body">For a stat or a prefab, the entry's body at one indentation, without its id line.</param>
public sealed record GalleryItem(GalleryItemKind Kind, string Name, string Title, string Hint, string? Body = null)
{
    public string KindLabel => Kind.ToString().ToUpperInvariant();
}

/// <summary>One group of the gallery.</summary>
public sealed record GalleryGroup(string Title, IReadOnlyList<GalleryItem> Items);

/// <summary>
///     The gallery a user drags from: the stat kinds, the catalog's views, the gates the corpus uses, and
///     prefabs for the stats most rulesets start with. Views come from the catalog; the gate paths are the
///     v2 spelling, which the catalog does not carry by that name.
/// </summary>
public static class CanvasGallery
{
    public static IReadOnlyList<GalleryGroup> Build(CatalogRoot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return
        [
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
                new GalleryItem(GalleryItemKind.Stat, "tally", "tally", "Counts the scopes in which a sibling reached a threshold.",
                    "tally: round_kills\nthresholds: [1, 2, 3, 4, 5]\nper: match"),
            ]),
            new GalleryGroup("Views", [.. catalog.Views.OrderBy(v => v.Name, StringComparer.Ordinal).Select(ViewItem)]),
            new GalleryGroup("Gates",
            [
                Gate("round.active", "while the round is live, after freeze time and before it is decided"),
                Gate("match.live", "while the match is live, between its start and its end"),
                Gate("player.alive", "while this player is alive"),
                Gate("round.no_deaths_yet", "until the round's first death"),
                Gate("round.bomb.was_planted", "after the bomb is planted this round"),
                Gate("player.traded", "once this player has been traded this round"),
                Gate("player.survived", "once this player has survived the round"),
            ]),
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
            ])
        ];
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
}
