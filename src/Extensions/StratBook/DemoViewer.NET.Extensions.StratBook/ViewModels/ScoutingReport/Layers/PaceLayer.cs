#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Round pace (strip, T): the median plant time over the team's own T rounds (Round Facts, team-scoped), how
///     often they plant at all, and when first contact comes. The plant and contact numbers open their rounds.
///     <para>Phase 3 (P3.5) adds the pace classes (fast, default, late) and time to contact by buy.</para>
/// </summary>
public sealed class PaceLayer() : ScoutingLayerBase("pace", "Pace", ScoutingLayerPlacement.Strip,
    ScoutingSides.T, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.T), "D")
{
    /// <inheritdoc />
    public override string TitleFor(ScoutingSide side) => "Round pace";

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        TeamMapFacts? facts = (await context.Data.Facts.ConfigureAwait(false))?.For(context.Map);
        PostPlantBlock? plants = await LayerData.PostPlant(context).ConfigureAwait(false);
        OpeningTendencies? openings = await LayerData.Openings(context).ConfigureAwait(false);
        if (facts is null && plants is null && openings is null)
        {
            return context.Data.Sources.RoundFacts is null
                ? ScoutingLayerResult.Empty("not built on this host: Round Facts are unavailable")
                : ScoutingLayerResult.Empty($"no T rounds on {context.Map}");
        }

        List<ScoutingInspectorRow> plantRows = [];
        List<ScoutingRead> reads = [];
        if (facts?.PlantMedianSeconds is { } median)
        {
            plantRows.Add(new ScoutingInspectorRow { Label = "plant time, median", Value = Clock(median) });
            reads.Add(new ScoutingRead($"Their median plant comes {Clock(median)} after freeze end."));
        }

        if (plants is not null)
        {
            plantRows.Add(Row(context, plants.Planted, plants.SideRounds.Count, "", label: "rounds with a plant") with
            {
                StarText = "plants", OpenTitle = $"{context.Prefix} · plants"
            });
        }

        List<ScoutingInspectorSection> sections =
        [
            new("Plant time", plantRows),
            new("First contact, seconds after freeze end",
                openings is null ? [] : Rows(context, openings.FirstContactClock, openings.Rounds.Count, "first contact")),
            new("Pace class and time to contact by buy",
            [
                new ScoutingInspectorRow
                {
                    Label = "fast, default, late",
                    Note = "comes with the strip build (P3.5)",
                    IsPlaceholder = true
                }
            ])
        ];

        return new ScoutingLayerResult
        {
            CountLabel = facts?.PlantMedianSeconds is { } m ? $"median {Clock(m)}" : "",
            Reads = reads,
            Sections = [.. sections.Where(s => s.Rows.Count > 0)],
            Mini = [.. plantRows.Take(2)]
        };
    }
}
