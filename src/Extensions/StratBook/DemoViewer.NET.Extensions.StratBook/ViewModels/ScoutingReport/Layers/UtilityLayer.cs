#region

using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Utility: the team's own grenades on the map side from the Grenade Index joined to Round Facts, scoped to
///     the buy. The map draws dashed clouds at the landing clusters, lettered S smoke, M molotov, F flash and
///     H HE and sized by the rounds they were thrown in; the inspector reads the most thrown with their median
///     timing and gives the first-grenade clock. Every number opens its rounds in Review.
/// </summary>
public sealed class UtilityLayer() : ScoutingLayerBase("util", "Utility", ScoutingLayerPlacement.Map,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Cloud, ScoutingColor.Neutral), "E/D")
{
    /// <summary>Clusters drawn and listed.</summary>
    public const int MaxClusters = 8;

    private static readonly Dictionary<ScoutingBuy, BuyType> _buys = new()
    {
        [ScoutingBuy.Eco] = BuyType.Eco,
        [ScoutingBuy.Semi] = BuyType.Semi,
        [ScoutingBuy.Force] = BuyType.Force,
        [ScoutingBuy.Full] = BuyType.Full
    };

    /// <inheritdoc />
    public override bool UsesBuy => true;

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ScoutingReportSources sources = context.Data.Sources;
        if (sources.Grenades is not { } grenades || sources.RoundFacts is not { } roundFacts)
        {
            return await FirstGrenadeOnly(context).ConfigureAwait(false);
        }

        BuyType? buy = context.Buy == ScoutingBuy.All ? null : _buys[context.Buy];
        UtilityBlock block = await ScoutingLayerContext.Offload(() =>
            UtilityTendencies.Build(Demos(context, grenades, roundFacts), (int)context.Side, buy))
            .ConfigureAwait(false);
        string buyWord = buy is null ? "" : context.Buy.ToString().ToLowerInvariant() + " ";
        if (block.Rounds == 0)
        {
            return ScoutingLayerResult.Empty(
                $"no {buyWord}{context.Side.Label()} rounds with grenades indexed on {context.Map}; index grenades");
        }

        List<UtilityCluster> top = [.. block.Clusters.Take(MaxClusters)];
        int most = Math.Max(1, top.Count == 0 ? 1 : top.Max(c => c.Rounds.Count));
        ScoutingColor color = context.Side == ScoutingSide.Ct ? ScoutingColor.Ct : ScoutingColor.T;
        List<ScoutingMapMark> marks =
        [
            .. top.Select(c => new ScoutingMapMark(c.Landing.X, c.Landing.Y, c.Landing.Z, ScoutingMarkShape.Cloud,
                color, 9f + 7f * c.Rounds.Count / most, c.Letter, 0.9f))
        ];

        List<ScoutingRead> reads = [];
        if (top.Count > 0)
        {
            UtilityCluster lead = top[0];
            reads.Add(new ScoutingRead(
                $"Most thrown: {lead.Noun} at {lead.Place}, {CountText(lead.Rounds.Count, block.Rounds)}, median {Clock(lead.MedianSeconds)} after freeze end.",
                "most thrown"));
        }

        if (Top(block.FirstClock, UtilityTendencies.NoUtility) is { } first)
        {
            reads.Add(new ScoutingRead(
                $"The first grenade most often goes at {first.Label} ({CountText(first.Count, block.Rounds)}).",
                "first grenade"));
        }

        List<ScoutingInspectorSection> sections = [];
        if (top.Count > 0)
        {
            sections.Add(new ScoutingInspectorSection("Most thrown",
                [.. top.Select(c => ClusterRow(context, c, block.Rounds))]));
        }

        sections.Add(new ScoutingInspectorSection(
            $"First {context.Side.Label()} grenade, seconds after freeze end · {Plural(block.Rounds, "round")}",
            Rows(context, block.FirstClock, block.Rounds, "first utility")));
        return new ScoutingLayerResult
        {
            CountLabel = $"{block.Rounds} rds",
            Marks = marks,
            Reads = reads,
            Sections = sections,
            Legend =
            [
                new ScoutingLegendEntry(new ScoutingSwatch(ScoutingMarkShape.Cloud, color),
                    "letter = S smoke, M molotov, F flash, H HE; size = rounds thrown")
            ]
        };
    }

    // A host that holds the Opening Tendencies but not the index: the first-grenade clock and place, no map marks.
    private static async Task<ScoutingLayerResult> FirstGrenadeOnly(ScoutingLayerContext context)
    {
        if (await LayerData.Openings(context).ConfigureAwait(false) is not { } block)
        {
            return await LayerData.OpeningsMissing(context).ConfigureAwait(false);
        }

        int rounds = block.UtilityRounds;
        if (rounds == 0)
        {
            return ScoutingLayerResult.Empty("no grenades indexed for these rounds; index grenades");
        }

        List<ScoutingRead> reads = [];
        TendencyBucket? clock = Top(block.FirstUtilityClock, "no utility");
        TendencyBucket? place = Top(block.FirstUtilityPlaces);
        if (clock is not null)
        {
            reads.Add(new ScoutingRead(place is null
                ? $"The first grenade most often goes at {clock.Label}: {CountText(clock.Count, rounds)}."
                : $"The first grenade most often goes at {clock.Label} ({CountText(clock.Count, rounds)}); the most common is {place.Label}."));
        }

        return new ScoutingLayerResult
        {
            CountLabel = $"{rounds} rds",
            Reads = reads,
            Sections =
            [
                new ScoutingInspectorSection($"First grenade, seconds after freeze end · {Plural(rounds, "round")}",
                    Rows(context, block.FirstUtilityClock, rounds, "first utility")),
                new ScoutingInspectorSection("First grenade, what and where",
                    Rows(context, block.FirstUtilityPlaces, rounds, "first utility"))
            ]
        };
    }

    private static ScoutingInspectorRow ClusterRow(ScoutingLayerContext context, UtilityCluster cluster, int of)
    {
        string text = $"most thrown {cluster.Noun} at {cluster.Place}";
        return new ScoutingInspectorRow
        {
            Label = $"{cluster.Noun} at {cluster.Place}",
            Value = CountText(cluster.Rounds.Count, of),
            StarText = text,
            Badge = cluster.Letter,
            BarPercent = of > 0 ? Math.Min(100, 100.0 * cluster.Rounds.Count / of) : null,
            Note = $"median {Clock(cluster.MedianSeconds)} after freeze end · {cluster.Throws} thrown",
            Rounds = cluster.Rounds,
            OpenTitle = $"{context.Prefix} · {text}"
        };
    }

    private static List<UtilityDemo> Demos(ScoutingLayerContext context, GrenadeIndex grenades,
        IRoundFacts roundFacts)
    {
        ScoutingReportSources sources = context.Data.Sources;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<UtilityDemo> demos = [];
        foreach ((DemoRef demo, int endSide, TeamAssignment assignment) in sources.Teams.SidesOf(context.TeamId))
        {
            if (!seen.Add(demo.Path)
                || sources.Library.Find(demo.Path) is not { MapName: { Length: > 0 } map } record
                || !string.Equals(map, context.Map, StringComparison.OrdinalIgnoreCase)
                || roundFacts.TryGet(demo.Path) is not { } rows)
            {
                continue;
            }

            int rate = rows.Clock?.TickRate ?? 0;
            string path = demo.Path;
            demos.Add(new UtilityDemo(path, record.Sha256, rate > 0 ? rate : 64,
                new HashSet<string>(assignment.Side(endSide).Key, StringComparer.Ordinal),
                grenades.Rows(new GrenadeQuery(map,
                    DemoPaths: new HashSet<string>([path], StringComparer.OrdinalIgnoreCase))),
                rows.Rounds, n => sources.Teams.SideAtRound(path, context.TeamId, n)));
        }

        return demos;
    }
}
