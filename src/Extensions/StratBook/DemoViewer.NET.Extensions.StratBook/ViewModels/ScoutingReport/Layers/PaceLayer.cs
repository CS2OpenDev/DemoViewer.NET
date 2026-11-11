#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Round pace (strip, T): the pace classes (fast execute by 0:35, default into execute to 1:15, late or no
///     plant), plant time per site as a strip plot with the fast and late bands and the median, the rounds with no
///     plant and how many of them were won, and time to first contact by buy (<see cref="PaceDerivation" /> over
///     the team's own T rounds). The buy filter narrows the classes and the plant times; time to contact always
///     splits by buy. Every number opens its rounds.
/// </summary>
public sealed class PaceLayer() : ScoutingLayerBase("pace", "Pace", ScoutingLayerPlacement.Strip,
    ScoutingSides.T, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.T), "D")
{
    /// <inheritdoc />
    public override bool UsesBuy => true;

    /// <inheritdoc />
    public override string TitleFor(ScoutingSide side) => "Round pace";

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        TeamRoundSet? set = await StripData.RoundsFor(context.Data).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TeamRound> all = set?.On(context.Map, 2) ?? [];
        if (all.Count == 0)
        {
            return set is { DemosRead: > 0 }
                ? ScoutingLayerResult.Empty($"no T rounds on {context.Map}")
                : StripData.Missing(context, set);
        }

        IReadOnlyList<TeamRound> rounds = StripData.Filter(all, context.Buy);
        List<ScoutingInspectorSection> sections = [];
        List<ScoutingRead> reads = [];
        ScoutingMiniChart? chart = null;
        string count = "";
        if (rounds.Count == 0)
        {
            sections.Add(new ScoutingInspectorSection("Pace class",
            [
                new ScoutingInspectorRow
                {
                    Label = $"no {StripData.BuyWord(context.Buy)}T rounds on {context.Map}", StarText = "pace class"
                }
            ]));
        }
        else
        {
            IReadOnlyList<TendencyBucket> classes = PaceDerivation.Classes(rounds);
            sections.Add(new ScoutingInspectorSection("Pace class",
            [
                .. classes.Select(c => StripData.Share(c.Label, c.Rounds, rounds.Count, c.Label.ToLowerInvariant(),
                    $"{context.Prefix} · {c.Label.ToLowerInvariant()}", ScoutingColor.T))
            ]));

            IReadOnlyList<PlantDot> plants = PaceDerivation.Plants(rounds);
            List<ScoutingInspectorRow> plantRows = [];
            foreach (BombSite site in (BombSite[])[BombSite.A, BombSite.B])
            {
                List<PlantDot> here = [.. plants.Where(p => p.Site == site)];
                if (TeamRounds.Median(here.Select(p => p.Seconds)) is { } median)
                {
                    plantRows.Add(new ScoutingInspectorRow
                    {
                        Label = $"{site} median",
                        Value = Clock(median),
                        StarText = $"plant time {site}",
                        Note = $"{Plural(here.Count, "plant")}, won {here.Count(p => p.Round.Won)}",
                        Rounds = [.. here.Select(p => p.Round.Around(p.Round.Facts.PlantTick!.Value))],
                        OpenTitle = $"{context.Prefix} · plants on {site}"
                    });
                }
            }

            IReadOnlyList<TeamRound> noPlant = PaceDerivation.NoPlant(rounds);
            if (noPlant.Count > 0)
            {
                plantRows.Add(new ScoutingInspectorRow
                {
                    Label = "No plant",
                    Value = $"{Plural(noPlant.Count, "round")}, won {noPlant.Count(r => r.Won)}",
                    StarText = "no plant",
                    Note = $"{Percent((double)noPlant.Count / rounds.Count)} of T rounds",
                    Rounds = [.. noPlant.Select(r => r.Clip)],
                    OpenTitle = $"{context.Prefix} · no plant"
                });
            }

            sections.Add(new ScoutingInspectorSection("Plant time, after freeze end", plantRows));
            chart = Chart(plants);
            count = TeamRounds.Median(plants.Select(p => p.Seconds)) is { } m ? $"median {Clock(m)}" : "";
            reads.Add(new ScoutingRead(Read(classes, rounds.Count), Suggested: true));
        }

        IReadOnlyList<ContactByBuy> contact = PaceDerivation.ContactByBuy(all);
        sections.Add(new ScoutingInspectorSection("Time to first contact, by buy",
        [
            .. contact.Select(c => new ScoutingInspectorRow
            {
                Label = c.Rounds.Label,
                Value = c.MedianSeconds is { } m ? $"median {Clock(m)}" : "no contact",
                StarText = $"contact {c.Rounds.Label.ToLowerInvariant()}",
                Note = $"contact in {CountText(c.Rounds.Count, c.Of)}",
                Rounds = c.Rounds.Rounds,
                Buy = BuyFilter(c.Buy),
                OpenTitle = $"{context.Prefix} · first contact on a {SituationalBehaviourService.BuyLabel(c.Buy)}"
            })
        ]));

        return new ScoutingLayerResult
        {
            CountLabel = count,
            Reads = reads,
            Sections = [.. sections.Where(s => s.Rows.Count > 0)],
            MiniChart = chart
        };
    }

    /// <summary>The read: the most common class, and how often they plant fast.</summary>
    /// <param name="classes">The three classes.</param>
    /// <param name="of">The rounds.</param>
    public static string Read(IReadOnlyList<TendencyBucket> classes, int of)
    {
        ArgumentNullException.ThrowIfNull(classes);
        TendencyBucket top = classes.MaxBy(c => c.Count)!;
        string fast = $"{CountText(classes[0].Count, of)} is a fast plant by 0:35";
        if (ReferenceEquals(top, classes[0]))
        {
            return $"They play fast: {fast}.";
        }

        return ReferenceEquals(top, classes[1])
            ? $"Most T rounds default into an execute; {fast}."
            : $"Most T rounds end late or with no plant; {fast}.";
    }

    /// <summary>The mini: one lane per site, 0:00 to 1:55, the fast and late bands, filled = won, the median line.</summary>
    /// <param name="plants">The planted rounds.</param>
    public static ScoutingStripChart? Chart(IReadOnlyList<PlantDot> plants)
    {
        ArgumentNullException.ThrowIfNull(plants);
        if (plants.Count == 0)
        {
            return null;
        }

        ScoutingStripBand[] bands =
        [
            new(0, PaceDerivation.FastSeconds), new(PaceDerivation.LateSeconds, PaceDerivation.AxisSeconds)
        ];
        List<ScoutingStripLane> lanes = [];
        foreach (BombSite site in (BombSite[])[BombSite.A, BombSite.B])
        {
            List<PlantDot> here = [.. plants.Where(p => p.Site == site)];
            double? median = TeamRounds.Median(here.Select(p => p.Seconds));
            lanes.Add(new ScoutingStripLane(site.ToString(),
                [.. here.Select(p => new ScoutingStripDot(p.Seconds, p.Round.Won))], median,
                PaceDerivation.AxisSeconds, bands, ScoutingColor.T)
            {
                Text = median is { } m ? $"{site}: {Plural(here.Count, "plant")}, median {Clock(m)}" : $"{site}: no plant"
            });
        }

        string overall = TeamRounds.Median(plants.Select(p => p.Seconds)) is { } all ? Clock(all) : "";
        return new ScoutingStripChart(lanes)
        {
            Caption = $"plant time 0:00 → 1:55 · bands: fast by 0:35, late after 1:15 · filled = round won · median {overall}"
        };
    }

    private static ScoutingBuy? BuyFilter(BuyType buy) => buy switch
    {
        BuyType.Eco => ScoutingBuy.Eco,
        BuyType.Semi => ScoutingBuy.Semi,
        BuyType.Force => ScoutingBuy.Force,
        BuyType.Full => ScoutingBuy.Full,
        _ => null
    };
}
