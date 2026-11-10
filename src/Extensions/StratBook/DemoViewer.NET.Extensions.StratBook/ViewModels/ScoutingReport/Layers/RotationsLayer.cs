#region

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Rotations (strip, CT): how long after T contact the first CT from elsewhere reaches the hit site, per site
///     as a strip plot with the median, who rotates first, how often a rotator is there before the plant, and how
///     often a fake pulls a defender off the real site. The same derivation the Setups layer's rotations section
///     reads (<see cref="SetupsDerivation.Rotations" /> over the Setup Heatmaps' rounds), at 1 s resolution. The
///     buy filter applies. Every number opens its rounds.
/// </summary>
public sealed class RotationsLayer() : ScoutingLayerBase("rotations", "Rotations", ScoutingLayerPlacement.Strip,
    ScoutingSides.Ct, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Ct), "D")
{
    /// <summary>The strip plot's axis end, in seconds after contact.</summary>
    public const double AxisSeconds = 30;

    // One read of the team's fake rounds per map per load.
    private static readonly ConditionalWeakTable<ScoutingReportData, ConcurrentDictionary<string, Task<Fakes>>>
        _fakes = [];

    /// <inheritdoc />
    public override bool UsesBuy => true;

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Data.Sources.Heatmaps is null)
        {
            return ScoutingLayerResult.Empty(ScoutingLayerResult.NotOnThisHost);
        }

        SetupHeatmapSet? set = await context.Data.Heatmaps.ConfigureAwait(false);
        if (set is null)
        {
            return ScoutingLayerResult.Unreadable("setups",
                context.Data.Problems.TryGetValue("heatmaps", out string? why) ? why : null);
        }

        BuyType? buy = StripData.BuyOf(context.Buy);
        List<SetupHeatmapRound> rounds =
        [
            .. set.Heatmaps.Where(h => string.Equals(h.Map, context.Map, StringComparison.OrdinalIgnoreCase)
                                       && (buy is null || h.Buy == buy))
                .SelectMany(h => h.Rounds)
        ];
        if (rounds.Count == 0)
        {
            return ScoutingLayerResult.Empty($"no {StripData.BuyWord(context.Buy)}CT rounds on {context.Map}");
        }

        SetupAreaClassifier areas = await StripData.AreasFor(context.Data, context.Map)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SetupRotation> rotations = SetupsDerivation.Rotations(rounds, areas);
        if (rotations.Count == 0)
        {
            return ScoutingLayerResult.Empty(rounds.Any(r => r.Detail is not null)
                ? $"no planted {StripData.BuyWord(context.Buy)}round on {context.Map} with T contact and a CT set up off the site"
                : "needs round positions, which these demos do not have yet");
        }

        List<SetupRotation> arrived = [.. rotations.Where(r => r.ArrivalSeconds is not null)];
        List<ScoutingInspectorRow> rows = [];
        foreach (BombSite site in (BombSite[])[BombSite.A, BombSite.B])
        {
            List<SetupRotation> hits = [.. rotations.Where(r => r.Site == site)];
            List<SetupRotation> here = [.. arrived.Where(r => r.Site == site)];
            if (hits.Count == 0)
            {
                continue;
            }

            rows.Add(new ScoutingInspectorRow
            {
                Label = $"{site} site",
                Value = TeamRounds.Median(here.Select(r => r.ArrivalSeconds!.Value)) is { } m
                    ? $"median {Seconds(m)}"
                    : "never reached",
                StarText = $"rotator {site}",
                Note = $"reached in {CountText(here.Count, hits.Count)}",
                Rounds = Open(here.Select(r => r.Round)),
                Swatch = ScoutingColor.Ct,
                OpenTitle = $"{context.Prefix} · first rotator on {site}"
            });
        }

        List<SetupRotation> before = [.. rotations.Where(r => r.BeforePlant)];
        rows.Add(StripData.Share("Rotated before the plant", Open(before.Select(r => r.Round)), rotations.Count,
            "rotated before the plant", $"{context.Prefix} · rotated before the plant", ScoutingColor.Ct));

        if (Fastest(arrived) is { } fastest)
        {
            rows.Add(new ScoutingInspectorRow
            {
                Label = "Rotates first most often",
                Value = $"{fastest.Name} · median {Seconds(fastest.Median)}",
                StarText = "first rotator",
                Note = $"first on the site in {CountText(fastest.Rounds.Count, arrived.Count)}",
                Rounds = Open(fastest.Rounds.Select(r => r.Round)),
                OpenTitle = $"{context.Prefix} · {fastest.Name} rotates first"
            });
        }

        rows.Add(await FakeRow(context, set, rounds, areas, cancellationToken).ConfigureAwait(false));

        double? median = TeamRounds.Median(arrived.Select(r => r.ArrivalSeconds!.Value));
        List<ScoutingRead> reads = median is { } overall
            ? [new ScoutingRead($"The first rotator reaches the hit site a median {Seconds(overall)} after T contact.", Suggested: true)]
            : [];
        return new ScoutingLayerResult
        {
            CountLabel = median is { } c ? $"median {Seconds(c)}" : Plural(rotations.Count, "round"),
            Reads = reads,
            Sections = [new ScoutingInspectorSection("First rotator, seconds after T contact", rows)],
            MiniChart = Chart(rotations)
        };
    }

    /// <summary>The mini: one lane per site, 0 s to 30 s after contact, a dot per arrival, the median line.</summary>
    /// <param name="rotations">The rotations.</param>
    public static ScoutingStripChart? Chart(IReadOnlyList<SetupRotation> rotations)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        List<ScoutingStripLane> lanes = [];
        foreach (BombSite site in (BombSite[])[BombSite.A, BombSite.B])
        {
            List<double> here =
            [
                .. rotations.Where(r => r.Site == site && r.ArrivalSeconds is not null)
                    .Select(r => r.ArrivalSeconds!.Value)
            ];
            double? median = TeamRounds.Median(here);
            lanes.Add(new ScoutingStripLane(site.ToString(), [.. here.Select(s => new ScoutingStripDot(s, true))],
                median, AxisSeconds, [], ScoutingColor.Ct)
            {
                Text = median is { } m ? $"{site}: {Plural(here.Count, "rotation")}, median {Seconds(m)}" : $"{site}: none"
            });
        }

        return lanes.All(l => l.Dots.Count == 0)
            ? null
            : new ScoutingStripChart(lanes) { Caption = "first rotator at the hit site, 0s → 30s after contact · line = median" };
    }

    /// <summary>The player who reached the hit site first most often, with their median; null with no arrival.</summary>
    /// <param name="arrived">The rotations with an arrival.</param>
    public static (string Name, double Median, IReadOnlyList<SetupRotation> Rounds)? Fastest(
        IReadOnlyList<SetupRotation> arrived)
    {
        ArgumentNullException.ThrowIfNull(arrived);
        IGrouping<string, SetupRotation>? top = arrived.Where(r => r.Rotator is not null)
            .GroupBy(r => r.Rotator!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return top is null
            ? null
            : (top.Key, TeamRounds.Median(top.Select(r => r.ArrivalSeconds!.Value))!.Value, [.. top]);
    }

    private const string FakeLabel = "Rotated off a site on a fake";

    private static async Task<ScoutingInspectorRow> FakeRow(ScoutingLayerContext context, SetupHeatmapSet set,
        IReadOnlyList<SetupHeatmapRound> rounds, SetupAreaClassifier areas, CancellationToken cancellationToken)
    {
        if (context.Data.Sources.Fakes is null)
        {
            return new ScoutingInspectorRow
            {
                Label = FakeLabel,
                IsPlaceholder = true,
                Note = "needs Suggested Tags' fake rounds, which this host does not build"
            };
        }

        Fakes fakes = await _fakes.GetValue(context.Data, _ => new ConcurrentDictionary<string, Task<Fakes>>(
                StringComparer.OrdinalIgnoreCase))
            .GetOrAdd(context.Map, map => ScoutingLayerContext.Offload(() => Fakes.Load(context.Data, set, map)))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FakeRotation> on = SetupsDerivation.FakeRotations(rounds, areas,
            r => fakes.Rounds.GetValueOrDefault((r.DemoPath, r.RoundNumber)));
        if (on.Count == 0)
        {
            return new ScoutingInspectorRow
            {
                Label = FakeLabel,
                StarText = "rotated off a site on a fake",
                Note = fakes.Read == 0
                    ? "no current Suggested Tags suggestions for these demos; run Suggested Tags to find the fakes"
                    : "no fake round planted on the real site with a CT set up there"
            };
        }

        List<FakeRotation> off = [.. on.Where(f => f.RotatedOff)];
        return StripData.Share(FakeLabel, Open(off.Select(f => f.Round)), on.Count, "rotated off a site on a fake",
            $"{context.Prefix} · rotated off a site on a fake", ScoutingColor.Ct,
            $"fakes from Suggested Tags, current for {fakes.Read} of {Plural(fakes.Demos, "demo")}");
    }

    private static string Seconds(double seconds) =>
        $"{Math.Round(seconds, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}s";

    private static IReadOnlyList<TendencyRound> Open(IEnumerable<SetupHeatmapRound> rounds) =>
    [
        .. rounds.Select(r => new TendencyRound(r.DemoPath, r.Sha256, r.RoundNumber, r.FreezeEndTick, r.ClipEndTick,
            r.TickRate))
    ];

    private sealed record Fakes(IReadOnlyDictionary<(string Path, int Round), FakeRound> Rounds, int Read, int Demos)
    {
        public static Fakes Load(ScoutingReportData data, SetupHeatmapSet set, string map)
        {
            Dictionary<(string, int), FakeRound> rounds = [];
            int read = 0;
            List<string> demos =
            [
                .. set.Heatmaps.Where(h => string.Equals(h.Map, map, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(h => h.Rounds.Select(r => r.DemoPath)).Distinct(StringComparer.Ordinal)
            ];
            foreach (string path in demos)
            {
                IReadOnlyList<FakeRound>? found;
                try
                {
                    found = data.Sources.Fakes?.Invoke(path, map);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or InvalidOperationException)
                {
                    found = null;
                }

                if (found is null)
                {
                    continue;
                }

                read++;
                foreach (FakeRound fake in found)
                {
                    rounds[(path, fake.Round)] = fake;
                }
            }

            return new Fakes(rounds, read, demos.Count);
        }
    }
}
