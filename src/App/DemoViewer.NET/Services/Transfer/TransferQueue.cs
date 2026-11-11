#region

using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>Runs a data export or import as a user-requested item of the processing queue.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It submits data transfers to the processing queue; the name says which queue.")]
public static class TransferQueue
{
    /// <summary>The owner of a transfer's job.</summary>
    public const string Owner = "data-transfer";

    /// <summary>The <see cref="QueueJobRequest.Serial" /> every serial transfer shares, so two imports never overlap.</summary>
    public const string SerialKey = "data-transfer";

    /// <summary>
    ///     Queues <paramref name="body" /> as a <see cref="QueueJobKind.DataTransfer" /> item that a user's item
    ///     cannot preempt, and completes with its result. Cancelling <paramref name="ct" /> cancels the queue item,
    ///     queued or running; cancelling the item from the queue list cancels the body's token.
    /// </summary>
    /// <param name="queue">The processing queue.</param>
    /// <param name="title">The queue list's line.</param>
    /// <param name="target">The bundle folder, for the row tooltip.</param>
    /// <param name="body">
    ///     The transfer. It gets the queue context (its token, and <see cref="IQueueJobContext.ReleaseSlot" /> for a
    ///     body that should let demo processing run beside it) and a progress sink that reaches both the queue row
    ///     and <paramref name="progress" />.
    /// </param>
    /// <param name="progress">The caller's progress sink.</param>
    /// <param name="serial">True for an import: it never runs beside another serial transfer.</param>
    /// <param name="ct">The caller's cancel.</param>
    public static Task<T> RunAsync<T>(IDemoProcessingQueue queue, string title, string? target,
        Func<IQueueJobContext, IProgress<TransferProgress>, Task<T>> body, IProgress<TransferProgress>? progress,
        bool serial, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(body);
        TaskCompletionSource<T> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IDemoQueueHandle handle = queue.SubmitJob(new QueueJobRequest(QueueJobKind.DataTransfer, title, Owner,
            DemoJobPriority.UserRequested, async job =>
            {
                try
                {
                    T value = await body(job, new Relay(progress, job)).ConfigureAwait(false);
                    result.TrySetResult(value);
                }
                catch (OperationCanceledException)
                {
                    result.TrySetCanceled(job.CancellationToken);
                    throw;
                }
                catch (Exception ex)
                {
                    result.TrySetException(ex);
                    throw;
                }
            }, Target: target, Preemptible: false, Serial: serial ? SerialKey : null));

        CancellationTokenRegistration registration = ct.Register(handle.Cancel);
        _ = handle.Completion.ContinueWith(_ =>
        {
            registration.Dispose();
            result.TrySetCanceled(); // cancelled or refused before it ran
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return result.Task;
    }

    private sealed class Relay(IProgress<TransferProgress>? inner, IQueueJobContext job) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            inner?.Report(value);
            // The row shows demos, not bytes: a byte count in the millions reads worse than "12 of 40".
            job.Report(value.DemosDone, value.DemosTotal, value.Current);
        }
    }
}
