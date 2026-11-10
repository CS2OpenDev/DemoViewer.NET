#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Opening duels (T) and opening deaths (CT): when first contact comes, and on T who takes the entry at
///     which site, from the Opening Tendencies. Every number opens its rounds.
///     <para>Phase 2 (P2.2) draws the duels on the map from the nearest 1 Hz sample, with traded % and who opens.</para>
/// </summary>
public sealed class OpeningLayer() : ScoutingLayerBase("opening", "Opening duels", ScoutingLayerPlacement.Map,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.Bad), "D")
{
    /// <inheritdoc />
    public override string NameFor(ScoutingSide side) => side == ScoutingSide.Ct ? "Opening deaths" : Name;

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await LayerData.Openings(context).ConfigureAwait(false) is not { } block)
        {
            return await LayerData.OpeningsMissing(context).ConfigureAwait(false);
        }

        int rounds = block.Rounds.Count;
        List<ScoutingInspectorSection> sections =
        [
            new("", [Sample(context, block.Rounds, "rounds")]),
            new("First contact, seconds after freeze end", Rows(context, block.FirstContactClock, rounds, "first contact"))
        ];
        List<ScoutingRead> reads = [];
        if (Top(block.FirstContactClock, "no contact") is { } contact)
        {
            reads.Add(new ScoutingRead(
                $"First contact most often comes at {contact.Label}: {CountText(contact.Count, rounds)}."));
        }

        if (context.Side == ScoutingSide.T)
        {
            sections.Add(new ScoutingInspectorSection("Who takes the entry, by site",
                Rows(context, block.EntryBySite, rounds, "entry", ScoutingColor.T)));
            if (Top(block.EntryBySite) is { } entry)
            {
                reads.Insert(0, new ScoutingRead($"{entry.Label} takes the entry most often: {CountText(entry.Count, rounds)}.",
                    "read entry"));
            }
        }

        return new ScoutingLayerResult
        {
            CountLabel = $"{rounds} rds",
            Reads = reads,
            Sections = sections
        };
    }
}
