#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Economy and pistols (strip): the side's pistol rounds and what followed them, how it plays a low-buy
///     opponent, whether it closes out a man advantage, and what it buys after a loss (the Situational
///     Behaviour, over Round Facts alone). Every number opens its rounds.
///     <para>Phase 3 (P3.5) adds the buy after the pistol as a stacked bar, force rate and eco rush or stack.</para>
/// </summary>
public sealed class EconomyLayer() : ScoutingLayerBase("econ", "Economy", ScoutingLayerPlacement.Strip,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral), "E/D")
{
    /// <inheritdoc />
    public override string TitleFor(ScoutingSide side) => "Economy and pistols";

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await LayerData.Situational(context).ConfigureAwait(false) is not { } b)
        {
            return await LayerData.SituationalMissing(context).ConfigureAwait(false);
        }

        int rounds = b.SideRounds.Count;
        int pistols = b.PistolRounds.Count;
        int pistolsWon = b.PistolOutcomes.Where(o => o.Label.StartsWith("won", StringComparison.Ordinal))
            .Sum(o => o.Count);
        List<ScoutingRead> reads = [];
        if (pistols > 0)
        {
            reads.Add(new ScoutingRead(
                $"They win {pistolsWon} of {pistols} {context.Side.Label()} pistols on {context.Map}."));
        }

        List<ScoutingInspectorSection> sections =
        [
            new("Pistols", [
                .. Situation(context, b.PistolRounds, rounds, "pistols", "pistol rounds"),
                .. Rows(context, b.PistolOutcomes, pistols, "pistol"),
                .. Rows(context, b.PistolFollowUp, pistols, "")
            ]),
            new("Against a low buy", [
                .. Situation(context, b.AntiEcoRounds, rounds, "vs low buy"),
                .. Rows(context, b.AntiEcoBuyTypes, b.AntiEcoRounds.Count, "vs"),
                .. Rows(context, b.AntiEcoOutcomes, b.AntiEcoRounds.Count, "vs low buy"),
                .. Rows(context, b.AntiEcoContactClock, b.AntiEcoRounds.Count, "vs low buy, first contact")
            ]),
            new("With a man advantage", [
                .. Situation(context, b.ManAdvantageRounds, rounds, "man advantage"),
                .. Rows(context, b.ManAdvantage, b.ManAdvantageRounds.Count, "led"),
                .. Rows(context, b.ManAdvantageOutcomes, b.ManAdvantageRounds.Count, "man advantage")
            ]),
            new("After a loss", [
                .. Situation(context, b.RoundsLost, rounds, "losses"),
                .. Rows(context, b.SaveDiscipline, b.RoundsLost.Count, "after a loss,")
            ])
        ];

        return new ScoutingLayerResult
        {
            CountLabel = Plural(pistols, "pistol"),
            Reads = reads,
            Sections = [.. sections.Where(s => s.Rows.Count > 0)],
            Mini =
            [
                .. Situation(context, b.PistolRounds, rounds, "pistols", "pistol rounds"),
                .. Rows(context, b.PistolFollowUp, pistols, "").Take(2)
            ]
        };
    }

    // A situation's own sample row ("vs low buy: 4 of 12 rounds"), titled the way the Situational section was.
    private static IEnumerable<ScoutingInspectorRow> Situation(ScoutingLayerContext context, TendencyBucket bucket,
        int rounds, string word, string? label = null)
    {
        if (bucket.Count > 0)
        {
            yield return Row(context, bucket, rounds, "", label: label ?? word) with
            {
                StarText = word, OpenTitle = $"{context.Prefix} · {word}"
            };
        }
    }
}
