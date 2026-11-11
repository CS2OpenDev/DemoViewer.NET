#region

using DemoViewer.NET.Extensions.Sdk.Review;
using DemoViewer.NET.Extensions.StratBook.Modules.Review;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Sends the rounds behind a Scouting Report number to the Review Queue under one title card naming the team
///     and what the number counts, then shows the Review tab. Rounds already queued are skipped. Every clip is
///     filed under <see cref="ReviewSources.ScoutingReport" /> and carries the team.
/// </summary>
/// <param name="review">The Review Queue; null says so on open.</param>
/// <param name="selectTab">Shows a tab by id; null stays on the Scouting Report.</param>
public sealed class ScoutingReviewOpener(IExtensionReviewQueue? review, Func<string, bool>? selectTab)
{
    /// <summary>The clip for one round behind a number: its note is the number's title and the round.</summary>
    /// <param name="title">What the number counts.</param>
    /// <param name="round">The round.</param>
    /// <param name="teamId">The scouting report's team.</param>
    public static ReviewClip ClipFor(string title, TendencyRound round, Guid? teamId)
    {
        ArgumentNullException.ThrowIfNull(round);
        return ReviewClip.Clip(round.DemoPath, round.FromTick, round.ToTick, $"{title} · round {round.RoundNumber}",
            ReviewSources.ScoutingReport, round.TickRate, round.Sha256, teamId);
    }

    /// <summary>Sends the rounds and returns the line to show: "6 rounds sent to Review", or why not.</summary>
    /// <param name="teamName">The team, for the title card.</param>
    /// <param name="teamId">The team, carried on every clip.</param>
    /// <param name="rounds">The rounds.</param>
    /// <param name="title">What the number counts.</param>
    public string Open(string teamName, Guid? teamId, IReadOnlyList<TendencyRound> rounds, string title)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        if (rounds.Count == 0)
        {
            return "";
        }

        if (review is null)
        {
            return "no Review Queue on this host";
        }

        int added = review.Add(rounds.Select(r => ClipFor(title, r, teamId)),
            $"Scouting report · {teamName} · {title}", ScoutingLayerBase.Plural(rounds.Count, "round"));
        selectTab?.Invoke(ReviewQueueModule.TabId);
        return added == 0 ? "already in Review" : $"{ScoutingLayerBase.Plural(added, "round")} sent to Review";
    }
}
