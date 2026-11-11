#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>One tactical timeout the team called, with the round played straight after it.</summary>
/// <param name="Round">The round after the timeout, in the team's own side.</param>
/// <param name="Timeout">The stored timeout.</param>
/// <param name="State">The score state before that round.</param>
/// <param name="LostInARow">Rounds the team had lost in a row going into the timeout.</param>
/// <param name="Call">What the team did in the round after, or null when nothing names it.</param>
public sealed record TeamTimeout(TeamRound Round, RoundTimeout Timeout, ScoreState State, int LostInARow, string? Call)
{
    public bool Won => Round.Won;
}

/// <summary>The team's timeouts on a map: tactical ones with their context, and how many technical ones fell before its rounds.</summary>
/// <param name="Tactical">The tactical timeouts the team called.</param>
/// <param name="Technical">Technical timeouts before the team's rounds, either side's; never in <paramref name="Tactical" />.</param>
public sealed record TeamTimeouts(IReadOnlyList<TeamTimeout> Tactical, int Technical);

/// <summary>
///     The Timeouts strip's numbers over a team's rounds and a demo's stored timeouts. A timeout belongs to the team
///     when it was called by the side the team played in the round it fell ahead of; technical timeouts are
///     counted apart and never read as a call. The call after a timeout is what the team did in that next round:
///     on T the site it planted or "no plant"; on CT nothing names it, so none is given.
/// </summary>
public static class TimeoutDerivation
{
    /// <summary>Rounds lost in a row that make a timeout "after a losing run".</summary>
    public const int LostRowMin = 3;

    /// <summary>The team's timeouts.</summary>
    /// <param name="rounds">The team's rounds on one map.</param>
    /// <param name="timeoutsOf">A demo path's stored timeouts, or null when it has none read.</param>
    public static TeamTimeouts Build(IEnumerable<TeamRound> rounds, Func<string, IReadOnlyList<RoundTimeout>?> timeoutsOf)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(timeoutsOf);
        List<TeamTimeout> tactical = [];
        int technical = 0;
        foreach (IGrouping<string, TeamRound> demo in rounds.GroupBy(r => r.DemoPath, StringComparer.Ordinal))
        {
            if (timeoutsOf(demo.Key) is not { Count: > 0 } timeouts)
            {
                continue;
            }

            Dictionary<int, TeamRound> byNumber = [];
            foreach (TeamRound round in demo)
            {
                byNumber.TryAdd(round.Number, round);
            }

            foreach (RoundTimeout timeout in timeouts.OrderBy(t => t.StartTick))
            {
                if (timeout.BeforeRound is not { } n || !byNumber.TryGetValue(n, out TeamRound? round))
                {
                    continue;
                }

                if (timeout.Technical || timeout.Side is not (2 or 3))
                {
                    technical++;
                    continue;
                }

                if (timeout.Side == round.Side)
                {
                    tactical.Add(new TeamTimeout(round, timeout, ScoreDerivation.StateOf(round),
                        LostRunBefore(byNumber, n), CallOf(round)));
                }
            }
        }

        return new TeamTimeouts(tactical, technical);
    }

    /// <summary>Rounds the team lost in a row straight before a round number.</summary>
    /// <param name="byNumber">The demo's team rounds.</param>
    /// <param name="number">The round after the timeout.</param>
    public static int LostRunBefore(IReadOnlyDictionary<int, TeamRound> byNumber, int number)
    {
        ArgumentNullException.ThrowIfNull(byNumber);
        int run = 0;
        for (int k = number - 1; k >= 1 && byNumber.TryGetValue(k, out TeamRound? r) && r.Lost; k--)
        {
            run++;
        }

        return run;
    }

    /// <summary>"A plant", "B plant" or "no plant" for a T round; null on CT, where nothing names the call.</summary>
    /// <param name="round">The round after the timeout.</param>
    public static string? CallOf(TeamRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.Side != 2)
        {
            return null;
        }

        return round.Facts.PlantTick is null ? "no plant"
            : round.Facts.PlantSite switch
            {
                BombSite.A => "A plant",
                BombSite.B => "B plant",
                _ => null
            };
    }
}
