#region

using DemoViewer.NET.Extensions.Sdk.Ui;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
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

    /// <summary>
    ///     The name the roster table shows for each of the team's players, by SteamID64: every roster's members,
    ///     the newest roster's name winning. A player's in-game name changes between demos ("sandman" one week,
    ///     "1Sandman" the next) and its case varies; the layers group by SteamID and print this name, so one player
    ///     is one row under one name everywhere on the page. Empty for a team with no roster.
    /// </summary>
    /// <param name="teams">Team Identity.</param>
    /// <param name="teamId">The team.</param>
    public static IReadOnlyDictionary<string, string> Names(TeamIdentityService teams, Guid teamId)
    {
        ArgumentNullException.ThrowIfNull(teams);
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        if (teams.AllTeams.FirstOrDefault(t => t.Id == teamId) is not { } team)
        {
            return names;
        }

        foreach (Roster roster in team.Rosters.OrderBy(r => r.Since))
        {
            foreach (KeyValuePair<string, TeamIndexMember> member in teams.MembersOf(teamId, roster.Id))
            {
                if (DisplayText.Sanitize(member.Value.LastName) is { Length: > 0 } name)
                {
                    names[member.Key] = name;
                }
            }
        }

        return names;
    }

    /// <summary>The key a layer groups a player on: the SteamID64 as text, or <c>name:</c> and the name without one.</summary>
    /// <param name="steamId">The SteamID64, or 0.</param>
    /// <param name="name">The name seen in the demo.</param>
    public static string KeyOf(ulong steamId, string name) => SetupRoundCapture.KeyOf(steamId, name);

    /// <summary>The name to print for a player key: the roster's, else the name the demo had.</summary>
    /// <param name="names">From <see cref="Names" />.</param>
    /// <param name="key">From <see cref="KeyOf" />.</param>
    /// <param name="seen">The name the demo had.</param>
    public static string NameOf(IReadOnlyDictionary<string, string> names, string key, string seen) =>
        names.TryGetValue(key, out string? name) ? name : DisplayText.Sanitize(seen);
}
