#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Retakes and saves (CT): how often the opponents plant on them, how many CTs are alive at the plant, how
///     the round ends after it, and whether the retake comes together or trickles in (the Post-Plant And
///     Retake). Every number opens its rounds.
///     <para>Phase 2 (P2.4) adds the "saved" rule, per-site splits and the retake positions on the map.</para>
/// </summary>
public sealed class RetakesLayer() : ScoutingLayerBase("retakes", "Retakes and saves", ScoutingLayerPlacement.Map,
    ScoutingSides.Ct, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Ct), "E/D")
{
    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await LayerData.PostPlant(context).ConfigureAwait(false) is not { } block)
        {
            return await LayerData.PostPlantMissing(context).ConfigureAwait(false);
        }

        int rounds = block.SideRounds.Count;
        int planted = block.Planted.Count;
        List<ScoutingRead> reads = [];
        if (planted > 0)
        {
            TendencyBucket? group = Top(block.RetakeGroups);
            reads.Add(new ScoutingRead(group is null
                    ? $"Opponents plant on them in {CountText(planted, rounds)}."
                    : $"Opponents plant on them in {CountText(planted, rounds)}; the retake is most often {group.Label} ({group.Count} of {planted}).",
                Suggested: true));
        }

        return new ScoutingLayerResult
        {
            CountLabel = $"{planted} plants",
            Reads = reads,
            Sections =
            [
                .. new ScoutingInspectorSection[]
                {
                    new("", [Sample(context, block.SideRounds, "rounds"), Row(context, block.Planted, rounds, "", label: "opponent planted") with { StarText = "opponent plants", OpenTitle = $"{context.Prefix} · opponent plants" }]),
                    new("CTs alive at the plant", Rows(context, block.ManCount, planted, "at the plant")),
                    new("After the plant", Rows(context, block.Outcomes, planted, "after the plant")),
                    new("Retake grouping", Rows(context, block.RetakeGroups, planted, "retake", ScoutingColor.Ct))
                }.Where(s => s.Rows.Count > 0)
            ]
        };
    }
}
