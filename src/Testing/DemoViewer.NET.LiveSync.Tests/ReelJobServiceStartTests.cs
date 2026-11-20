#region

using DemoViewer.NET.Services;
using DemoViewer.NET.Services.LiveSync;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     <see cref="ReelJobService.Start" />'s one-job-at-a-time guard. The job is held inside
///     <see cref="HeavyJobGate.EnterReelSessionAsync" /> by a background parse that never finishes,
///     so it never reaches the CSVG host or port 50051, and is then cancelled.
/// </summary>
[Category("Unit")]
public class ReelJobServiceStartTests
{
    [Test]
    [Timeout(30_000)]
    public async Task Start_IsRunningOnReturn_SoASecondStartIsRefused(CancellationToken cancellationToken)
    {
        using HeavyJobGate gate = new();
        IDisposable parse = await gate.AcquireBackgroundAsync(cancellationToken);
        using ReelJobService reel = new(null, gate, null);
        ReelRequest request = new(
            [new ReelClip("a.dem", null, 1, "p", 0, 500, 64, "clip")],
            Path.GetTempPath(), "reel", "mp4", 60, false, false, null, null, false, true);

        bool runningOnReturn;
        bool secondRefused = false;
        try
        {
            reel.Start(request);
            runningOnReturn = reel.Status.IsRunning;
            if (runningOnReturn)
            {
                try
                {
                    reel.Start(request);
                }
                catch (InvalidOperationException)
                {
                    secondRefused = true;
                }
            }
        }
        finally
        {
            await reel.CancelAsync();
            parse.Dispose();
        }

        await Assert.That(runningOnReturn).IsTrue()
            .Because("the guard reads Status, so Start must publish StartingSession before returning");
        await Assert.That(secondRefused).IsTrue();
        await Assert.That(reel.Status.Phase).IsEqualTo(ReelJobPhase.Cancelled);
        await Assert.That(gate.IsReelActive).IsFalse();
    }
}
