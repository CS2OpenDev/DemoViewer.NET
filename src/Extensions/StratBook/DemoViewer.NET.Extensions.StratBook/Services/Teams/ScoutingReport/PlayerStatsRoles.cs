#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>What the demos say about one player on one map, for the role suggestion.</summary>
/// <param name="SteamId">The SteamID64 as text.</param>
/// <param name="AwpKills">AWP kills over the demos with facts.</param>
/// <param name="TOpenings">T-side opening duels taken.</param>
/// <param name="Entries">T rounds the player was the entry (Opening Tendencies).</param>
/// <param name="EntrySite">The site the player entered most, or empty.</param>
/// <param name="Lurks">T rounds the player lurked.</param>
/// <param name="CtPlace">The place held most in the CT setup window, or null.</param>
/// <param name="CtHeld">CT rounds at that place.</param>
/// <param name="CtOf">CT rounds with the player's setup positions.</param>
public sealed record PlayerRoleEvidence(
    string SteamId,
    int AwpKills,
    int TOpenings,
    int Entries,
    string EntrySite,
    int Lurks,
    string? CtPlace,
    int CtHeld,
    int CtOf);

/// <summary>The team-wide denominators the shares are over.</summary>
/// <param name="AwpKills">The roster's AWP kills.</param>
/// <param name="TOpenings">The roster's T-side opening duels.</param>
/// <param name="LurkRounds">T rounds the lurk detector could read.</param>
public sealed record PlayerRoleTotals(int AwpKills, int TOpenings, int LurkRounds);

/// <summary>A suggested role, the evidence line under it, and the T role column.</summary>
/// <param name="Role">"AWP", "Entry", "Lurk", "Anchor", "Support", or "—"; " · IGL" appended when set by hand.</param>
/// <param name="Note">"41% of AWP kills".</param>
/// <param name="TRole">"entry A", "lurk", "support", or "—".</param>
public sealed record PlayerRoleSuggestion(string Role, string Note, string TRole);

/// <summary>
///     Suggests one role per player from the evidence, each role to one player at most, in this order: the AWP
///     (the most AWP kills, at least <see cref="AwpShare" /> of the team's), the entry (the most T openings, at
///     least <see cref="EntryShare" />), the lurk (at least <see cref="LurkShare" /> of readable T rounds), then
///     anyone anchoring a bombsite in at least <see cref="AnchorShare" /> of their CT setups. Everyone else with
///     any evidence is support. IGL is never suggested; it is the user's pick.
/// </summary>
public static class PlayerStatsRoles
{
    public const double AwpShare = 0.35;

    public const int AwpMinKills = 3;

    public const double EntryShare = 0.25;

    public const double LurkShare = 0.25;

    public const double AnchorShare = 0.6;

    /// <summary>The suggestions, by SteamID.</summary>
    /// <param name="players">The roster's evidence.</param>
    /// <param name="totals">The denominators.</param>
    /// <param name="igl">The IGL the user picked, SteamID64 as text, or null.</param>
    public static IReadOnlyDictionary<string, PlayerRoleSuggestion> Suggest(IReadOnlyList<PlayerRoleEvidence> players,
        PlayerRoleTotals totals, string? igl)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(totals);
        Dictionary<string, (string Role, string Note)> roles = new(StringComparer.Ordinal);

        if (totals.AwpKills >= AwpMinKills
            && players.MaxBy(p => p.AwpKills) is { } awp
            && (double)awp.AwpKills / totals.AwpKills >= AwpShare)
        {
            roles[awp.SteamId] = ("AWP", $"{Percent((double)awp.AwpKills / totals.AwpKills)} of AWP kills");
        }

        if (totals.TOpenings > 0
            && players.Where(p => !roles.ContainsKey(p.SteamId)).MaxBy(p => p.TOpenings) is { } entry
            && (double)entry.TOpenings / totals.TOpenings >= EntryShare)
        {
            roles[entry.SteamId] = ("Entry", $"{Percent((double)entry.TOpenings / totals.TOpenings)} of T openings");
        }

        if (totals.LurkRounds > 0
            && players.Where(p => !roles.ContainsKey(p.SteamId)).MaxBy(p => p.Lurks) is { } lurk
            && (double)lurk.Lurks / totals.LurkRounds >= LurkShare)
        {
            roles[lurk.SteamId] = ("Lurk", $"lurked {Percent((double)lurk.Lurks / totals.LurkRounds)} of T rounds");
        }

        foreach (PlayerRoleEvidence p in players.Where(p => !roles.ContainsKey(p.SteamId)))
        {
            if (Site(p.CtPlace) is { } site && p.CtOf > 0 && (double)p.CtHeld / p.CtOf >= AnchorShare)
            {
                roles[p.SteamId] = ("Anchor", $"{site} anchor in {Percent((double)p.CtHeld / p.CtOf)} of setups");
            }
        }

        bool anyEvidence = totals.AwpKills > 0 || totals.TOpenings > 0 || totals.LurkRounds > 0
                           || players.Any(p => p.CtOf > 0);
        Dictionary<string, PlayerRoleSuggestion> result = new(StringComparer.Ordinal);
        foreach (PlayerRoleEvidence p in players)
        {
            (string role, string note) = roles.TryGetValue(p.SteamId, out (string, string) r)
                ? r
                : anyEvidence ? ("Support", "") : ("—", "");
            if (igl is not null && string.Equals(igl, p.SteamId, StringComparison.Ordinal))
            {
                role = role == "—" ? "IGL" : $"{role} · IGL";
                note = note.Length == 0 ? "IGL set by you" : $"{note}; IGL set by you";
            }

            string tRole = p.Entries > 0 && p.EntrySite.Length > 0 ? $"entry {p.EntrySite}"
                : p.Lurks > 0 ? "lurk"
                : totals.TOpenings > 0 || totals.LurkRounds > 0 ? "support"
                : "—";
            result[p.SteamId] = new PlayerRoleSuggestion(role, note, tRole);
        }

        return result;
    }

    // "A" or "B" for a bombsite place; null elsewhere.
    private static string? Site(string? place) => place switch
    {
        "BombsiteA" => "A",
        "BombsiteB" => "B",
        _ => null
    };

    private static string Percent(double share) =>
        Math.Round(share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
