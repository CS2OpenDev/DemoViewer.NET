#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.ViewModels.EntityTracking;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A seek a newer one has superseded must stand down quietly: no failure on the status line, and no
///     clearing the debugger's suppression while the newer seek is still replaying.
/// </summary>
[NotInParallel]
public class EntitySeekSupersedeTests
{
    /// <summary>
    ///     Cancelling a seek does not stop a replay already running, so the older seek's finally ran
    ///     while the newer one was mid-replay, and the shell's OnSeekFinally re-armed breakpoints the
    ///     newer seek had been started with suppressed.
    /// </summary>
    [Test]
    public async Task ASupersededSeekFinishingLate_DoesNotRunOnSeekFinally()
    {
        using SemaphoreSlim enteredA = new(0);
        using SemaphoreSlim enteredB = new(0);
        using SemaphoreSlim releaseA = new(0);
        using SemaphoreSlim releaseB = new(0);
        int factoryCalls = 0;
        int finallies = 0;

        EntityTrackingTabViewModel vm = new()
        {
            FrameSource = () => [],
            SeekService = new EntitySeekService(() =>
            {
                // Each replay parks until released and then ends as a cancelled one, so the test needs
                // no real frames.
                bool first = Interlocked.Increment(ref factoryCalls) == 1;
                (first ? enteredA : enteredB).Release();
                (first ? releaseA : releaseB).Wait(TimeSpan.FromSeconds(30));
                throw new OperationCanceledException();
            }),
            OnSeekFinally = () => Interlocked.Increment(ref finallies)
        };

        Task a = vm.SeekEntitiesAsync(5);
        await Assert.That(await enteredA.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();

        Task b = vm.SeekEntitiesAsync(6);
        await Assert.That(await enteredB.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();

        releaseA.Release();
        await a.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(Volatile.Read(ref finallies)).IsEqualTo(0)
            .Because("the superseded seek must not re-arm breakpoints under the newer one");

        releaseB.Release();
        await b.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(Volatile.Read(ref finallies)).IsEqualTo(1);
    }
}
