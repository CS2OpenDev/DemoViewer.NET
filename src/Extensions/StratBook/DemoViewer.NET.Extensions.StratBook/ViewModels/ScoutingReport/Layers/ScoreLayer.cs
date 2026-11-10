#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     By score (strip, map-wide): rounds won per side when leading by 3+, within 2 and trailing by 3+ (the score
///     before the round, captured from the server since R2), on and facing match point, and what changes with the
///     score: the force-buy rate and which site the T side plants. <see cref="ScoreDerivation" /> over Round
///     Facts. Every number opens its rounds; the buy filter does not apply (the force rate is one of the answers).
/// </summary>
public sealed class ScoreLayer() : ScoutingLayerBase("score", "By score", ScoutingLayerPlacement.Strip,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral), "D")
{
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
        List<ScoutingTableRow> table = [];
        foreach (ScoreState state in states)
        {
            string label = ScoreDerivation.Label(state);
            List<string> cells = [];
            foreach (int side in (int[])[3, 2])
            {
                List<TeamRound> here = [.. rounds.Where(r => r.Side == side && ScoreDerivation.StateOf(r) == state)];
                string s = StripData.SideLabel(side);
                cells.Add(here.Count > 0 ? Percent((double)here.Count(r => r.Won) / here.Count) : "–");
                if (here.Count > 0)
                {
                    byState.Add(StripData.Share($"{label} · {s}", [.. here.Where(r => r.Won).Select(r => r.Clip)],
                        here.Count, $"{label.ToLowerInvariant()} {s}",
                        StripData.MapTitle(context, $"{s} {label.ToLowerInvariant()}"), StripData.SideColor(side),
                        "rounds won"));
                }
            }

            table.Add(new ScoutingTableRow(label, cells));
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
            MiniChart = new ScoutingTableChart("CT", "T", table)
            {
                Caption = "rounds won, by the score before the round"
            }
        };
    }

    /// <summary>The read: how much their win rate moves between leading and trailing, both sides together.</summary>
    /// <param name="rounds">The rounds.</param>
    public static string? Read(IReadOnlyList<TeamRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        List<TeamRound> leading = [.. rounds.Where(r => ScoreDerivation.StateOf(r) == ScoreState.Leading)];
        List<TeamRound> trailing = [.. rounds.Where(r => ScoreDerivation.StateOf(r) == ScoreState.Trailing)];
        if (leading.Count == 0 || trailing.Count == 0)
        {
            return null;
        }

        double lead = (double)leading.Count(r => r.Won) / leading.Count;
        double trail = (double)trailing.Count(r => r.Won) / trailing.Count;
        return $"They win {Percent(lead)} of rounds when leading by {ScoreDerivation.Margin}+ and {Percent(trail)} when trailing by {ScoreDerivation.Margin}+.";
    }

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
