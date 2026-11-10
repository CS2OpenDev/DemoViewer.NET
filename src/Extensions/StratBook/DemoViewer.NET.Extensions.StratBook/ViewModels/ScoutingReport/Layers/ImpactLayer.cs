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
            [.. won.Select(d => d.Rest)], [.. won.Where(d => d.RoundWon).Select(d => d.Rest)],
            [.. lost.Select(d => d.Rest)], [.. lost.Where(d => d.RoundWon).Select(d => d.Rest)],
            [.. lost.Where(d => d.Traded).Select(d => d.Clip)]);
    }
}

/// <summary>
///     Opening impact (strip, map-wide): per side, round win % after losing and after winning the opening duel,
///     drawn as a dumbbell (ring = lost, dot = won), and how often a lost opening is traded. Read from the
///     Opening layer's duels (<see cref="OpeningLayer.DuelsFor" />: Round Facts kills, 5 s trade window). Every
///     number opens its rounds; the buy filter does not apply.
/// </summary>
public sealed class ImpactLayer() : ScoutingLayerBase("impact", "Opening impact", ScoutingLayerPlacement.Strip,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral), "D")
{
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

        List<ScoutingInspectorRow> rows = [];
        List<ScoutingInspectorRow> traded = [];
        foreach (OpeningImpact side in sides)
        {
            string s = StripData.SideLabel(side.Side);
            ScoutingColor color = StripData.SideColor(side.Side);
            rows.Add(StripData.Share($"{s} · lost the opening", side.WonAfterLosing, side.LostOpening.Count,
                $"{s} won after losing the opening", StripData.MapTitle(context, $"{s} won after losing the opening"),
                color, "rounds won"));
            rows.Add(StripData.Share($"{s} · won the opening", side.WonAfterWinning, side.WonOpening.Count,
                $"{s} won after winning the opening", StripData.MapTitle(context, $"{s} won after winning the opening"),
                color, "rounds won"));
            traded.Add(StripData.Share($"Opening deaths traded · {s}", side.Traded, side.LostOpening.Count,
                $"{s} opening deaths traded", StripData.MapTitle(context, $"{s} opening deaths traded"), color,
                $"killer dies within {OpeningDuelsService.TradeWindowSeconds:0} s"));
        }

        return new ScoutingLayerResult
        {
            CountLabel = Plural(sides.Sum(s => s.WonOpening.Count + s.LostOpening.Count), "duel"),
            Reads = Read(sides) is { } read ? [new ScoutingRead(read, Suggested: StripData.SuggestOn(context))] : [],
            Sections =
            [
                new ScoutingInspectorSection("Round win % by opening duel", rows),
                new ScoutingInspectorSection("", traded)
            ],
            MiniChart = Chart(sides)
        };
    }

    /// <summary>The read: the side where the opening swings the round most.</summary>
    /// <param name="sides">The sides with duels.</param>
    public static string? Read(IReadOnlyList<OpeningImpact> sides)
    {
        ArgumentNullException.ThrowIfNull(sides);
        OpeningImpact? top = sides.Where(s => s is { Low: not null, High: not null })
            .MaxBy(s => s.High!.Value - s.Low!.Value);
        return top is null
            ? null
            : $"Winning the opening lifts their {StripData.SideLabel(top.Side)} rounds from {Percent(top.Low!.Value / 100)} to {Percent(top.High!.Value / 100)}.";
    }

    /// <summary>The mini: a dumbbell per side, ring after losing the opening, dot after winning it.</summary>
    /// <param name="sides">The sides with duels.</param>
    public static ScoutingDumbbellChart Chart(IReadOnlyList<OpeningImpact> sides)
    {
        ArgumentNullException.ThrowIfNull(sides);
        return new ScoutingDumbbellChart(
        [
            .. sides.Select(s => new ScoutingDumbbell(StripData.SideLabel(s.Side), StripData.SideColor(s.Side), s.Low,
                s.High, $"{Text(s.Low)} → {Text(s.High)}"))
        ]) { Caption = "round win % after losing (ring) and winning (filled) the opening duel" };
    }

    private static string Text(double? percent) => percent is { } p ? Percent(p / 100) : "–";
}
