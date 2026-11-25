#region

using CS2DemoKit.Parser;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.DemoTrimmer.Tests;

/// <summary>
///     Where the seating is sampled. The synthesized <c>player_team</c> events state who is on which
///     side when the trimmed file begins, so the sample has to come from the window and not from the
///     source's first round: the two differ by a side swap once the window starts after halftime.
/// </summary>
public sealed class TeamEventSynthesizerTests
{
    private static ParsedDemo TwoHalves() => SyntheticDemo.Build(events:
    [
        TestGameEvents.RoundFreezeEnd(frameNumber: 100, serverTick: 100, gameTick: 100),
        TestGameEvents.RoundFreezeEnd(frameNumber: 5000, serverTick: 5000, gameTick: 5000)
    ]).Demo;

    [Test]
    public async Task AWindowEnteredAfterTheFirstRound_SamplesItsOwnFirstFreezeEnd() =>
        await Assert.That(TeamEventSynthesizer.SampleFrame(TwoHalves(), 3000)).IsEqualTo(5000);

    [Test]
    public async Task AWindowFromFrameZero_SamplesTheSourcesFirstFreezeEnd() =>
        await Assert.That(TeamEventSynthesizer.SampleFrame(TwoHalves(), 0)).IsEqualTo(100);

    [Test]
    public async Task AFreezeEndOnTheEntryFrameItself_Counts() =>
        await Assert.That(TeamEventSynthesizer.SampleFrame(TwoHalves(), 5000)).IsEqualTo(5000);

    [Test]
    public async Task NoFreezeEndFromTheEntryOnward_SamplesNothing() =>
        await Assert.That(TeamEventSynthesizer.SampleFrame(TwoHalves(), 5001)).IsEqualTo(-1);
}
