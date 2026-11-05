#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     An <see cref="IExtensionJobs" /> that runs each job on the caller's thread and records what was
///     submitted, so a test sees the options a service queued its work with. A job on a demo gets the parse
///     <see cref="ParseDemo" /> hands out.
/// </summary>
internal sealed class InlineJobs : IExtensionJobs
{
    private readonly List<(string Title, JobOptions Options)> _submitted = [];

    private readonly ChangeSignal _submissions = new();

    public IReadOnlyList<(string Title, JobOptions Options)> Submitted
    {
        get
        {
            lock (_submitted)
            {
                return [.. _submitted];
            }
        }
    }


    /// <summary>Returns once <paramref name="until" /> holds, re-checking on each submission.</summary>
    public Task WaitForSubmissionAsync(Func<bool> until) =>
        _submissions.WaitUntilAsync(until, "the expected job to be submitted");

    /// <summary>The parse a job on a demo gets; a job on a demo is rejected when null.</summary>
    public Func<string, ParsedDemo>? ParseDemo { get; init; }

    public IJobHandle Enqueue(JobRequest request)
    {
        lock (_submitted)
        {
            _submitted.Add((request.Title, request.Options));
        }

        _submissions.Raise();

        if (request.DemoPath is { } path)
        {
            if (ParseDemo is not { } parse)
            {
                return Handle.Ended(new JobResult(JobStatus.Rejected));
            }

            return Handle.Of(request.DemoWork!(new DemoContext(path, parse(path), CancellationToken.None)));
        }

        return Handle.Of(request.Work!(new Context(CancellationToken.None)));
    }

    public Task RunAsync(string title, Func<IJobContext, Task> work, JobOptions? options = null)
    {
        lock (_submitted)
        {
            _submitted.Add((title, options ?? new JobOptions()));
        }

        _submissions.Raise();

        return work(new Context(CancellationToken.None));
    }

    public void CancelAll()
    {
    }

    private sealed class Context(CancellationToken token) : IJobContext
    {
        public CancellationToken CancellationToken => token;

        public void Report(int done, int total, string? detail = null)
        {
        }
    }

    private sealed class DemoContext(string path, ParsedDemo parsed, CancellationToken token) : IDemoJobContext
    {
        public CancellationToken CancellationToken => token;

        public string DemoPath => path;

        public ParsedDemo Parsed => parsed;

        public void Report(int done, int total, string? detail = null)
        {
        }
    }

    private sealed class Handle : IJobHandle
    {
        private Handle(Task<JobResult> completion) => Completion = completion;

        public JobStatus Status => Completion.IsCompleted ? Completion.Result.Status : JobStatus.Running;

        public Task<JobResult> Completion { get; }

        public event Action<JobResult>? Completed
        {
            add => Completion.ContinueWith(t => value?.Invoke(t.Result), TaskScheduler.Default);
            remove { }
        }

        public void Cancel()
        {
        }

        public static Handle Ended(JobResult result) => new(Task.FromResult(result));

        public static Handle Of(Task work) => new(Finish(work));

        private static async Task<JobResult> Finish(Task work)
        {
            try
            {
                await work.ConfigureAwait(false);
                return new JobResult(JobStatus.Completed);
            }
            catch (OperationCanceledException)
            {
                return new JobResult(JobStatus.Cancelled);
            }
            catch (Exception ex)
            {
                return new JobResult(JobStatus.Failed, ex);
            }
        }
    }
}
