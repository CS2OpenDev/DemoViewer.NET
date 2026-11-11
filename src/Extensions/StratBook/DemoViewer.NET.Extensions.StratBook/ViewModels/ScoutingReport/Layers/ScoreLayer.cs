#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     By score (strip, map-wide): round win % per side overall and when leading by 3+, within 2 and trailing by 3+
///     (the score before the round, captured from the server since R2), each against the side's overall; on and
///     facing match point; and what changes with the score: the force-buy rate and which site the T side plants.
///     A state with fewer than <see cref="ScoreDerivation.MinRounds" /> rounds on a side shows "—" in the mini.
///     <see cref="ScoreDerivation" /> over Round Facts. Every number opens its rounds; the buy filter does not apply
///     (the force rate is one of the answers). Tactical timeouts are their own strip layer (<see cref="TimeoutsLayer" />).
/// </summary>
public sealed class ScoreLayer() : ScoutingLayerBase("score", "By score", ScoutingLayerPlacement.Strip,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral), "D")
{
    /// <summary>The mini table's first row: every round on the side.</summary>
    public const string OverallRow = "Overall";

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        TeamRoundSet? set = await StripData.RoundsFor(context.Data).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TeamRound> rounds = set?.On(context.Map) ?? [];
        if (rounds.Count == 0)
        {
            return StripData.Missing(context, set);
        }

        ScoreState[] states = Enum.GetValues<ScoreState>();
        List<ScoutingInspectorRow> byState = [];
        foreach (int side in (int[])[3, 2])
        {
            List<TeamRound> all = [.. rounds.Where(r => r.Side == side)];
            if (all.Count > 0)
            {
                string s = StripData.SideLabel(side);
                byState.Add(StripData.Share($"{OverallRow} · {s}", [.. all.Where(r => r.Won).Select(r => r.Clip)],
                    all.Count, $"overall {s}", StripData.MapTitle(context, $"{s} rounds won"),
                    StripData.SideColor(side), "rounds won"));
            }
        }

        foreach (ScoreState state in states)
        {
            string label = ScoreDerivation.Label(state);
            foreach (int side in (int[])[3, 2])
            {
                List<TeamRound> all = [.. rounds.Where(r => r.Side == side)];
                List<TeamRound> here = [.. all.Where(r => ScoreDerivation.StateOf(r) == state)];
                if (here.Count == 0)
                {
                    continue;
                }

                string s = StripData.SideLabel(side);
                string note = here.Count < ScoreDerivation.MinRounds
                    ? $"under {ScoreDerivation.MinRounds} rounds, not compared · rounds won"
                    : $"{Signed(WinPercent(here) - WinPercent(all))} vs {WinPercent(all)}% overall · rounds won";
                byState.Add(StripData.Share($"{label} · {s}", [.. here.Where(r => r.Won).Select(r => r.Clip)],
                    here.Count, $"{label.ToLowerInvariant()} {s}",
                    StripData.MapTitle(context, $"{s} {label.ToLowerInvariant()}"), StripData.SideColor(side), note));
            }
        }

        List<ScoutingInspectorRow> matchPoint = [];
        AddMatchPoint(context, matchPoint, [.. rounds.Where(ScoreDerivation.OnMatchPoint)], "On match point");
        AddMatchPoint(context, matchPoint, [.. rounds.Where(ScoreDerivation.FacingMatchPoint)], "Facing match point");

        List<ScoutingInspectorRow> changes = [];
        foreach (ScoreState state in states)
        {
            IReadOnlyList<TeamRound> buying =
                EconomyDerivation.Buying(rounds.Where(r => ScoreDerivation.StateOf(r) == state));
            if (buying.Count > 0)
            {
                string label = ScoreDerivation.Label(state).ToLowerInvariant();
                changes.Add(StripData.Share($"Force-buy rate · {label}",
                    [.. EconomyDerivation.Forces(buying).Select(r => r.Clip)], buying.Count, $"force rate {label}",
                    StripData.MapTitle(context, $"force buys {label}")));
            }
        }

        foreach (ScoreState state in states)
        {
            List<TeamRound> plants =
            [
                .. rounds.Where(r => r.Side == 2 && ScoreDerivation.StateOf(r) == state
                                                 && r.Facts.PlantSite is BombSite.A or BombSite.B)
            ];
            if (plants.Count > 0)
            {
                string label = ScoreDerivation.Label(state).ToLowerInvariant();
                changes.Add(StripData.Share($"T plants on A · {label}",
                    [.. plants.Where(r => r.Facts.PlantSite == BombSite.A).Select(r => r.Around(r.Facts.PlantTick ?? r.Facts.FreezeEndTick))],
                    plants.Count, $"plants on A {label}", StripData.MapTitle(context, $"T plants on A {label}"),
                    ScoutingColor.T, "the rest on B"));
            }
        }

        List<ScoutingInspectorSection> sections =
        [
            new("By score before the round", byState),
            new("Match point", matchPoint),
            new("What changes with the score", changes)
        ];
        return new ScoutingLayerResult
        {
            CountLabel = Plural(rounds.Count, "round"),
            Reads = Read(rounds) is { } read ? [new ScoutingRead(read, Suggested: StripData.SuggestOn(context))] : [],
            Sections = [.. sections.Where(s => s.Rows.Count > 0)],
            MiniChart = Chart(rounds)
        };
    }

    /// <summary>
    ///     The mini: round win % and its rounds per side, overall and by the score before the round, "58% (24)";
    ///     "—" for a cell with fewer than <see cref="ScoreDerivation.MinRounds" /> rounds.
    /// </summary>
    /// <param name="rounds">The rounds.</param>
    public static ScoutingTableChart Chart(IReadOnlyList<TeamRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        List<ScoutingTableRow> table =
        [
            new(OverallRow, [.. ((int[])[3, 2]).Select(side => Cell([.. rounds.Where(r => r.Side == side)]))])
        ];
        foreach (ScoreState state in Enum.GetValues<ScoreState>())
        {
            table.Add(new ScoutingTableRow(ScoreDerivation.Label(state),
            [
                .. ((int[])[3, 2]).Select(side =>
                    Cell([.. rounds.Where(r => r.Side == side && ScoreDerivation.StateOf(r) == state)]))
            ]));
        }

        return new ScoutingTableChart("CT", "T", table)
        {
            Caption = $"round win % (rounds) by the score before the round; — = under {ScoreDerivation.MinRounds} rounds"
        };
    }

    /// <summary>"58% (24)", or "—" under <see cref="ScoreDerivation.MinRounds" /> rounds.</summary>
    /// <param name="rounds">The rounds of one cell.</param>
    public static string Cell(IReadOnlyList<TeamRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        return rounds.Count < ScoreDerivation.MinRounds
            ? "—"
            : $"{Percent((double)rounds.Count(r => r.Won) / rounds.Count)} ({rounds.Count})";
    }

    /// <summary>
    ///     The read: the score state and side whose round win % moves furthest from that side's overall, among
    ///     states with at least <see cref="ScoreDerivation.MinRounds" /> rounds. Null when none has that many.
    /// </summary>
    /// <param name="rounds">The rounds.</param>
    public static string? Read(IReadOnlyList<TeamRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        (ScoreState State, int Side, int Rate, int Overall)? best = null;
        foreach (int side in (int[])[3, 2])
        {
            List<TeamRound> all = [.. rounds.Where(r => r.Side == side)];
            if (all.Count < ScoreDerivation.MinRounds)
            {
                continue;
            }

            int overall = WinPercent(all);
            foreach (ScoreState state in Enum.GetValues<ScoreState>())
            {
                List<TeamRound> here = [.. all.Where(r => ScoreDerivation.StateOf(r) == state)];
                if (here.Count < ScoreDerivation.MinRounds)
                {
                    continue;
                }

                int rate = WinPercent(here);
                if (best is not { } b || Math.Abs(rate - overall) > Math.Abs(b.Rate - b.Overall))
                {
                    best = (state, side, rate, overall);
                }
            }
        }

        if (best is not { } top)
        {
            return null;
        }

        int delta = top.Rate - top.Overall;
        if (delta == 0)
        {
            return "Their round win % does not move with the score on either side.";
        }

        string lead = top.State == ScoreState.Within
            ? $"With the score within {ScoreDerivation.Margin - 1}"
            : ScoreDerivation.Label(top.State);
        return $"{lead}, their {StripData.SideLabel(top.Side)} win rate {(delta > 0 ? "rises" : "falls")} to {top.Rate}% ({Signed(delta)} vs their {top.Overall}% overall).";
    }

    // Round win %, rounded the way Percent rounds it, so a difference of two matches the numbers shown.
    private static int WinPercent(List<TeamRound> rounds) => rounds.Count == 0
        ? 0
        : (int)Math.Round(100.0 * rounds.Count(r => r.Won) / rounds.Count, MidpointRounding.AwayFromZero);

    // "+15", "−22" (a true minus sign), "±0".
    private static string Signed(int points) => points switch
    {
        > 0 => $"+{points}",
        < 0 => $"−{-points}",
        _ => "±0"
    };

    private static void AddMatchPoint(ScoutingLayerContext context, List<ScoutingInspectorRow> rows,
        IReadOnlyList<TeamRound> rounds, string label)
    {
        if (rounds.Count > 0)
        {
            rows.Add(StripData.Share(label, [.. rounds.Where(r => r.Won).Select(r => r.Clip)], rounds.Count,
                label.ToLowerInvariant(), StripData.MapTitle(context, label.ToLowerInvariant()),
                note: "rounds won · regulation only"));
        }
    }
}
