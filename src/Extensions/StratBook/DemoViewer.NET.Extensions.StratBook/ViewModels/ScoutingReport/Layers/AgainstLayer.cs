#region

using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Strats run against them (CT): Strat Mining's T patterns narrowed to the rounds this team was the CT, run
///     at least twice, best win rate first, each with how many opponents ran it. Drawn like the Detected strats;
///     every number opens its rounds and each row plays its typical round in 2D.
/// </summary>
public sealed class AgainstLayer : ScoutingLayerBase
{
    private readonly Func<ScoutingLayerContext, MinedMember, int?>? _sideOf;

    public AgainstLayer() : this(null)
    {
    }

    /// <param name="sideOf">The team's side in a member round; Team Identity's <c>SideAtRound</c> when null.</param>
    internal AgainstLayer(Func<ScoutingLayerContext, MinedMember, int?>? sideOf) : base("against",
        "Strats run against them", ScoutingLayerPlacement.Map, ScoutingSides.Ct,
        new ScoutingSwatch(ScoutingMarkShape.Badge, ScoutingColor.T), "D") =>
        _sideOf = sideOf;

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (StratLayerKit.Patterns(context) is not { } patterns)
        {
            return StratLayerKit.NoPatterns(context);
        }

        return await ScoutingLayerContext.Offload(() =>
        {
            IReadOnlyList<ScoutedStrat> strats =
                StratScouting.Against(patterns, context.Map, StratLayerKit.SideOf(context, _sideOf));
            if (strats.Count == 0)
            {
                return ScoutingLayerResult.Empty(
                    $"no opponent T strat was run twice against them on {context.Map} in the mined library");
            }

            List<ScoutedStrat> shown = [.. strats.Take(StratLayerKit.MaxShown)];
            StratLayerKit.Ticks ticks = new(context);
            List<ScoutingInspectorRow> rows = [];
            List<ScoutingMapMark> marks = [];
            for (int i = 0; i < shown.Count; i++)
            {
                ScoutedStrat strat = shown[i];
                string letter = StratLayerKit.Letter(i);
                string name = MinedStratBuilder.Name(strat.Pattern);
                int trailAt = marks.Count;
                marks.AddRange(StratLayerKit.Trail(context, strat, letter));
                rows.Add(StratLayerKit.Row(context, strat, name, letter, ticks) with
                {
                    Note = strat.Opponents == 0
                        ? $"{Plural(strat.Support, "round")}; opponent not identified"
                        : $"seen from {Plural(strat.Opponents, "opponent")}"
                });
                StratLayerKit.Pin(marks, trailAt, letter, name, [rows[^1]]);
            }

            ScoutedStrat best = shown[0];
            string bestName = MinedStratBuilder.Name(best.Pattern);
            List<ScoutingRead> reads =
            [
                new($"What worked best against their CT on {context.Map}: {bestName}, {StratLayerKit.Record(best)}"
                    + (best.Opponents > 0 ? $", seen from {Plural(best.Opponents, "opponent")}." : "."),
                    Suggested: true)
            ];
            return new ScoutingLayerResult
            {
                CountLabel = Plural(strats.Count, "strat"),
                Marks = marks,
                Reads = reads,
                Sections =
                [
                    new ScoutingInspectorSection(
                        strats.Count > shown.Count
                            ? $"Best first · top {shown.Count} of {strats.Count}"
                            : "Best first", rows)
                ],
                Legend = StratLayerKit.Legend
            };
        }).ConfigureAwait(false);
    }
}
