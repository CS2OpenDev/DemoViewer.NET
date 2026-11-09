#region

using System.Diagnostics;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

/// <summary>
///     Evaluates the composed ruleset over the open demo through the host's processing queue and hands back
///     the reduced <see cref="LiveValuesRun" />. The work runs on the queue thread, on the parse the shell
///     already holds; the caller awaits from the UI thread. Every failure is logged and answered with null,
///     and so is the absence of a queue or an open demo.
/// </summary>
public sealed class LiveValuesService
{
    /// <summary>The job kind the pack declares; the queue list shows its label on the job.</summary>
    public const string JobKindId = "net.demoviewer.pack.rulecanvas.live";

    /// <summary>One queued run at a time: a newer request replaces the queued older one.</summary>
    public const string JobKey = "rulecanvas.live";

    public static readonly ExtensionJobKind JobKind = new(JobKindId, "Rule values", IsLight: false);

    private readonly Func<IExtensionJobs?> _jobs;
    private readonly Func<string?> _demoPath;
    private readonly Func<string?> _demoHash;
    private readonly ILogger? _log;

    /// <param name="jobs">The host's queue, or null where there is none (no host context, the browser).</param>
    /// <param name="demoPath">The open demo's path, or null when none is open.</param>
    /// <param name="demoHash">The open demo's content key; a run whose demo is no longer open is dropped.</param>
    /// <param name="log">Where failures are written; null logs nothing.</param>
    public LiveValuesService(Func<IExtensionJobs?> jobs, Func<string?> demoPath, Func<string?> demoHash, ILogger? log = null)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _demoPath = demoPath ?? throw new ArgumentNullException(nameof(demoPath));
        _demoHash = demoHash ?? throw new ArgumentNullException(nameof(demoHash));
        _log = log;
    }

    /// <summary>The title the queue list shows for a run of <paramref name="rulesetId" />.</summary>
    public static string TitleFor(string rulesetId) => "Rule Canvas: values for " + rulesetId;

    /// <summary>
    ///     Evaluates <paramref name="docs" /> over the open demo. Null when there is no queue or no demo, when
    ///     the job was cancelled, replaced or failed, or when another demo is open by the time it ends.
    /// </summary>
    public async Task<LiveValuesRun?> RunAsync(string rulesetId, IReadOnlyList<RulesetDoc> docs, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(rulesetId);
        ArgumentNullException.ThrowIfNull(docs);
        IExtensionJobs? jobs = _jobs();
        string? path = _demoPath();
        if (jobs is null || path is null || docs.Count == 0)
        {
            return null;
        }

        string? hash = _demoHash();
        LiveValuesRun? run = null;
        JobRequest request = JobRequest.OnDemo(TitleFor(rulesetId), path, job =>
            {
                run = Evaluate(job, docs);
                return Task.CompletedTask;
            },
            new JobOptions(JobKindId, JobPriority.UserRequested, JobKey));

        IJobHandle handle;
        try
        {
            handle = jobs.Enqueue(request);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            if (_log is not null)
            {
                LiveValuesLog.NotQueued(_log, rulesetId, ex);
            }

            return null;
        }

        JobResult result;
        using (cancellationToken.Register(handle.Cancel))
        {
            result = await handle.Completion;
        }

        if (result.Status != JobStatus.Completed)
        {
            if (result.Error is { } error && _log is not null)
            {
                LiveValuesLog.Failed(_log, rulesetId, error);
            }

            return null;
        }

        return string.Equals(_demoHash(), hash, StringComparison.Ordinal) ? run : null;
    }

    // Runs on the queue thread holding the parse slot: nothing here awaits.
    private static LiveValuesRun Evaluate(IDemoJobContext job, IReadOnlyList<RulesetDoc> docs)
    {
        ParsedDemo parsed = job.Parsed;
        long before = Environment.WorkingSet;
        Stopwatch build = Stopwatch.StartNew();
        BuildResult built = DemoAnalysis.Build(parsed, docs);
        build.Stop();
        Stopwatch evaluate = Stopwatch.StartNew();
        AnalysisRun run = DemoAnalysis.Evaluate(parsed, built, new AnalysisOptions { CaptureSnapshots = true, CancellationToken = job.CancellationToken });
        evaluate.Stop();
        job.CancellationToken.ThrowIfCancellationRequested();
        RulesetComposition.Result composed = RulesetComposition.Compose(docs, EmbeddedCatalog.Adapter, parsed.TickRate, built.Profile.GetType().Name);
        long peak = Math.Max(0, Environment.WorkingSet - before);
        return LiveValuesReducer.Reduce(run, composed.Rulesets, job.DemoPath, parsed.TickRate, build.Elapsed, evaluate.Elapsed, peak);
    }
}

internal static partial class LiveValuesLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Live values for {ruleset} could not be queued")]
    public static partial void NotQueued(ILogger logger, string ruleset, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Live values for {ruleset} failed")]
    public static partial void Failed(ILogger logger, string ruleset, Exception exception);
}
