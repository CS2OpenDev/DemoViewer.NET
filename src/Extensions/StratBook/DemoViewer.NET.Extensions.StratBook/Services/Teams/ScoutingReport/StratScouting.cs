#region

using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>A mined pattern narrowed to the rounds that concern one team: its own, or the ones run against it.</summary>
/// <param name="Pattern">The library-wide pattern.</param>
/// <param name="Members">The pattern's rounds that concern the team, nearest the medoid first.</param>
public sealed record ScoutedStrat(MinedPattern Pattern, IReadOnlyList<MinedMember> Members)
{
    public int Support => Members.Count;

    /// <summary>Rounds the pattern's side won.</summary>
    public int Wins => Members.Count(m => m.Won == true);

    /// <summary>Rounds with a known winner.</summary>
    public int Known => Members.Count(m => m.Won is not null);

    /// <summary>Wins over rounds with a known winner; 0 with none known.</summary>
    public double WinRate => Known > 0 ? Wins / (double)Known : 0;

    /// <summary>How many teams ran it, counting the rounds whose team is known.</summary>
    public int Opponents => Members.Where(m => m.TeamId is not null).Select(m => m.TeamId!.Value).Distinct().Count();

    /// <summary>The round nearest the pattern's medoid: the one drawn and played.</summary>
    public MinedMember Representative => Members[0];

    /// <summary>The rounds the pattern's side lost.</summary>
    public IReadOnlyList<MinedMember> Losses => [.. Members.Where(m => m.Won == false)];
}

/// <summary>What the strat's lost rounds have in common on the other side.</summary>
/// <param name="Feature">The opponent's feature: "early Mid smoke".</param>
/// <param name="Rounds">The lost rounds that had it.</param>
/// <param name="Losses">The lost rounds the feature could be read in.</param>
/// <param name="WinsWith">The won rounds that had it.</param>
/// <param name="Wins">The won rounds the feature could be read in.</param>
public sealed record LostToFinding(string Feature, IReadOnlyList<MinedMember> Rounds, int Losses, int WinsWith, int Wins)
{
    /// <summary>"Lost to: early Mid smoke (3 of 4 losses)".</summary>
    public string Sentence => $"Lost to: {Feature} ({Rounds.Count} of {Losses} losses)";
}

/// <summary>
///     Strat Mining's patterns for the Scouting Report. Mining is library-wide, so a pattern's rounds are split by
///     the side the scouted team played in each one (Team Identity's <c>SideAtRound</c>): the team's own T strats
///     are the rounds where it was T, and the strats run against it are the T rounds where it was CT.
/// </summary>
public static class StratScouting
{
    /// <summary>The fewest rounds a strat, or a "lost to" counter, is claimed from.</summary>
    public const int MinSupport = 2;

    /// <summary>A CT grenade this soon after freeze end is called "early".</summary>
    public const double EarlySeconds = 20;

    /// <summary>
    ///     The team's own T patterns on a map: the rounds where the team was T, at least <see cref="MinSupport" />
    ///     of them, most run first.
    /// </summary>
    /// <param name="patterns">The library-wide patterns.</param>
    /// <param name="map">The map.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="teamSide">The team's side in a member's round (2 = T, 3 = CT), or null when unknown.</param>
    public static IReadOnlyList<ScoutedStrat> Own(IEnumerable<MinedPattern> patterns, string map, Guid teamId,
        Func<MinedMember, int?> teamSide)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(teamSide);
        // Without a side for the round, the miner's own stamp (the team on the pattern's side) decides.
        return Narrow(patterns, map, m => teamSide(m) is { } side ? side == 2 : m.TeamId == teamId)
            .OrderByDescending(s => s.Support)
            .ThenByDescending(s => s.Wins)
            .ThenBy(s => s.Pattern.Spread)
            .ThenBy(s => s.Pattern.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     The opponents' T patterns against the team on a map: the T rounds where the team was CT, at least
    ///     <see cref="MinSupport" /> of them, best win rate first.
    /// </summary>
    /// <param name="patterns">The library-wide patterns.</param>
    /// <param name="map">The map.</param>
    /// <param name="teamSide">The team's side in a member's round (2 = T, 3 = CT), or null when unknown.</param>
    public static IReadOnlyList<ScoutedStrat> Against(IEnumerable<MinedPattern> patterns, string map,
        Func<MinedMember, int?> teamSide)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(teamSide);
        return Narrow(patterns, map, m => teamSide(m) == 3)
            .OrderByDescending(s => s.WinRate)
            .ThenByDescending(s => s.Support)
            .ThenBy(s => s.Pattern.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<ScoutedStrat> Narrow(IEnumerable<MinedPattern> patterns, string map,
        Func<MinedMember, bool> keep) =>
        patterns
            .Where(p => p.Side == 2 && string.Equals(p.Map, map, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ScoutedStrat(p, [.. p.Members.Where(keep).OrderBy(m => m.Distance)]))
            .Where(s => s.Support >= MinSupport);

    /// <summary>
    ///     The "lost to" counter. Heuristic: for every round of the strat, the opponent CT's first grenade of the
    ///     round (its kind and landing place, "early" when inside <see cref="EarlySeconds" />) is the feature. The
    ///     counter is the feature most common across the lost rounds, claimed only when at least
    ///     <see cref="MinSupport" /> losses had it and it is a larger share of the losses than of the wins, so a
    ///     smoke the CTs throw every round is not blamed for the losses. Null otherwise: no claim beats a weak one.
    /// </summary>
    /// <param name="strat">The strat.</param>
    /// <param name="feature">The opponent's feature in a round, or null when it cannot be read.</param>
    public static LostToFinding? LostTo(ScoutedStrat strat, Func<MinedMember, string?> feature)
    {
        ArgumentNullException.ThrowIfNull(strat);
        ArgumentNullException.ThrowIfNull(feature);
        List<(MinedMember Round, string Feature)> lost =
        [
            .. strat.Members.Where(m => m.Won == false)
                .Select(m => (m, feature(m)))
                .Where(x => x.Item2 is not null)
                .Select(x => (x.m, x.Item2!))
        ];
        if (lost.Count < MinSupport)
        {
            return null;
        }

        List<string> won = [.. strat.Members.Where(m => m.Won == true).Select(feature).OfType<string>()];
        var top = lost.GroupBy(x => x.Feature, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First();
        int inLosses = top.Count();
        int inWins = won.Count(f => string.Equals(f, top.Key, StringComparison.Ordinal));
        double lossShare = inLosses / (double)lost.Count;
        double winShare = won.Count > 0 ? inWins / (double)won.Count : 0;
        if (inLosses < MinSupport || lossShare <= winShare)
        {
            return null;
        }

        return new LostToFinding(top.Key, [.. top.Select(x => x.Round)], lost.Count, inWins, won.Count);
    }

    /// <summary>"early Mid smoke": the first of a round's CT grenades, or null when there is none with a place.</summary>
    /// <param name="throws">The CT side's grenades of one round: kind, landing place, seconds after freeze end.</param>
    public static string? Opener(IEnumerable<(GrenadeKind Kind, string? Place, double Seconds)> throws)
    {
        ArgumentNullException.ThrowIfNull(throws);
        (GrenadeKind Kind, string? Place, double Seconds)? first = throws
            .Where(t => t.Seconds >= 0)
            .OrderBy(t => t.Seconds)
            .Cast<(GrenadeKind, string?, double)?>()
            .FirstOrDefault();
        if (first is not { Place: { Length: > 0 } place } at)
        {
            return null;
        }

        return $"{(at.Seconds <= EarlySeconds ? "early " : "")}{place} {Word(at.Kind)}";
    }

    /// <summary>The grenade's word in a sentence.</summary>
    /// <param name="kind">The kind.</param>
    public static string Word(GrenadeKind kind) => kind switch
    {
        GrenadeKind.Smoke => "smoke",
        GrenadeKind.Molotov or GrenadeKind.Incendiary => "molotov",
        GrenadeKind.He => "HE",
        GrenadeKind.Flash => "flash",
        _ => "decoy"
    };
}
