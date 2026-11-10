namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>The team's score against the opponent's before a round.</summary>
public enum ScoreState
{
    /// <summary>Ahead by <see cref="ScoreDerivation.Margin" /> or more.</summary>
    Leading,

    /// <summary>Within <see cref="ScoreDerivation.Margin" /> - 1 either way.</summary>
    Within,

    /// <summary>Behind by <see cref="ScoreDerivation.Margin" /> or more.</summary>
    Trailing
}

/// <summary>
///     The By score strip's numbers: the score state before each round (from Round Facts' <c>ScoreBefore</c>,
///     captured from the server since R2), and match point. Match point is one team at half the regulation rounds
///     (12 in MR12); it never fires in overtime (R9).
/// </summary>
public static class ScoreDerivation
{
    /// <summary>A lead or deficit of this many rounds or more is leading or trailing.</summary>
    public const int Margin = 3;

    /// <summary>The state before a round.</summary>
    /// <param name="own">The team's score.</param>
    /// <param name="enemy">The opponent's.</param>
    public static ScoreState StateOf(int own, int enemy) =>
        own - enemy >= Margin ? ScoreState.Leading
        : enemy - own >= Margin ? ScoreState.Trailing
        : ScoreState.Within;

    /// <summary>The state before a round for the team.</summary>
    /// <param name="round">The round.</param>
    public static ScoreState StateOf(TeamRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        return StateOf(round.Own.ScoreBefore, round.Enemy.ScoreBefore);
    }

    /// <summary>"Leading by 3+", "Within 2", "Trailing by 3+".</summary>
    /// <param name="state">The state.</param>
    public static string Label(ScoreState state) => state switch
    {
        ScoreState.Leading => $"Leading by {Margin}+",
        ScoreState.Trailing => $"Trailing by {Margin}+",
        _ => $"Within {Margin - 1}"
    };

    /// <summary>The team is one round from winning the map in regulation.</summary>
    /// <param name="round">The round.</param>
    public static bool OnMatchPoint(TeamRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        return round.Own.ScoreBefore == Half(round) && round.Enemy.ScoreBefore < Half(round);
    }

    /// <summary>The opponent is one round from winning the map in regulation.</summary>
    /// <param name="round">The round.</param>
    public static bool FacingMatchPoint(TeamRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        return round.Enemy.ScoreBefore == Half(round) && round.Own.ScoreBefore < Half(round);
    }

    private static int Half(TeamRound round) => round.Own.Thresholds.RegulationRounds / 2;
}
