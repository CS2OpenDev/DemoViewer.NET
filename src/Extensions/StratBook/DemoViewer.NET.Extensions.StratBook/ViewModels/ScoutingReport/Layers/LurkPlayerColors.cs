using DemoViewer.NET.Extensions.Sdk.Ui;

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     The colour of each player on the Scouting Report's players table: the five members with the most demos
///     of the team's newest roster, in that order, take Player1 to Player5. The Lurks layer uses the same rule,
///     so a lurker's bar matches the player's row.
/// </summary>
public static class LurkPlayerColors
{
    private static readonly ScoutingColor[] _palette =
        [ScoutingColor.Player1, ScoutingColor.Player2, ScoutingColor.Player3, ScoutingColor.Player4, ScoutingColor.Player5];

    /// <summary>Name to colour; empty when the team or its roster is unknown.</summary>
    /// <param name="sources">The services.</param>
    /// <param name="teamId">The team.</param>
    public static IReadOnlyDictionary<string, ScoutingColor> For(ScoutingReportSources sources, Guid teamId)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Dictionary<string, ScoutingColor> colors = new(StringComparer.Ordinal);
        if (sources.Teams.AllTeams.FirstOrDefault(t => t.Id == teamId) is not { } team
            || team.Rosters.OrderByDescending(r => r.Since).FirstOrDefault() is not { } roster)
        {
            return colors;
        }

        int i = 0;
        foreach (string name in sources.Teams.MembersOf(teamId, roster.Id).Values.OrderByDescending(m => m.Count)
                     .Take(_palette.Length).Select(m => DisplayText.Sanitize(m.LastName)))
        {
            colors.TryAdd(name, _palette[i++ % _palette.Length]);
        }

        return colors;
    }
}
