#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Playback2D.Overlay;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Setups (CT): where the team stands in the setup window, per buy. The marks are the Setup Heatmap's own
///     points (every sampled CT position from freeze end to the window's end); the inspector lists each buy's
///     rounds, which open in Review, and its fixed and rotating places. One read per buy, starred under that buy,
///     carrying the heatmap picture for the export.
///     <para>Phase 2 (P2.1) adds per player, CTs per area and rotations.</para>
/// </summary>
public sealed class SetupsLayer() : ScoutingLayerBase("setups", "Setups", ScoutingLayerPlacement.Map,
    ScoutingSides.Ct, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Ct), "E")
{
    /// <summary>The buys the filter offers, in the fact vocabulary.</summary>
    private static readonly Dictionary<ScoutingBuy, BuyType> _buys = new Dictionary<ScoutingBuy, BuyType>
    {
        [ScoutingBuy.Eco] = BuyType.Eco,
        [ScoutingBuy.Semi] = BuyType.Semi,
        [ScoutingBuy.Force] = BuyType.Force,
        [ScoutingBuy.Full] = BuyType.Full
    };

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

        List<SetupHeatmap> heatmaps =
        [
            .. set.Heatmaps.Where(h => string.Equals(h.Map, context.Map, StringComparison.OrdinalIgnoreCase)
                                       && (context.Buy == ScoutingBuy.All || h.Buy == _buys[context.Buy]))
        ];
        if (heatmaps.Count == 0)
        {
            return ScoutingLayerResult.Empty(set.Heatmaps.Count == 0
                ? ScoutingReportLines.HeatmapLineFor(set)
                : $"no {(context.Buy == ScoutingBuy.All ? "" : context.Buy.ToString().ToLowerInvariant() + " ")}CT rounds on {context.Map}");
        }

        List<ScoutingMapMark> marks = [];
        List<ScoutingRead> reads = [];
        List<ScoutingInspectorSection> sections = [];
        int rounds = 0;
        SetupHeatmap largest = heatmaps.MaxBy(h => h.SampledRounds)!;
        foreach (SetupHeatmap heatmap in heatmaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rounds += heatmap.Rounds.Count;
            ScoutingBuy? buy = BuyOf(heatmap.Buy);
            string buyLabel = ScoutingReportLines.BuyLabelFor(heatmap.Buy);
            marks.AddRange(heatmap.Points.Select(p =>
                new ScoutingMapMark(p.WorldX, p.WorldY, p.WorldZ, ScoutingMarkShape.Dot, ScoutingColor.Ct, 3f,
                    Opacity: 0.55f)));

            string[] fixedPlaces = [.. heatmap.Positions.Where(p => p.IsFixed).Select(p => p.Place)];
            SetupHeatmap captured = heatmap;
            reads.Add(new ScoutingRead(
                fixedPlaces.Length == 0
                    ? $"{Capital(buyLabel)}: no place is held in most of {Plural(heatmap.SampledRounds, "sampled round")}."
                    : $"{Capital(buyLabel)}: {string.Join(", ", fixedPlaces)} held in the setup window over {Plural(heatmap.SampledRounds, "sampled round")}.",
                Suggested: ReferenceEquals(heatmap, largest) && fixedPlaces.Length > 0,
                Buy: buy,
                Image: () => Render(context.Data.Sources, captured)));

            List<ScoutingInspectorRow> rows =
            [
                new()
                {
                    Label = $"{buyLabel} rounds",
                    Value = $"{Plural(heatmap.Rounds.Count, "round")} · {heatmap.SampledRounds} sampled",
                    StarText = "rounds",
                    Buy = buy,
                    Rounds = [.. heatmap.Rounds.Select(r => new TendencyRound(r.DemoPath, r.Sha256, r.RoundNumber,
                        r.FreezeEndTick, r.ClipEndTick, r.TickRate))],
                    OpenTitle = $"{heatmap.Map} · {buyLabel}"
                },
                .. heatmap.Positions.Select(p => new ScoutingInspectorRow
                {
                    Label = p.Place,
                    Value = $"held {p.RoundsHeld} of {p.SampledRounds}",
                    StarText = $"place {p.Place}",
                    Buy = buy,
                    Note = p.IsFixed ? "fixed" : "rotating",
                    BarPercent = p.SampledRounds > 0 ? 100.0 * p.RoundsHeld / p.SampledRounds : null,
                    Swatch = ScoutingColor.Ct
                })
            ];
            sections.Add(new ScoutingInspectorSection($"Places held in the setup window · {buyLabel}", rows));
        }

        return new ScoutingLayerResult
        {
            CountLabel = $"{rounds} rds",
            Marks = marks,
            Reads = reads,
            Sections = sections,
            Legend =
            [
                new ScoutingLegendEntry(Swatch,
                    "setups: CT positions in the setup window (per player in Phase 2)")
            ]
        };
    }

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
