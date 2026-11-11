#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Playback2D.Levels;

/// <summary>
///     The names a map's floors are shown under. A map with names of its own (Nuke, Vertigo) uses them;
///     any other two-floor map reads "Lower floor" / "Upper floor", and three or more count up from the
///     bottom as "Level 1", "Level 2", ...
///     <para>
///         Display only. A Z band is never part of the name: it means nothing to a player, and the level
///         strip's tooltip still carries it for anyone debugging the split.
///     </para>
/// </summary>
[PublicAPI]
public static class LevelNames
{
    private static readonly string[] _upperLower = ["Lower", "Upper"];

    // Two-floor maps whose floors players call by name, lowest first. Only consulted when the map
    // actually splits into that many floors; a histogram split into three falls back to "Level N".
    private static readonly Dictionary<string, string[]> _named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de_nuke"] = _upperLower,
        ["de_vertigo"] = _upperLower
    };

    /// <summary>The name of one floor.</summary>
    /// <param name="mapName">The map, e.g. <c>de_nuke</c>; a workshop path is reduced to its last segment. Null or empty for none.</param>
    /// <param name="index">The floor's position, 0 = lowest.</param>
    /// <param name="count">How many floors the map has.</param>
    public static string For(string? mapName, int index, int count)
    {
        if (count == 2 && index is 0 or 1)
        {
            return _named.TryGetValue(BaseName(mapName), out string[]? names)
                ? names[index]
                : index == 0 ? "Lower floor" : "Upper floor";
        }

        return string.Create(CultureInfo.InvariantCulture, $"Level {index + 1}");
    }

    private static string BaseName(string? mapName)
    {
        if (string.IsNullOrEmpty(mapName))
        {
            return "";
        }

        int slash = mapName.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? mapName[(slash + 1)..] : mapName;
    }
}
