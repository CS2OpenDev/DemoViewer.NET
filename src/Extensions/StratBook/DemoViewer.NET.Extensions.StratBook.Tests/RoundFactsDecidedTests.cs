#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>A round with a winner is decided; a recording cut off mid-round leaves one with none.</summary>
public class RoundFactsDecidedTests
{
    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task OnlyATOrCtWinner_IsDecided(int winner, bool decided)
    {
        RoundFacts round = new() { WinnerSide = winner };

        await Assert.That(round.IsDecided()).IsEqualTo(decided);
    }
}
