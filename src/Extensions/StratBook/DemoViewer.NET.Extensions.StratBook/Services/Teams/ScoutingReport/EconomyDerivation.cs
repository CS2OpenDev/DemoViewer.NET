#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>What the team bought in one kind of round after a pistol, and how those rounds went.</summary>
/// <param name="Label">"Won pistol → R2".</param>
/// <param name="Buys">Eco, semi, force and full, in that order; a round with an unknown buy is in none.</param>
/// <param name="Rounds">Every round of the row.</param>
/// <param name="Won">The rounds of the row the team won.</param>
public sealed record AfterPistolRow(
    string Label,
    IReadOnlyList<TendencyBucket> Buys,
    TendencyBucket Rounds,
    TendencyBucket Won)
{
    /// <summary>The rounds with a known buy: the bar's denominator.</summary>
    public int Known => Buys.Sum(b => b.Count);
}

/// <summary>What the team did with its own eco at <see cref="EconomyDerivation.PostureSeconds" />.</summary>
public enum EcoPosture
{
    /// <summary>T: most of the alive players already on one site.</summary>
    Rush,

    /// <summary>CT: most of the alive players on one site.</summary>
    Stack,

    /// <summary>Neither: spread out, saving or playing for picks.</summary>
    Spread
}

/// <summary>
///     The Economy strip's numbers over a team's rounds: the buy after a pistol, force rate, forces won, the save
///     after a lost full buy, what an own eco looks like at 0:20, and pistols won. Pure functions of
///     <see cref="TeamRound" /> lists and positions, so they are tested on synthetic rounds.
///     <para>
///         <b>After a pistol</b> pairs a pistol round with the team's next two rounds while they stay on the same
///         side (R2 and R3 of the half). <b>Force rate</b> counts force buys over the rounds with a known buy that
///         are not pistols. <b>Full save</b> is an eco straight after a full buy the team lost, the half's pistol
///         left out. <b>Eco posture</b> reads the alive players' positions at <see cref="PostureSeconds" />: all
///         but one of them (at least three) on one site is a rush on T and a stack on CT.
///     </para>
/// </summary>
public static class EconomyDerivation
{
    /// <summary>Seconds after freeze end an eco's positions are read at.</summary>
    public const int PostureSeconds = 20;

    /// <summary>The fewest players on one site that make a rush or a stack.</summary>
    public const int PostureMinPlayers = 3;

    /// <summary>The buys a stacked bar splits into, ramp order.</summary>
    public static IReadOnlyList<BuyType> RampBuys { get; } = [BuyType.Eco, BuyType.Semi, BuyType.Force, BuyType.Full];

    /// <summary>"Eco", "Semi", "Force", "Full".</summary>
    /// <param name="buy">The buy.</param>
    public static string ShortLabel(BuyType buy) => buy switch
    {
        BuyType.Eco => "Eco",
        BuyType.Semi => "Semi",
        BuyType.Force => "Force",
        BuyType.Full => "Full",
        BuyType.Pistol => "Pistol",
        _ => "Unknown"
    };

    /// <summary>
    ///     Four rows, both sides together: won pistol → R2, won pistol → R3, lost pistol → R2, lost pistol → R3.
    ///     A row with no round is still returned, empty.
    /// </summary>
    /// <param name="rounds">The team's rounds on a map.</param>
    public static IReadOnlyList<AfterPistolRow> AfterPistol(IEnumerable<TeamRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        Dictionary<string, List<TeamRound>> rows = new(StringComparer.Ordinal);
        string[] order = ["Won pistol → R2", "Won pistol → R3", "Lost pistol → R2", "Lost pistol → R3"];
        foreach (string label in order)
        {
            rows[label] = [];
        }

        foreach (TeamRound pistol in rounds.Where(r => r.Buy == BuyType.Pistol && (r.Won || r.Lost)))
        {
            string result = pistol.Won ? "Won" : "Lost";
            if (pistol.Next is not { } r2 || r2.Side != pistol.Side || r2.Buy == BuyType.Pistol)
            {
                continue;
            }

            rows[$"{result} pistol → R2"].Add(r2);
            if (r2.Next is { } r3 && r3.Side == pistol.Side && r3.Buy != BuyType.Pistol)
            {
                rows[$"{result} pistol → R3"].Add(r3);
            }
        }

        return
        [
            .. order.Select(label => new AfterPistolRow(label,
                [.. RampBuys.Select(b => Bucket(ShortLabel(b), rows[label].Where(r => r.Buy == b)))],
                Bucket(label, rows[label]),
                Bucket($"{label} won", rows[label].Where(r => r.Won))))
        ];
    }

    /// <summary>The rounds a force rate is counted over: a known buy that is not a pistol.</summary>
    /// <param name="rounds">The rounds.</param>
    public static IReadOnlyList<TeamRound> Buying(IEnumerable<TeamRound> rounds) =>
        [.. rounds.Where(r => r.Buy is BuyType.Eco or BuyType.Semi or BuyType.Force or BuyType.Full)];

    /// <summary>The force buys among <paramref name="rounds" />.</summary>
    /// <param name="rounds">The rounds.</param>
    public static IReadOnlyList<TeamRound> Forces(IEnumerable<TeamRound> rounds) =>
        [.. rounds.Where(r => r.Buy == BuyType.Force)];

    /// <summary>
    ///     The full buys the team lost with a next round that is not a pistol (the denominator), and the ones
    ///     followed by an eco (the full saves); the rounds are the next rounds.
    /// </summary>
    /// <param name="rounds">The rounds.</param>
    public static (IReadOnlyList<TeamRound> LostFullBuys, IReadOnlyList<TeamRound> Saved) FullSaves(
        IEnumerable<TeamRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        List<TeamRound> next =
        [
            .. rounds.Where(r => r.Buy == BuyType.Full && r.Lost)
                .Select(r => r.Next)
                .OfType<TeamRound>()
                .Where(n => n.Buy is not (BuyType.Pistol or BuyType.Unknown))
        ];
        return (next, [.. next.Where(n => n.Buy == BuyType.Eco)]);
    }

    /// <summary>
    ///     A round's eco posture from the areas the team's alive players stood in: all but one (and at least
    ///     <see cref="PostureMinPlayers" />) on one site is a rush on T and a stack on CT. Null with fewer than
    ///     <see cref="PostureMinPlayers" /> players read.
    /// </summary>
    /// <param name="areas">One area per alive player.</param>
    /// <param name="side">2 = T, 3 = CT.</param>
    public static EcoPosture? Posture(IReadOnlyList<SetupArea> areas, int side)
    {
        ArgumentNullException.ThrowIfNull(areas);
        if (areas.Count < PostureMinPlayers)
        {
            return null;
        }

        int need = Math.Max(PostureMinPlayers, areas.Count - 1);
        int a = areas.Count(x => x == SetupArea.A);
        int b = areas.Count(x => x == SetupArea.B);
        if (Math.Max(a, b) >= need)
        {
            return side == 3 ? EcoPosture.Stack : EcoPosture.Rush;
        }

        return EcoPosture.Spread;
    }

    /// <summary>
    ///     Where the team's alive players stood <paramref name="seconds" /> after freeze end: the positions sample
    ///     there, or the one before it when that step was not sampled. Empty without the round.
    /// </summary>
    /// <param name="positions">The demo's positions.</param>
    /// <param name="round">The round.</param>
    /// <param name="seconds">Seconds after freeze end.</param>
    public static IReadOnlyList<SetupSample> OwnAt(RoundPositionsDocument positions, TeamRound round, int seconds)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(round);
        if (positions.Round(round.Number) is not { } at)
        {
            return [];
        }

        HashSet<int> ct = [.. at.Ct];
        int step = positions.StepFor(at, at.FreezeEndTick + seconds * round.TickRate);
        IReadOnlyList<RoundPosition> tuples = at.At(step);
        if (tuples.Count == 0)
        {
            tuples = at.At(step - 1);
        }

        return
        [
            .. tuples.Where(p => ct.Contains(p.Slot) == (round.Side == 3))
                .Select(p => new SetupSample(p.X, p.Y, p.Z, positions.PlaceOf(p.PlaceId)))
        ];
    }

    private static TendencyBucket Bucket(string label, IEnumerable<TeamRound> rounds) =>
        new(label, [.. rounds.Select(r => r.Clip)]);
}
