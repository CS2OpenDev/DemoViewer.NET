#region

using DemoViewer.NET.Extensions.Sdk.Ui;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

/// <summary>One roster player and the colour the players table and the Setups marks share.</summary>
/// <param name="SteamId">The SteamID64 as text, the members table's key.</param>
/// <param name="Member">The member row.</param>
/// <param name="Color">Player1 to Player5, in table order.</param>
public sealed record ScoutingRosterPlayer(string SteamId, TeamIndexMember Member, ScoutingColor Color);

/// <summary>
///     The team's current roster in table order (most appearances first, ties by SteamID, five at most), each with
///     its colour. The one player colour rule of the Scouting Report: the players table, the Setups marks, the
///     lurkers and who opens all read it, so a player's swatch and their marks always match.
/// </summary>
public static class ScoutingPlayerColors
{
    private static readonly ScoutingColor[] _colors =
        [ScoutingColor.Player1, ScoutingColor.Player2, ScoutingColor.Player3, ScoutingColor.Player4, ScoutingColor.Player5];

    /// <summary>The current roster's players with their colours; empty with no roster.</summary>
    /// <param name="teams">Team Identity.</param>
    /// <param name="teamId">The team.</param>
    public static IReadOnlyList<ScoutingRosterPlayer> Roster(TeamIdentityService teams, Guid teamId)
    {
        ArgumentNullException.ThrowIfNull(teams);
        if (teams.AllTeams.FirstOrDefault(t => t.Id == teamId) is not { } team
            || team.Rosters.OrderByDescending(r => r.Since).FirstOrDefault() is not { } roster)
        {
            return [];
        }

        return
        [
            .. teams.MembersOf(teamId, roster.Id)
                .OrderByDescending(m => m.Value.Count)
                .ThenBy(m => m.Key, StringComparer.Ordinal)
                .Take(_colors.Length)
                .Select((m, i) => new ScoutingRosterPlayer(m.Key, m.Value, _colors[i]))
        ];
    }

    /// <summary>
    ///     The roster's colours keyed by SteamID64 and by display name, for rows that know a player by either (the
    ///     duels key by SteamID when the roster has one, the lurk samples by name); empty with no roster.
    /// </summary>
    /// <param name="teams">Team Identity.</param>
    /// <param name="teamId">The team.</param>
    public static IReadOnlyDictionary<string, ScoutingColor> Lookup(TeamIdentityService teams, Guid teamId)
    {
        Dictionary<string, ScoutingColor> colors = new(StringComparer.Ordinal);
        foreach (ScoutingRosterPlayer player in Roster(teams, teamId))
        {
            colors.TryAdd(player.SteamId, player.Color);
            colors.TryAdd(DisplayText.Sanitize(player.Member.LastName), player.Color);
        }

        return colors;
    }
}
