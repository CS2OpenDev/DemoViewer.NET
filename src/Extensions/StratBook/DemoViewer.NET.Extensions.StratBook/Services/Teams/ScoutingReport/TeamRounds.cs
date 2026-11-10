namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>
///     One live round a team played, as Round Facts has it, with the side Team Identity puts the team on and the
///     team's next round in the same demo. The strip layers (economy, pace, score) read these rather than a
///     pre-bucketed service, because their numbers pair rounds (the buy after a pistol, the save after a lost
///     full buy) and split them several ways at once.
/// </summary>
public sealed class TeamRound
{
    // Seconds a moment clip runs before and after the moment it was counted for.
    private const int ClipLeadSeconds = 5;
    private const int ClipTailSeconds = 8;

    // Seconds a round clip runs when the round has no end.
    private const int FallbackRoundSeconds = 115;

    public required string DemoPath { get; init; }

    public string? Sha256 { get; init; }

    /// <summary>The demo's frame clock rate.</summary>
    public required int TickRate { get; init; }

    public required string Map { get; init; }

    public required RoundFacts Facts { get; init; }

    /// <summary>2 = T, 3 = CT.</summary>
    public required int Side { get; init; }

    /// <summary>Round <c>N + 1</c> of the same demo when it is live and Team Identity places the team in it, on either side.</summary>
    public TeamRound? Next { get; internal set; }

    public int Number => Facts.Number;

    /// <summary>The team's own side of the round.</summary>
    public SideFacts Own => Side == 3 ? Facts.Ct : Facts.T;

    /// <summary>The opponent's side of the round.</summary>
    public SideFacts Enemy => Side == 3 ? Facts.T : Facts.Ct;

    /// <summary>What the team bought.</summary>
    public BuyType Buy => Own.BuyType;

    public bool Won => Facts.WinnerSide == Side;

    /// <summary>Decided against the team; a round with no winner is neither won nor lost.</summary>
    public bool Lost => Facts.WinnerSide is 2 or 3 && Facts.WinnerSide != Side;

    /// <summary>Seconds from freeze end to a tick, or null without one.</summary>
    /// <param name="tick">Frame clock.</param>
    public double? SecondsTo(int? tick) => tick is { } t ? (t - Facts.FreezeEndTick) / (double)TickRate : null;

    /// <summary>The whole round, freeze end to its end.</summary>
    public TendencyRound Clip => Range(Facts.FreezeEndTick,
        Facts.EndTick ?? Facts.FreezeEndTick + FallbackRoundSeconds * TickRate);

    /// <summary>A few seconds either side of a moment, clamped to the round.</summary>
    /// <param name="tick">The moment.</param>
    public TendencyRound Around(int tick) =>
        Range(tick - ClipLeadSeconds * TickRate, tick + ClipTailSeconds * TickRate);

    private TendencyRound Range(int from, int to)
    {
        int start = Math.Max(Facts.FreezeEndTick, from);
        int end = Facts.EndTick is { } e ? Math.Min(e, to) : to;
        return new TendencyRound(DemoPath, Sha256, Facts.Number, start, Math.Max(start, end), TickRate);
    }
}

/// <summary>Every live round of one team, demo order then round order.</summary>
public sealed class TeamRoundSet
{
    public IReadOnlyList<TeamRound> Rounds { get; init; } = [];

    /// <summary>Demos with Round Facts rows.</summary>
    public int DemosRead { get; init; }

    /// <summary>The rounds on a map.</summary>
    /// <param name="map">The map.</param>
    public IReadOnlyList<TeamRound> On(string map) =>
        [.. Rounds.Where(r => string.Equals(r.Map, map, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The rounds on a map and side.</summary>
    /// <param name="map">The map.</param>
    /// <param name="side">2 = T, 3 = CT.</param>
    public IReadOnlyList<TeamRound> On(string map, int side) => [.. On(map).Where(r => r.Side == side)];
}

/// <summary>Reads a team's <see cref="TeamRoundSet" /> from Round Facts and Team Identity. Safe off the UI thread.</summary>
public static class TeamRounds
{
    // A record without a clock header is still a CS2 demo; the frame clock of every build here is 64.
    private const int FallbackTickRate = 64;

    /// <summary>The team's rounds. Reads records: call it off the UI thread.</summary>
    /// <param name="teams">Team Identity.</param>
    /// <param name="library">The library: map and hash per demo.</param>
    /// <param name="roundFacts">The Round Facts rows.</param>
    /// <param name="teamId">The team.</param>
    public static TeamRoundSet Build(TeamIdentityService teams, IExtensionLibrary library, IRoundFacts roundFacts,
        Guid teamId)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(roundFacts);
        List<TeamRound> all = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int read = 0;
        foreach ((DemoRef demo, _, _) in teams.SidesOf(teamId))
        {
            if (!seen.Add(demo.Path)
                || library.Find(demo.Path) is not { MapName: { Length: > 0 } map } record
                || roundFacts.TryGet(demo.Path) is not { } rows)
            {
                continue;
            }

            read++;
            string path = demo.Path;
            all.AddRange(FromRows(path, record.Sha256, map, rows, n => teams.SideAtRound(path, teamId, n)));
        }

        return new TeamRoundSet { Rounds = all, DemosRead = read };
    }

    /// <summary>One demo's live rounds the team is placed in, each linked to the next.</summary>
    /// <param name="demoPath">The demo.</param>
    /// <param name="sha256">Its hash.</param>
    /// <param name="map">Its map.</param>
    /// <param name="rows">Its Round Facts rows.</param>
    /// <param name="sideAt">The team's side in a round number, or null when it is not placed.</param>
    public static IReadOnlyList<TeamRound> FromRows(string demoPath, string? sha256, string map, RoundFactsRows rows,
        Func<int, int?> sideAt)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(sideAt);
        int rate = rows.Clock?.TickRate is > 0 and var r ? r : FallbackTickRate;
        Dictionary<int, TeamRound> byNumber = [];
        List<TeamRound> list = [];
        foreach (RoundFacts round in rows.Rounds.OrderBy(x => x.Number))
        {
            if (!round.IsLive || sideAt(round.Number) is not (2 or 3) || byNumber.ContainsKey(round.Number))
            {
                continue;
            }

            TeamRound one = new()
            {
                DemoPath = demoPath,
                Sha256 = sha256,
                TickRate = rate,
                Map = map,
                Facts = round,
                Side = sideAt(round.Number)!.Value
            };
            byNumber[round.Number] = one;
            list.Add(one);
        }

        foreach (TeamRound one in list)
        {
            one.Next = byNumber.GetValueOrDefault(one.Number + 1);
        }

        return list;
    }

    /// <summary>The median, or null with no value.</summary>
    /// <param name="values">The values.</param>
    public static double? Median(IEnumerable<double> values) => SetupsDerivation.Median(values);
}
