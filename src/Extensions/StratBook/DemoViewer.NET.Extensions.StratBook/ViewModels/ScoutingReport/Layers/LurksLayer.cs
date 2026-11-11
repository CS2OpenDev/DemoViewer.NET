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
            return ScoutingLayerResult.Empty("no positions for these rounds; rebuild the round index");
        }

        IReadOnlyList<LurkSample> rounds = Filter(block.LurkSamples, context.Buy);
        if (rounds.Count == 0)
        {
            return ScoutingLayerResult.Empty(
                $"no {(context.Buy == ScoutingBuy.All ? "" : context.Buy.ToString().ToLowerInvariant() + " ")}T rounds with positions on {context.Map}");
        }

        IReadOnlyDictionary<string, ScoutingColor> colors = ScoutingPlayerColors.Lookup(context.Data.Sources.Teams, context.TeamId);
        IReadOnlyDictionary<string, string> names = ScoutingPlayerColors.Names(context.Data.Sources.Teams, context.TeamId);
        List<LurkDetail> lurks = [.. rounds.Where(r => r.Lurk is not null).Select(r => r.Lurk!)];
        // One lurker per player by SteamID, under the roster's name, most rounds first.
        List<(string Key, string Name, List<LurkDetail> Rounds)> lurkers =
        [
            .. lurks.GroupBy(l => ScoutingPlayerColors.KeyOf(l.SteamId, l.Name), StringComparer.Ordinal)
                .Select(g => (g.Key, Name: ScoutingPlayerColors.NameOf(names, g.Key, g.Last().Name), Rounds: g.ToList()))
                .OrderByDescending(g => g.Rounds.Count).ThenBy(g => g.Name, StringComparer.Ordinal)
        ];
        ScoutingColor ColorOf(string key, string name) =>
            colors.TryGetValue(key, out ScoutingColor byId) ? byId
            : colors.TryGetValue(name, out ScoutingColor byName) ? byName
            : ScoutingColor.Lurk;
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
                .. lurkers.Select(g => Row(context, new TendencyBucket(g.Name, [.. g.Rounds.Select(l => l.Clip)]),
                    rounds.Count, "lurker", ColorOf(g.Key, g.Name)))
            ]),
            new("Split-off, seconds after freeze end", Rows(context, ClockBuckets(rounds), rounds.Count, "lurk")),
            // The star keeps the place's own name ("via BombsiteA"); the row shows it as the setups do ("A site").
            new("Via", [
                .. ViaBuckets(lurks).Select(b => Row(context, b, lurks.Count, "via",
                    label: SetupsDerivation.PlaceLabel(b.Label)))
            ])
        ];

        List<ScoutingRead> reads = [];
        if (lurkers.Count > 0 && lurkers[0] is var top)
        {
            string via = ViaBuckets(lurks).FirstOrDefault() is { } v ? $", most often via {SetupsDerivation.PlaceLabel(v.Label)}" : "";
            reads.Add(new ScoutingRead(
                $"{top.Name} lurks most: {CountText(top.Rounds.Count, rounds.Count)}; the lurk splits off at {Clock(Median(lurks.Select(l => l.SplitSeconds)))} (median){via}.",
                Suggested: true));
        }

        // By reference: two lurks can be equal records, and each belongs to the group it was put in.
        Dictionary<LurkDetail, (string Key, string Name, List<LurkDetail> Rounds)> lurkerOf =
            new(ReferenceEqualityComparer.Instance);
        foreach ((string Key, string Name, List<LurkDetail> Rounds) lurker in lurkers)
        {
            foreach (LurkDetail l in lurker.Rounds)
            {
                lurkerOf[l] = lurker;
            }
        }

        List<ScoutingMapMark> marks = [];
        foreach (LurkDetail l in lurks.Where(l => l.At30 is not null))
        {
            ScoutingColor color = ColorOf(ScoutingPlayerColors.KeyOf(l.SteamId, l.Name), l.Name);
            marks.Add(new ScoutingMapMark(l.At30!.Value.X, l.At30.Value.Y, l.At30.Value.Z, ScoutingMarkShape.Dot,
                color, 5f, Opacity: 0.85f)
            {
                Info = LurkInfo(context, l, lurkerOf[l], rounds.Count, color)
            });
        }
        return new ScoutingLayerResult
        {
            CountLabel = $"{lurks.Count} rds",
            Reads = reads,
            Marks = marks,
            Legend = [new ScoutingLegendEntry(Swatch,
                $"dot = the lurker at {Clock(LurkRoutes.SnapshotSeconds)}, in their Players colour")],
            Sections = [.. sections.Where(s => s.Rows.Count > 0)]
        };
    }

    // One lurk's dot: when and where the lurker split off, the way they went, the round to watch; clicking rings
    // the same player's other lurks.
    private static ScoutingMarkInfo LurkInfo(ScoutingLayerContext context, LurkDetail lurk,
        (string Key, string Name, List<LurkDetail> Rounds) lurker, int rounds, ScoutingColor color)
    {
        List<ScoutingInspectorRow> rows =
        [
            new()
            {
                Label = "Split off",
                Value = Clock(lurk.SplitSeconds),
                StarText = "lurk split off",
                Note = lurk.SplitPlace is { } at ? $"from {SetupsDerivation.PlaceLabel(at)}" : null
            }
        ];
        if (lurk.Via.Count > 0)
        {
            rows.Add(new ScoutingInspectorRow
            {
                Label = "Via",
                Note = string.Join(" → ", lurk.Via.Select(SetupsDerivation.PlaceLabel)),
                StarText = "lurk via"
            });
        }

        rows.Add(new ScoutingInspectorRow
        {
            Label = $"{lurker.Name} lurks",
            Value = CountText(lurker.Rounds.Count, rounds),
            StarText = $"lurker {lurker.Name}",
            Swatch = color,
            Rounds = [.. lurker.Rounds.Select(l => l.Clip)],
            OpenTitle = $"{context.Prefix} · {lurker.Name} lurking"
        });
        rows.Add(new ScoutingInspectorRow
        {
            Label = "Watch the round",
            Value = $"round {lurk.Clip.RoundNumber}",
            StarText = "watch round",
            Rounds = [lurk.Clip],
            OpenTitle = $"{context.Prefix} · {lurker.Name} lurk",
            Play = (lurk.Clip.DemoPath, lurk.Clip.FromTick),
            FollowSteamId = lurk.SteamId > 0 ? lurk.SteamId : null
        });
        return new ScoutingMarkInfo($"{lurker.Name} · round {lurk.Clip.RoundNumber}", rows)
        {
            Hint = lurker.Rounds.Count > 1 ? $"ring {lurker.Name}'s other lurks" : "",
            Detail =
            [
                .. lurker.Rounds.Where(l => !ReferenceEquals(l, lurk) && l.At30 is not null).Select(l =>
                    new ScoutingMapMark(l.At30!.Value.X, l.At30.Value.Y, l.At30.Value.Z, ScoutingMarkShape.Ring, color, 7f))
            ]
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
