#region

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.Sdk.Ui;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     AWP routes, per side, map and buy: where the AWP starts and where it goes to 0:15, 0:30 or first contact (the
///     inspector's pills), as clustered routes (<see cref="AwpRoutes" />) drawn as white trails that fade from a start
///     ring to a numbered end marker; who holds it; and the stat rows: AWP bought, AWP opening duels, moves after the
///     first shot, saved when the round is lost, two AWPs, and peeks mid before 0:10 (T) or holds the start spot
///     (CT). The holder comes from the round captures' evidence over the round, or the positions' weapon samples
///     over a window when captures are missing (<see cref="AwpRules.Read" />). Every number opens its rounds; a
///     route plays its medoid round in 2D with the AWPer followed.
/// </summary>
public sealed class AwpLayer() : ScoutingLayerBase("awp", "AWP routes", ScoutingLayerPlacement.Map,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.Neutral), "D-rs"), IScoutingWindowedLayer
{
    /// <summary>The window key of the first-contact trail, the one shown first.</summary>
    public const string ContactKey = "contact";

    // One read per team load and map: every side, buy and window of the map filters the same rounds.
    private static readonly ConditionalWeakTable<ScoutingReportData, ConcurrentDictionary<string, Task<IReadOnlyList<AwpRound>?>>>
        _reads = [];

    /// <inheritdoc />
    public override bool UsesBuy => true;

    /// <inheritdoc />
    public IReadOnlyList<ScoutingWindow> Windows { get; } =
        [new("0:15", "0:15"), new("0:30", "0:30"), new(ContactKey, "first contact")];

    /// <inheritdoc />
    public string Window { get; set; } = ContactKey;

    /// <inheritdoc />
    public override string TitleFor(ScoutingSide side) => "AWP";

    /// <summary>The window a key names; first contact for anything else.</summary>
    /// <param name="key">"0:15", "0:30", "contact".</param>
    public static AwpWindow WindowOf(string key) => key switch
    {
        "0:15" => AwpWindow.To15,
        "0:30" => AwpWindow.To30,
        _ => AwpWindow.Contact
    };

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Data.Sources.RoundFacts is null)
        {
            return ScoutingLayerResult.Empty(ScoutingLayerResult.NotOnThisHost);
        }

        IReadOnlyList<AwpRound>? all = await _reads.GetValue(context.Data,
                _ => new ConcurrentDictionary<string, Task<IReadOnlyList<AwpRound>?>>(StringComparer.OrdinalIgnoreCase))
            .GetOrAdd(context.Map, map => ReadAsync(context.Data, map))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (all is null)
        {
            return ScoutingLayerResult.Unreadable("AWP rounds", null);
        }

        List<AwpRound> rounds =
            [.. all.Where(r => r.Round.Side == (int)context.Side && Matches(context.Buy, r.Round.Buy))];
        IReadOnlyDictionary<string, ScoutingColor> colors =
            ScoutingPlayerColors.Lookup(context.Data.Sources.Teams, context.TeamId);
        string window = context.Window.Length > 0 ? context.Window : Window;
        return Compose(context.Prefix, context.Side, context.Buy, context.Map, rounds, WindowOf(window),
            Windows.FirstOrDefault(w => w.Key == window)?.Label ?? "first contact", colors);
    }

    private static Task<IReadOnlyList<AwpRound>?> ReadAsync(ScoutingReportData data, string map) =>
        StripData.RoundsFor(data).ContinueWith(t => t.Result is { } set
                ? ScoutingLayerContext.Offload<IReadOnlyList<AwpRound>?>(() =>
                {
                    try
                    {
                        return Read(data, set.On(map));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                   or InvalidOperationException)
                    {
                        return null;
                    }
                })
                : Task.FromResult<IReadOnlyList<AwpRound>?>(null),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();

    // Each demo's positions and captures read once, then every round of it.
    private static List<AwpRound> Read(ScoutingReportData data, IReadOnlyList<TeamRound> rounds)
    {
        List<AwpRound> reads = [];
        foreach (IGrouping<string, TeamRound> demo in rounds.GroupBy(r => r.DemoPath, StringComparer.Ordinal))
        {
            TeamRound first = demo.First();
            RoundPositionsDocument? positions = data.Positions.Read(demo.Key, first.Map, first.Sha256);
            DemoCaptures? captures = data.Captures.Read(demo.Key, first.Map, first.Sha256);
            reads.AddRange(demo.Select(r => AwpRules.Read(r, positions, captures?.Round(r.Number))));
        }

        return reads;
    }

    /// <summary>The layer for one scope's rounds: routes, who holds it, the stat rows, the read and the marks.</summary>
    /// <param name="prefix">"de_mirage CT".</param>
    /// <param name="side">The side.</param>
    /// <param name="buy">The buy filter, for the empty message.</param>
    /// <param name="map">The map, for the empty message.</param>
    /// <param name="rounds">The scope's rounds, AWP or not.</param>
    /// <param name="window">How far the trails run.</param>
    /// <param name="windowLabel">"first contact", "0:15".</param>
    /// <param name="colors">The roster's colours by SteamID64 and by name.</param>
    public static ScoutingLayerResult Compose(string prefix, ScoutingSide side, ScoutingBuy buy, string map,
        IReadOnlyList<AwpRound> rounds, AwpWindow window, string windowLabel,
        IReadOnlyDictionary<string, ScoutingColor> colors)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(colors);
        string sideLabel = side.Label();
        if (rounds.Count == 0)
        {
            return ScoutingLayerResult.Empty(
                $"no {(buy == ScoutingBuy.All ? "" : buy.ToString().ToLowerInvariant() + " ")}{sideLabel} rounds on {map}");
        }

        List<AwpRound> read = [.. rounds.Where(r => r.PositionsRead || r.CapturesRead)];
        if (read.Count == 0)
        {
            return ScoutingLayerResult.Empty("no positions or round captures for these rounds; rebuild the round index");
        }

        List<AwpRound> awp = [.. read.Where(r => r.HasAwp)];
        if (awp.Count == 0)
        {
            return ScoutingLayerResult.Empty($"no {sideLabel} AWP seen in {Plural(read.Count, "round")}");
        }

        AwpRouteSet routes = AwpRoutes.Build(awp, window);
        List<ScoutingInspectorSection> sections =
        [
            new($"Routes · trail to {windowLabel}", RouteRows(prefix, routes)),
            new("Who holds it", HolderRows(prefix, awp, colors)),
            new("", StatRows(prefix, side, read, awp))
        ];

        return new ScoutingLayerResult
        {
            CountLabel = routes.Routes.Count == 1 ? "1 route" : $"{routes.Routes.Count} routes",
            Reads = ReadOf(side, awp, routes, windowLabel) is { } sentence ? [sentence] : [],
            Marks = Marks(routes),
            Legend = [new ScoutingLegendEntry(new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.Neutral),
                $"ring = AWP start · white trail · numbered = position at {windowLabel}")],
            Sections = [.. sections.Where(s => s.Rows.Count > 0)]
        };
    }

    private static List<ScoutingInspectorRow> RouteRows(string prefix, AwpRouteSet set)
    {
        List<ScoutingInspectorRow> rows =
        [
            .. set.Routes.Select((route, i) => new ScoutingInspectorRow
            {
                Label = route.Name,
                Value = Percent((double)route.Members.Count / set.Clustered),
                StarText = $"route {route.Name}",
                Badge = (i + 1).ToString(CultureInfo.InvariantCulture),
                BarPercent = 100.0 * route.Members.Count / set.Clustered,
                Swatch = ScoutingColor.Neutral,
                Note = CountText(route.Members.Count, set.Clustered),
                Rounds = [.. route.Members.Select(m => m.Round.Clip)],
                OpenTitle = $"{prefix} · AWP route {route.Name}",
                Play = (route.Medoid.Round.DemoPath, route.Medoid.Round.Facts.FreezeEndTick),
                FollowSteamId = route.Medoid.HolderSteamId is > 0 and var id ? id : null
            })
        ];
        if (set.Other.Count > 0)
        {
            rows.Add(new ScoutingInspectorRow
            {
                Label = "other",
                Value = Percent((double)set.Other.Count / set.Clustered),
                StarText = "route other",
                BarPercent = 100.0 * set.Other.Count / set.Clustered,
                Swatch = ScoutingColor.Neutral,
                Note = CountText(set.Other.Count, set.Clustered),
                Rounds = [.. set.Other.Select(m => m.Round.Clip)],
                OpenTitle = $"{prefix} · AWP route other"
            });
        }

        return rows;
    }

    private static List<ScoutingInspectorRow> HolderRows(string prefix, List<AwpRound> awp,
        IReadOnlyDictionary<string, ScoutingColor> colors) =>
    [
        .. awp.GroupBy(r => Who(r), StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ScoutingInspectorRow
            {
                Label = g.Key,
                Value = CountText(g.Count(), awp.Count),
                StarText = $"holder {g.Key}",
                BarPercent = 100.0 * g.Count() / awp.Count,
                Swatch = ColorOf(g.First(), colors),
                Rounds = [.. g.Select(r => r.Round.Clip)],
                OpenTitle = $"{prefix} · AWP held by {g.Key}"
            })
    ];

    private static List<ScoutingInspectorRow> StatRows(string prefix, ScoutingSide side, List<AwpRound> read,
        List<AwpRound> awp)
    {
        List<ScoutingInspectorRow> rows = [Count(prefix, "rounds with an AWP", awp, read.Count)];

        List<AwpRound> captured = [.. read.Where(r => r.CapturesRead && r.Round.Buy != BuyType.Pistol)];
        if (captured.Count > 0)
        {
            rows.Add(Count(prefix, "AWP bought", [.. captured.Where(r => r.Bought == true)], captured.Count) with
            {
                Note = "of the non-pistol rounds; an AWP kept from the round before is not bought"
            });
        }
        else
        {
            rows.Add(Count(prefix, "AWP held (approximate)", awp, read.Count) with
            {
                StarText = "AWP held",
                Note = "from the weapon in hand; the round captures that know buys are missing"
            });
        }

        List<AwpRound> duels = [.. awp.Where(r => r.OpeningWon is not null)];
        int won = duels.Count(r => r.OpeningWon == true);
        rows.Add(new ScoutingInspectorRow
        {
            Label = "AWP opening duels",
            Value = $"{won}–{duels.Count - won}",
            StarText = "AWP opening duels",
            Note = duels.Count == 0 ? null : "won–lost, of the round's first kill",
            Rounds = [.. duels.Select(r => r.Round.Clip)],
            OpenTitle = $"{prefix} · AWP opening duels"
        });

        rows.Add(Share(prefix, "moves after the first shot", "Moves after the first shot",
            awp.Where(r => r.MovedAfterShot is not null).ToList(), r => r.MovedAfterShot == true,
            $"more than {AwpRules.MovedAfterShotUnits} units within {AwpRules.MovedAfterShotSeconds} s"));

        List<AwpRound> lost = [.. awp.Where(r => r.SavedOnLoss is not null)];
        rows.Add(Count(prefix, "Saved when the round is lost", [.. lost.Where(r => r.SavedOnLoss == true)], lost.Count));

        List<AwpRound> doubles = [.. awp.Where(r => r.Holders >= 2)];
        rows.Add(new ScoutingInspectorRow
        {
            Label = "Two AWPs",
            Value = Plural(doubles.Count, "round"),
            StarText = "Two AWPs",
            Rounds = [.. doubles.Select(r => r.Round.Clip)],
            OpenTitle = $"{prefix} · two AWPs"
        });

        rows.Add(side == ScoutingSide.T
            ? Share(prefix, $"peeks mid before {Clock(AwpRules.PeekMidSeconds)}",
                $"Peeks mid before {Clock(AwpRules.PeekMidSeconds)}",
                awp.Where(r => r.PeeksMid is not null).ToList(), r => r.PeeksMid == true, "in a mid place")
            : Share(prefix, "holds the start spot", "Holds the start spot",
                awp.Where(r => r.HoldsStartSpot is not null).ToList(), r => r.HoldsStartSpot == true,
                $"within {AwpRules.HoldRadius} units of the {Clock(AwpRules.HoldFromSeconds)} spot until first contact"));
        return rows;
    }

    private static ScoutingInspectorRow Count(string prefix, string label, List<AwpRound> rounds, int of) => new()
    {
        Label = label,
        Value = CountText(rounds.Count, of),
        StarText = label,
        Rounds = [.. rounds.Select(r => r.Round.Clip)],
        OpenTitle = $"{prefix} · {label.ToLowerInvariant()}"
    };

    // A share row: the percent of the sample; the number opens the rounds it counts.
    private static ScoutingInspectorRow Share(string prefix, string key, string label, List<AwpRound> sample,
        Func<AwpRound, bool> counts, string how)
    {
        List<AwpRound> yes = [.. sample.Where(counts)];
        return new ScoutingInspectorRow
        {
            Label = label,
            Value = sample.Count == 0 ? "—" : Percent((double)yes.Count / sample.Count),
            StarText = label,
            Note = sample.Count == 0 ? "no rounds to read" : $"{CountText(yes.Count, sample.Count)}, {how}",
            Rounds = [.. yes.Select(r => r.Round.Clip)],
            OpenTitle = $"{prefix} · AWP {key}"
        };
    }

    // "CT AWP: yuurih holds it in 84% of AWP rounds; most often CTSpawn → A site (52% to first contact); moves after
    // the first shot in 61%."
    private static ScoutingRead? ReadOf(ScoutingSide side, List<AwpRound> awp, AwpRouteSet routes, string windowLabel)
    {
        if (awp.GroupBy(r => Who(r), StringComparer.Ordinal).OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault() is not { } top)
        {
            return null;
        }

        List<string> parts = [$"{top.Key} holds it in {Percent((double)top.Count() / awp.Count)} of AWP rounds"];
        if (routes.Routes.Count > 0 && routes.Routes[0] is { } route)
        {
            parts.Add($"most often {route.Name} ({Percent((double)route.Members.Count / routes.Clustered)} to {windowLabel})");
        }

        (List<AwpRound> sample, int yes, string what) = side == ScoutingSide.T
            ? (awp.Where(r => r.PeeksMid is not null).ToList(), awp.Count(r => r.PeeksMid == true),
                $"peeks mid before {Clock(AwpRules.PeekMidSeconds)}")
            : (awp.Where(r => r.MovedAfterShot is not null).ToList(), awp.Count(r => r.MovedAfterShot == true),
                "moves after the first shot");
        if (sample.Count > 0)
        {
            parts.Add($"{what} in {Percent((double)yes / sample.Count)}");
        }

        return new ScoutingRead($"{side.Label()} AWP: {string.Join("; ", parts)}.", Suggested: true);
    }

    // Per route: the medoid trail as dots fading from the start, a ring at the start and a numbered badge at the
    // window's end. A bigger route draws a thicker trail.
    private static List<ScoutingMapMark> Marks(AwpRouteSet set)
    {
        List<ScoutingMapMark> marks = [];
        for (int i = 0; i < set.Routes.Count; i++)
        {
            AwpRoute route = set.Routes[i];
            IReadOnlyList<AwpPoint> trail = route.Trail;
            double share = (double)route.Members.Count / Math.Max(1, set.Clustered);
            float size = (float)(2.0 + 2.4 * share);
            float start = Math.Max(0.4f, 0.95f - 0.15f * i);
            List<(float X, float Y, float Z)> dots = [];
            for (int j = 0; j < trail.Count; j++)
            {
                dots.Add((trail[j].X, trail[j].Y, trail[j].Z));
                if (j + 1 < trail.Count)
                {
                    // Two in-between dots a second, so a run reads as a line and not a string of beads.
                    for (int k = 1; k <= 2; k++)
                    {
                        float f = k / 3f;
                        dots.Add((trail[j].X + (trail[j + 1].X - trail[j].X) * f,
                            trail[j].Y + (trail[j + 1].Y - trail[j].Y) * f,
                            trail[j].Z + (trail[j + 1].Z - trail[j].Z) * f));
                    }
                }
            }

            for (int j = 0; j < dots.Count; j++)
            {
                float opacity = start - 0.55f * j / Math.Max(1, dots.Count);
                marks.Add(new ScoutingMapMark(dots[j].X, dots[j].Y, dots[j].Z, ScoutingMarkShape.Dot,
                    ScoutingColor.Neutral, size, Opacity: Math.Max(0.15f, opacity)));
            }

            AwpPoint first = trail[0];
            AwpPoint last = trail[^1];
            marks.Add(new ScoutingMapMark(first.X, first.Y, first.Z, ScoutingMarkShape.Ring, ScoutingColor.Neutral,
                StartRingSize));
            marks.Add(new ScoutingMapMark(last.X, last.Y, last.Z, ScoutingMarkShape.Badge, ScoutingColor.Neutral,
                EndBadgeSize, (i + 1).ToString(CultureInfo.InvariantCulture)));
        }

        return marks;
    }

    /// <summary>The start ring's radius in screen pixels.</summary>
    public const float StartRingSize = 7f;

    /// <summary>The numbered end marker's radius in screen pixels.</summary>
    public const float EndBadgeSize = 9f;

    private static string Who(AwpRound round) =>
        round.HolderName.Length > 0 ? round.HolderName : $"slot {round.HolderSlot}";

    private static ScoutingColor ColorOf(AwpRound round, IReadOnlyDictionary<string, ScoutingColor> colors) =>
        round.HolderSteamId > 0
        && colors.TryGetValue(round.HolderSteamId.ToString(CultureInfo.InvariantCulture), out ScoutingColor byId)
            ? byId
            : colors.TryGetValue(DisplayText.Sanitize(round.HolderName), out ScoutingColor byName)
                ? byName
                : ScoutingColor.Neutral;

    private static bool Matches(ScoutingBuy buy, BuyType actual) => buy switch
    {
        ScoutingBuy.Eco => actual == BuyType.Eco,
        ScoutingBuy.Semi => actual == BuyType.Semi,
        ScoutingBuy.Force => actual == BuyType.Force,
        ScoutingBuy.Full => actual == BuyType.Full,
        _ => true
    };
}
