#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Plants (T): which site they hit (the Opening Tendencies' site split), where on it they plant, how they
///     hold it and how the round ends after the plant (the Post-Plant And Retake). Every number opens its rounds.
///     <para>
///         The map draws each plant at the planter's spot: a filled dot when the round was won, a ring when it was
///         lost (the shape says it, not only the colour), and a badge ("A1") at the centre of each named spot.
///         Splits by the T buy.
///     </para>
/// </summary>
public sealed class PlantsLayer() : ScoutingLayerBase("plants", "Plants", ScoutingLayerPlacement.Map,
    ScoutingSides.T, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.T), "E/D")
{
    // Sized for the stage over real radar art: the Phase 1 marks at 3 to 4 px vanished into it.
    private const float WonSize = 7f;
    private const float LostSize = 8f;
    private const float BadgeSize = 11f;

    /// <inheritdoc />
    public override bool UsesBuy => true;

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

        BuyType? buy = BuyOf(context.Buy);
        PlantReading reading = PlantReading.Read(block, openings, buy);
        if (reading.SideRounds.Count == 0 && reading.SiteRounds == 0)
        {
            return ScoutingLayerResult.Empty(buy is null
                ? $"no T rounds on {context.Map}"
                : $"no {context.Buy.ToString().ToLowerInvariant()} T rounds on {context.Map}");
        }

        return Compose(context, reading);
    }

    /// <summary>The layer's result from a reading: the marks, the reads and the inspector rows.</summary>
    /// <param name="context">The scope.</param>
    /// <param name="reading">The numbers.</param>
    public static ScoutingLayerResult Compose(ScoutingLayerContext context, PlantReading reading)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reading);
        List<ScoutingInspectorSection> sections = [];
        List<ScoutingRead> reads = [];
        int rounds = reading.SideRounds.Count;
        int planted = reading.Planted.Count;

        if (reading.HasBlock)
        {
            sections.Add(new ScoutingInspectorSection("",
            [
                Sample(context, reading.SideRounds, "rounds"),
                Row(context, reading.Planted, rounds, "", label: "planted") with
                {
                    StarText = "plants", OpenTitle = $"{context.Prefix} · plants"
                }
            ]));
        }

        sections.Add(new ScoutingInspectorSection("Site targeting", SiteRows(context, reading)));

        if (reading.HasBlock)
        {
            List<ScoutingInspectorRow> top = [];
            HashSet<string> tops = [];
            foreach ((string site, PlantSpot spot, int ofSite) in reading.TopPerSite())
            {
                tops.Add(spot.Label);
                top.Add(Row(context, spot.Bucket, ofSite, "plant", ScoutingColor.T, $"{SiteName(site)} · {spot.Label}") with
                {
                    Badge = spot.Badge
                });
            }

            sections.Add(new ScoutingInspectorSection("Most common plant per site", top));
            sections.Add(new ScoutingInspectorSection("Other plant spots", [
                .. reading.Spots.Where(s => !tops.Contains(s.Label))
                    .Select(s => Row(context, s.Bucket, planted, "plant", ScoutingColor.T) with
                    {
                        Badge = s.HasCentre ? s.Badge : null
                    })
            ]));
            sections.Add(new ScoutingInspectorSection("How they hold it", [
                .. Rows(context, reading.HoldShapes, planted, "hold"),
                .. Rows(context, reading.HoldPlaces, planted, "hold from")
            ]));
            sections.Add(new ScoutingInspectorSection("At the plant", [
                .. Rows(context, reading.ManCount, planted, "at the plant"),
                .. Rows(context, reading.Outcomes, planted, "after the plant")
            ]));
            if (planted > 0)
            {
                sections.Add(new ScoutingInspectorSection("Post-plant", [
                    Row(context, reading.PlantedWon, planted, "", ScoutingColor.T, "planted rounds won") with
                    {
                        StarText = "plants won", OpenTitle = $"{context.Prefix} · plants won"
                    }
                ]));
            }

            PlantSpot? best = reading.Spots.Where(s => s.HasCentre).OrderByDescending(s => s.Bucket.Count).FirstOrDefault();
            if (planted > 0)
            {
                reads.Add(new ScoutingRead(best is null
                        ? $"They plant in {CountText(planted, rounds)} on T."
                        : $"They plant in {CountText(planted, rounds)} on T; most often at {best.Label} ({best.Bucket.Count} of {planted}).",
                    Suggested: true));
            }
        }

        if (Top(reading.SiteSplit, "no plant") is { } hit)
        {
            reads.Add(new ScoutingRead($"They hit {hit.Label} most: {CountText(hit.Count, reading.SiteRounds)}.",
                "read site"));
        }

        return new ScoutingLayerResult
        {
            CountLabel = $"{planted} plants",
            Reads = reads,
            Marks = Marks(reading),
            Legend =
            [
                new ScoutingLegendEntry(new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.T),
                    "dot = plant, round won"),
                new ScoutingLegendEntry(new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.T),
                    "ring = plant, round lost"),
                new ScoutingLegendEntry(new ScoutingSwatch(ScoutingMarkShape.Badge, ScoutingColor.T),
                    "A1 = named plant spot")
            ],
            Sections = [.. sections.Where(s => s.Rows.Count > 0)]
        };
    }

    /// <summary>The marks of a reading: one per plant, filled when won and a ring when lost, and a badge per named spot.</summary>
    /// <param name="reading">The numbers.</param>
    public static IReadOnlyList<ScoutingMapMark> Marks(PlantReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        Dictionary<(string, int), PlantSpot> spotOf = [];
        foreach (PlantSpot spot in reading.Spots)
        {
            foreach (TendencyRound round in spot.Bucket.Rounds)
            {
                spotOf.TryAdd((round.DemoPath, round.RoundNumber), spot);
            }
        }

        PlantSpot? SpotOf(PlantSpotPoint p) =>
            spotOf.TryGetValue((p.Round.DemoPath, p.Round.RoundNumber), out PlantSpot? s) ? s : null;
        List<ScoutingMapMark> marks =
        [
            .. reading.Points.Select(p => (p.Won
                ? new ScoutingMapMark(p.X, p.Y, p.Z, ScoutingMarkShape.Dot, ScoutingColor.T, WonSize, Opacity: 0.9f)
                : new ScoutingMapMark(p.X, p.Y, p.Z, ScoutingMarkShape.Ring, ScoutingColor.T, LostSize)) with
            {
                Info = PointInfo(p, SpotOf(p))
            })
        ];
        marks.AddRange(reading.Spots.Where(s => s.HasCentre).Select(s =>
            new ScoutingMapMark(s.X, s.Y, s.Z, ScoutingMarkShape.Badge, ScoutingColor.T, BadgeSize, s.Badge)
            {
                Info = SpotInfo(s, [.. reading.Points.Where(p => ReferenceEquals(SpotOf(p), s))], reading.Planted.Count)
            }));
        return marks;
    }

    // One plant: the site and spot, the buy, whether the round was won, the plant to watch.
    private static ScoutingMarkInfo PointInfo(PlantSpotPoint point, PlantSpot? spot)
    {
        List<ScoutingInspectorRow> rows =
        [
            new() { Label = "Spot", Value = spot?.Label ?? $"{point.Site} site", StarText = "plant spot" },
            new() { Label = "Buy", Value = ScoutingReportLines.BuyLabelFor(point.Buy), StarText = "plant buy" },
            new() { Label = "Round", Value = point.Won ? "won" : "lost", StarText = "plant round" },
            new()
            {
                Label = "Watch the plant",
                Value = $"round {point.Round.RoundNumber}",
                StarText = "watch plant",
                Rounds = [point.Round],
                OpenTitle = $"plant {point.Site} · {spot?.Label ?? "spot"}",
                Play = (point.Round.DemoPath, point.Round.FromTick)
            }
        ];
        return new ScoutingMarkInfo($"Plant {point.Site} · round {point.Round.RoundNumber}", rows);
    }

    // A numbered plant spot: how often they plant there and how those rounds go; clicking rings each plant.
    private static ScoutingMarkInfo SpotInfo(PlantSpot spot, List<PlantSpotPoint> points, int planted)
    {
        List<ScoutingInspectorRow> rows =
        [
            new()
            {
                Label = "Planted here",
                Value = CountText(spot.Bucket.Count, planted),
                StarText = $"plant {spot.Label}",
                BarPercent = planted > 0 ? 100.0 * spot.Bucket.Count / planted : null,
                Rounds = spot.Bucket.Rounds,
                OpenTitle = $"plant {spot.Label}"
            }
        ];
        if (points.Count > 0)
        {
            rows.Add(new ScoutingInspectorRow
            {
                Label = "Rounds won after planting here",
                Value = CountText(points.Count(p => p.Won), points.Count),
                StarText = $"plant {spot.Label} won"
            });
            rows.AddRange(points.GroupBy(p => p.Buy).OrderByDescending(g => g.Count()).Select(g =>
                new ScoutingInspectorRow
                {
                    Label = Capital(ScoutingReportLines.BuyLabelFor(g.Key)),
                    Value = Plural(g.Count(), "plant"),
                    StarText = $"plant {spot.Label} {g.Key}"
                }));
        }

        return new ScoutingMarkInfo($"Plant spot {spot.Badge} · {spot.Label}", rows)
        {
            Hint = points.Count > 0 ? $"ring its {Plural(points.Count, "plant")}" : "",
            Detail =
            [
                .. points.Select(p => new ScoutingMapMark(p.X, p.Y, p.Z, ScoutingMarkShape.Ring, ScoutingColor.Neutral,
                    LostSize + 3f))
            ]
        };
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static IReadOnlyList<ScoutingInspectorRow> SiteRows(ScoutingLayerContext context, PlantReading reading) =>
    [
        .. reading.SiteSplit.Select(b => Row(context, b, reading.SiteRounds, "site", ScoutingColor.T) with
        {
            Label = PlantReading.IsNoPlant(b.Label) ? b.Label : SiteName(b.Label)
        })
    ];

    private static string SiteName(string site) => site is "A" or "B" ? $"{site} site" : site;

    private static BuyType? BuyOf(ScoutingBuy buy) => buy switch
    {
        ScoutingBuy.Eco => BuyType.Eco,
        ScoutingBuy.Semi => BuyType.Semi,
        ScoutingBuy.Force => BuyType.Force,
        ScoutingBuy.Full => BuyType.Full,
        _ => null
    };
}
