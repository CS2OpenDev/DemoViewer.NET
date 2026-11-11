#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Detected strats (T): Strat Mining's T patterns narrowed to the rounds this team was T, most run first. Each
///     is drawn as its most typical round's T trails, lettered where they end; its row says how often it won,
///     plays that round in 2D, and carries the "lost to" counter when the losses share one (see
///     <see cref="StratScouting.LostTo" />). Every number opens its rounds.
/// </summary>
public sealed class StratsLayer : ScoutingLayerBase
{
    private readonly Func<ScoutingLayerContext, MinedMember, string?>? _opener;
    private readonly Func<ScoutingLayerContext, MinedMember, int?>? _sideOf;

    public StratsLayer() : this(null)
    {
    }

    /// <param name="sideOf">The team's side in a member round; Team Identity's <c>SideAtRound</c> when null.</param>
    /// <param name="opener">The opponent CT's first grenade in a member round; the Grenade Index's when null.</param>
    internal StratsLayer(Func<ScoutingLayerContext, MinedMember, int?>? sideOf,
        Func<ScoutingLayerContext, MinedMember, string?>? opener = null) : base("strats", "Detected strats",
        ScoutingLayerPlacement.Map, ScoutingSides.T, new ScoutingSwatch(ScoutingMarkShape.Badge, ScoutingColor.T),
        "E/D")
    {
        _sideOf = sideOf;
        _opener = opener;
    }

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (StratLayerKit.Patterns(context) is not { } patterns)
        {
            return StratLayerKit.NoPatterns(context);
        }

        return await ScoutingLayerContext.Offload(() =>
        {
            IReadOnlyList<ScoutedStrat> strats = StratScouting.Own(patterns, context.Map, context.TeamId,
                StratLayerKit.SideOf(context, _sideOf));
            if (strats.Count == 0)
            {
                return ScoutingLayerResult.Empty(
                    $"no T strat of theirs was run twice on {context.Map} in the mined library");
            }

            List<ScoutedStrat> shown = [.. strats.Take(StratLayerKit.MaxShown)];
            Func<MinedMember, string?> opener = _opener is { } given
                ? m => given(context, m)
                : StratLayerKit.CtOpeners(context, shown);
            StratLayerKit.Ticks ticks = new(context);
            List<ScoutingInspectorRow> rows = [];
            List<ScoutingMapMark> marks = [];
            List<ScoutingRead> reads = [];
            for (int i = 0; i < shown.Count; i++)
            {
                ScoutedStrat strat = shown[i];
                string letter = StratLayerKit.Letter(i);
                string name = MinedStratBuilder.Name(strat.Pattern);
                int trailAt = marks.Count;
                int rowAt = rows.Count;
                marks.AddRange(StratLayerKit.Trail(context, strat, letter));
                rows.Add(StratLayerKit.Row(context, strat, name, letter, ticks) with
                {
                    Note = $"{Plural(strat.Support, "round")} in {Plural(strat.Pattern.Demos, "demo")}"
                });
                LostToFinding? lostTo = StratScouting.LostTo(strat, opener);
                if (lostTo is not null)
                {
                    rows.Add(new ScoutingInspectorRow
                    {
                        Label = $"Lost to: {lostTo.Feature}",
                        Value = $"{lostTo.Rounds.Count} of {lostTo.Losses} losses",
                        StarText = $"{name} lost to {lostTo.Feature}",
                        Swatch = ScoutingColor.Bad,
                        Note = lostTo.Wins > 0
                            ? $"the CTs' first grenade; in {lostTo.WinsWith} of {lostTo.Wins} wins"
                            : "the CTs' first grenade of the round",
                        Rounds = [.. StratLayerKit.InOrder(lostTo.Rounds).Select(ticks.Round)],
                        OpenTitle = $"{context.Prefix} · {name} lost to {lostTo.Feature}"
                    });
                }

                StratLayerKit.Pin(marks, trailAt, letter, name, rows[rowAt..]);
                if (i == 0)
                {
                    reads.Add(new ScoutingRead(
                        $"Their most run T strat on {context.Map} is {name}: {StratLayerKit.Record(strat)}.",
                        Suggested: true));
                }

                if (lostTo is not null && reads.Count < 2)
                {
                    reads.Add(new ScoutingRead(
                        $"{name} ({letter}) loses to the {lostTo.Feature}: {lostTo.Rounds.Count} of {lostTo.Losses} losses.",
                        "read lost to"));
                }
            }

            return new ScoutingLayerResult
            {
                CountLabel = Plural(strats.Count, "strat"),
                Marks = marks,
                Reads = reads,
                Sections =
                [
                    new ScoutingInspectorSection(
                        strats.Count > shown.Count
                            ? $"Most run first · top {shown.Count} of {strats.Count}"
                            : "Most run first", rows)
                ],
                Legend = StratLayerKit.Legend
            };
        }).ConfigureAwait(false);
    }
}

/// <summary>What the two strats layers share: the patterns, the sides, the trails, the rows and their rounds.</summary>
internal static class StratLayerKit
{
    /// <summary>The most strats one layer lists and draws.</summary>
    public const int MaxShown = 5;

    /// <summary>Seconds a member round opens before the take, so the approach is on screen.</summary>
    private const int ExecuteLeadSeconds = 10;

    /// <summary>Seconds of an opening a setup's trail covers: the miner's last setup anchor.</summary>
    private const int SetupTrailSeconds = 25;

    public static IReadOnlyList<ScoutingLegendEntry> Legend { get; } =
    [
        new(new ScoutingSwatch(ScoutingMarkShape.Badge, ScoutingColor.T), "letter = where the strat ends"),
        new(new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.T),
            "trail = its most typical round's T players, 1 s apart")
    ];

    /// <summary>The mined patterns, or null when there is no miner or nothing has been mined.</summary>
    public static IReadOnlyList<MinedPattern>? Patterns(ScoutingLayerContext context) =>
        context.Data.Sources.MinedPatterns?.Invoke();

    /// <summary>Why there are no patterns at all.</summary>
    public static ScoutingLayerResult NoPatterns(ScoutingLayerContext context) =>
        ScoutingLayerResult.Empty(context.Data.Sources.MinedPatterns is null
            ? ScoutingLayerResult.NotOnThisHost
            : "strats not mined yet: Find strats in the Strat Book's Detected inbox");

    /// <summary>The team's side in a member round.</summary>
    public static Func<MinedMember, int?> SideOf(ScoutingLayerContext context,
        Func<ScoutingLayerContext, MinedMember, int?>? sideOf) =>
        sideOf is not null
            ? m => sideOf(context, m)
            : m => context.Data.Sources.Teams.SideAtRound(m.DemoPath, context.TeamId, m.Round);

    /// <summary>
    ///     Gives a strat's letter badge, the last badge from <paramref name="from" /> on, a card with the strat's rows:
    ///     its record opening every round, its typical round to play, what it lost to.
    /// </summary>
    /// <param name="marks">The layer's marks.</param>
    /// <param name="from">Where the strat's trail starts in them.</param>
    /// <param name="letter">Its letter.</param>
    /// <param name="name">Its name.</param>
    /// <param name="rows">Its inspector rows.</param>
    public static void Pin(List<ScoutingMapMark> marks, int from, string letter, string name,
        List<ScoutingInspectorRow> rows)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(rows);
        for (int i = marks.Count - 1; i >= from; i--)
        {
            if (marks[i].Shape == ScoutingMarkShape.Badge)
            {
                marks[i] = marks[i] with
                {
                    Info = new ScoutingMarkInfo($"{letter} · {name}", [.. rows.Select((r, n) => n == 0 ? r with { Label = "Record" } : r)])
                };
                return;
            }
        }
    }

    public static string Letter(int index) => ((char)('A' + index)).ToString();

    /// <summary>"won 7 of 11", or "11 rounds" when no winner is known.</summary>
    public static string Record(ScoutedStrat strat) =>
        strat.Known > 0
            ? string.Create(CultureInfo.InvariantCulture, $"won {strat.Wins} of {strat.Known}")
            : ScoutingLayerBase.Plural(strat.Support, "round");

    /// <summary>A strat's row: its name, its record opening every round, a play button on its typical round.</summary>
    public static ScoutingInspectorRow Row(ScoutingLayerContext context, ScoutedStrat strat, string name,
        string letter, Ticks ticks) => new()
    {
        Label = name,
        Value = Record(strat),
        StarText = name,
        Badge = letter,
        BarPercent = strat.Known > 0 ? 100 * strat.WinRate : null,
        Swatch = ScoutingColor.T,
        Rounds = [.. InOrder(strat.Members).Select(ticks.Round)],
        OpenTitle = $"{context.Prefix} · {name}",
        Play = (strat.Representative.DemoPath, PlayTick(strat.Pattern.Kind, strat.Representative))
    };

    /// <summary>Rounds in the order Review plays them: by demo, then by round.</summary>
    public static IEnumerable<MinedMember> InOrder(IEnumerable<MinedMember> rounds) =>
        rounds.OrderBy(m => m.DemoPath, StringComparer.Ordinal).ThenBy(m => m.Round);

    /// <summary>Where 2D play lands: the take minus a lead for an execute, freeze end for a setup.</summary>
    public static int PlayTick(PatternKind kind, MinedMember m) =>
        kind == PatternKind.Execute
            ? Math.Max(m.FreezeEndTick, m.AnchorTick - ExecuteLeadSeconds * Math.Max(1, m.TickRate))
            : m.FreezeEndTick;

    /// <summary>
    ///     The typical round's T players from freeze end to the take (an execute) or to the end of the opening (a
    ///     setup), one dot per player per second, and the letter at where they end. The positions file is read when
    ///     the host has one; otherwise the pattern's medoid anchors stand in.
    /// </summary>
    public static IEnumerable<ScoutingMapMark> Trail(ScoutingLayerContext context, ScoutedStrat strat, string letter)
    {
        MinedMember rep = strat.Representative;
        int rate = Math.Max(1, rep.TickRate);
        int endTick = strat.Pattern.Kind == PatternKind.Execute
            ? rep.AnchorTick
            : rep.FreezeEndTick + SetupTrailSeconds * rate;
        List<IReadOnlyList<(float X, float Y, float Z)>> steps = Steps(context, rep, endTick);
        if (steps.Count == 0)
        {
            steps = [.. strat.Pattern.Medoid.Anchors.Select(a => (IReadOnlyList<(float, float, float)>)
                [.. a.Select(p => (p.X, p.Y, p.Z))])];
        }

        List<ScoutingMapMark> marks = [];
        for (int i = 0; i < steps.Count; i++)
        {
            float opacity = 0.25f + 0.55f * (i + 1) / steps.Count;
            marks.AddRange(steps[i].Select(p =>
                new ScoutingMapMark(p.X, p.Y, p.Z, ScoutingMarkShape.Dot, ScoutingColor.T, 2f, Opacity: opacity)));
        }

        if (steps.LastOrDefault(s => s.Count > 0) is { } last)
        {
            marks.Add(new ScoutingMapMark(last.Average(p => p.X), last.Average(p => p.Y),
                last.OrderBy(p => p.Z).ElementAt(last.Count / 2).Z, ScoutingMarkShape.Badge, ScoutingColor.T, 9f,
                letter));
        }

        return marks;
    }

    private static List<IReadOnlyList<(float X, float Y, float Z)>> Steps(ScoutingLayerContext context,
        MinedMember rep, int endTick)
    {
        if (context.Data.Positions.Read(rep.DemoPath, context.Map, rep.Sha256) is not { } positions
            || positions.Round(rep.Round) is not { } round)
        {
            return [];
        }

        HashSet<int> ct = [.. round.Ct];
        int last = Math.Min(round.Pos.Count - 1, positions.StepFor(round, endTick));
        List<IReadOnlyList<(float, float, float)>> steps = [];
        for (int step = 0; step <= last; step++)
        {
            steps.Add([.. round.At(step).Where(p => !ct.Contains(p.Slot)).Select(p => ((float)p.X, (float)p.Y, (float)p.Z))]);
        }

        return steps;
    }

    /// <summary>
    ///     The opponent CT's first grenade per member round (<see cref="StratScouting.Opener" />) from the Grenade
    ///     Index, read once for every demo the strats were run in. Null for a round whose demo has no grenade rows.
    /// </summary>
    public static Func<MinedMember, string?> CtOpeners(ScoutingLayerContext context, IEnumerable<ScoutedStrat> strats)
    {
        if (context.Data.Sources.Grenades is not { } grenades)
        {
            return _ => null;
        }

        HashSet<string> demos = new(strats.SelectMany(s => s.Members).Select(m => m.DemoPath),
            StringComparer.OrdinalIgnoreCase);
        ILookup<(string, int), IndexedGrenade> byRound = grenades
            .Rows(new GrenadeQuery(context.Map, ThrowerTeam: 3, DemoPaths: demos))
            .ToLookup(g => (g.Demo.Path.ToUpperInvariant(), g.Row.RoundNumber));
        return m =>
        {
            IEnumerable<IndexedGrenade> round = byRound[(m.DemoPath.ToUpperInvariant(), m.Round)];
            int rate = Math.Max(1, m.TickRate);
            return StratScouting.Opener(round.Select(g =>
                (g.Kind, g.LandingPlace,
                    ((g.Row.ReleaseTick > 0 ? g.Row.ReleaseTick : g.Row.SpawnTick) - m.FreezeEndTick) / (double)rate)));
        };
    }

    /// <summary>A member round as Review opens it: freeze end to the round's end, from Round Facts when it has them.</summary>
    public sealed class Ticks(ScoutingLayerContext context)
    {
        private readonly Dictionary<string, Dictionary<int, int>?> _ends = new(StringComparer.OrdinalIgnoreCase);

        public TendencyRound Round(MinedMember m)
        {
            int rate = Math.Max(1, m.TickRate);
            int end = EndOf(m) ?? Math.Max(m.AnchorTick, m.FreezeEndTick) + 30 * rate;
            return new TendencyRound(m.DemoPath, m.Sha256, m.Round, m.FreezeEndTick, Math.Max(end, m.FreezeEndTick),
                rate);
        }

        private int? EndOf(MinedMember m)
        {
            if (context.Data.Sources.RoundFacts is not { } facts)
            {
                return null;
            }

            if (!_ends.TryGetValue(m.DemoPath, out Dictionary<int, int>? ends))
            {
                ends = facts.TryGet(m.DemoPath)?.Rounds.Where(r => r.EndTick is not null)
                    .GroupBy(r => r.Number).ToDictionary(g => g.Key, g => g.First().EndTick!.Value);
                _ends[m.DemoPath] = ends;
            }

            return ends is not null && ends.TryGetValue(m.Round, out int tick) ? tick : null;
        }
    }
}
