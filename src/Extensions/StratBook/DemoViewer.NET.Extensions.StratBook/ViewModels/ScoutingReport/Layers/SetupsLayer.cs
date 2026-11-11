#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Playback2D.Overlay;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Setups (CT): where each player stands in the setup window and how the team spreads over the sites.
///     <list type="bullet">
///         <item>The marks: one dot per player per round at their held spot, and a ringed dot on each player's
///         most-held spot, in the player's colour from the players table (grey for anyone off the current roster).
///         <see cref="Isolated" /> fades every other player.</item>
///         <item>The read: the buy in view's, or with every buy the most sampled buy's, saying so.</item>
///         <item>The inspector, in three tabs. Players: the places each player held ("41 of 50"). Shape: the CTs per
///         area (A · mid · B) with their win %. Both follow the buy filter. By buy: one row per buy with its rounds
///         and its top two held places, whatever the filter. Every number opens its rounds.</item>
///     </list>
///     Rotations are the Rotations layer's. The heatmap's picture is what the export carries beside the read.
/// </summary>
public sealed class SetupsLayer() : ScoutingLayerBase("setups", "Setups", ScoutingLayerPlacement.Map,
    ScoutingSides.Ct, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Player1), "E/D")
{
    /// <summary>A faded player's dots keep this share of their opacity while another is isolated.</summary>
    public const float IsolatedFade = 0.1f;

    /// <summary>The inspector tab with each player's places.</summary>
    public const string PlayersTab = "Players";

    /// <summary>The inspector tab with the CTs per area.</summary>
    public const string ShapeTab = "Shape";

    /// <summary>The inspector tab with one row per buy.</summary>
    public const string ByBuyTab = "By buy";

    /// <summary>The CTs per area section's title.</summary>
    public const string FormationsTitle = "CTs per area · A · mid · B";

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
    public override string TitleFor(ScoutingSide side) => "Setups";

    /// <inheritdoc />
    public override bool OnByDefault(ScoutingSide side) => side == ScoutingSide.Ct;

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
        IReadOnlyDictionary<string, string> names = ScoutingPlayerColors.Names(context.Data.Sources.Teams, context.TeamId);
        SetupAreaClassifier areas = SetupAreaClassifier.Learn(onMap.SelectMany(h => h.Rounds),
            RegionsFor(context));
        List<SetupHeatmapRound> rounds = [.. heatmaps.SelectMany(h => h.Rounds)];
        IReadOnlyList<SetupPlayerPlace> places = SetupsDerivation.PlacesByPlayer(rounds);
        string scopeBuy = context.Buy == ScoutingBuy.All
            ? "all buys"
            : ScoutingReportLines.BuyLabelFor(_buys[context.Buy]);

        List<ScoutingInspectorSection> sections =
        [
            new($"Places held in the setup window · {scopeBuy}",
                places.Count > 0
                    ? [.. places.Select(p => PlaceRow(context, p, colors, names))]
                    : [.. FixedPlaceRows(heatmaps)], PlayersTab)
        ];

        IReadOnlyList<SetupFormation> formations = SetupsDerivation.Formations(rounds, areas);
        int of = formations.Sum(f => f.Rounds.Count);
        sections.Add(new ScoutingInspectorSection(FormationsTitle, formations.Count > 0
            ? [.. formations.Take(6).Select(f => FormationRow(context, f, of, areas.HasSites || areas.HasRegions))]
            : [new ScoutingInspectorRow { Label = "No decided round with CT positions", Note = $"{scopeBuy} on {context.Map}" }],
            ShapeTab));

        sections.Add(new ScoutingInspectorSection("Rounds by buy",
            [.. onMap.Select(h => BuyRow(h, names, context.Buy != ScoutingBuy.All && h.Buy == _buys[context.Buy]))],
            ByBuyTab));

        return new ScoutingLayerResult
        {
            CountLabel = $"{rounds.Count} rds",
            Marks = Marks(rounds, places, colors, Isolated, names,
                [.. heatmaps.SelectMany(h => h.Rounds.Select(r => (r, ScoutingReportLines.BuyLabelFor(h.Buy))))]),
            Reads = [ReadFor(context, heatmaps, names)],
            Sections = sections,
            Legend =
            [
                new ScoutingLegendEntry(Swatch, "dot = a player's setup in one round, in their Players colour"),
                new ScoutingLegendEntry(new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.Neutral),
                    "ringed dot = the player's most-held spot")
            ]
        };
    }

    /// <summary>
    ///     The marks: a dot per player per round where they held (see
    ///     <see cref="SetupsDerivation.HeldSpot" />), then a ringed dot on each player's
    ///     main spot on top. With a player isolated (and in the rounds), everyone else fades to
    ///     <see cref="IsolatedFade" />.
    /// </summary>
    /// <param name="rounds">The rounds in scope.</param>
    /// <param name="places">Their places by player.</param>
    /// <param name="colors">The roster's colours by key.</param>
    /// <param name="isolated">The isolated player's key, or null.</param>
    /// <param name="names">Roster names by key, for the marks' cards; null uses the in-game names.</param>
    /// <param name="buys">Each round's buy label, for the round dots' cards.</param>
    public static IReadOnlyList<ScoutingMapMark> Marks(IEnumerable<SetupHeatmapRound> rounds,
        IReadOnlyList<SetupPlayerPlace> places, IReadOnlyDictionary<string, ScoutingColor> colors, string? isolated,
        IReadOnlyDictionary<string, string>? names = null,
        IReadOnlyList<(SetupHeatmapRound Round, string Buy)>? buys = null)
    {
        names ??= new Dictionary<string, string>();
        Dictionary<SetupHeatmapRound, string> buyOf = new(ReferenceEqualityComparer.Instance);
        foreach ((SetupHeatmapRound round, string buy) in buys ?? [])
        {
            buyOf[round] = buy;
        }

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
        List<(string Key, SetupSample Spot)> spots = [];
        foreach (SetupHeatmapRound round in rounds.Where(r => r.Detail is not null))
        {
            SetupRoundDetail detail = round.Detail!;
            List<(SetupRoundPlayer Player, SetupSample Spot)> held =
                [.. detail.Players.Where(p => p.Setup.Count > 0).Select(p => (p, SetupsDerivation.HeldSpot(p.Setup)))];
            foreach ((SetupRoundPlayer player, SetupSample spot) in held)
            {
                spots.Add((player.Key, spot));
                ScoutingMapMark dot = new(spot.X, spot.Y, spot.Z, ScoutingMarkShape.Dot, ColorOf(player.Key), 4.5f,
                    Opacity: 0.8f * Fade(player.Key))
                {
                    Info = RoundInfo(round, player, spot, held, names, colors,
                        buyOf.TryGetValue(round, out string? buy) ? buy : null)
                };
                (player.Key == isolated ? front : marks).Add(dot);
            }
        }

        marks.AddRange(front);
        foreach (SetupPlayerPlace place in places.Where(p => p.IsMain).OrderBy(p => p.Key == isolated))
        {
            float fade = Fade(place.Key);
            marks.Add(new ScoutingMapMark(place.X, place.Y, place.Z, ScoutingMarkShape.Dot, ScoutingColor.Neutral,
                10f, Opacity: 0.95f * fade));
            marks.Add(new ScoutingMapMark(place.X, place.Y, place.Z, ScoutingMarkShape.Dot, ColorOf(place.Key), 8f,
                Opacity: fade)
            {
                Info = MainSpotInfo(place, places, spots, names, colors)
            });
        }

        return marks;
    }

    // One player's dot in one round: where they held, how the round went, the round to watch; clicking rings the
    // other four where they held that round.
    private static ScoutingMarkInfo RoundInfo(SetupHeatmapRound round, SetupRoundPlayer player, SetupSample spot,
        List<(SetupRoundPlayer Player, SetupSample Spot)> held, IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, ScoutingColor> colors, string? buy)
    {
        SetupRoundDetail detail = round.Detail!;
        string name = ScoutingPlayerColors.NameOf(names, player.Key, player.Name);
        string place = spot.Place is { } p ? SetupsDerivation.PlaceLabel(p) : "an unnamed spot";
        string outcome = !detail.Decided ? "undecided"
            : (detail.Won ? "won" : "lost") + (detail.PlantSite == BombSite.Unknown ? "" : $", bomb planted {detail.PlantSite}");
        List<ScoutingInspectorRow> rows =
        [
            new() { Label = "Held", Value = place, StarText = $"{name} at {spot.Place}" },
            new() { Label = "Round", Value = outcome, StarText = "round outcome" },
            new()
            {
                Label = "The others that round",
                Note = string.Join(" · ", held.Where(h => h.Player.Key != player.Key).Select(h =>
                    $"{ScoutingPlayerColors.NameOf(names, h.Player.Key, h.Player.Name)} {(h.Spot.Place is { } q ? SetupsDerivation.PlaceLabel(q) : "?")}")),
                StarText = "setup that round"
            },
            new()
            {
                Label = "Watch the round",
                Value = Path.GetFileNameWithoutExtension(round.DemoPath),
                StarText = "watch round",
                Rounds = Open([round]),
                OpenTitle = $"{name} at {place}",
                Play = (round.DemoPath, round.FreezeEndTick),
                FollowSteamId = ulong.TryParse(player.Key, out ulong id) ? id : null
            }
        ];
        if (buy is not null)
        {
            rows.Insert(1, new ScoutingInspectorRow { Label = "Buy", Value = buy, StarText = "buy" });
        }

        return new ScoutingMarkInfo($"{name} · round {round.RoundNumber}", rows)
        {
            Hint = "ring where the other CTs held that round",
            Detail =
            [
                .. held.Where(h => h.Player.Key != player.Key).Select(h => new ScoutingMapMark(h.Spot.X, h.Spot.Y,
                    h.Spot.Z, ScoutingMarkShape.Ring, colors.TryGetValue(h.Player.Key, out ScoutingColor c) ? c : ScoutingColor.Neutral, 7f))
            ]
        };
    }

    // A player's main spot: how often they hold it, where else they go; clicking rings every round they held.
    private static ScoutingMarkInfo MainSpotInfo(SetupPlayerPlace main, IReadOnlyList<SetupPlayerPlace> places,
        List<(string Key, SetupSample Spot)> spots, IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, ScoutingColor> colors)
    {
        string name = ScoutingPlayerColors.NameOf(names, main.Key, main.Name);
        ScoutingColor color = colors.TryGetValue(main.Key, out ScoutingColor c) ? c : ScoutingColor.Neutral;
        List<ScoutingInspectorRow> rows =
        [
            .. places.Where(p => p.Key == main.Key).OrderByDescending(p => p.Rounds.Count).Select(p =>
                new ScoutingInspectorRow
                {
                    Label = SetupsDerivation.PlaceLabel(p.Place),
                    Value = CountText(p.Rounds.Count, p.Of),
                    StarText = $"{name} at {p.Place}",
                    Note = p.IsMain ? (p.IsFixed ? "main spot, fixed" : "main spot") : null,
                    BarPercent = p.Of > 0 ? 100.0 * p.Rounds.Count / p.Of : null,
                    Swatch = color,
                    Rounds = Open(p.Rounds),
                    OpenTitle = $"{name} at {SetupsDerivation.PlaceLabel(p.Place)}"
                })
        ];
        return new ScoutingMarkInfo($"{name} · main spot {SetupsDerivation.PlaceLabel(main.Place)}", rows)
        {
            Hint = $"ring every round {name} set up",
            Detail =
            [
                .. spots.Where(s => s.Key == main.Key).Select(s =>
                    new ScoutingMapMark(s.Spot.X, s.Spot.Y, s.Spot.Z, ScoutingMarkShape.Ring, color, 6f))
            ]
        };
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

    // The read for the scope in view: one buy's, filed under that buy. With one buy filtered, that buy. With every
    // buy, the buy with the most sampled rounds, its lead saying so when there is more than one buy: a setup is a
    // per-buy habit, and a read over every buy at once mixes eco stacks into the full-buy shape.
    private static ScoutingRead ReadFor(ScoutingLayerContext context, List<SetupHeatmap> heatmaps,
        IReadOnlyDictionary<string, string> names)
    {
        ScoutingReportSources sources = context.Data.Sources;
        SetupHeatmap heatmap = heatmaps.MaxBy(h => h.SampledRounds)!;
        string lead = $"{Capital(ScoutingReportLines.BuyLabelFor(heatmap.Buy))} CT"
                      + (context.Buy == ScoutingBuy.All && heatmaps.Count > 1 ? ", the most sampled buy" : "");
        (string text, bool any) = ReadFor(heatmap, lead, names);
        return new ScoutingRead(text, Suggested: any, Buy: BuyOf(heatmap.Buy),
            Image: () => Render(sources, heatmap));
    }

    // "Full buy CT: Alice holds B site in 47 of 50 rounds; Bob holds Window in 41 of 50."
    private static (string Text, bool Any) ReadFor(SetupHeatmap heatmap, string lead,
        IReadOnlyDictionary<string, string> names)
    {
        if (TopText(SetupsDerivation.PlacesByPlayer(heatmap.Rounds), names) is { } top)
        {
            return ($"{lead}: {top}.", true);
        }

        string[] fixedPlaces = [.. heatmap.Positions.Where(p => p.IsFixed).Select(p => p.Place)];
        return fixedPlaces.Length == 0
            ? ($"{lead}: no place is held in most of {Plural(heatmap.SampledRounds, "sampled round")}.", false)
            : ($"{lead}: {string.Join(", ", fixedPlaces)} held in the setup window over {Plural(heatmap.SampledRounds, "sampled round")}.", true);
    }

    // "Alice holds B site in 47 of 50 rounds; Bob holds Window in 41 of 50": the two players who held their main
    // place in the most rounds. Null when no player has a main place.
    private static string? TopText(IReadOnlyList<SetupPlayerPlace> places, IReadOnlyDictionary<string, string> names)
    {
        List<SetupPlayerPlace> top = [.. TopPlaces(places)];
        if (top.Count == 0)
        {
            return null;
        }

        string first =
            $"{ScoutingPlayerColors.NameOf(names, top[0].Key, top[0].Name)} holds {SetupsDerivation.PlaceLabel(top[0].Place)} in {top[0].Rounds.Count} of {Plural(top[0].Of, "round")}";
        return top.Count == 1
            ? first
            : $"{first}; {ScoutingPlayerColors.NameOf(names, top[1].Key, top[1].Name)} holds {SetupsDerivation.PlaceLabel(top[1].Place)} in {top[1].Rounds.Count} of {top[1].Of}";
    }

    private static IEnumerable<SetupPlayerPlace> TopPlaces(IReadOnlyList<SetupPlayerPlace> places) =>
        places.Where(p => p.IsMain).OrderByDescending(p => p.Rounds.Count).Take(2);

    // One buy's row on the By buy tab: its rounds, the sampled ones, and its top two held places. The buy in view
    // carries a CT swatch and leads its note with "in view".
    private static ScoutingInspectorRow BuyRow(SetupHeatmap heatmap, IReadOnlyDictionary<string, string> names,
        bool inView)
    {
        string buyLabel = ScoutingReportLines.BuyLabelFor(heatmap.Buy);
        List<string> top =
        [
            .. TopPlaces(SetupsDerivation.PlacesByPlayer(heatmap.Rounds)).Select(p =>
                $"{ScoutingPlayerColors.NameOf(names, p.Key, p.Name)} {SetupsDerivation.PlaceLabel(p.Place)}")
        ];
        if (top.Count == 0)
        {
            top = [.. heatmap.Positions.Where(p => p.IsFixed).Take(2).Select(p => SetupsDerivation.PlaceLabel(p.Place))];
        }

        string setup = top.Count > 0 ? string.Join(" · ", top) : "no place held in most rounds";
        return new ScoutingInspectorRow
        {
            Label = Capital(buyLabel),
            Value = $"{Plural(heatmap.Rounds.Count, "round")} · {heatmap.SampledRounds} sampled",
            StarText = "rounds",
            Buy = BuyOf(heatmap.Buy),
            Note = inView ? $"in view · {setup}" : setup,
            Swatch = inView ? ScoutingColor.Ct : null,
            Rounds = Open(heatmap.Rounds),
            OpenTitle = $"{heatmap.Map} · {buyLabel}"
        };
    }

    private static ScoutingInspectorRow PlaceRow(ScoutingLayerContext context, SetupPlayerPlace place,
        IReadOnlyDictionary<string, ScoutingColor> colors, IReadOnlyDictionary<string, string> names)
    {
        string name = ScoutingPlayerColors.NameOf(names, place.Key, place.Name);
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
