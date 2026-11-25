#region

using Cs2VideoGenerator.Core.Models;
using DemoViewer.NET.Services.LiveSync;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     <see cref="ReelJobService.CompilationTerminalStatus" />: the real reel's compilation result →
///     terminal chip status. CSVG's runner stops at the first failed clip and folds load failures and
///     cancellation into a Failed result carrying only the clips it started, so the retryable set has
///     to be computed against the request, not read off the results.
/// </summary>
[Category("Unit")]
public class ReelTerminalStatusTests
{
    private static ReelRequest Request(int clips) => new(
        [
            .. Enumerable.Range(0, clips).Select(i =>
                new ReelClip(i < clips / 2 ? "a.dem" : "b.dem", null, 1, "p", i * 1000, i * 1000 + 500, 64, $"c{i}"))
        ],
        "out", "reel", "mp4", 60, false, false, null, null, false, false);

    private static Cs2ClipResult Clip(int index, bool success) => new()
    {
        ClipIndex = index, Success = success, OutputFilePath = $"clip_{index}.mp4", Duration = TimeSpan.Zero
    };

    [Test]
    public async Task FailFastRun_RetriesTheFailedClip_AndEveryClipThatNeverStarted()
    {
        Cs2CompilationResult result = Cs2CompilationResult.Failed(
            [Clip(0, true), Clip(1, true), Clip(2, false)], "spectate timed out");

        ReelJobStatus status = ReelJobService.CompilationTerminalStatus(Request(10), result, false, "out");

        await Assert.That(status.Phase).IsEqualTo(ReelJobPhase.Failed);
        await Assert.That(status.ClipsCompleted).IsEqualTo(2);
        await Assert.That(status.FailedClipIndices)
            .IsEquivalentTo([2, 3, 4, 5, 6, 7, 8, 9], CollectionOrdering.Matching);
        await Assert.That(status.Error).Contains("spectate timed out");
    }

    [Test]
    public async Task DemoLoadFailure_LeavesEveryUncapturedClipRetryable_AndShowsTheError()
    {
        // Demo B failed to load after A's clips were captured: the result holds A's successes only.
        Cs2CompilationResult result = Cs2CompilationResult.Failed(
            [Clip(0, true), Clip(1, true)], "Demo file not found: b.dem");

        ReelJobStatus status = ReelJobService.CompilationTerminalStatus(Request(4), result, false, "out");

        await Assert.That(status.FailedClipIndices).IsEquivalentTo([2, 3], CollectionOrdering.Matching);
        await Assert.That(status.HasRetryableClips).IsTrue();
        await Assert.That(status.Error).Contains("Demo file not found: b.dem");
    }

    [Test]
    public async Task ReorderedRun_MatchesResultsByClipIndex_NotByPosition()
    {
        // The runner records grouped by demo and start tick, so a later clip can succeed first.
        Cs2CompilationResult result = Cs2CompilationResult.Failed([Clip(3, true), Clip(1, false)], "boom");

        ReelJobStatus status = ReelJobService.CompilationTerminalStatus(Request(4), result, false, "out");

        await Assert.That(status.ClipsCompleted).IsEqualTo(1);
        await Assert.That(status.FailedClipIndices).IsEquivalentTo([0, 1, 2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CancelledRun_IsReportedAsCancelled_NotFailed()
    {
        // The runner catches the cancellation into a Failed result instead of throwing.
        Cs2CompilationResult result = Cs2CompilationResult.Failed([Clip(0, true)], "The operation was canceled.");

        ReelJobStatus status = ReelJobService.CompilationTerminalStatus(Request(3), result, true, "out");

        await Assert.That(status.Phase).IsEqualTo(ReelJobPhase.Cancelled);
        await Assert.That(status.FailedClipIndices).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ACancelThatArrivesAfterTheRunSucceeded_IsStillCompleted_WithItsOutput()
    {
        // The token is read after the runner returns: a Cancel (or app exit) in that gap finds every clip
        // captured and the reel already on disk.
        Cs2CompilationResult result = Cs2CompilationResult.Succeeded([Clip(0, true), Clip(1, true)]);

        ReelJobStatus status = ReelJobService.CompilationTerminalStatus(Request(2), result, true, "out");

        await Assert.That(status.Phase).IsEqualTo(ReelJobPhase.Completed);
        await Assert.That(status.OutputPath).IsEqualTo("out");
        await Assert.That(status.FailedClipIndices).IsEmpty();
    }

    [Test]
    public async Task SuccessfulRun_IsCompleted_WithNothingToRetry()
    {
        Cs2CompilationResult result = Cs2CompilationResult.Succeeded([Clip(0, true), Clip(1, true)]);

        ReelJobStatus status = ReelJobService.CompilationTerminalStatus(Request(2), result, false, "out");

        await Assert.That(status.Phase).IsEqualTo(ReelJobPhase.Completed);
        await Assert.That(status.ClipsCompleted).IsEqualTo(2);
        await Assert.That(status.Error).IsNull();
        await Assert.That(status.OutputPath).IsEqualTo("out");
        await Assert.That(status.HasRetryableClips).IsFalse();
    }
}
