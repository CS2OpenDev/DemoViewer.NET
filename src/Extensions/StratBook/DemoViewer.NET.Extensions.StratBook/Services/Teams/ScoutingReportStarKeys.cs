namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     Where a Scouting Report line sits: the map, the side, the layer, the buy and (for the AWP trail only)
///     the time window. A star, an edit or a dismissal is filed under <see cref="ScoutingReportStarKeys.Key" />
///     of the scope plus the line's own text, so the same words starred under two buys are two stars.
/// </summary>
/// <param name="Map">The map, or empty for a line about the team as a whole.</param>
/// <param name="Side">"CT", "T", or empty when the line is not about one side.</param>
/// <param name="Layer">The layer id, or a team-wide group: <c>pool</c>, <c>period</c>, <c>veto</c>.</param>
/// <param name="Buy">"All", "Eco", "Semi", "Force", "Full", or empty when the line is not about a buy.</param>
/// <param name="Window">The AWP trail window ("0:15", "0:30", "contact"); empty everywhere else.</param>
public sealed record ScoutingScope(string Map, string Side, string Layer, string Buy = "", string Window = "");

/// <summary>
///     The star key format, <c>map|side|layer|buy|window|text</c>, and the schema-1 remap. The text is the line's
///     label (what it counts, never its number), so a star survives a rebuild that moves the number.
/// </summary>
public static class ScoutingReportStarKeys
{
    private const char Separator = '|';

    /// <summary>The key of a line in a scope.</summary>
    /// <param name="scope">Where the line sits.</param>
    /// <param name="text">The line's label.</param>
    public static string Key(ScoutingScope scope, string text)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return string.Join(Separator, Clean(scope.Map), Clean(scope.Side), Clean(scope.Layer), Clean(scope.Buy),
            Clean(scope.Window), Clean(text));
    }

    /// <summary>The scope and text of a key in the current format, or null for any other key (a note's, an old one).</summary>
    /// <param name="key">The key.</param>
    public static (ScoutingScope Scope, string Text)? Parse(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string[] parts = key.Split(Separator);
        return parts.Length == 6
            ? (new ScoutingScope(parts[0], parts[1], parts[2], parts[3], parts[4]), parts[5])
            : null;
    }

    /// <summary>"de_nuke CT · Full", the scope as the one-pager preview names it; empty for a team-wide line.</summary>
    /// <param name="scope">The scope.</param>
    public static string Where(ScoutingScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        string head = string.Join(' ', new[] { scope.Map, scope.Side }.Where(s => s.Length > 0));
        List<string> parts = head.Length > 0 ? [head] : [];
        if (scope.Buy.Length > 0 && scope.Buy != "All")
        {
            parts.Add(scope.Buy + " buy");
        }

        if (scope.Window.Length > 0)
        {
            parts.Add("to " + scope.Window);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    ///     A schema-1 key in the current format, or null when there is no mapping (the key is then kept as it is:
    ///     user data is never dropped). Old keys were a section slug plus what the line counted:
    ///     <c>map|de_nuke</c>, <c>map|deciders</c>, <c>heatmap|de_nuke|full buy</c>,
    ///     <c>openings|de_nuke T · first utility 5-10 s</c>, <c>postplant|…</c>, <c>situational|…</c>,
    ///     <c>period|last</c>, <c>veto|{id}</c>.
    /// </summary>
    /// <param name="key">The old key.</param>
    public static string? Remap(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        int bar = key.IndexOf(Separator, StringComparison.Ordinal);
        if (bar <= 0)
        {
            return null;
        }

        string slug = key[..bar];
        string rest = key[(bar + 1)..];
        switch (slug)
        {
            case "map":
                return rest == "deciders"
                    ? Key(new ScoutingScope("", "", "pool"), "deciders")
                    : Key(new ScoutingScope(rest, "", "pool"), "record");
            case "heatmap":
            {
                string[] parts = rest.Split(Separator);
                return parts.Length == 2 && BuyFromLabel(parts[1]) is { } buy
                    ? Key(new ScoutingScope(parts[0], "CT", "setups", buy), "read")
                    : null;
            }
            case "openings":
            case "postplant":
            case "situational":
            {
                if (SplitTitle(rest) is not { } t)
                {
                    return null;
                }

                string? layer = slug switch
                {
                    "openings" => OpeningsLayer(t.Text),
                    "postplant" => t.Side == "T" ? "plants" : "retakes",
                    _ => "econ"
                };
                return layer is null ? null : Key(new ScoutingScope(t.Map, t.Side, layer, "All"), t.Text);
            }
            case "period":
                return Key(new ScoutingScope("", "", "period"), rest);
            case "veto":
                return Key(new ScoutingScope("", "", "veto"), rest);
            default:
                return null;
        }
    }

    /// <summary>The buy filter's word for the fact vocabulary's label ("full buy" to "Full"); null for pistol or unknown.</summary>
    /// <param name="label">The label.</param>
    public static string? BuyFromLabel(string label) => label switch
    {
        "eco" => "Eco",
        "semi buy" => "Semi",
        "force buy" => "Force",
        "full buy" => "Full",
        _ => null
    };

    // "de_nuke T · first utility 5-10 s" or "de_nuke T won pistol, won the bonus": the map, the side, and the
    // rest, which is what the new layer rows file their stars under.
    private static (string Map, string Side, string Text)? SplitTitle(string title)
    {
        int space = title.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0)
        {
            return null;
        }

        string map = title[..space];
        string after = title[(space + 1)..];
        foreach (string side in (string[])["CT", "T"])
        {
            if (after.StartsWith(side + " · ", StringComparison.Ordinal))
            {
                return (map, side, after[(side.Length + 3)..]);
            }

            if (after.StartsWith(side + " ", StringComparison.Ordinal))
            {
                return (map, side, after[(side.Length + 1)..]);
            }
        }

        return null;
    }

    private static string? OpeningsLayer(string text)
    {
        if (text.StartsWith("first utility", StringComparison.Ordinal))
        {
            return "util";
        }

        if (text.StartsWith("lurk", StringComparison.Ordinal))
        {
            return "lurks";
        }

        if (text.StartsWith("site ", StringComparison.Ordinal))
        {
            return "plants";
        }

        return text.StartsWith("first contact", StringComparison.Ordinal)
               || text.StartsWith("entry", StringComparison.Ordinal) || text == "rounds"
            ? "opening"
            : null;
    }

    private static string Clean(string? part) => (part ?? "").Replace(Separator, '/');
}
