namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Lurks (T): who lurks and when the lurk splits off, from the Opening Tendencies. Every number opens its rounds.
///     <para>Phase 2 (P2.6) adds the route ("via") and the lurker's position at 0:30 on the map.</para>
/// </summary>
public sealed class LurksLayer() : ScoutingLayerBase("lurks", "Lurks", ScoutingLayerPlacement.Map,
    ScoutingSides.T, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Lurk), "E")
{
    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await LayerData.Openings(context).ConfigureAwait(false) is not { } block)
        {
            return await LayerData.OpeningsMissing(context).ConfigureAwait(false);
        }

        int rounds = block.LurkRounds;
        if (rounds == 0)
        {
            return ScoutingLayerResult.Empty("lurk: no positions for these rounds; rebuild the index");
        }

        List<ScoutingRead> reads = [];
        if (Top(block.Lurkers) is { } lurker)
        {
            reads.Add(new ScoutingRead($"{lurker.Label} lurks most: {CountText(lurker.Count, rounds)}."));
        }

        return new ScoutingLayerResult
        {
            CountLabel = $"{rounds} rds",
            Reads = reads,
            Sections =
            [
                new ScoutingInspectorSection($"Who lurks · {Plural(rounds, "round")} with positions",
                    Rows(context, block.Lurkers, rounds, "lurker", ScoutingColor.Lurk)),
                new ScoutingInspectorSection("Split-off, seconds after freeze end",
                    Rows(context, block.LurkClock, rounds, "lurk"))
            ]
        };
    }
}
