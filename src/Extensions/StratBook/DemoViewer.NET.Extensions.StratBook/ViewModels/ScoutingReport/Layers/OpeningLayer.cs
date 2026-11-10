#region

using System.Globalization;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Opening duels (T) and opening deaths (CT), from <see cref="OpeningDuelsService" />. CT rings only the
///     rounds the team lost the opening duel (their player died to the round's first kill), and its place bars
///     count the same rounds; T draws opening kills as white dots and opening deaths as rings, both at the team's
///     player. The inspector has the place bars, the duel win %, the round won after winning or losing it, the
///     traded opening deaths and who opens, and on T the entry by site from the Opening Tendencies. Every number
///     opens its rounds.
///     <para>
///         Positions are the 1 Hz sample at or before the kill, labelled "position ±1 s"; P3.3 switches them to
///         exact captures inside <see cref="OpeningDuelsService.SpotAt" />.
///     </para>
/// </summary>
public sealed class OpeningLayer() : ScoutingLayerBase("opening", "Opening duels", ScoutingLayerPlacement.Map,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.Bad), "D")
{
    /// <summary>A ring's radius in screen pixels: large enough to read on a small floor pane.</summary>
    public const float RingSize = 7f;

    /// <summary>A dot's radius in screen pixels.</summary>
    public const float DotSize = 5f;

    private const string NoPosition = "no position";

    // One build per team load, shared by every scope of it; dropped with the load.
    private static readonly ConditionalWeakTable<ScoutingReportData, Task<OpeningDuelSet?>> _sets = [];

    private static readonly ScoutingSwatch _killSwatch = new(ScoutingMarkShape.Dot, ScoutingColor.Neutral);

    /// <inheritdoc />
    public override string NameFor(ScoutingSide side) => side == ScoutingSide.Ct ? "Opening deaths" : Name;

    /// <summary>The team's opening duels for a load, built once off the UI thread; null when it cannot be built.</summary>
    /// <param name="data">The team's loads.</param>
    public static Task<OpeningDuelSet?> DuelsFor(ScoutingReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _sets.GetValue(data, d => d.Sources.RoundFacts is { } facts
            ? ScoutingLayerContext.Offload<OpeningDuelSet?>(() =>
            {
                try
                {
                    return OpeningDuelsService.Build(d.Sources.Teams, d.Sources.Library, facts, d.Positions,
                        d.TeamId);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or InvalidOperationException)
                {
                    return null;
                }
            })
            : Task.FromResult<OpeningDuelSet?>(null));
    }

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        OpeningDuelSet? set = await DuelsFor(context.Data).ConfigureAwait(false);
        OpeningTendencies? openings = await LayerData.Openings(context).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (set?.For(context.Map, (int)context.Side) is { } block)
        {
            return context.Side == ScoutingSide.Ct ? Ct(context, block, openings) : T(context, block, openings);
        }

        // No duels (no Round Facts on this host, or none for the scope): the Opening Tendencies alone, if any.
        if (openings is null)
        {
            return await LayerData.OpeningsMissing(context).ConfigureAwait(false);
        }

        List<ScoutingInspectorSection> sections = [new("", [Sample(context, openings.Rounds, "rounds")])];
        if (context.Side == ScoutingSide.T)
        {
            sections.Add(new ScoutingInspectorSection("Who takes the entry, by site",
                Rows(context, openings.EntryBySite, openings.Rounds.Count, "entry", ScoutingColor.T)));
        }

        AddFirstContact(context, sections, openings);
        return new ScoutingLayerResult
        {
            CountLabel = $"{openings.Rounds.Count} rds",
            Sections = [.. sections.Where(s => s.Rows.Count > 0)],
            Reads = context.Side == ScoutingSide.T && Top(openings.EntryBySite) is { } entry
                ? [new ScoutingRead($"{entry.Label} takes the entry most often: {CountText(entry.Count, openings.Rounds.Count)}.", "read entry")]
                : []
        };
    }

    private static ScoutingLayerResult Ct(ScoutingLayerContext context, OpeningDuelBlock block,
        OpeningTendencies? openings)
    {
        int rounds = block.Rounds.Count;
        // The rings and the bars read the same rounds: the ones where their player died to the opening kill.
        List<OpeningDeathRound> lostDuels = [.. block.FirstDeaths.Where(d => d.IsOpening)];
        List<TendencyBucket> places = ByPlace(lostDuels.Select(d => (d.Spot, d.Clip)));
        List<ScoutingInspectorSection> sections =
        [
            new("", [SampleRow(context, block)]),
            new("Where they lose the opening duel · position ±1 s",
                Rows(context, places, rounds, "opening death", ScoutingColor.Bad)),
            new("Opening duels", DuelRows(context, block, "CT")),
            new("Who opens", WhoOpens(context, block))
        ];
        AddFirstContact(context, sections, openings);

        List<ScoutingRead> reads = [];
        if (Top(places, NoPosition) is { } place)
        {
            reads.Add(new ScoutingRead(
                $"They lose the CT opening duel at {place.Label} in {Percent((double)place.Count / rounds)} of rounds.",
                Suggested: true));
        }

        if (block.Duels.Count > 0)
        {
            int won = block.Duels.Count(d => d.Won);
            int lost = block.Duels.Count - won;
            string after = lost > 0
                ? $"; after losing it they still win {Percent((double)block.Duels.Count(d => !d.Won && d.RoundWon) / lost)} of rounds"
                : "";
            reads.Add(new ScoutingRead(
                $"They win {Percent((double)won / block.Duels.Count)} of CT opening duels{after}.", "read duels"));
        }

        return new ScoutingLayerResult
        {
            CountLabel = $"{rounds} rds",
            Marks =
            [
                .. lostDuels.Where(d => d.Spot is not null).SelectMany(d => DeathMarks(d.Spot!))
            ],
            Reads = reads,
            Sections = [.. sections.Where(s => s.Rows.Count > 0)],
            Legend = [new ScoutingLegendEntry(new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.Bad),
                "ring = where they lose the opening duel (position ±1 s)")]
        };
    }

    private static ScoutingLayerResult T(ScoutingLayerContext context, OpeningDuelBlock block,
        OpeningTendencies? openings)
    {
        int rounds = block.Rounds.Count;
        List<OpeningDuelRound> kills = [.. block.Duels.Where(d => d.Won)];
        List<OpeningDuelRound> deaths = [.. block.Duels.Where(d => !d.Won)];
        List<ScoutingInspectorSection> sections =
        [
            new("", [SampleRow(context, block)]),
            new("Opening duels on T", DuelRows(context, block, "T")),
            new("Opening kills by place · position ±1 s",
                Rows(context, ByPlace(kills.Select(d => (d.Spot, d.Clip))), kills.Count, "opening kill",
                    ScoutingColor.Neutral)),
            new("Opening deaths by place · position ±1 s",
                Rows(context, ByPlace(deaths.Select(d => (d.Spot, d.Clip))), deaths.Count, "opening death",
                    ScoutingColor.Bad)),
            new("Who opens", WhoOpens(context, block))
        ];
        List<ScoutingRead> reads = [];
        if (openings is not null)
        {
            sections.Add(new ScoutingInspectorSection("Who takes the entry, by site",
                Rows(context, openings.EntryBySite, openings.Rounds.Count, "entry", ScoutingColor.T)));
        }

        AddFirstContact(context, sections, openings);

        if (Top(PlayerBuckets(block)) is { } opener && block.Duels.Count > 0)
        {
            string converts = kills.Count > 0
                ? $"; a won opening converts {Percent((double)kills.Count(d => d.RoundWon) / kills.Count)} of the time"
                : "";
            reads.Add(new ScoutingRead(
                $"{opener.Label} takes {Percent((double)opener.Count / block.Duels.Count)} of T opening duels{converts}.",
                Suggested: true));
        }

        return new ScoutingLayerResult
        {
            CountLabel = $"{rounds} rds",
            Marks =
            [
                .. kills.Where(d => d.Spot is not null).Select(d =>
                    new ScoutingMapMark(d.Spot!.X, d.Spot.Y, d.Spot.Z, ScoutingMarkShape.Dot, ScoutingColor.Neutral,
                        DotSize, Opacity: 0.95f)),
                .. deaths.Where(d => d.Spot is not null).SelectMany(d => DeathMarks(d.Spot!))
            ],
            Reads = reads,
            Sections = [.. sections.Where(s => s.Rows.Count > 0)],
            Legend =
            [
                new ScoutingLegendEntry(_killSwatch, "white dot = opening kill (position ±1 s)"),
                new ScoutingLegendEntry(new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.Bad),
                    "ring = opening death (position ±1 s)")
            ]
        };
    }

    // A death: the ring, over a faint disc of the same colour. The ring alone is close to the grey of Nuke's
    // radar in lightness (contrast about 1.2); the disc's area carries it there, and the ring still reads as a ring
    // next to a kill's solid white dot.
    private static IEnumerable<ScoutingMapMark> DeathMarks(OpeningSpot spot) =>
    [
        new ScoutingMapMark(spot.X, spot.Y, spot.Z, ScoutingMarkShape.Dot, ScoutingColor.Bad, RingSize, Opacity: 0.3f),
        new ScoutingMapMark(spot.X, spot.Y, spot.Z, ScoutingMarkShape.Ring, ScoutingColor.Bad, RingSize, Opacity: 0.95f)
    ];

    // The duel lines both sides share; each one opens the rounds it counts.
    private static IReadOnlyList<ScoutingInspectorRow> DuelRows(ScoutingLayerContext context, OpeningDuelBlock block,
        string side)
    {
        List<OpeningDuelRound> won = [.. block.Duels.Where(d => d.Won)];
        List<OpeningDuelRound> lost = [.. block.Duels.Where(d => !d.Won)];
        List<ScoutingInspectorRow> rows =
        [
            Share(context, "Opening duel win %", "opening duels won", [.. won.Select(d => d.Clip)], block.Duels.Count,
                "duel"),
            Share(context, "Opening kills", "opening kills", [.. won.Select(d => d.Clip)], 0, "") with
            {
                Value = won.Count.ToString(CultureInfo.InvariantCulture)
            },
            Share(context, "Opening deaths", "opening deaths", [.. lost.Select(d => d.Clip)], 0, "") with
            {
                Value = lost.Count.ToString(CultureInfo.InvariantCulture)
            },
            Share(context, "Round won after winning the opening", "round won after winning the opening",
                [.. won.Where(d => d.RoundWon).Select(d => d.Rest)], won.Count, "won duel"),
            Share(context, "Round won after losing the opening", "round won after losing the opening",
                [.. lost.Where(d => d.RoundWon).Select(d => d.Rest)], lost.Count, "lost duel"),
            Share(context, "Opening deaths traded", "opening deaths traded",
                [.. lost.Where(d => d.Traded).Select(d => d.Clip)], lost.Count, "opening death") with
            {
                Note = $"killer dies to a {side} teammate within {OpeningDuelsService.TradeWindowSeconds:0} s"
            }
        ];
        return [.. rows.Where(r => r.Value.Length > 0)];
    }

    // A share row: "52% · 12 of 23 duels", or only the count when the sample is 0 (the caller sets the value).
    private static ScoutingInspectorRow Share(ScoutingLayerContext context, string label, string star,
        IReadOnlyList<TendencyRound> rounds, int of, string noun) => new()
    {
        Label = label,
        Value = of > 0 ? $"{Percent((double)rounds.Count / of)} · {rounds.Count} of {Plural(of, noun)}" : "",
        StarText = star,
        BarPercent = of > 0 ? 100.0 * rounds.Count / of : null,
        Rounds = rounds,
        OpenTitle = $"{context.Prefix} · {star}"
    };

    // Who takes the opening duels: one row per player, most first, with their won and lost.
    private static IReadOnlyList<ScoutingInspectorRow> WhoOpens(ScoutingLayerContext context, OpeningDuelBlock block)
    {
        int total = block.Duels.Count;
        IReadOnlyDictionary<string, ScoutingColor> colors =
            ScoutingPlayerColors.Lookup(context.Data.Sources.Teams, context.TeamId);
        return
        [
            .. block.Duels.GroupBy(d => d.PlayerKey, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Last().Player, StringComparer.Ordinal)
                .Select(g =>
                {
                    string name = g.Last().Player;
                    int won = g.Count(d => d.Won);
                    return new ScoutingInspectorRow
                    {
                        Label = name,
                        Value = $"{g.Count()} of {Plural(total, "duel")}",
                        StarText = $"opens {name}",
                        BarPercent = 100.0 * g.Count() / total,
                        Swatch = colors.TryGetValue(g.Key, out ScoutingColor c) ? c : ScoutingColor.Neutral,
                        Note = $"won {won} · lost {g.Count() - won}",
                        Rounds = [.. g.Select(d => d.Clip)],
                        OpenTitle = $"{context.Prefix} · opening duels {name}"
                    };
                })
        ];
    }

    private static List<TendencyBucket> PlayerBuckets(OpeningDuelBlock block) =>
    [
        .. block.Duels.GroupBy(d => d.PlayerKey, StringComparer.Ordinal)
            .Select(g => new TendencyBucket(g.Last().Player, [.. g.Select(d => d.Clip)]))
    ];

    // Rounds per place, most first, ties by name; the rounds with no sample last.
    private static List<TendencyBucket> ByPlace(IEnumerable<(OpeningSpot? Spot, TendencyRound Clip)> moments) =>
    [
        .. moments.GroupBy(m => m.Spot is null ? NoPosition : m.Spot.Place ?? "unplaced", StringComparer.Ordinal)
            .OrderBy(g => g.Key == NoPosition).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TendencyBucket(g.Key, [.. g.Select(m => m.Clip)]))
    ];

    // The sample row, with how many of its rounds had positions to draw from.
    private static ScoutingInspectorRow SampleRow(ScoutingLayerContext context, OpeningDuelBlock block) =>
        Sample(context, block.Rounds, "rounds") with
        {
            Note = $"positions read for {block.PositionRounds} of {Plural(block.Rounds.Count, "round")} · 1 Hz sample at or before the kill (±1 s)"
        };

    private static void AddFirstContact(ScoutingLayerContext context, List<ScoutingInspectorSection> sections,
        OpeningTendencies? openings)
    {
        if (openings is not null)
        {
            sections.Add(new ScoutingInspectorSection("First contact, seconds after freeze end",
                Rows(context, openings.FirstContactClock, openings.Rounds.Count, "first contact")));
        }
    }
}
