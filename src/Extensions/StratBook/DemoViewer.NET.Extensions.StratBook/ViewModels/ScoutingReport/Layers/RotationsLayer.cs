#region

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.Sdk.Ui;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Rotations (map, CT): every CT rotation onto the hit site, drawn from the rotating player's own samples in the
///     round positions file, one faint trail per player per round in their Players colour, from a ring where they
///     stood at T contact to a dot where they first stood on the hit site. Nothing is averaged, smoothed or shared
///     between rounds; the trail is the file's one-second samples, joined by straight dots between consecutive ones.
///     <para>
///         Only planted rounds are read, the plant site being the hit site. A movement is a rotation under
///         <see cref="RotationRule" />: the CT was off the hit site in the setup window, just before contact and at
///         contact; their track from contact has no gap over 1.5 s and no step faster than a player can run; they
///         left their contact spot before the plant (one sample of slack), so a walk over after the plant is a retake
///         and not counted; and their first sample on the site (by the map's areas) is at least 400 units from where
///         they started. The trail ends there, so leaving the site and coming back never shows.
///     </para>
///     The inspector reads the first rotator of each round: per site the median arrival after contact and how
///     often it beat the plant, who rotates first by SteamID under the roster's name, and how often a fake pulls a
///     defender off the real site. The Setups layer's rotations section reads the same derivation
///     (<see cref="SetupsDerivation.Rotations" /> over the Setup Heatmaps' rounds). The buy filter applies. Every
///     number opens its rounds.
/// </summary>
public sealed class RotationsLayer() : ScoutingLayerBase("rotations", "Rotations", ScoutingLayerPlacement.Map,
    ScoutingSides.Ct, new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.Ct), "D")
{
    /// <summary>The start ring's radius in screen pixels.</summary>
    public const float StartRingSize = 4.5f;

    /// <summary>The arrival dot's radius in screen pixels.</summary>
    public const float ArrivalSize = 4f;

    /// <summary>A sample dot's radius in screen pixels.</summary>
    public const float SampleSize = 2.2f;

    /// <summary>The radius of the dots that join two consecutive samples.</summary>
    public const float JoinSize = 1.3f;

    /// <summary>A sample dot's opacity: faint, so the routes many rounds share read darker.</summary>
    public const float TrailOpacity = 0.5f;

    // Dots drawn on the straight line between two consecutive samples, so a trail reads as a line.
    private const int JoinDots = 3;

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
                ? $"no planted {StripData.BuyWord(context.Buy)}round on {context.Map} with T contact and a CT off the site"
                : "needs round positions, which these demos do not have yet");
        }

        ScoutingInspectorRow fake = await FakeRow(context, set, rounds, areas, cancellationToken).ConfigureAwait(false);
        return Compose(context.Prefix, rotations,
            ScoutingPlayerColors.Lookup(context.Data.Sources.Teams, context.TeamId),
            ScoutingPlayerColors.Names(context.Data.Sources.Teams, context.TeamId), fake);
    }

    /// <summary>The layer for one scope's rotations: the read, the per-site and first-rotator rows, and the marks.</summary>
    /// <param name="prefix">"de_mirage CT".</param>
    /// <param name="rotations">The scope's rotations (<see cref="SetupsDerivation.Rotations" />); at least one.</param>
    /// <param name="colors">The roster's colours by SteamID64 and by name.</param>
    /// <param name="names">The roster's names by SteamID64 (<see cref="ScoutingPlayerColors.Names" />).</param>
    /// <param name="fake">The fake row, appended last; null leaves it out.</param>
    public static ScoutingLayerResult Compose(string prefix, IReadOnlyList<SetupRotation> rotations,
        IReadOnlyDictionary<string, ScoutingColor> colors, IReadOnlyDictionary<string, string> names,
        ScoutingInspectorRow? fake = null)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentNullException.ThrowIfNull(names);
        List<SetupRotation> arrived = [.. rotations.Where(r => r.ArrivalSeconds is not null)];
        List<SetupRotation> before = [.. rotations.Where(r => r.BeforePlant)];

        List<ScoutingInspectorRow> sites = [];
        foreach (BombSite site in (BombSite[])[BombSite.A, BombSite.B])
        {
            List<SetupRotation> hits = [.. rotations.Where(r => r.Site == site)];
            if (hits.Count == 0)
            {
                continue;
            }

            List<SetupRotation> here = [.. arrived.Where(r => r.Site == site)];
            int early = here.Count(r => r.BeforePlant);
            sites.Add(new ScoutingInspectorRow
            {
                Label = $"{site} site",
                Value = TeamRounds.Median(here.Select(r => r.ArrivalSeconds!.Value)) is { } m
                    ? $"median {Seconds(m)}"
                    : "no rotation",
                StarText = $"rotator {site}",
                Note = $"a rotator reached it in {CountText(here.Count, hits.Count)} planted there; before the plant in {early}",
                BarPercent = 100.0 * here.Count / hits.Count,
                Rounds = Open(here.Select(r => r.Round)),
                Swatch = ScoutingColor.Ct,
                OpenTitle = $"{prefix} · first rotator on {site}"
            });
        }

        sites.Add(StripData.Share("Rotated before the plant", Open(before.Select(r => r.Round)), rotations.Count,
            "rotated before the plant", $"{prefix} · rotated before the plant", ScoutingColor.Ct));

        List<ScoutingInspectorRow> first =
        [
            .. FirstRotators(arrived, names).Select(g => new ScoutingInspectorRow
            {
                Label = g.Name,
                Value = CountText(g.Rounds.Count, arrived.Count),
                StarText = $"first rotator {g.Name}",
                Note = $"median {Seconds(g.Median)}; before the plant in {g.Rounds.Count(r => r.BeforePlant)}",
                BarPercent = 100.0 * g.Rounds.Count / arrived.Count,
                Swatch = ColorOf(g.Key, g.Rounds[^1].Rotator ?? "", colors),
                Rounds = Open(g.Rounds.Select(r => r.Round)),
                OpenTitle = $"{prefix} · {g.Name} rotates first"
            })
        ];

        List<ScoutingInspectorSection> sections =
        [
            new("First rotator onto the hit site, seconds after T contact", sites),
            new("Who rotates first", first)
        ];
        if (fake is not null)
        {
            sections.Add(new ScoutingInspectorSection("", [fake]));
        }

        double? median = TeamRounds.Median(arrived.Select(r => r.ArrivalSeconds!.Value));
        string read = median is { } overall
            ? $"The first rotator reaches the hit site a median {Seconds(overall)} after T contact; before the plant in {before.Count} of {Plural(rotations.Count, "planted round")}."
            : $"No CT rotated onto the hit site after T contact in {Plural(rotations.Count, "planted round")}.";
        int trails = rotations.Sum(r => r.Trails.Count);
        return new ScoutingLayerResult
        {
            CountLabel = Plural(trails, "rotation"),
            Reads = [new ScoutingRead(read, Suggested: median is not null)],
            Marks = Marks(rotations, colors),
            Legend =
            [
                new ScoutingLegendEntry(new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.Neutral),
                    "ring = where a CT stood at T contact"),
                new ScoutingLegendEntry(new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral),
                    "faint trail = their sampled path that round, in their Players colour; dot = first step on the hit site")
            ],
            Sections = [.. sections.Where(s => s.Rows.Count > 0)]
        };
    }

    /// <summary>
    ///     The marks: per rotation, a dot at each of the player's samples from contact to the arrival with
    ///     <see cref="JoinDots" /> smaller dots on the straight line to the next, all faint; then a ring at every
    ///     start and a dot at every arrival on top, in the player's colour (grey off the roster).
    /// </summary>
    /// <param name="rotations">The rotations.</param>
    /// <param name="colors">The roster's colours by SteamID64 and by name.</param>
    public static IReadOnlyList<ScoutingMapMark> Marks(IReadOnlyList<SetupRotation> rotations,
        IReadOnlyDictionary<string, ScoutingColor> colors)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        ArgumentNullException.ThrowIfNull(colors);
        List<RotationTrail> trails = [.. rotations.SelectMany(r => r.Trails)];
        List<ScoutingMapMark> marks = [];
        foreach (RotationTrail trail in trails)
        {
            ScoutingColor color = ColorOf(trail.Key, trail.Name, colors);
            IReadOnlyList<SetupTimedSample> samples = trail.Samples;
            for (int i = 0; i < samples.Count; i++)
            {
                SetupSample s = samples[i].Sample;
                marks.Add(new ScoutingMapMark(s.X, s.Y, s.Z, ScoutingMarkShape.Dot, color, SampleSize,
                    Opacity: TrailOpacity));
                if (i + 1 == samples.Count)
                {
                    continue;
                }

                SetupSample next = samples[i + 1].Sample;
                for (int k = 1; k <= JoinDots; k++)
                {
                    float f = k / (JoinDots + 1f);
                    marks.Add(new ScoutingMapMark(s.X + (next.X - s.X) * f, s.Y + (next.Y - s.Y) * f,
                        s.Z + (next.Z - s.Z) * f, ScoutingMarkShape.Dot, color, JoinSize,
                        Opacity: TrailOpacity * 0.7f));
                }
            }
        }

        foreach (RotationTrail trail in trails)
        {
            SetupSample start = trail.Start.Sample;
            marks.Add(new ScoutingMapMark(start.X, start.Y, start.Z, ScoutingMarkShape.Ring,
                ColorOf(trail.Key, trail.Name, colors), StartRingSize, Opacity: 0.85f));
        }

        foreach (RotationTrail trail in trails)
        {
            SetupSample end = trail.Arrival.Sample;
            marks.Add(new ScoutingMapMark(end.X, end.Y, end.Z, ScoutingMarkShape.Dot,
                ColorOf(trail.Key, trail.Name, colors), ArrivalSize, Opacity: 0.95f));
        }

        return marks;
    }

    /// <summary>
    ///     The players who were first onto the hit site, one per player key (SteamID) under the roster's name, most
    ///     rounds first, with the median of their arrivals.
    /// </summary>
    /// <param name="arrived">The rotations with an arrival.</param>
    /// <param name="names">The roster's names by SteamID64.</param>
    public static IReadOnlyList<(string Key, string Name, double Median, IReadOnlyList<SetupRotation> Rounds)>
        FirstRotators(IReadOnlyList<SetupRotation> arrived, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(arrived);
        ArgumentNullException.ThrowIfNull(names);
        return
        [
            .. arrived.Where(r => r.ArrivalSeconds is not null && (r.RotatorKey ?? r.Rotator) is not null)
                .GroupBy(r => r.RotatorKey ?? "name:" + r.Rotator, StringComparer.Ordinal)
                .Select(g => (Key: g.Key, Name: ScoutingPlayerColors.NameOf(names, g.Key, g.Last().Rotator ?? ""),
                    Median: TeamRounds.Median(g.Select(r => r.ArrivalSeconds!.Value))!.Value,
                    Rounds: (IReadOnlyList<SetupRotation>)[.. g]))
                .OrderByDescending(g => g.Rounds.Count).ThenBy(g => g.Median).ThenBy(g => g.Name, StringComparer.Ordinal)
        ];
    }

    private static ScoutingColor ColorOf(string key, string name, IReadOnlyDictionary<string, ScoutingColor> colors) =>
        colors.TryGetValue(key, out ScoutingColor byKey)
            ? byKey
            : colors.TryGetValue(DisplayText.Sanitize(name), out ScoutingColor byName)
                ? byName
                : ScoutingColor.Neutral;

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
