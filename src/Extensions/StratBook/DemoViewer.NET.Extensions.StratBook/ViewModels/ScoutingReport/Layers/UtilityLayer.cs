#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Utility: when the first grenade of the round goes and where it lands, from the Opening Tendencies (the
///     Grenade Index joined to Round Facts). Every number opens its rounds.
///     <para>Phase 2 (P2.3) draws smoke, molotov and flash markers from the Grenade Index and adds "most thrown".</para>
/// </summary>
public sealed class UtilityLayer() : ScoutingLayerBase("util", "Utility", ScoutingLayerPlacement.Map,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Cloud, ScoutingColor.Neutral), "E/D")
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

        int rounds = block.UtilityRounds;
        if (rounds == 0)
        {
            return ScoutingLayerResult.Empty("no grenades indexed for these rounds; index grenades");
        }

        List<ScoutingRead> reads = [];
        TendencyBucket? clock = Top(block.FirstUtilityClock, "no utility");
        TendencyBucket? place = Top(block.FirstUtilityPlaces);
        if (clock is not null)
        {
            reads.Add(new ScoutingRead(place is null
                ? $"The first grenade most often goes at {clock.Label}: {CountText(clock.Count, rounds)}."
                : $"The first grenade most often goes at {clock.Label} ({CountText(clock.Count, rounds)}); the most common is {place.Label}."));
        }

        return new ScoutingLayerResult
        {
            CountLabel = $"{rounds} rds",
            Reads = reads,
            Sections =
            [
                new ScoutingInspectorSection($"First grenade, seconds after freeze end · {Plural(rounds, "round")}",
                    Rows(context, block.FirstUtilityClock, rounds, "first utility")),
                new ScoutingInspectorSection("First grenade, what and where",
                    Rows(context, block.FirstUtilityPlaces, rounds, "first utility"))
            ]
        };
    }
}
