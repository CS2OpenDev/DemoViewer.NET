#region

using DemoViewer.NET.Modules.Library;

#endregion

namespace DemoViewer.NET.ViewModels.Library;

/// <summary>
///     The Library tab's filters as one predicate: free-text search over file name, map and players, the
///     multi-select map filter (by <see cref="DemoEntry.MapDisplay" />, empty = all maps), the player filter
///     (null = all players) and the contributed filters such as the Team filter. The Library tab and the transfer
///     wizard's demo picker share it, so a demo the tab shows for a search is the one the picker offers.
/// </summary>
/// <param name="SearchText">The search box text; blank matches everything.</param>
/// <param name="Maps">Checked map display names, compared case-insensitively. Empty matches every map.</param>
/// <param name="Player">The chosen player, or null for all players.</param>
/// <param name="Contributed">The contributed filters' choices, each of which a demo must pass. Null for none.</param>
public sealed record LibraryFilterPredicate(
    string? SearchText,
    IReadOnlySet<string> Maps,
    string? Player,
    IReadOnlyList<ContributedFilterChoice>? Contributed = null)
{
    /// <summary>Matches everything.</summary>
    public static LibraryFilterPredicate None { get; } =
        new(null, new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);

    /// <summary>The checked map display names as a case-insensitive set, whatever set the caller handed in.</summary>
    public IReadOnlySet<string> Maps { get; } = CaseInsensitive(Maps);

    /// <summary>True when no part of the predicate filters anything.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(SearchText) && Maps.Count == 0 && Player is null
                           && Contributed?.All(c => c.IsEmpty) != false;

    /// <summary>Builds the predicate from the map chips' checked state.</summary>
    public static LibraryFilterPredicate From(string? searchText, IEnumerable<MapFilterItem> mapFilters,
        string? player, IReadOnlyList<ContributedFilterChoice>? contributed = null) =>
        new(searchText,
            mapFilters.Where(m => m.IsSelected).Select(m => m.Display).ToHashSet(StringComparer.OrdinalIgnoreCase),
            player, contributed);

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

        if (Maps.Count > 0 && !Maps.Contains(entry.MapDisplay))
        {
            return false;
        }

        if (Player is not null
            && !entry.Players.Any(p => string.Equals(p, Player, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return Contributed is null || Contributed.All(c => c.Matches(entry));
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

    private static HashSet<string> CaseInsensitive(IReadOnlySet<string> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        return maps is HashSet<string> set && ReferenceEquals(set.Comparer, StringComparer.OrdinalIgnoreCase)
            ? set
            : new HashSet<string>(maps, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>A contributed <see cref="LibraryFilter" /> as a host offers it, before anything is picked.</summary>
/// <param name="Filter">The contributed filter.</param>
/// <param name="DemoOf">How an entry is handed to the filter; <see cref="DemoEntry.ToLibraryDemo" /> when null.</param>
public sealed record ContributedLibraryFilter(LibraryFilter Filter, Func<DemoEntry, LibraryDemo>? DemoOf = null)
{
    /// <summary>The choice of <paramref name="keys" /> in this filter.</summary>
    public ContributedFilterChoice Pick(IReadOnlyCollection<string> keys) => new(Filter, keys, DemoOf);
}

/// <summary>
///     What is picked in one contributed <see cref="LibraryFilter" />: a demo passes when the filter matches it
///     for any picked key. No keys, or the "" (All) key, passes every demo. The Library tab picks one key; the
///     transfer wizard's picker may pick several.
/// </summary>
/// <param name="Filter">The contributed filter.</param>
/// <param name="Keys">The picked keys.</param>
/// <param name="DemoOf">How an entry is handed to the filter; <see cref="DemoEntry.ToLibraryDemo" /> when null.</param>
public sealed record ContributedFilterChoice(
    LibraryFilter Filter,
    IReadOnlyCollection<string> Keys,
    Func<DemoEntry, LibraryDemo>? DemoOf = null)
{
    /// <summary>True when the choice passes every demo.</summary>
    public bool IsEmpty => Keys.Count == 0 || Keys.Any(k => k.Length == 0);

    /// <summary>Whether <paramref name="entry" /> passes the filter for one of the picked keys.</summary>
    public bool Matches(DemoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (IsEmpty)
        {
            return true;
        }

        LibraryDemo demo = DemoOf?.Invoke(entry) ?? entry.ToLibraryDemo();
        return Keys.Any(k => Filter.Matches(demo, k));
    }
}
