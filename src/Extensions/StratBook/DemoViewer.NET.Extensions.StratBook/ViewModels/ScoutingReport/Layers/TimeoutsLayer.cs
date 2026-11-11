#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>One tactical timeout the team called.</summary>
/// <param name="Round">The round played straight after it.</param>
/// <param name="Side">The team's side in that round: 2 = T, 3 = CT.</param>
/// <param name="Won">The team won that round.</param>
/// <param name="Call">What they ran after it ("B plant"), or null when nothing names it.</param>
/// <param name="State">The score state before that round, or null when not known.</param>
/// <param name="LostInARow">Rounds the team had lost in a row going into the timeout.</param>
public sealed record TacticalTimeout(TendencyRound Round, int Side, bool Won, string? Call = null,
    ScoreState? State = null, int LostInARow = 0);

/// <summary>
///     Tactical timeouts (strip, map-wide): how many the team took per side, when (the score state, after a losing
///     run), the round after won, and the usual call after one; its mini is a CT | T table of the count and the
///     round after won, with the usual call in the caption. They come from the round captures
///     (gamerules timeout state), scoped to the team by the side it played in the round the timeout fell ahead of;
///     technical timeouts are counted apart. <see cref="TimeoutDerivation" /> does the scoping.
/// </summary>
public sealed class TimeoutsLayer() : ScoutingLayerBase("timeouts", "Timeouts", ScoutingLayerPlacement.Strip,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral), "D-rs")
{
    /// <summary>What the layer says when the demos have no captures.</summary>
    public const string NoCaptures = "needs round captures, which these demos do not have yet";

    /// <inheritdoc />
    public override string TitleFor(ScoutingSide side) => "Tactical timeouts";

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        TeamTimeouts? read = await EconCaptureReads.TimeoutsFor(context).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (read is null)
        {
            return context.Data.Sources.RoundFacts is null || context.Data.Sources.RoundIndex is null
                ? ScoutingLayerResult.Empty(ScoutingLayerResult.NotOnThisHost)
                : ScoutingLayerResult.Empty(NoCaptures);
        }

        if (context.Data.Captures.WithoutCount > 0 && read.Tactical.Count == 0 && read.Technical == 0)
        {
            return ScoutingLayerResult.Empty(NoCaptures);
        }

        return Build(context, Convert(read), read.Technical);
    }

    /// <summary>
    ///     The team's tactical timeouts on the scope's map, both sides, or null when the captures cannot be read on
    ///     this host.
    /// </summary>
    /// <param name="context">The scope.</param>
    public static async Task<IReadOnlyList<TacticalTimeout>?> TimeoutsFor(ScoutingLayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await EconCaptureReads.TimeoutsFor(context).ConfigureAwait(false) is { } read ? Convert(read) : null;
    }

    private static List<TacticalTimeout> Convert(TeamTimeouts read) =>
    [
        .. read.Tactical.Select(t => new TacticalTimeout(t.Round.Clip, t.Round.Side, t.Won, t.Call, t.State,
            t.LostInARow))
    ];

    /// <summary>The inspector for a list of timeouts: taken per side, when, the round after won, the usual call.</summary>
    /// <param name="context">The scope.</param>
    /// <param name="timeouts">The timeouts.</param>
    /// <param name="technical">Technical timeouts that fell before the team's rounds, left out of the list.</param>
    public static ScoutingLayerResult Build(ScoutingLayerContext context, IReadOnlyList<TacticalTimeout> timeouts,
        int technical = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeouts);
        if (timeouts.Count == 0)
        {
            return ScoutingLayerResult.Empty(technical > 0
                ? $"no tactical timeout on {context.Map} ({Plural(technical, "technical timeout")} not counted)"
                : $"no tactical timeout on {context.Map}");
        }

        string taken = string.Join(" · ", ((int[])[3, 2]).Select(s =>
            $"{StripData.SideLabel(s)} {timeouts.Count(t => t.Side == s)}"));
        List<ScoutingInspectorRow> rows =
        [
            new()
            {
                Label = "Taken",
                Value = Plural(timeouts.Count, "timeout"),
                StarText = "timeouts taken",
                Note = technical > 0 ? $"{taken} · {technical} technical not counted" : taken,
                Rounds = [.. timeouts.Select(t => t.Round)],
                OpenTitle = StripData.MapTitle(context, "rounds after a tactical timeout")
            },
            .. ByScoreRows(context, timeouts)
        ];
        List<TacticalTimeout> afterRun = [.. timeouts.Where(t => t.LostInARow >= TimeoutDerivation.LostRowMin)];
        if (afterRun.Count > 0)
        {
            rows.Add(StripData.Share($"After {TimeoutDerivation.LostRowMin}+ lost in a row",
                [.. afterRun.Select(t => t.Round)], timeouts.Count, "timeout after a losing run",
                StripData.MapTitle(context, "tactical timeout after a losing run")));
        }

        rows.Add(StripData.Share("Round after the timeout won", [.. timeouts.Where(t => t.Won).Select(t => t.Round)],
            timeouts.Count, "round after the timeout won", StripData.MapTitle(context, "won after a tactical timeout")));
        IGrouping<string, TacticalTimeout>? call = UsualCall(timeouts);
        if (call is not null)
        {
            rows.Add(StripData.Share($"Usual call after the timeout · {call.Key}", [.. call.Select(t => t.Round)],
                timeouts.Count, "usual call after the timeout",
                StripData.MapTitle(context, $"{call.Key} after a tactical timeout")));
        }

        // The mini table's cells, each opening its rounds: called and the round after won, per side.
        List<ScoutingInspectorRow> bySide = [];
        foreach (int side in (int[])[3, 2])
        {
            List<TacticalTimeout> here = [.. timeouts.Where(t => t.Side == side)];
            if (here.Count == 0)
            {
                continue;
            }

            string s = StripData.SideLabel(side);
            ScoutingColor color = StripData.SideColor(side);
            bySide.Add(new ScoutingInspectorRow
            {
                Label = $"{s} · called",
                Value = Plural(here.Count, "timeout"),
                StarText = $"{s} timeouts taken",
                Swatch = color,
                Rounds = [.. here.Select(t => t.Round)],
                OpenTitle = StripData.MapTitle(context, $"{s} rounds after a tactical timeout")
            });
            bySide.Add(StripData.Share($"{s} · round after won", [.. here.Where(t => t.Won).Select(t => t.Round)],
                here.Count, $"{s} round after the timeout won",
                StripData.MapTitle(context, $"{s} won after a tactical timeout"), color));
        }

        int won = timeouts.Count(t => t.Won);
        string run = afterRun.Count > 0
            ? $", {afterRun.Count} of them after losing {TimeoutDerivation.LostRowMin}+ in a row"
            : "";
        return new ScoutingLayerResult
        {
            CountLabel = Plural(timeouts.Count, "timeout"),
            Reads =
            [
                new ScoutingRead(
                    $"They call {Plural(timeouts.Count, "tactical timeout")} on {context.Map}{run}, and win {won} of the {Plural(timeouts.Count, "round")} after.",
                    "read timeouts", StripData.SuggestOn(context))
            ],
            Sections =
            [
                new ScoutingInspectorSection("Tactical timeouts", rows),
                new ScoutingInspectorSection("By side", bySide)
            ],
            MiniChart = Chart(timeouts)
        };
    }

    /// <summary>
    ///     The mini: per side, how many tactical timeouts they called and how many of the rounds after they won
    ///     ("2 of 3"), with the usual call after one in the caption.
    /// </summary>
    /// <param name="timeouts">The timeouts.</param>
    public static ScoutingTableChart Chart(IReadOnlyList<TacticalTimeout> timeouts)
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        int[] sides = [3, 2];
        IGrouping<string, TacticalTimeout>? call = UsualCall(timeouts);
        return new ScoutingTableChart("CT", "T",
        [
            new ScoutingTableRow("Called",
                [.. sides.Select(s => timeouts.Count(t => t.Side == s).ToString(CultureInfo.InvariantCulture))]),
            new ScoutingTableRow("Round after won",
            [
                .. sides.Select(s => timeouts.Count(t => t.Side == s) is var n and > 0
                    ? $"{timeouts.Count(t => t.Side == s && t.Won).ToString(CultureInfo.InvariantCulture)} of {n.ToString(CultureInfo.InvariantCulture)}"
                    : "—")
            ])
        ])
        {
            Caption = call is null
                ? "tactical timeouts the team called"
                : $"tactical timeouts the team called; usual call after one: {call.Key} ({call.Count()} of {timeouts.Count})"
        };
    }

    // The call they ran most often after a timeout, or null when none is named.
    private static IGrouping<string, TacticalTimeout>? UsualCall(IReadOnlyList<TacticalTimeout> timeouts) =>
        timeouts.Where(t => t.Call is not null).GroupBy(t => t.Call!, StringComparer.Ordinal).MaxBy(g => g.Count());

    /// <summary>The timeouts by the score state before the round after them.</summary>
    /// <param name="context">The scope.</param>
    /// <param name="timeouts">The timeouts.</param>
    public static List<ScoutingInspectorRow> ByScoreRows(ScoutingLayerContext context,
        IReadOnlyList<TacticalTimeout> timeouts)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeouts);
        List<ScoutingInspectorRow> rows = [];
        foreach (ScoreState state in Enum.GetValues<ScoreState>())
        {
            List<TacticalTimeout> here = [.. timeouts.Where(t => t.State == state)];
            if (here.Count > 0)
            {
                string label = ScoreDerivation.Label(state).ToLowerInvariant();
                rows.Add(StripData.Share($"Called when {label}", [.. here.Select(t => t.Round)], timeouts.Count,
                    $"timeout when {label}", StripData.MapTitle(context, $"tactical timeout, {label}")));
            }
        }

        return rows;
    }
}
