#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>One side's opening impact: rounds won after winning and after losing the opening duel, and the trades.</summary>
/// <param name="Side">2 = T, 3 = CT.</param>
/// <param name="WonOpening">Rounds the team won the opening duel in.</param>
/// <param name="WonAfterWinning">Those the team went on to win.</param>
/// <param name="LostOpening">Rounds the team lost the opening duel in.</param>
/// <param name="WonAfterLosing">Those the team still won.</param>
/// <param name="Traded">Lost openings a teammate traded.</param>
public sealed record OpeningImpact(
    int Side,
    IReadOnlyList<TendencyRound> WonOpening,
    IReadOnlyList<TendencyRound> WonAfterWinning,
    IReadOnlyList<TendencyRound> LostOpening,
    IReadOnlyList<TendencyRound> WonAfterLosing,
    IReadOnlyList<TendencyRound> Traded)
{
    /// <summary>Round win % after winning the opening, 0 to 100, or null with none.</summary>
    public double? High => WonOpening.Count > 0 ? 100.0 * WonAfterWinning.Count / WonOpening.Count : null;

    /// <summary>Round win % after losing it, or null with none.</summary>
    public double? Low => LostOpening.Count > 0 ? 100.0 * WonAfterLosing.Count / LostOpening.Count : null;

    /// <summary>One side's impact from its opening duels.</summary>
    /// <param name="block">The side's duels.</param>
    public static OpeningImpact Of(OpeningDuelBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        List<OpeningDuelRound> won = [.. block.Duels.Where(d => d.Won)];
        List<OpeningDuelRound> lost = [.. block.Duels.Where(d => !d.Won)];
        return new OpeningImpact(block.Side,
            // Only decided rounds have an outcome to split by; an undecided trailing round is left out.
            [.. won.Where(d => d.RoundDecided).Select(d => d.Rest)], [.. won.Where(d => d.RoundWon).Select(d => d.Rest)],
            [.. lost.Where(d => d.RoundDecided).Select(d => d.Rest)], [.. lost.Where(d => d.RoundWon).Select(d => d.Rest)],
            [.. lost.Where(d => d.Traded).Select(d => d.Clip)]);
    }
}

/// <summary>
///     Opening impact (strip, map-wide): per side, round win % after winning and after losing the opening duel, and
///     how often a lost opening is traded, as a small CT | T table. Read from the Opening layer's duels
///     (<see cref="OpeningLayer.DuelsFor" />: Round Facts kills, 5 s trade window). Every number opens its rounds;
///     the buy filter does not apply.
/// </summary>
public sealed class ImpactLayer() : ScoutingLayerBase("impact", "Opening impact", ScoutingLayerPlacement.Strip,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral), "D")
{
    /// <summary>The mini table's first row; its inspector section starts with it.</summary>
    public const string WonRow = "Won the opening";

    /// <summary>The mini table's second row.</summary>
    public const string LostRow = "Lost the opening";

    /// <summary>The mini table's third row: the share of lost openings a teammate traded.</summary>
    public const string TradedRow = "Lost opening traded";

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Data.Sources.RoundFacts is null)
        {
            return ScoutingLayerResult.Empty(ScoutingLayerResult.NotOnThisHost);
        }

        OpeningDuelSet? set = await OpeningLayer.DuelsFor(context.Data).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (set is null)
        {
            return ScoutingLayerResult.Unreadable("opening duels", null);
        }

        List<OpeningImpact> sides =
        [
            .. ((int[])[3, 2]).Select(s => set.For(context.Map, s)).OfType<OpeningDuelBlock>()
                .Where(b => b.Duels.Count > 0).Select(OpeningImpact.Of)
        ];
        if (sides.Count == 0)
        {
            return ScoutingLayerResult.Empty($"no opening duels on {context.Map}");
        }

        // One section per table row and one row per side in it, so every cell of the mini opens its rounds here.
        List<ScoutingInspectorRow> won = [];
        List<ScoutingInspectorRow> lost = [];
        List<ScoutingInspectorRow> traded = [];
        foreach (OpeningImpact side in sides)
        {
            string s = StripData.SideLabel(side.Side);
            ScoutingColor color = StripData.SideColor(side.Side);
            won.Add(StripData.Share($"{s} · won the opening", side.WonAfterWinning, side.WonOpening.Count,
                $"{s} won after winning the opening", StripData.MapTitle(context, $"{s} won after winning the opening"),
                color, "rounds won"));
            lost.Add(StripData.Share($"{s} · lost the opening", side.WonAfterLosing, side.LostOpening.Count,
                $"{s} won after losing the opening", StripData.MapTitle(context, $"{s} won after losing the opening"),
                color, Swing(side) is { } swing ? $"rounds won · {swing}" : "rounds won"));
            traded.Add(StripData.Share($"{s} · opening deaths traded", side.Traded, side.LostOpening.Count,
                $"{s} opening deaths traded", StripData.MapTitle(context, $"{s} opening deaths traded"), color,
                $"killer dies within {OpeningDuelsService.TradeWindowSeconds:0} s"));
        }

        return new ScoutingLayerResult
        {
            CountLabel = Plural(sides.Sum(s => s.WonOpening.Count + s.LostOpening.Count), "duel"),
            Reads = Read(sides) is { } read ? [new ScoutingRead(read, Suggested: StripData.SuggestOn(context))] : [],
            Sections =
            [
                new ScoutingInspectorSection($"{WonRow} · round win %", won),
                new ScoutingInspectorSection($"{LostRow} · round win %", lost),
                new ScoutingInspectorSection($"{TradedRow} · share of lost openings", traded)
            ],
            MiniChart = Chart(sides)
        };
    }

    /// <summary>
    ///     The read: the side where losing the opening costs the most, in points of round win %, with both rates.
    ///     When losing it costs nothing on any side, it says that instead.
    /// </summary>
    /// <param name="sides">The sides with duels.</param>
    public static string? Read(IReadOnlyList<OpeningImpact> sides)
    {
        ArgumentNullException.ThrowIfNull(sides);
        OpeningImpact? top = sides.Where(s => s is { Low: not null, High: not null }).MaxBy(Points);
        if (top is null)
        {
            return null;
        }

        string side = StripData.SideLabel(top.Side);
        int points = Points(top);
        return points > 0
            ? $"Losing the opening costs them {Plural(points, "point")} of round win % on {side} ({Text(top.High)} → {Text(top.Low)})."
            : $"Losing the opening does not cost them on {side}: they win {Text(top.Low)} of rounds after losing it and {Text(top.High)} after winning it.";
    }

    /// <summary>
    ///     The mini: a CT | T table of round win % after winning and after losing the opening, each with its
    ///     rounds, and the share of lost openings a teammate traded.
    /// </summary>
    /// <param name="sides">The sides with duels.</param>
    public static ScoutingTableChart Chart(IReadOnlyList<OpeningImpact> sides)
    {
        ArgumentNullException.ThrowIfNull(sides);
        OpeningImpact? ct = sides.FirstOrDefault(s => s.Side == 3);
        OpeningImpact? t = sides.FirstOrDefault(s => s.Side == 2);
        return new ScoutingTableChart("CT", "T",
        [
            new ScoutingTableRow(WonRow,
                [Cell(ct?.WonAfterWinning.Count, ct?.WonOpening.Count), Cell(t?.WonAfterWinning.Count, t?.WonOpening.Count)]),
            new ScoutingTableRow(LostRow,
                [Cell(ct?.WonAfterLosing.Count, ct?.LostOpening.Count), Cell(t?.WonAfterLosing.Count, t?.LostOpening.Count)]),
            new ScoutingTableRow(TradedRow,
                [Cell(ct?.Traded.Count, ct?.LostOpening.Count), Cell(t?.Traded.Count, t?.LostOpening.Count)])
        ]) { Caption = "round win % (rounds) after the opening duel; traded = lost openings a teammate traded" };
    }

    /// <summary>"67% (12)": a share and the rounds it is out of, or "—" with none.</summary>
    /// <param name="count">The rounds counted.</param>
    /// <param name="of">The rounds it is out of.</param>
    public static string Cell(int? count, int? of) =>
        count is { } c && of is > 0 ? $"{Percent((double)c / of.Value)} ({of.Value})" : "—";

    // Points of round win % lost with the opening, from the shown (rounded) percentages so the read adds up.
    private static int Points(OpeningImpact side) => Rounded(side.High) - Rounded(side.Low);

    private static int Rounded(double? percent) =>
        percent is { } p ? (int)Math.Round(p, MidpointRounding.AwayFromZero) : 0;

    // "17 points under winning it", for the lost row's note; null when either rate is missing.
    private static string? Swing(OpeningImpact side) => side is { Low: not null, High: not null }
        ? Points(side) switch
        {
            > 0 and var p => $"{Plural(p, "point")} under winning it",
            < 0 and var p => $"{Plural(-p, "point")} over winning it",
            _ => "same as winning it"
        }
        : null;

    private static string Text(double? percent) => percent is { } p ? Percent(p / 100) : "—";
}
