#region

using DemoViewer.NET.Modules.Library;

#endregion

namespace DemoViewer.NET.ViewModels.Library;

/// <summary>
///     The Library tab's built-in filters as one predicate: free-text search over file name, map and players,
///     the multi-select map filter (by <see cref="DemoEntry.MapDisplay" />, empty = all maps) and the player
///     filter (null = all players). The Library tab and the transfer wizard's demo picker share it, so a demo
///     the tab shows for a search is the one the picker offers. Contributed filters stay with the tab.
/// </summary>
/// <param name="SearchText">The search box text; blank matches everything.</param>
/// <param name="Maps">Checked map display names, compared case-insensitively. Empty matches every map.</param>
/// <param name="Player">The chosen player, or null for all players.</param>
public sealed record LibraryFilterPredicate(string? SearchText, IReadOnlySet<string> Maps, string? Player)
{
    /// <summary>Matches everything.</summary>
    public static LibraryFilterPredicate None { get; } =
        new(null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);

    /// <summary>True when no part of the predicate filters anything.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(SearchText) && Maps.Count == 0 && Player is null;

    /// <summary>Builds the predicate from the map chips' checked state.</summary>
    public static LibraryFilterPredicate From(string? searchText, IEnumerable<MapFilterItem> mapFilters,
        string? player) =>
        new(searchText,
            mapFilters.Where(m => m.IsSelected).Select(m => m.Display).ToHashSet(StringComparer.OrdinalIgnoreCase),
            player);

    /// <summary>Whether <paramref name="entry" /> passes every part of the predicate.</summary>
    public bool Matches(DemoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string needle = SearchText.Trim();
            if (!entry.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !(entry.MapName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                && !entry.Players.Any(p => p.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // A caller may hand in an ordinal set, so the case-insensitive match is done here rather than by the set.
        if (Maps.Count > 0 && !Maps.Contains(entry.MapDisplay, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return Player is null
            || entry.Players.Any(p => string.Equals(p, Player, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     The map chips for <paramref name="entries" />: one per distinct display name, "Unknown" left out,
    ///     sorted, each keyed by a raw map name for the accent dot. The Library tab builds the same set.
    /// </summary>
    public static List<(string Display, string MapKey)> MapGroups(IEnumerable<DemoEntry> entries) =>
    [
        .. entries
            .Where(e => e.MapDisplay is { Length: > 0 } && e.MapDisplay != "Unknown")
            .GroupBy(e => e.MapDisplay, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Display: g.Key,
                MapKey: g.Select(e => e.MapName).FirstOrDefault(m => m is { Length: > 0 }) ?? g.Key))
            .OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase)
    ];
}
