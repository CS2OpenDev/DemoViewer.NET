#region

using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Playback2D.Overlay;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Setups (CT): where each player stands in the setup window, per buy, and how the team rotates.
///     <list type="bullet">
///         <item>The marks: one dot per player per round at their mean setup position, and a ringed dot on each
///         player's most-held spot, in the player's colour from the players table (grey for anyone off the current
///         roster). <see cref="Isolated" /> fades every other player.</item>
///         <item>The inspector: one read per buy, the places each player held ("41 of 50"), the CTs per area
///         (A · mid · B) with their win %, and the rotations: how soon the first CT from elsewhere reaches the
///         site the bomb went down on after T contact, and how often before the plant. Every number opens its
///         rounds.</item>
///     </list>
///     The heatmap's picture is still what the export carries beside each buy's read.
/// </summary>
public sealed class SetupsLayer() : ScoutingLayerBase("setups", "Setups", ScoutingLayerPlacement.Map,
    ScoutingSides.Ct, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Player1), "E/D")
{
    /// <summary>A faded player's dots keep this share of their opacity while another is isolated.</summary>
    public const float IsolatedFade = 0.1f;

    /// <summary>The buys the filter offers, in the fact vocabulary.</summary>
    private static readonly Dictionary<ScoutingBuy, BuyType> _buys = new()
    {
        [ScoutingBuy.Eco] = BuyType.Eco,
        [ScoutingBuy.Semi] = BuyType.Semi,
        [ScoutingBuy.Force] = BuyType.Force,
        [ScoutingBuy.Full] = BuyType.Full
    };

    private volatile string? _isolated;

    /// <summary>
    ///     The player (SteamID64 as text) whose setup is shown alone, others fading; null shows everyone. Read at
    ///     build time: the players table sets it and rebuilds the layer. A key not in the scope fades nobody.
    /// </summary>
    public string? Isolated
    {
        get => _isolated;
        set => _isolated = value;
    }

    /// <inheritdoc />
    public override bool UsesBuy => true;

    /// <inheritdoc />
    public override string TitleFor(ScoutingSide side) => "Setups and rotations";

    /// <inheritdoc />
    public override bool OnByDefault(ScoutingSide side) => side == ScoutingSide.Ct;

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Data.Sources.Heatmaps is null)
        {
            return ScoutingLayerResult.Empty("setups are not built on this host");
        }

        SetupHeatmapSet? set = await context.Data.Heatmaps.ConfigureAwait(false);
        if (set is null)
        {
            return ScoutingLayerResult.Empty(context.Data.Problems.TryGetValue("heatmaps", out string? why)
                ? $"setups unavailable: {why}"
                : "setups unavailable");
        }

        List<SetupHeatmap> onMap =
            [.. set.Heatmaps.Where(h => string.Equals(h.Map, context.Map, StringComparison.OrdinalIgnoreCase))];
        List<SetupHeatmap> heatmaps =
            [.. onMap.Where(h => context.Buy == ScoutingBuy.All || h.Buy == _buys[context.Buy])];
        if (heatmaps.Count == 0)
        {
            return ScoutingLayerResult.Empty(set.Heatmaps.Count == 0
                ? ScoutingReportLines.HeatmapLineFor(set)
                : $"no {(context.Buy == ScoutingBuy.All ? "" : context.Buy.ToString().ToLowerInvariant() + " ")}CT rounds on {context.Map}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, ScoutingColor> colors = ColorsFor(context);
        SetupAreaClassifier areas = SetupAreaClassifier.Learn(onMap.SelectMany(h => h.Rounds),
            RegionsFor(context));
        List<SetupHeatmapRound> rounds = [.. heatmaps.SelectMany(h => h.Rounds)];
        IReadOnlyList<SetupPlayerPlace> places = SetupsDerivation.PlacesByPlayer(rounds);
        string scopeBuy = context.Buy == ScoutingBuy.All
            ? "all buys"
            : ScoutingReportLines.BuyLabelFor(_buys[context.Buy]);

        List<ScoutingRead> reads = [];
        List<ScoutingInspectorRow> samples = [];
        SetupHeatmap largest = heatmaps.MaxBy(h => h.SampledRounds)!;
        foreach (SetupHeatmap heatmap in heatmaps)
        {
            ScoutingBuy? buy = BuyOf(heatmap.Buy);
            string buyLabel = ScoutingReportLines.BuyLabelFor(heatmap.Buy);
            SetupHeatmap captured = heatmap;
            (string text, bool any) = ReadFor(heatmap, buyLabel);
            reads.Add(new ScoutingRead(text, Suggested: ReferenceEquals(heatmap, largest) && any, Buy: buy,
                Image: () => Render(context.Data.Sources, captured)));
            samples.Add(new ScoutingInspectorRow
            {
                Label = $"{buyLabel} rounds",
                Value = $"{Plural(heatmap.Rounds.Count, "round")} · {heatmap.SampledRounds} sampled",
                StarText = "rounds",
                Buy = buy,
                Rounds = Open(heatmap.Rounds),
                OpenTitle = $"{heatmap.Map} · {buyLabel}"
            });
        }

        List<ScoutingInspectorSection> sections = [new("", samples)];
        sections.Add(new ScoutingInspectorSection($"Places held in the setup window · {scopeBuy}",
            places.Count > 0
                ? [.. places.Select(p => PlaceRow(context, p, colors))]
                : [.. FixedPlaceRows(heatmaps)]));

        IReadOnlyList<SetupFormation> formations = SetupsDerivation.Formations(rounds, areas);
        if (formations.Count > 0)
        {
            int of = formations.Sum(f => f.Rounds.Count);
            sections.Add(new ScoutingInspectorSection("CTs per area · A · mid · B",
                [.. formations.Take(6).Select(f => FormationRow(context, f, of, areas.HasSites || areas.HasRegions))]));
        }

        IReadOnlyList<SetupRotation> rotations = SetupsDerivation.Rotations(rounds, areas);
        (List<ScoutingInspectorRow> rotationRows, string? rotationRead) = RotationRows(context, rotations);
        rotationRows.Add(await FakeRow(context, set, rounds, areas, cancellationToken).ConfigureAwait(false));
        sections.Add(new ScoutingInspectorSection("Rotations", rotationRows));
        if (rotationRead is not null)
        {
            reads.Add(new ScoutingRead(rotationRead, "rotations"));
        }

        return new ScoutingLayerResult
        {
            CountLabel = $"{rounds.Count} rds",
            Marks = Marks(rounds, places, colors, Isolated),
            Reads = reads,
            Sections = sections,
            Legend =
            [
                new ScoutingLegendEntry(Swatch, "setups: one colour per player (see Players), a dot per round"),
                new ScoutingLegendEntry(new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.Neutral),
                    "ringed dot = the player's most-held spot")
            ]
        };
    }

    /// <summary>
    ///     The marks: a dot per player per round at their mean setup position, then a ringed dot on each player's
    ///     main spot on top. With a player isolated (and in the rounds), everyone else fades to
    ///     <see cref="IsolatedFade" />.
    /// </summary>
    /// <param name="rounds">The rounds in scope.</param>
    /// <param name="places">Their places by player.</param>
    /// <param name="colors">The roster's colours by key.</param>
    /// <param name="isolated">The isolated player's key, or null.</param>
    public static IReadOnlyList<ScoutingMapMark> Marks(IEnumerable<SetupHeatmapRound> rounds,
        IReadOnlyList<SetupPlayerPlace> places, IReadOnlyDictionary<string, ScoutingColor> colors, string? isolated)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(colors);
        if (isolated is not null && places.All(p => p.Key != isolated))
        {
            isolated = null;
        }

        float Fade(string key) => isolated is null || key == isolated ? 1f : IsolatedFade;
        ScoutingColor ColorOf(string key) => colors.TryGetValue(key, out ScoutingColor c) ? c : ScoutingColor.Neutral;

        List<ScoutingMapMark> marks = [];
        List<ScoutingMapMark> front = [];
        foreach (SetupRoundPlayer player in rounds.Select(r => r.Detail).OfType<SetupRoundDetail>()
                     .SelectMany(d => d.Players).Where(p => p.Setup.Count > 0))
        {
            ScoutingMapMark dot = new((float)player.Setup.Average(s => s.X), (float)player.Setup.Average(s => s.Y),
                (float)player.Setup.Average(s => s.Z), ScoutingMarkShape.Dot, ColorOf(player.Key), 4.5f,
                Opacity: 0.8f * Fade(player.Key));
            (player.Key == isolated ? front : marks).Add(dot);
        }

        marks.AddRange(front);
        foreach (SetupPlayerPlace place in places.Where(p => p.IsMain).OrderBy(p => p.Key == isolated))
        {
            float fade = Fade(place.Key);
            marks.Add(new ScoutingMapMark(place.X, place.Y, place.Z, ScoutingMarkShape.Dot, ScoutingColor.Neutral,
                10f, Opacity: 0.95f * fade));
            marks.Add(new ScoutingMapMark(place.X, place.Y, place.Z, ScoutingMarkShape.Dot, ColorOf(place.Key), 8f,
                Opacity: fade));
        }

        return marks;
    }

    // The map's site regions from Suggested Tags, or null: the areas fall back to distance from the site centres.
    private static SetupSiteRegions? RegionsFor(ScoutingLayerContext context)
    {
        try
        {
            return context.Data.Sources.SiteRegions?.Invoke(context.Map);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    // The roster's colours by SteamID64; anyone else draws grey.
    private static IReadOnlyDictionary<string, ScoutingColor> ColorsFor(ScoutingLayerContext context) =>
        ScoutingPlayerColors.Lookup(context.Data.Sources.Teams, context.TeamId);

    // "Full buy CT: Alice holds B site in 47 of 50 rounds; Bob holds Window in 41 of 50."
    private static (string Text, bool Any) ReadFor(SetupHeatmap heatmap, string buyLabel)
    {
        string lead = $"{Capital(buyLabel)} CT";
        List<SetupPlayerPlace> top =
            [.. SetupsDerivation.PlacesByPlayer(heatmap.Rounds).Where(p => p.IsMain).OrderByDescending(p => p.Rounds.Count).Take(2)];
        if (top.Count > 0)
        {
            string first =
                $"{DisplayText.Sanitize(top[0].Name)} holds {SetupsDerivation.PlaceLabel(top[0].Place)} in {top[0].Rounds.Count} of {Plural(top[0].Of, "round")}";
            return (top.Count == 1
                ? $"{lead}: {first}."
                : $"{lead}: {first}; {DisplayText.Sanitize(top[1].Name)} holds {SetupsDerivation.PlaceLabel(top[1].Place)} in {top[1].Rounds.Count} of {top[1].Of}.", true);
        }

        string[] fixedPlaces = [.. heatmap.Positions.Where(p => p.IsFixed).Select(p => p.Place)];
        return fixedPlaces.Length == 0
            ? ($"{lead}: no place is held in most of {Plural(heatmap.SampledRounds, "sampled round")}.", false)
            : ($"{lead}: {string.Join(", ", fixedPlaces)} held in the setup window over {Plural(heatmap.SampledRounds, "sampled round")}.", true);
    }

    private static ScoutingInspectorRow PlaceRow(ScoutingLayerContext context, SetupPlayerPlace place,
        IReadOnlyDictionary<string, ScoutingColor> colors)
    {
        string name = DisplayText.Sanitize(place.Name);
        string label = SetupsDerivation.PlaceLabel(place.Place);
        return new ScoutingInspectorRow
        {
            Label = $"{label} · {name}",
            Value = CountText(place.Rounds.Count, place.Of),
            StarText = $"{name} at {place.Place}",
            Note = place.IsFixed ? "fixed" : place.IsMain ? "rotating" : "rotating, second spot",
            BarPercent = place.Of > 0 ? 100.0 * place.Rounds.Count / place.Of : null,
            Swatch = colors.TryGetValue(place.Key, out ScoutingColor c) ? c : ScoutingColor.Neutral,
            Rounds = Open(place.Rounds),
            OpenTitle = $"{context.Prefix} · {name} at {label}"
        };
    }

    // No positions named a player: the heatmap's own fixed and rotating places, as before.
    private static IEnumerable<ScoutingInspectorRow> FixedPlaceRows(IEnumerable<SetupHeatmap> heatmaps) =>
        heatmaps.SelectMany(h => h.Positions.Select(p => new ScoutingInspectorRow
        {
            Label = p.Place,
            Value = $"held {p.RoundsHeld} of {p.SampledRounds}",
            StarText = $"place {p.Place}",
            Buy = BuyOf(h.Buy),
            Note = p.IsFixed ? "fixed" : "rotating",
            BarPercent = p.SampledRounds > 0 ? 100.0 * p.RoundsHeld / p.SampledRounds : null,
            Swatch = ScoutingColor.Ct
        }));

    private static ScoutingInspectorRow FormationRow(ScoutingLayerContext context, SetupFormation formation, int of,
        bool hasSites) => new()
    {
        Label = formation.Label,
        Value = CountText(formation.Rounds.Count, of),
        StarText = $"formation {formation.Label}",
        Note = $"won {Percent((double)formation.Won / formation.Rounds.Count)} ({formation.Won} of {formation.Rounds.Count})"
               + (hasSites ? "" : " · only the bombsite places count as A and B on this map"),
        BarPercent = of > 0 ? 100.0 * formation.Rounds.Count / of : null,
        Swatch = ScoutingColor.Ct,
        Rounds = Open(formation.Rounds),
        OpenTitle = $"{context.Prefix} · {formation.Label}"
    };

    private static (List<ScoutingInspectorRow> Rows, string? Read) RotationRows(ScoutingLayerContext context,
        IReadOnlyList<SetupRotation> rotations)
    {
        List<ScoutingInspectorRow> rows = [];
        string? read = null;
        List<SetupRotation> arrived = [.. rotations.Where(r => r.ArrivalSeconds is not null)];
        if (rotations.Count == 0)
        {
            rows.Add(new ScoutingInspectorRow
            {
                Label = "First rotator reaches the hit site",
                Note = "no planted round with T contact and a CT set up off the site"
            });
        }
        else
        {
            double? median = SetupsDerivation.Median(arrived.Select(r => r.ArrivalSeconds!.Value));
            rows.Add(new ScoutingInspectorRow
            {
                Label = "First rotator reaches the hit site",
                Value = median is { } m ? $"median {Seconds(m)}" : "never reached",
                StarText = "first rotator",
                Note = $"after T contact · reached in {arrived.Count} of {Plural(rotations.Count, "planted round")}",
                Rounds = Open(arrived.Select(r => r.Round)),
                OpenTitle = $"{context.Prefix} · first rotator reaches the hit site"
            });
            foreach (BombSite site in (BombSite[])[BombSite.A, BombSite.B])
            {
                List<SetupRotation> here = [.. arrived.Where(r => r.Site == site)];
                if (SetupsDerivation.Median(here.Select(r => r.ArrivalSeconds!.Value)) is { } siteMedian)
                {
                    rows.Add(new ScoutingInspectorRow
                    {
                        Label = $"{site} hits",
                        Value = $"median {Seconds(siteMedian)}",
                        StarText = $"first rotator {site}",
                        Note = $"{Plural(here.Count, "round")}",
                        Rounds = Open(here.Select(r => r.Round)),
                        OpenTitle = $"{context.Prefix} · first rotator on {site}"
                    });
                }
            }

            List<SetupRotation> before = [.. rotations.Where(r => r.BeforePlant)];
            rows.Add(new ScoutingInspectorRow
            {
                Label = "Rotated before the plant",
                Value = CountText(before.Count, rotations.Count),
                StarText = "rotated before the plant",
                Note = Percent((double)before.Count / rotations.Count),
                BarPercent = 100.0 * before.Count / rotations.Count,
                Swatch = ScoutingColor.Ct,
                Rounds = Open(before.Select(r => r.Round)),
                OpenTitle = $"{context.Prefix} · rotated before the plant"
            });
            if (median is { } readMedian)
            {
                read =
                    $"The first rotator reaches the hit site a median {Seconds(readMedian)} after T contact; on the site before the plant in {before.Count} of {Plural(rotations.Count, "planted round")}.";
            }
        }

        return (rows, read);
    }

    private const string FakeLabel = "Rotated off a site on a fake";

    // "Rotated off a site on a fake": the fake rounds Suggested Tags proposed, read once per team load off the UI
    // thread, and how often a CT set up on the real site left it before the plant.
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

        FakeIndex index = await _fakes.GetValue(context.Data, d => ScoutingLayerContext.Offload(() => FakeIndex.Load(d, set)))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FakeRotation> fakes = SetupsDerivation.FakeRotations(rounds, areas,
            r => index.Rounds.GetValueOrDefault((r.DemoPath, r.RoundNumber)));
        string coverage = $"fakes from Suggested Tags, current for {index.Read} of {Plural(index.Demos, "demo")}";
        if (fakes.Count == 0)
        {
            return new ScoutingInspectorRow
            {
                Label = FakeLabel,
                StarText = "rotated off a site on a fake",
                Note = index.Read == 0
                    ? "no current Suggested Tags suggestions for these demos; run Suggested Tags to find the fakes"
                    : $"no fake round planted on the real site with a CT set up there · {coverage}"
            };
        }

        List<FakeRotation> off = [.. fakes.Where(f => f.RotatedOff)];
        return new ScoutingInspectorRow
        {
            Label = FakeLabel,
            Value = CountText(off.Count, fakes.Count),
            StarText = "rotated off a site on a fake",
            Note = $"a CT set up on the real site left it before the plant · {coverage}",
            BarPercent = 100.0 * off.Count / fakes.Count,
            Swatch = ScoutingColor.Ct,
            Rounds = Open(off.Select(f => f.Round)),
            OpenTitle = $"{context.Prefix} · rotated off a site on a fake"
        };
    }

    // One read of the team's fake rounds per load: every CT demo of the heatmaps, once.
    private static readonly ConditionalWeakTable<ScoutingReportData, Task<FakeIndex>> _fakes = [];

    private sealed record FakeIndex(IReadOnlyDictionary<(string Path, int Round), FakeRound> Rounds, int Read, int Demos)
    {
        public static FakeIndex Load(ScoutingReportData data, SetupHeatmapSet set)
        {
            Dictionary<(string, int), FakeRound> rounds = [];
            int read = 0;
            List<(string Path, string Map)> demos =
            [
                .. set.Heatmaps.SelectMany(h => h.Rounds.Select(r => (r.DemoPath, h.Map)))
                    .Distinct()
            ];
            foreach ((string path, string map) in demos)
            {
                IReadOnlyList<FakeRound>? fakes;
                try
                {
                    fakes = data.Sources.Fakes!(path, map);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or InvalidOperationException)
                {
                    fakes = null;
                }

                if (fakes is null)
                {
                    continue;
                }

                read++;
                foreach (FakeRound fake in fakes)
                {
                    rounds.TryAdd((path, fake.Round), fake);
                }
            }

            return new FakeIndex(rounds, read, demos.Count);
        }
    }

    private static string Seconds(double seconds) =>
        $"{Math.Round(seconds, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture)}s";

    private static IReadOnlyList<TendencyRound> Open(IEnumerable<SetupHeatmapRound> rounds) =>
    [
        .. rounds.Select(r => new TendencyRound(r.DemoPath, r.Sha256, r.RoundNumber, r.FreezeEndTick, r.ClipEndTick,
            r.TickRate))
    ];

    private static ScoutingBuy? BuyOf(BuyType buy) => buy switch
    {
        BuyType.Eco => ScoutingBuy.Eco,
        BuyType.Semi => ScoutingBuy.Semi,
        BuyType.Force => ScoutingBuy.Force,
        BuyType.Full => ScoutingBuy.Full,
        _ => null
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // The heatmap's picture for the export: drawn on demand, never for the screen (the stage draws the marks).
    private static byte[]? Render(ScoutingReportSources sources, SetupHeatmap heatmap)
    {
        if (heatmap.Points.Count == 0)
        {
            return null;
        }

        OverlayDocument overlay = new();
        overlay.Replace(heatmap.Map, heatmap.Points, heatmap.StateCount);
        using Modules.ScoutingReport.SetupHeatmapRenderer renderer = sources.Renderer();
        return renderer.Render(overlay);
    }
}
