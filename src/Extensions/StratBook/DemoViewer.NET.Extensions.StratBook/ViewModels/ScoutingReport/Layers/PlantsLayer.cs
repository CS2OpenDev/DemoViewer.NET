#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Plants (T): which site they hit (the Opening Tendencies' site split), where on it they plant, how they
///     hold it and how the round ends after the plant (the Post-Plant And Retake). Every number opens its rounds.
///     <para>Phase 2 (P2.5) draws the plant spots, won and lost, on the map.</para>
/// </summary>
public sealed class PlantsLayer() : ScoutingLayerBase("plants", "Plants", ScoutingLayerPlacement.Map,
    ScoutingSides.T, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.T), "E/D")
{
    /// <inheritdoc />
    public override string TitleFor(ScoutingSide side) => "Plants and post-plant";

    /// <inheritdoc />
    public override bool OnByDefault(ScoutingSide side) => side == ScoutingSide.T;

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        OpeningTendencies? openings = await LayerData.Openings(context).ConfigureAwait(false);
        PostPlantBlock? block = await LayerData.PostPlant(context).ConfigureAwait(false);
        if (block is null && openings is null)
        {
            return await LayerData.PostPlantMissing(context).ConfigureAwait(false);
        }

        List<ScoutingInspectorSection> sections = [];
        List<ScoutingRead> reads = [];
        if (openings is not null)
        {
            sections.Add(new ScoutingInspectorSection("Site targeting",
                Rows(context, openings.SiteSplit, openings.Rounds.Count, "site", ScoutingColor.T)));
        }

        int planted = block?.Planted.Count ?? 0;
        if (block is not null)
        {
            int rounds = block.SideRounds.Count;
            sections.Insert(0, new ScoutingInspectorSection("",
                [Sample(context, block.SideRounds, "rounds"), Row(context, block.Planted, rounds, "", label: "planted") with { StarText = "plants", OpenTitle = $"{context.Prefix} · plants" }]));
            sections.Add(new ScoutingInspectorSection("Where they plant", Rows(context, block.PlantClusters, planted, "plant", ScoutingColor.T)));
            sections.Add(new ScoutingInspectorSection("How they hold it", [
                .. Rows(context, block.HoldShapes, planted, "hold"),
                .. Rows(context, block.HoldPlaces, planted, "hold from")
            ]));
            sections.Add(new ScoutingInspectorSection("At the plant", [
                .. Rows(context, block.ManCount, planted, "at the plant"),
                .. Rows(context, block.Outcomes, planted, "after the plant")
            ]));
            TendencyBucket? spot = Top(block.PlantClusters);
            if (planted > 0)
            {
                reads.Add(new ScoutingRead(spot is null
                        ? $"They plant in {CountText(planted, rounds)} on T."
                        : $"They plant in {CountText(planted, rounds)} on T; most often at {spot.Label} ({spot.Count} of {planted}).",
                    Suggested: true));
            }
        }

        if (openings is not null && Top(openings.SiteSplit, "no plant") is { } site)
        {
            reads.Add(new ScoutingRead($"They hit {site.Label} most: {CountText(site.Count, openings.Rounds.Count)}.",
                "read site"));
        }

        return new ScoutingLayerResult
        {
            CountLabel = $"{planted} plants",
            Reads = reads,
            Sections = [.. sections.Where(s => s.Rows.Count > 0)]
        };
    }
}
