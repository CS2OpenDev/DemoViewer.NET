#region

using CS2DemoKit.Parser;
using DemoViewer.NET.AppTests.Extensions;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Transfer;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     A transfer as a queue item: a user-requested, non-preemptible <see cref="QueueJobKind.DataTransfer" /> job
///     whose progress reaches both the queue row and the caller, that only an import marks serial, and that
///     cancels from either side.
/// </summary>
public class TransferQueueTests
{
    private static ParsedDemo SyntheticDemo() => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 0, 1f / 64, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown);

    private static DemoProcessingQueue NewQueue(HeavyJobGate gate) =>
        new(gate, a => a(), _ => SyntheticDemo(), _ => SyntheticDemo(), () => Task.CompletedTask);

    private static async Task<bool> EndsCancelledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            return false;
        }
        catch (TaskCanceledException)
        {
            return true;
        }
    }

    [Test]
    [Arguments(false, null)]
    [Arguments(true, TransferQueue.SerialKey)]
    public async Task TheRequest_IsAUserRequestedDataTransfer_ThatCannotBePreempted(bool serial, string? serialKey)
    {
        UpdateFixtures.RecordingQueue queue = new();
        int result = await TransferQueue.RunAsync(queue, "Export 3 demos", "/out/bundle",
                (_, _) => Task.FromResult(3), null, serial, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        QueueJobRequest request = queue.Requests.Single();
        using (Assert.Multiple())
        {
            await Assert.That(result).IsEqualTo(3);
            await Assert.That(request.Kind).IsEqualTo(QueueJobKind.DataTransfer);
            await Assert.That(request.Priority).IsEqualTo(DemoJobPriority.UserRequested);
            await Assert.That(request.Preemptible).IsFalse();
            await Assert.That(request.Serial).IsEqualTo(serialKey);
            await Assert.That(request.Target).IsEqualTo("/out/bundle");
            await Assert.That(request.OwnerTag).IsEqualTo(TransferQueue.Owner);
        }
    }

    [Test]
    public async Task ATransfer_RelaysProgressToTheRowAndTheCaller_AndCancelsFromEitherSide(
        CancellationToken cancellationToken)
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        List<TransferProgress> seen = [];
        SyncProgress sink = new(seen);

        string value = await TransferQueue.RunAsync(queue, "Export 2 demos", "/out/bundle", (_, progress) =>
            {
                progress.Report(new TransferProgress(1, 2, 10, 40, "a.dem"));
                return Task.FromResult("done");
            }, sink, false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        DemoQueueItemSnapshot item = queue.Snapshot().Single(s => s.Kind == QueueJobKind.DataTransfer);
        using (Assert.Multiple())
        {
            await Assert.That(value).IsEqualTo("done");
            await Assert.That(seen.Single().Current).IsEqualTo("a.dem");
            await Assert.That(item.State).IsEqualTo(DemoQueueItemState.Completed);
            await Assert.That(item.Progress).IsEqualTo(0.5);
            await Assert.That(item.Path).IsEqualTo("/out/bundle");
        }

        // Cancelled by the caller while running: the body's token fires.
        using CancellationTokenSource cts = new();
        TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> byCaller = TransferQueue.RunAsync(queue, "Import", null, async (job, _) =>
        {
            running.SetResult();
            await Task.Delay(Timeout.Infinite, job.CancellationToken);
            return 0;
        }, null, true, cts.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await cts.CancelAsync();
        await Assert.That(await EndsCancelledAsync(byCaller)).IsTrue();

        // Cancelled from the queue list while running.
        running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> byList = TransferQueue.RunAsync(queue, "Export", null, async (job, _) =>
        {
            running.SetResult();
            await Task.Delay(Timeout.Infinite, job.CancellationToken);
            return 0;
        }, null, false, CancellationToken.None);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        queue.RemoveByUser(queue.Snapshot().Single(s => s.State == DemoQueueItemState.Running).Id);
        await Assert.That(await EndsCancelledAsync(byList)).IsTrue();
    }

    [Test]
    public async Task ABodyThatThrows_FaultsTheCallersTask()
    {
        using HeavyJobGate gate = new();
        using DemoProcessingQueue queue = NewQueue(gate);
        Task<int> failing = TransferQueue.RunAsync<int>(queue, "Export", null,
            (_, _) => throw new IOException("drive removed"), null, false, CancellationToken.None);

        await Assert.That(async () => await failing.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<IOException>().WithMessage("drive removed");
    }

    // Progress<T> posts to the thread pool, so a test reading it right after the await could miss the report.
    private sealed class SyncProgress(List<TransferProgress> seen) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            lock (seen)
            {
                seen.Add(value);
            }
        }
    }
}
