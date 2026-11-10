#region

using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Retakes and saves (CT), per map and buy: how many CTs are alive when the opponents plant, and at each
///     man count how often they retake, how often they save (spec Q6, <see cref="RetakeRules" />) and how many
///     retakes they win; per site the retakes won and whether the retake comes together or trickles in. The
///     map draws where CTs reached the bomb (blue dots) and where the savers stood (grey rings). Every number
///     opens its rounds.
/// </summary>
public sealed class RetakesLayer() : ScoutingLayerBase("retakes", "Retakes and saves", ScoutingLayerPlacement.Map,
    ScoutingSides.Ct, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Ct), "E/D")
{
    /// <summary>The fewest rounds at and below a man count before the read calls the retakes collapsed there.</summary>
    private const int CollapseSample = 3;

    private static readonly ScoutingSwatch _reached = new(ScoutingMarkShape.Dot, ScoutingColor.Ct);
    private static readonly ScoutingSwatch _saved = new(ScoutingMarkShape.Ring, ScoutingColor.Neutral);

    // One read per team load: every scope of the team filters the same rounds.
    private static readonly ConditionalWeakTable<ScoutingReportData, Task<RetakeSet?>> _sets = new();

    /// <inheritdoc />
    public override bool UsesBuy => true;

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ScoutingReportSources sources = context.Data.Sources;
        if (sources.RoundFacts is null)
        {
            return await Legacy(context).ConfigureAwait(false);
        }

        RetakeSet? set = await _sets.GetValue(context.Data, data => ScoutingLayerContext.Offload(() =>
        {
            try
            {
                return new RetakeService(sources.Teams, sources.Library, sources.RoundFacts, sources.RoundIndex)
                    .Build(data.TeamId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return null;
            }
        })).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (set is null)
        {
            return ScoutingLayerResult.Empty("retakes unavailable");
        }

        List<RetakeRound> rounds =
        [
            .. set.Rounds.Where(r => string.Equals(r.Map, context.Map, StringComparison.OrdinalIgnoreCase)
                                     && Matches(context.Buy, r.Buy))
        ];
        return rounds.Count == 0
            ? ScoutingLayerResult.Empty(
                $"no {(context.Buy == ScoutingBuy.All ? "" : context.Buy.ToString().ToLowerInvariant() + " ")}CT rounds on {context.Map}")
            : Compose(context.Prefix, rounds);
    }

    // A host with the Post-Plant load but no Round Facts source: the block's own rows, with no save read or marks.
    private static async Task<ScoutingLayerResult> Legacy(ScoutingLayerContext context)
    {
        if (await LayerData.PostPlant(context).ConfigureAwait(false) is not { } block)
        {
            return await LayerData.PostPlantMissing(context).ConfigureAwait(false);
        }

        int rounds = block.SideRounds.Count;
        int planted = block.Planted.Count;
        return new ScoutingLayerResult
        {
            CountLabel = $"{planted} plants",
            Reads = planted > 0
                ? [new ScoutingRead($"Opponents plant on them in {CountText(planted, rounds)}.", Suggested: true)]
                : [],
            Sections =
            [
                .. new ScoutingInspectorSection[]
                {
                    new("", [Sample(context, block.SideRounds, "rounds"), Row(context, block.Planted, rounds, "", label: "opponent planted") with { StarText = "opponent plants", OpenTitle = $"{context.Prefix} · opponent plants" }]),
                    new("CTs alive at the plant", Rows(context, block.ManCount, planted, "at the plant")),
                    new("After the plant", Rows(context, block.Outcomes, planted, "after the plant")),
                    new("Retake grouping", Rows(context, block.RetakeGroups, planted, "retake", ScoutingColor.Ct))
                }.Where(s => s.Rows.Count > 0)
            ]
        };
    }

    /// <summary>The layer over a scope's CT rounds: the rows, the read, the marks and the legend.</summary>
    /// <param name="prefix">"de_nuke CT": how the Review titles start.</param>
    /// <param name="rounds">The scope's CT rounds, planted or not.</param>
    internal static ScoutingLayerResult Compose(string prefix, IReadOnlyList<RetakeRound> rounds)
    {
        List<RetakeRound> planted = [.. rounds.Where(r => r.Planted)];
        List<ScoutingInspectorSection> sections =
        [
            new("",
            [
                Open("rounds", Plural(rounds.Count, "round"), "rounds", rounds, $"{prefix} · rounds"),
                Open("opponent planted", CountText(planted.Count, rounds.Count), "opponent plants", planted,
                    $"{prefix} · opponent plants")
            ]),
            new("CTs alive at the plant", ManCountRows(prefix, planted)),
            new("Saves", SaveRows(prefix, planted)),
            new("By site", SiteRows(prefix, planted)),
            new("Retake grouping",
            [
                .. planted.Where(r => r.Kind == RetakeKind.Retook && r.Grouping is not null)
                    .GroupBy(r => r.Grouping!, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => Open(g.Key, CountText(g.Count(), planted.Count(r => r.Kind == RetakeKind.Retook)),
                        $"retake {g.Key}", [.. g], $"{prefix} · retake {g.Key}") with { Swatch = ScoutingColor.Ct })
            ]),
            new("After the plant",
            [
                .. planted.GroupBy(r => r.Outcome, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => Open(g.Key, CountText(g.Count(), planted.Count), $"after the plant {g.Key}",
                        [.. g], $"{prefix} · after the plant {g.Key}"))
            ])
        ];

        return new ScoutingLayerResult
        {
            CountLabel = $"{planted.Count} plants",
            Marks =
            [
                .. planted.SelectMany(r => r.Reached).Select(p => Mark(p, ScoutingMarkShape.Dot, ScoutingColor.Ct, 4f, 0.85f)),
                .. planted.SelectMany(r => r.Saves).Select(p => Mark(p, ScoutingMarkShape.Ring, ScoutingColor.Neutral, 5f, 1f))
            ],
            Reads = Read(planted) is { } read ? [new ScoutingRead(read, Suggested: true)] : [],
            Sections = [.. sections.Where(s => s.Rows.Count > 0)],
            Legend =
            [
                new ScoutingLegendEntry(_reached, "blue = CT reached the bomb"),
                new ScoutingLegendEntry(_saved, "grey ring = CT saved")
            ]
        };
    }

    /// <summary>
    ///     The layer's sentence: where the retakes collapse into saves, if they do, else how often they
    ///     retake and win. Null with no plant.
    /// </summary>
    /// <param name="planted">The scope's planted rounds.</param>
    internal static string? Read(IReadOnlyList<RetakeRound> planted)
    {
        if (planted.Count == 0)
        {
            return null;
        }

        // The highest CT count at and below which the saves outnumber the retakes, over a real sample.
        foreach (int ct in planted.Select(r => r.CtAlive).Where(c => c > 0).Distinct().OrderDescending())
        {
            List<RetakeRound> below = [.. planted.Where(r => r.CtAlive > 0 && r.CtAlive <= ct)];
            int saved = below.Count(r => r.Kind == RetakeKind.Saved);
            int retook = below.Count(r => r.Kind == RetakeKind.Retook);
            if (saved + retook >= CollapseSample && saved > retook)
            {
                return ct == 1
                    ? $"Retakes collapse at one CT alive: they save in {saved} of {Plural(saved + retook, "round")}."
                    : $"Retakes collapse at {ct} CTs alive and below: they save in {saved} of {Plural(saved + retook, "round")}.";
            }
        }

        List<RetakeRound> retakes = [.. planted.Where(r => r.Kind == RetakeKind.Retook)];
        int saves = planted.Count(r => r.Kind == RetakeKind.Saved);
        if (retakes.Count == 0)
        {
            return saves == 0
                ? $"Opponents plant on them in {Plural(planted.Count, "round")}; no retake or save is read."
                : $"They save in {saves} of {Plural(planted.Count, "planted round")}.";
        }

        return $"They retake {CountText(retakes.Count, planted.Count)} after the plant and win {retakes.Count(r => r.Won)} of them; they save in {saves}.";
    }

    // One row per man count, the most CTs first: "4v4 · retook 8 · saved 1", "won 3 of 8", opening every round at it.
    private static List<ScoutingInspectorRow> ManCountRows(string prefix, IReadOnlyList<RetakeRound> planted) =>
    [
        .. planted.GroupBy(r => (r.CtAlive, r.TAlive))
            .OrderByDescending(g => g.Key.CtAlive).ThenBy(g => g.Key.TAlive)
            .Select(g =>
            {
                List<RetakeRound> all = [.. g];
                string count = all[0].ManCount;
                int retook = all.Count(r => r.Kind == RetakeKind.Retook);
                int saved = all.Count(r => r.Kind == RetakeKind.Saved);
                int won = all.Count(r => r.Kind == RetakeKind.Retook && r.Won);
                int unread = all.Count(r => r.Kind == RetakeKind.Unread);
                bool none = g.Key.CtAlive == 0 || all.All(r => r.Kind == RetakeKind.NoCtAlive);
                return Open(
                    none ? $"{count} · no CT alive" : $"{count} · retook {retook} · saved {saved}",
                    none ? Plural(all.Count, "round") : retook > 0 ? $"won {won} of {retook}" : $"saved {saved} of {all.Count}",
                    $"at the plant {count}", all, $"{prefix} · retakes at {count}") with
                {
                    BarPercent = retook > 0 ? 100.0 * won / retook : null,
                    Swatch = ScoutingColor.Ct,
                    Note = unread > 0 ? $"{unread} without positions" : null
                };
            })
    ];

    // The Q6 saves per man count, opening the saved rounds.
    private static List<ScoutingInspectorRow> SaveRows(string prefix, IReadOnlyList<RetakeRound> planted) =>
    [
        .. planted.Where(r => r.CtAlive > 0).GroupBy(r => (r.CtAlive, r.TAlive))
            .OrderByDescending(g => g.Key.CtAlive).ThenBy(g => g.Key.TAlive)
            .Where(g => g.Any(r => r.Kind == RetakeKind.Saved))
            .Select(g =>
            {
                List<RetakeRound> saved = [.. g.Where(r => r.Kind == RetakeKind.Saved)];
                string count = g.First().ManCount;
                return Open($"{count} · saved", CountText(saved.Count, g.Count()), $"saved at {count}", saved,
                    $"{prefix} · saved at {count}") with
                {
                    BarPercent = 100.0 * saved.Count / g.Count(), Swatch = ScoutingColor.Neutral
                };
            })
    ];

    // Per site: the retakes won (opening the won retakes) and each grouping of the retakes (opening its rounds).
    private static List<ScoutingInspectorRow> SiteRows(string prefix, IReadOnlyList<RetakeRound> planted)
    {
        List<ScoutingInspectorRow> rows = [];
        foreach (IGrouping<BombSite, RetakeRound> site in planted.Where(r => r.Kind == RetakeKind.Retook)
                     .GroupBy(r => r.Site).OrderBy(g => g.Key == BombSite.Unknown).ThenBy(g => g.Key.ToString()))
        {
            string name = site.Key == BombSite.Unknown ? "unknown site" : site.Key.ToString();
            List<RetakeRound> retakes = [.. site];
            List<RetakeRound> won = [.. retakes.Where(r => r.Won)];
            rows.Add(Open($"{name} · retake won", CountText(won.Count, retakes.Count), $"{name} retake won", won,
                $"{prefix} · {name} retakes won") with
            {
                BarPercent = 100.0 * won.Count / retakes.Count, Swatch = ScoutingColor.Ct
            });
            rows.AddRange(retakes.Where(r => r.Grouping is not null)
                .GroupBy(r => r.Grouping!, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => Open($"{name} · {g.Key}", CountText(g.Count(), retakes.Count),
                    $"{name} retake {g.Key}", [.. g], $"{prefix} · {name} retake {g.Key}") with
                {
                    BarPercent = 100.0 * g.Count() / retakes.Count
                }));
        }

        return rows;
    }

    private static ScoutingInspectorRow Open(string label, string value, string starText,
        IEnumerable<RetakeRound> rounds, string title) => new()
    {
        Label = label,
        Value = value,
        StarText = starText,
        Rounds = [.. rounds.Select(r => r.Clip)],
        OpenTitle = title
    };

    private static ScoutingMapMark Mark(RoundPosition p, ScoutingMarkShape shape, ScoutingColor color, float size,
        float opacity) => new(p.X, p.Y, p.Z, shape, color, size, Opacity: opacity);

    private static bool Matches(ScoutingBuy buy, BuyType actual) => buy switch
    {
        ScoutingBuy.Eco => actual == BuyType.Eco,
        ScoutingBuy.Semi => actual == BuyType.Semi,
        ScoutingBuy.Force => actual == BuyType.Force,
        ScoutingBuy.Full => actual == BuyType.Full,
        _ => true
    };
}
