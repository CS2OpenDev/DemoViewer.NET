#region

using System.Text.RegularExpressions;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>
///     How the Scouting Report names maps and places on screen and in the export: <c>de_mirage</c> as
///     <c>Mirage</c> and <c>de_dust2</c> as <c>Dust2</c>, the app's Library convention, and a glued place name
///     such as <c>CTSpawn</c> or <c>BombsiteA</c> split into words the way the callout resolver shows it. Display
///     only: star keys, scopes and anything persisted keep the raw names, so a key never moves.
/// </summary>
public static partial class ScoutingNames
{
    private static readonly string[] _prefixes = ["de_", "cs_", "ar_", "dz_", "gd_", "coop_"];

    // Every shipped map's glued place names (the ones splitting changes), longest first so "CTSpawn" is not
    // read as part of a shorter name.
    private static readonly Lazy<(Regex Pattern, Dictionary<string, string> Display)?> _places = new(BuildPlaces);

    /// <summary>The map as people say it: the prefix dropped and the first letter raised.</summary>
    /// <param name="map">The map, as the demo header spells it.</param>
    public static string Map(string? map)
    {
        if (string.IsNullOrWhiteSpace(map))
        {
            return map ?? "";
        }

        string s = map;
        foreach (string prefix in _prefixes)
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }

        return s.Length == 0 ? map : char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>A place as people read it: <c>CTSpawn</c> to <c>CT Spawn</c>, <c>BombsiteA</c> to <c>A site</c>.</summary>
    /// <param name="place">A canonical place.</param>
    public static string Place(string? place) => string.IsNullOrEmpty(place)
        ? place ?? ""
        : SetupsDerivation.PlaceLabel(place) is var label && !string.Equals(label, place, StringComparison.Ordinal)
            ? label
            : CalloutResolver.SplitDisplay(place);

    /// <summary>
    ///     A sentence with every raw map name (<c>de_nuke</c>) and every glued shipped place name (<c>CTSpawn</c>)
    ///     in its display form. Words that are not a map or a known place are left alone.
    /// </summary>
    /// <param name="text">The text.</param>
    public static string Humanise(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        string mapped = text.Contains('_', StringComparison.Ordinal)
            ? MapId().Replace(text, m => Map(m.Value))
            : text;
        return _places.Value is { } places
            ? places.Pattern.Replace(mapped, m => places.Display[m.Value])
            : mapped;
    }

    private static (Regex, Dictionary<string, string>)? BuildPlaces()
    {
        Dictionary<string, string> display = new(StringComparer.Ordinal);
        foreach (string map in CanonicalPlaces.Maps)
        {
            foreach (string place in CanonicalPlaces.Embedded(map))
            {
                string shown = Place(place);
                if (!string.Equals(shown, place, StringComparison.Ordinal))
                {
                    display[place] = shown;
                }
            }
        }

        if (display.Count == 0)
        {
            return null;
        }

        string alternation = string.Join('|',
            display.Keys.OrderByDescending(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).Select(Regex.Escape));
        return (new Regex($@"(?<![\w|])(?:{alternation})(?![\w|])", RegexOptions.CultureInvariant), display);
    }

    [GeneratedRegex(@"\b(?:de|cs|ar|dz|gd|coop)_[a-z0-9_]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex MapId();
}
