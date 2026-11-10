#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Lurks (T): who lurks, in how many rounds, when the lurk splits off and which places it goes via, with the
///     lurker's position at 0:30 on the map. Reads the Opening Tendencies' per-round lurk samples, so the buy
///     filter re-counts every number. Every number opens its rounds.
/// </summary>
public sealed class LurksLayer() : ScoutingLayerBase("lurks", "Lurks", ScoutingLayerPlacement.Map,
    ScoutingSides.T, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Lurk), "E")
{
    private const string NoLurk = "no lurk";

    /// <inheritdoc />
    public override bool UsesBuy => true;

    /// <summary>The sample rounds a buy filter keeps.</summary>
    /// <param name="samples">Every round with positions.</param>
    /// <param name="buy">The filter.</param>
    public static IReadOnlyList<LurkSample> Filter(IEnumerable<LurkSample> samples, ScoutingBuy buy)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return [.. samples.Where(s => buy == ScoutingBuy.All || BuyOf(s.Buy) == buy)];
    }

    /// <summary>The median of the split-off seconds; 0 with none.</summary>
    /// <param name="seconds">The values.</param>
    public static double Median(IEnumerable<double> seconds)
    {
        double[] sorted = [.. seconds.Order()];
        return sorted.Length == 0 ? 0
            : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (await LayerData.Openings(context).ConfigureAwait(false) is not { } block)
        {
            return await LayerData.OpeningsMissing(context).ConfigureAwait(false);
        }

        if (block.LurkRounds == 0)
        {
            return ScoutingLayerResult.Empty("lurk: no positions for these rounds; rebuild the index");
        }

        IReadOnlyList<LurkSample> rounds = Filter(block.LurkSamples, context.Buy);
        if (rounds.Count == 0)
        {
            return ScoutingLayerResult.Empty(
                $"no {(context.Buy == ScoutingBuy.All ? "" : context.Buy.ToString().ToLowerInvariant() + " ")}T rounds with positions on {context.Map}");
        }

        IReadOnlyDictionary<string, ScoutingColor> colors = LurkPlayerColors.For(context.Data.Sources, context.TeamId);
        List<LurkDetail> lurks = [.. rounds.Where(r => r.Lurk is not null).Select(r => r.Lurk!)];
        TendencyBucket all = new("rounds", [.. rounds.Select(r => r.Opening)]);
        TendencyBucket lurking = new("lurk rounds", [.. lurks.Select(l => l.Clip)]);

        List<ScoutingInspectorSection> sections =
        [
            new("", [
                Sample(context, all, "rounds"),
                lurks.Count > 0 ? Row(context, lurking, rounds.Count, "") : EmptyRow(context, "lurk rounds"),
                .. lurks.Count > 0 ? [MedianRow(context, lurks)] : Array.Empty<ScoutingInspectorRow>()
            ]),
            new($"Who lurks · {Plural(rounds.Count, "round")} with positions", [
                .. lurks.GroupBy(l => l.Name, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => Row(context, new TendencyBucket(g.Key, [.. g.Select(l => l.Clip)]), rounds.Count,
                        "lurker", colors.TryGetValue(g.Key, out ScoutingColor c) ? c : ScoutingColor.Lurk))
            ]),
            new("Split-off, seconds after freeze end", Rows(context, ClockBuckets(rounds), rounds.Count, "lurk")),
            new("Via", Rows(context, ViaBuckets(lurks), lurks.Count, "via"))
        ];

        List<ScoutingRead> reads = [];
        if (lurks.GroupBy(l => l.Name, StringComparer.Ordinal).OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault() is { } top)
        {
            string via = ViaBuckets(lurks).FirstOrDefault() is { } v ? $", most often via {v.Label}" : "";
            reads.Add(new ScoutingRead(
                $"{top.Key} lurks most: {CountText(top.Count(), rounds.Count)}; the lurk splits off at {Clock(Median(lurks.Select(l => l.SplitSeconds)))} (median){via}.",
                Suggested: true));
        }

        List<ScoutingMapMark> marks =
        [
            .. lurks.Where(l => l.At30 is not null).Select(l => new ScoutingMapMark(l.At30!.Value.X, l.At30.Value.Y,
                l.At30.Value.Z, ScoutingMarkShape.Dot, ScoutingColor.Lurk, 5f, Opacity: 0.85f))
        ];
        return new ScoutingLayerResult
        {
            CountLabel = $"{lurks.Count} rds",
            Reads = reads,
            Marks = marks,
            Legend = [new ScoutingLegendEntry(Swatch, $"dot = lurker at {Clock(LurkRoutes.SnapshotSeconds)}")],
            Sections = [.. sections.Where(s => s.Rows.Count > 0)]
        };
    }

    private static ScoutingInspectorRow EmptyRow(ScoutingLayerContext context, string label) => new()
    {
        Label = label, Value = CountText(0, 0), StarText = label, OpenTitle = $"{context.Prefix} {label}"
    };

    private static ScoutingInspectorRow MedianRow(ScoutingLayerContext context, List<LurkDetail> lurks) => new()
    {
        Label = "split-off median",
        Value = Clock(Median(lurks.Select(l => l.SplitSeconds))),
        StarText = "split-off median",
        Rounds = [.. lurks.Select(l => l.Clip)],
        OpenTitle = $"{context.Prefix} · split-off median"
    };

    // The 5 s bars of the opening clock, in order, with "no lurk" last.
    private static List<TendencyBucket> ClockBuckets(IReadOnlyList<LurkSample> rounds)
    {
        List<TendencyBucket> bins =
        [
            .. rounds.Where(r => r.Lurk is not null)
                .GroupBy(r => (int)Math.Floor(Math.Max(0, r.Lurk!.SplitSeconds) / OpeningTendenciesService.ClockBinSeconds))
                .OrderBy(g => g.Key)
                .Select(g => new TendencyBucket(OpeningTendenciesService.ClockLabel(g.Key * OpeningTendenciesService.ClockBinSeconds),
                    [.. g.Select(r => r.Lurk!.Clip)]))
        ];
        TendencyRound[] none = [.. rounds.Where(r => r.Lurk is null).Select(r => r.Opening)];
        if (none.Length > 0)
        {
            bins.Add(new TendencyBucket(NoLurk, none));
        }

        return bins;
    }

    // Each place once per lurk, most lurks first: the split place and the places the lurker goes through.
    private static List<TendencyBucket> ViaBuckets(IEnumerable<LurkDetail> lurks) =>
    [
        .. lurks.SelectMany(l => l.Via.Select(p => (Place: p, l.Clip)))
            .GroupBy(x => x.Place, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TendencyBucket(g.Key, [.. g.Select(x => x.Clip)]))
    ];

    private static ScoutingBuy? BuyOf(BuyType buy) => buy switch
    {
        BuyType.Eco => ScoutingBuy.Eco,
        BuyType.Semi => ScoutingBuy.Semi,
        BuyType.Force => ScoutingBuy.Force,
        BuyType.Full => ScoutingBuy.Full,
        _ => null
    };
}
