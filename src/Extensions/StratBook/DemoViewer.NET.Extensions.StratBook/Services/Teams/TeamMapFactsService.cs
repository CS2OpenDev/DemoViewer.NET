#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>One demo of a team, as the Scouting Report header lists it: map, date, result and opponent.</summary>
/// <param name="Path">The demo.</param>
/// <param name="Map">Its map.</param>
/// <param name="Ticks">Its date: the file time, Team Identity's own stand-in for a match date.</param>
/// <param name="Won">The team won it; null when the score did not resolve.</param>
/// <param name="OpponentId">The team on the other side, when Team Identity named one.</param>
/// <param name="Score">The team's final score, when the library has one.</param>
/// <param name="OpponentScore">The other side's final score, when the library has one.</param>
public sealed record TeamDemoFacts(string Path, string Map, long Ticks, bool? Won, Guid? OpponentId,
    int? Score = null, int? OpponentScore = null);

/// <summary>
///     One map's round numbers for one team, counted on the side the team actually played each round
///     (<see cref="TeamIdentityService.SideAtRound" /> joined to <see cref="RoundFacts.WinnerSide" />). Not
///     <see cref="MapPoolMapRow.CtRoundsWon" />, which counts the CT side of the match, whoever stood on it.
/// </summary>
public sealed class TeamMapFacts
{
    public required string Map { get; init; }

    public int CtPlayed { get; init; }

    public int CtWon { get; init; }

    public int TPlayed { get; init; }

    public int TWon { get; init; }

    public int PistolsPlayed { get; init; }

    public int PistolsWon { get; init; }

    /// <summary>Seconds from freeze end to the plant over the team's T rounds with a plant; null with none.</summary>
    public double? PlantMedianSeconds { get; init; }

    /// <summary>
    ///     Non-pistol rounds, either side, whose round captures were collected: the sample <see cref="AwpBought" />
    ///     is counted in. 0 when no demo on the map has captures.
    /// </summary>
    public int AwpRoundsRead { get; init; }

    /// <summary>Of <see cref="AwpRoundsRead" />, the rounds the team bought at least one AWP in.</summary>
    public int AwpBought { get; init; }

    public double? CtShare => CtPlayed > 0 ? (double)CtWon / CtPlayed : null;

    public double? TShare => TPlayed > 0 ? (double)TWon / TPlayed : null;
}

/// <summary>A team's <see cref="TeamMapFacts" /> per map, and the demos that had no Round Facts to count.</summary>
public sealed class TeamMapFactsSet
{
    public IReadOnlyList<TeamMapFacts> Maps { get; init; } = [];

    public int DemosRead { get; init; }

    public int DemosWithoutRoundFacts { get; init; }

    /// <summary>The map's facts, or null when the team has no counted round on it.</summary>
    /// <param name="map">The map.</param>
    public TeamMapFacts? For(string map) =>
        Maps.FirstOrDefault(m => string.Equals(m.Map, map, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
///     The team-scoped numbers the Scouting Report header and map pool show beside the
///     <see cref="MapPoolRecord" />: the demos by date (<see cref="Demos" />, library only, cheap) and the
///     round-level facts per map (<see cref="Build" />, which reads Round Facts and runs off the UI thread).
/// </summary>
public static class TeamMapFactsService
{
    // A record without a clock header is still a CS2 demo; the frame clock of every build here is 64.
    private const int FallbackTickRate = 64;

    /// <summary>The team's demos, newest first, with result and opponent.</summary>
    /// <param name="teams">Team Identity.</param>
    /// <param name="library">The library: map, score and file time per demo.</param>
    /// <param name="teamId">The team.</param>
    public static IReadOnlyList<TeamDemoFacts> Demos(TeamIdentityService teams, IExtensionLibrary library, Guid teamId)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(library);
        List<TeamDemoFacts> demos = [];
        foreach ((DemoRef demo, int side, TeamAssignment assignment) in teams.SidesOf(teamId))
        {
            if (library.Find(demo.Path) is not { MapName: { Length: > 0 } map } entry)
            {
                continue;
            }

            bool? won = null;
            int? score = null, against = null;
            if (entry is { CtScore: { } ct, TScore: { } t })
            {
                (score, against) = side == 3 ? (ct, t) : (t, ct);
                if (ct != t)
                {
                    won = side == 3 ? ct > t : t > ct;
                }
            }

            demos.Add(new TeamDemoFacts(demo.Path, map, entry.Modified.Ticks, won,
                assignment.Side(side == 2 ? 3 : 2).TeamId, score, against));
        }

        return [.. demos.OrderByDescending(d => d.Ticks)];
    }

    /// <summary>The team's round facts per map. Reads Round Facts per demo: call it off the UI thread.</summary>
    /// <param name="teams">Team Identity, for the side per round.</param>
    /// <param name="library">The library, for the map.</param>
    /// <param name="roundFacts">The Round Facts rows per demo.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="captures">The round captures, for AWP bought; null leaves it unread.</param>
    public static TeamMapFactsSet Build(TeamIdentityService teams, IExtensionLibrary library, IRoundFacts roundFacts,
        Guid teamId, RoundCapturesReader? captures = null)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(roundFacts);
        Dictionary<string, Counter> maps = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        int read = 0;
        int without = 0;
        foreach ((DemoRef demo, _, _) in teams.SidesOf(teamId))
        {
            JobScope.ThrowIfStopped();
            if (!seen.Add(demo.Path) || library.Find(demo.Path) is not { MapName: { Length: > 0 } map } record)
            {
                continue;
            }

            if (roundFacts.TryGet(demo.Path) is not { } rows)
            {
                without++;
                continue;
            }

            read++;
            int rate = rows.Clock?.TickRate is > 0 and var r ? r : FallbackTickRate;
            if (!maps.TryGetValue(map, out Counter? counter))
            {
                counter = new Counter();
                maps[map] = counter;
            }

            DemoCaptures? demoCaptures = captures?.Read(demo.Path, map, record.Sha256) is { Collected: true } c ? c : null;
            foreach (RoundFacts round in rows.Rounds)
            {
                if (round.IsLive && teams.SideAtRound(demo.Path, teamId, round.Number) is { } side)
                {
                    counter.Add(round, side, rate);
                    if (demoCaptures?.Round(round.Number) is { } view)
                    {
                        counter.AddAwp(round, side, view);
                    }
                }
            }
        }

        return new TeamMapFactsSet
        {
            Maps = [.. maps.Select(m => m.Value.ToFacts(m.Key))],
            DemosRead = read,
            DemosWithoutRoundFacts = without
        };
    }

    /// <summary>The median of a list, or null when it is empty.</summary>
    /// <param name="values">The values.</param>
    public static double? Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return null;
        }

        double[] sorted = [.. values.Order()];
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private sealed class Counter
    {
        private readonly List<double> _plants = [];
        private int _ctPlayed, _ctWon, _tPlayed, _tWon, _pistols, _pistolsWon, _awpRead, _awpBought;

        // A pistol round cannot buy an AWP, so it is left out of the sample.
        public void AddAwp(RoundFacts round, int side, RoundCaptureView view)
        {
            if ((side == 3 ? round.Ct : round.T).BuyType == BuyType.Pistol || view.IsPistol)
            {
                return;
            }

            _awpRead++;
            _awpBought += view.AwpBought(side) ? 1 : 0;
        }

        public void Add(RoundFacts round, int side, int rate)
        {
            if (!round.IsDecided())
            {
                return;
            }

            bool won = round.WinnerSide == side;
            if (side == 3)
            {
                _ctPlayed++;
                _ctWon += won ? 1 : 0;
            }
            else
            {
                _tPlayed++;
                _tWon += won ? 1 : 0;
                if (round.PlantTick is { } plant && plant >= round.FreezeEndTick)
                {
                    _plants.Add((plant - round.FreezeEndTick) / (double)rate);
                }
            }

            if ((side == 3 ? round.Ct : round.T).BuyType == BuyType.Pistol)
            {
                _pistols++;
                _pistolsWon += won ? 1 : 0;
            }
        }

        public TeamMapFacts ToFacts(string map) => new()
        {
            Map = map,
            CtPlayed = _ctPlayed,
            CtWon = _ctWon,
            TPlayed = _tPlayed,
            TWon = _tWon,
            PistolsPlayed = _pistols,
            PistolsWon = _pistolsWon,
            PlantMedianSeconds = Median(_plants),
            AwpRoundsRead = _awpRead,
            AwpBought = _awpBought
        };
    }
}
