#region

using System.Diagnostics;
using DemoViewer.NET.Services.Startup;

#endregion

namespace DemoViewer.NET.AppTests.Startup;

/// <summary>
///     One desktop instance per config root: a second launch hands its demo to the first and exits, a relaunch
///     waits for the old process to let go, and an instance that never answers is not started beside. Each test
///     claims its own temporary root, so nothing here meets a real instance.
/// </summary>
public class SingleInstanceTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "dv-instance-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task ASecondLaunch_HandsItsDemoToTheFirst_AndDoesNotStart(CancellationToken cancellationToken)
    {
        string root = NewRoot();
        string demo = Path.Combine(Path.GetTempPath(), "replays", "match.dem");
        try
        {
            SingleInstanceClaim first = SingleInstance.Claim(root, null, TimeSpan.Zero);
            await Assert.That(first.Instance).IsNotNull();
            using SingleInstance running = first.Instance!;
            TaskCompletionSource<string?> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            running.Listen(path => received.TrySetResult(path));

            SingleInstanceClaim second = SingleInstance.Claim(root, demo, TimeSpan.FromSeconds(10));

            using (Assert.Multiple())
            {
                await Assert.That(second.HandedOff).IsTrue();
                await Assert.That(second.Instance).IsNull();
                await Assert.That(await received.Task.WaitAsync(cancellationToken)).IsEqualTo(demo);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task ALaunchWithNoDemo_StillBringsTheRunningWindowForward(CancellationToken cancellationToken)
    {
        string root = NewRoot();
        try
        {
            using SingleInstance running = SingleInstance.TryAcquire(root)!;
            TaskCompletionSource<string?> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            running.Listen(path => received.TrySetResult(path));

            SingleInstanceClaim second = SingleInstance.Claim(root, null, TimeSpan.FromSeconds(10));

            await Assert.That(second.HandedOff).IsTrue();
            await Assert.That(await received.Task.WaitAsync(cancellationToken)).IsNull();
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The safe-mode relaunch and an update's restart start while the old process is still finishing. The old one
    // has stopped answering by then; the new one waits for its lock instead of exiting or starting beside it.
    [Test]
    [Timeout(30_000)]
    public async Task ARelaunch_TakesTheLockOnceTheOldInstanceLetsGo(CancellationToken cancellationToken)
    {
        string root = NewRoot();
        try
        {
            SingleInstance old = SingleInstance.TryAcquire(root)!;
            Task release = Task.Run(async () =>
            {
                await Task.Delay(600, cancellationToken);
                old.Dispose();
            }, cancellationToken);

            Stopwatch waited = Stopwatch.StartNew();
            SingleInstanceClaim relaunch = SingleInstance.Claim(root, null, TimeSpan.FromSeconds(10));
            waited.Stop();
            await release;

            using (Assert.Multiple())
            {
                await Assert.That(relaunch.HandedOff).IsFalse();
                await Assert.That(relaunch.Instance).IsNotNull();
                await Assert.That(waited.Elapsed).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(400));
            }

            relaunch.Instance?.Dispose();
        }
        finally
        {
            Cleanup(root);
        }
    }

    // A held lock means a live process, since the OS lets go of it on exit. One that never answers is hung, and
    // starting beside it is the clobbering the lock exists to prevent, so the launch exits instead.
    [Test]
    [Timeout(30_000)]
    public async Task AnInstanceThatNeverAnswers_KeepsTheLaunchFromStartingBesideIt(CancellationToken cancellationToken)
    {
        string root = NewRoot();
        try
        {
            using SingleInstance stuck = SingleInstance.TryAcquire(root)!;

            SingleInstanceClaim claim = SingleInstance.Claim(root, null, TimeSpan.FromMilliseconds(600));

            using (Assert.Multiple())
            {
                await Assert.That(claim.HandedOff).IsFalse();
                await Assert.That(claim.Instance).IsNull();
                await Assert.That(claim.Blocked).IsTrue().Because("it exits rather than start without the guard");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The holder answers from the moment it takes the lock, however long the rest of its start takes; what a
    // launch sends before the window can take it waits for the handler.
    [Test]
    [Timeout(30_000)]
    public async Task ALaunchDuringTheHoldersStart_IsHandedOff_AndDeliveredOnceTheHandlerIsSet(
        CancellationToken cancellationToken)
    {
        string root = NewRoot();
        string demo = Path.Combine(Path.GetTempPath(), "replays", "early.dem");
        try
        {
            using SingleInstance running = SingleInstance.TryAcquire(root)!;
            running.Listen();

            SingleInstanceClaim second = SingleInstance.Claim(root, demo, TimeSpan.FromSeconds(10));
            TaskCompletionSource<string?> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            running.Listen(path => received.TrySetResult(path));

            using (Assert.Multiple())
            {
                await Assert.That(second.HandedOff).IsTrue();
                await Assert.That(await received.Task.WaitAsync(cancellationToken)).IsEqualTo(demo);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task TheLock_IsHeldUntilDisposed_AndIsPerRoot()
    {
        string root = NewRoot();
        string other = NewRoot();
        try
        {
            SingleInstance first = SingleInstance.TryAcquire(root)!;
            using SingleInstance? elsewhere = SingleInstance.TryAcquire(other);
            SingleInstance? whileHeld = SingleInstance.TryAcquire(root);
            first.Dispose();
            using SingleInstance? afterwards = SingleInstance.TryAcquire(root);

            using (Assert.Multiple())
            {
                await Assert.That(whileHeld).IsNull();
                await Assert.That(elsewhere).IsNotNull().Because("a run under another config root is another instance");
                await Assert.That(afterwards).IsNotNull();
                await Assert.That(SingleInstance.PipeName(root)).IsNotEqualTo(SingleInstance.PipeName(other));
            }
        }
        finally
        {
            Cleanup(root);
            Cleanup(other);
        }
    }

    // Any process of this user can write to the pipe: what it names is opened only as a full local .dem path.
    [Test]
    [Arguments("replays/match.dem")]
    [Arguments(@"\\attacker\share\match.dem")]
    [Arguments("//attacker/share/match.dem")]
    [Arguments(@"\??\UNC\attacker\share\match.dem")]
    [Arguments("")]
    public async Task AReceivedPath_ThatIsNotAFullLocalDemPath_OpensNothing(string message)
    {
        await Assert.That(SingleInstance.AcceptedDemoPath(message)).IsNull();
    }

    [Test]
    public async Task AReceivedFullDemPath_IsOpened_AndADemArgumentIsMadeFull()
    {
        string full = Path.Combine(Path.GetTempPath(), "match.dem");
        string notDem = Path.Combine(Path.GetTempPath(), "notes.txt");

        using (Assert.Multiple())
        {
            await Assert.That(SingleInstance.AcceptedDemoPath(full)).IsEqualTo(full);
            await Assert.That(SingleInstance.AcceptedDemoPath(notDem)).IsNull();
            await Assert.That(SingleInstance.DemoArgument(["--safe-mode", "match.dem"]))
                .IsEqualTo(Path.GetFullPath("match.dem"));
            await Assert.That(SingleInstance.DemoArgument(["--safe-mode"])).IsNull();
        }
    }
}
