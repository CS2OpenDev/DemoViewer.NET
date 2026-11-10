#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>One tactical timeout the team called.</summary>
/// <param name="Round">The round played straight after it.</param>
/// <param name="Side">The team's side in that round: 2 = T, 3 = CT.</param>
/// <param name="Won">The team won that round.</param>
/// <param name="Call">What they ran after it ("B execute"), or null when nothing names it.</param>
public sealed record TacticalTimeout(TendencyRound Round, int Side, bool Won, string? Call = null);

/// <summary>
///     Tactical timeouts (strip, companion of By score): how many the team took, the round after won, and the
///     usual call after one. The timeouts come from the capture lane (P3.1, gamerules timeout state); until it
///     lands <see cref="TimeoutsFor" /> answers null and the layer is a placeholder.
///     <para>
///         <b>Seam:</b> P3.1 overrides nothing here; it replaces the body of <see cref="TimeoutsFor" /> with a read
///         of its service for the scope's map, and <see cref="Build" /> turns the list into the inspector.
///     </para>
/// </summary>
public sealed class TimeoutsLayer() : ScoutingLayerBase("timeouts", "Timeouts", ScoutingLayerPlacement.Strip,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral), "D-rs")
{
    /// <summary>What the placeholder says until the capture lands.</summary>
    public const string Waiting = "tactical timeouts come with the rules capture (P3.1): gamerules timeout state";

    /// <inheritdoc />
    public override string TitleFor(ScoutingSide side) => "Tactical timeouts";

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<TacticalTimeout>? timeouts = await TimeoutsFor(context).ConfigureAwait(false);
        return timeouts is null ? ScoutingLayerResult.Placeholder("P3.1", Waiting) : Build(context, timeouts);
    }

    /// <summary>
    ///     The team's tactical timeouts on the scope's map, both sides, or null while nothing captures them. The
    ///     seam the P3.1 capture fills.
    /// </summary>
    /// <param name="context">The scope.</param>
    public static Task<IReadOnlyList<TacticalTimeout>?> TimeoutsFor(ScoutingLayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult<IReadOnlyList<TacticalTimeout>?>(null);
    }

    /// <summary>The inspector for a list of timeouts: taken per side, the round after won, the usual call.</summary>
    /// <param name="context">The scope.</param>
    /// <param name="timeouts">The timeouts.</param>
    public static ScoutingLayerResult Build(ScoutingLayerContext context, IReadOnlyList<TacticalTimeout> timeouts)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeouts);
        if (timeouts.Count == 0)
        {
            return ScoutingLayerResult.Empty($"no tactical timeout on {context.Map}");
        }

        List<ScoutingInspectorRow> rows =
        [
            new()
            {
                Label = "Taken",
                Value = Plural(timeouts.Count, "timeout"),
                StarText = "timeouts taken",
                Note = string.Join(" · ", ((int[])[3, 2]).Select(s =>
                    $"{StripData.SideLabel(s)} {timeouts.Count(t => t.Side == s)}")),
                Rounds = [.. timeouts.Select(t => t.Round)],
                OpenTitle = StripData.MapTitle(context, "rounds after a tactical timeout")
            },
            StripData.Share("Round after the timeout won", [.. timeouts.Where(t => t.Won).Select(t => t.Round)],
                timeouts.Count, "round after the timeout won",
                StripData.MapTitle(context, "won after a tactical timeout"))
        ];
        if (timeouts.Where(t => t.Call is not null).GroupBy(t => t.Call!, StringComparer.Ordinal)
                .MaxBy(g => g.Count()) is { } call)
        {
            rows.Add(StripData.Share($"Usual call after the timeout · {call.Key}", [.. call.Select(t => t.Round)],
                timeouts.Count, "usual call after the timeout",
                StripData.MapTitle(context, $"{call.Key} after a tactical timeout")));
        }

        return new ScoutingLayerResult
        {
            CountLabel = Plural(timeouts.Count, "timeout"),
            Sections = [new ScoutingInspectorSection("Tactical timeouts", rows)]
        };
    }
}
