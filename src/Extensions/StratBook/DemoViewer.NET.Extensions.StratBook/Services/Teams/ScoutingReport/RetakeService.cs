#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>
///     The retake and save rules, per round, over Round Facts and the positions file (spec decision Q6).
///     <para>
///         <b>Alive at the plant</b>: a CT slot in the positions at the plant's sampled step that no kill
///         at or before the plant tick names.
///     </para>
///     <para>
///         <b>Reached the bomb</b>: such a CT is sampled, before the round's end, within
///         <see cref="SaveRadius" /> (XY) of the plant position and within <see cref="SaveHeight" /> of its
///         height, so a CT on Nuke's A site does not reach a bomb on B underneath it.
///     </para>
///     <para>
///         <b>Saved</b>: a CT alive at the plant, never killed in the round, that never reached the bomb,
///         in a round the CTs did not win. A CT who reached the bomb and survived a lost round is a failed
///         retake, not a save; a CT who stayed away in a round the CTs won did not save, the round was won.
///     </para>
///     <para>
///         <b>The round</b> is a save when it was lost, no CT reached the bomb and at least one CT saved;
///         otherwise the CTs went for it and it is a retake, won or lost (a retake that died on the way is
///         still a retake).
///     </para>
/// </summary>
public static class RetakeRules
{
    /// <summary>World units (XY) to the plant position within which a CT has reached the bomb; Q6's radius.</summary>
    public const int SaveRadius = 600;

    /// <summary>World units of height to the plant position within which a CT can have reached the bomb.</summary>
    public const int SaveHeight = 250;

    /// <summary>
    ///     Where the bomb went down: for now the planter's nearest positions sample at the plant tick
    ///     (<see cref="PostPlantService.PlantSpot" />). P3.1 replaces it with the exact plant position; every
    ///     retake read goes through here.
    /// </summary>
    /// <param name="positions">The demo's positions file.</param>
    /// <param name="round">The round.</param>
    public static RoundPosition? PlantPosition(RoundPositionsDocument positions, RoundFacts round) =>
        PostPlantService.PlantSpot(positions, round);

    /// <summary>One planted round's read: the kind, the CTs that reached the bomb and the CTs that saved.</summary>
    /// <param name="positions">The demo's positions file, or null when it has none.</param>
    /// <param name="round">The round.</param>
    public static (RetakeKind Kind, IReadOnlyList<RoundPosition> Reached, IReadOnlyList<RoundPosition> Saves)
        Read(RoundPositionsDocument? positions, RoundFacts round)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.PlantTick is not { } plant)
        {
            return (RetakeKind.NoPlant, [], []);
        }

        if (positions is null || positions.CadenceTicks <= 0 || positions.Round(round.Number) is not { } stored
            || PlantPosition(positions, round) is not { } bomb)
        {
            return (RetakeKind.Unread, [], []);
        }

        HashSet<int> ct = [.. stored.Ct];
        int first = Math.Max(0, positions.StepFor(stored, plant));
        HashSet<int> alive =
        [
            .. stored.At(first).Where(p => ct.Contains(p.Slot)).Select(p => p.Slot)
                .Where(slot => !round.Kills.Any(k => k.VictimSlot == slot && k.Tick <= plant))
        ];
        if (alive.Count == 0)
        {
            return (RetakeKind.NoCtAlive, [], []);
        }

        int end = round.EndTick ?? int.MaxValue;
        Dictionary<int, RoundPosition> reached = [];
        Dictionary<int, RoundPosition> last = [];
        for (int step = first; step < stored.Pos.Count; step++)
        {
            if (stored.FreezeEndTick + step * positions.CadenceTicks >= end)
            {
                break;
            }

            foreach (RoundPosition p in stored.At(step))
            {
                if (!alive.Contains(p.Slot))
                {
                    continue;
                }

                last[p.Slot] = p;
                if (!reached.ContainsKey(p.Slot) && Near(p, bomb))
                {
                    reached[p.Slot] = p;
                }
            }
        }

        bool won = round.WinnerSide == 3;
        List<RoundPosition> saves = won
            ? []
            :
            [
                .. alive.Order()
                    .Where(slot => !reached.ContainsKey(slot)
                                   && !round.Kills.Any(k => k.VictimSlot == slot && k.Tick <= end)
                                   && last.ContainsKey(slot))
                    .Select(slot => last[slot])
            ];
        List<RoundPosition> arrivals = [.. reached.OrderBy(r => r.Key).Select(r => r.Value)];
        RetakeKind kind = !won && arrivals.Count == 0 && saves.Count > 0 ? RetakeKind.Saved : RetakeKind.Retook;
        return (kind, arrivals, saves);
    }

    private static bool Near(RoundPosition p, RoundPosition bomb)
    {
        double dx = p.X - bomb.X;
        double dy = p.Y - bomb.Y;
        return dx * dx + dy * dy <= (double)SaveRadius * SaveRadius && Math.Abs(p.Z - bomb.Z) <= SaveHeight;
    }
}

/// <summary>
///     Builds a team's <see cref="RetakeSet" />: every round Team Identity puts the team on CT, with the
///     retake read of <see cref="RetakeRules" /> where the opponents planted. Reads Round Facts and the
///     positions files, never a demo; call it off the UI thread.
///     <para>
///         The positions are read whatever fingerprint they were built under (the Scouting Report sources
///         carry no fingerprint); a file naming another demo's hash is refused.
///     </para>
/// </summary>
public sealed class RetakeService
{
    // A record without a clock header is still a CS2 demo; the frame clock of every build here is 64.
    private const int FallbackTickRate = 64;

    private readonly IExtensionLibrary _library;
    private readonly PositionsReader? _positions;
    private readonly IRoundFacts _roundFacts;
    private readonly TeamIdentityService _teams;

    /// <param name="teams">Team Identity: the team's demos and its side per round.</param>
    /// <param name="library">The library: map and hash per demo.</param>
    /// <param name="roundFacts">The Round Facts rows per demo.</param>
    /// <param name="positions">The positions files; null reads every planted round as unread.</param>
    public RetakeService(TeamIdentityService teams, IExtensionLibrary library, IRoundFacts roundFacts,
        PositionsReader? positions)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(roundFacts);
        _teams = teams;
        _library = library;
        _roundFacts = roundFacts;
        _positions = positions;
    }

    /// <summary>The team's CT rounds with their retake reads.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="cancellationToken">Stops between demos.</param>
    public RetakeSet Build(Guid teamId, CancellationToken cancellationToken = default)
    {
        List<RetakeRound> rounds = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int read = 0;
        int withoutPositions = 0;
        foreach ((DemoRef demo, _, _) in _teams.SidesOf(teamId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(demo.Path)
                || _library.Find(demo.Path) is not { MapName: { Length: > 0 } map } record
                || _roundFacts.TryGet(demo.Path) is not { } rows)
            {
                continue;
            }

            read++;
            RoundPositionsDocument? positions = _positions?.Read(demo.Path, map, record.Sha256);
            if (positions is null)
            {
                withoutPositions++;
            }

            int rate = rows.Clock?.TickRate is > 0 and var r ? r : FallbackTickRate;
            foreach (RoundFacts round in rows.Rounds.OrderBy(x => x.Number))
            {
                if (round.IsLive && _teams.SideAtRound(demo.Path, teamId, round.Number) == 3)
                {
                    rounds.Add(Round(map, demo.Path, record.Sha256, rate, positions, round));
                }
            }
        }

        return new RetakeSet { Rounds = rounds, DemosRead = read, DemosWithoutPositions = withoutPositions };
    }

    /// <summary>One CT round's read.</summary>
    /// <param name="map">The map.</param>
    /// <param name="demoPath">The demo.</param>
    /// <param name="sha256">Its hash.</param>
    /// <param name="tickRate">Its tick rate.</param>
    /// <param name="positions">Its positions file, or null.</param>
    /// <param name="round">The round, the team on CT.</param>
    public static RetakeRound Round(string map, string demoPath, string? sha256, int tickRate,
        RoundPositionsDocument? positions, RoundFacts round)
    {
        ArgumentNullException.ThrowIfNull(round);
        (RetakeKind kind, IReadOnlyList<RoundPosition> reached, IReadOnlyList<RoundPosition> saves) =
            RetakeRules.Read(positions, round);
        (int ct, int t) = RoundPhases.AliveAt(round, round.PlantTick ?? round.FreezeEndTick);
        string? grouping = kind is RetakeKind.Retook or RetakeKind.Saved
                           && positions is not null && RetakeRules.PlantPosition(positions, round) is { } bomb
            ? PostPlantService.RetakeGroup(positions, round, bomb, tickRate)
            : null;
        int from = round.PlantTick is { } plant
            ? plant - PostPlantService.ClipLeadSeconds * tickRate
            : round.FreezeEndTick;
        int to = round.PlantTick is { } p
            ? p + PostPlantService.PostPlantSeconds * tickRate
            : round.EndTick ?? round.FreezeEndTick + 3 * PostPlantService.PostPlantSeconds * tickRate;
        int start = Math.Max(round.FreezeEndTick, from);
        int stop = round.EndTick is { } e ? Math.Min(e, to) : to;
        return new RetakeRound(map, round.Ct.BuyType, round.PlantSite, ct, t, kind, round.WinnerSide == 3,
            PostPlantService.OutcomeLabel(round, 3), grouping, reached, saves,
            new TendencyRound(demoPath, sha256, round.Number, start, Math.Max(start, stop), tickRate));
    }
}
