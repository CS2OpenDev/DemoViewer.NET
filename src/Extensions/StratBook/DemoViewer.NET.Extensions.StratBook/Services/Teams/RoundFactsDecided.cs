#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>Whether a Round Facts row has a result.</summary>
public static class RoundFactsDecided
{
    /// <summary>
    ///     True when a side won the round. A recording that stops before the last round ends leaves a trailing row with
    ///     no winner; it is not a loss for either team and stays out of every win and loss count.
    /// </summary>
    /// <param name="round">The round.</param>
    public static bool IsDecided(this RoundFacts round)
    {
        ArgumentNullException.ThrowIfNull(round);
        return round.WinnerSide is 2 or 3;
    }
}
