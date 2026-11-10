#region

using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The queue's pick order: a user's request before the open demo's passes, the backlog before background
///     work, the demo the shell holds before other demos, and one demo's remaining work before the next demo's.
/// </summary>
public class QueueOrderTests
{
    private static DemoProcessingQueue Queue() =>
        new(new HeavyJobGate(), a => a(),
            parseBytes: _ => SyntheticParsedDemo.Create(),
            compactHeap: () => Task.CompletedTask,
            parseFileWithPlan: (_, plan) => SyntheticParsedDemo.Create(plan: plan));

    private static IDemoQueueHandle Visit(DemoProcessingQueue queue, string path, PassLevel level, List<string> ran,
        long orderHint = 0, bool userCommands = false) =>
        queue.SubmitVisit(new DemoVisitRequest(path, level,
        [
            new TestPass("p:" + path)
            {
                Ran = ran,
                NeedsValue = userCommands ? PassNeeds.RetainedParse : PassNeeds.RetainedWithoutUserCommands
            }
        ], orderHint));

    private static IDemoQueueHandle Job(DemoProcessingQueue queue, string name, DemoJobPriority priority,
        List<string> ran,
        PassLevel? level = null) =>
        queue.SubmitJob(new QueueJobRequest(QueueJobKind.SectionCompute, name, "test", priority, _ =>
        {
            lock (ran)
            {
                ran.Add(name);
            }

            return Task.CompletedTask;
        }, Level: level));

    [Test]
    public async Task AUsersRequest_RunsBeforeTheOpenDemosPasses()
    {
        List<string> ran = [];
        using DemoProcessingQueue queue = Queue();
        // Pause holds back nothing a user asked for, so a running job holds the lane instead.
        TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IDemoQueueHandle blocker = queue.SubmitJob(new QueueJobRequest(QueueJobKind.PackExport, "blocker", "test",
            DemoJobPriority.UserRequested, async _ =>
            {
                running.TrySetResult();
                await release.Task;
            }, Preemptible: false));
        await running.Task;
        IDemoQueueHandle open = Visit(queue, "/d/open.dem", PassLevel.OpenDemo, ran);
        IDemoQueueHandle user = Visit(queue, "/d/asked.dem", PassLevel.UserRequested, ran);
        release.SetResult();
        await Task.WhenAll(blocker.Completion, open.Completion, user.Completion);

        await Assert.That(ran).IsEquivalentTo(["p:/d/asked.dem", "p:/d/open.dem"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task RaisingAQueuedBackgroundVisit_RunsItFirst_OnceAndNeverLower()
    {
        List<string> ran = [];
        using DemoProcessingQueue queue = Queue();
        TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IDemoQueueHandle blocker = queue.SubmitJob(new QueueJobRequest(QueueJobKind.PackExport, "blocker", "test",
            DemoJobPriority.UserRequested, async _ =>
            {
                running.TrySetResult();
                await release.Task;
            }, Preemptible: false));
        await running.Task;
        IDemoQueueHandle first = Visit(queue, "/d/first.dem", PassLevel.Background, ran);
        IDemoQueueHandle asked = Visit(queue, "/d/asked.dem", PassLevel.Background, ran);

        bool raised = queue.RaiseVisit("/d/asked.dem", PassLevel.UserRequested);
        bool again = queue.RaiseVisit("/d/asked.dem", PassLevel.UserRequested);
        bool lower = queue.RaiseVisit("/d/asked.dem", PassLevel.Background);
        bool unknown = queue.RaiseVisit("/d/none.dem", PassLevel.UserRequested);
        DemoQueueItemSnapshot[] visits =
            [.. queue.Snapshot().Where(i => i.Kind == QueueJobKind.DemoProcessing)];
        release.SetResult();
        await Task.WhenAll(blocker.Completion, first.Completion, asked.Completion);

        using (Assert.Multiple())
        {
            await Assert.That(raised).IsTrue();
            await Assert.That(again).IsFalse().Because("a second raise to the same level changes nothing");
            await Assert.That(lower).IsFalse().Because("a raise never lowers a visit");
            await Assert.That(unknown).IsFalse();
            await Assert.That(visits).Count().IsEqualTo(2);
            await Assert.That(visits.Single(v => v.Path == "/d/asked.dem").Priority)
                .IsEqualTo(DemoJobPriority.UserRequested);
            await Assert.That(ran).IsEquivalentTo(["p:/d/asked.dem", "p:/d/first.dem"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task RaisingARunningVisit_DoesNothing()
    {
        using DemoProcessingQueue queue = Queue();
        using ManualResetEventSlim release = new(false);
        TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IDemoQueueHandle visit = queue.SubmitVisit(new DemoVisitRequest("/d/run.dem", PassLevel.Background,
        [
            new TestPass("p")
            {
                OnRun = _ =>
                {
                    running.TrySetResult();
                    release.Wait(5000);
                }
            }
        ]));
        await running.Task;

        bool raised = queue.RaiseVisit("/d/run.dem", PassLevel.UserRequested);
        DemoJobPriority priority = queue.Snapshot().Single(i => i.Path == "/d/run.dem").Priority;
        release.Set();
        await visit.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(raised).IsFalse();
            await Assert.That(priority).IsEqualTo(DemoJobPriority.Background);
        }
    }

    [Test]
    public async Task TheBacklog_RunsBeforeBackgroundWork_WhateverTheOrderHint()
    {
        List<string> ran = [];
        using DemoProcessingQueue queue = Queue();
        queue.Pause();
        IDemoQueueHandle sweep = Visit(queue, "/d/old.dem", PassLevel.Background, ran, orderHint: 100);
        IDemoQueueHandle import = Visit(queue, "/d/new.dem", PassLevel.Backlog, ran, orderHint: 1);
        queue.Resume();
        await Task.WhenAll(sweep.Completion, import.Completion);

        await Assert.That(ran).IsEquivalentTo(["p:/d/new.dem", "p:/d/old.dem"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ABacklogJob_RunsBeforeABackgroundJob_AndBothAfterAUsersJob()
    {
        List<string> ran = [];
        using DemoProcessingQueue queue = Queue();
        queue.Pause();
        IDemoQueueHandle background = Job(queue, "background", DemoJobPriority.Background, ran);
        IDemoQueueHandle backlog = Job(queue, "backlog", DemoJobPriority.Background, ran, PassLevel.Backlog);
        IDemoQueueHandle user = Job(queue, "user", DemoJobPriority.UserRequested, ran);
        queue.Resume();
        await Task.WhenAll(background.Completion, backlog.Completion, user.Completion);

        await Assert.That(ran).IsEquivalentTo(["user", "backlog", "background"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheDemoTheShellHolds_RunsBeforeOtherDemosOfItsLevel()
    {
        List<string> ran = [];
        using DemoProcessingQueue queue = Queue();
        queue.ShellDemo = new LoadedOnly("/d/loaded.dem");
        queue.Pause();
        IDemoQueueHandle newer = Visit(queue, "/d/newer.dem", PassLevel.Backlog, ran, orderHint: 100);
        IDemoQueueHandle loaded = Visit(queue, "/d/loaded.dem", PassLevel.Backlog, ran, orderHint: 1);
        queue.Resume();
        await Task.WhenAll(newer.Completion, loaded.Completion);

        await Assert.That(ran).IsEquivalentTo(["p:/d/loaded.dem", "p:/d/newer.dem"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task OneDemosRemainingWork_RunsBeforeTheNextDemo()
    {
        List<string> ran = [];
        TaskCompletionSource running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ManualResetEventSlim release = new();
        using DemoProcessingQueue queue = Queue();
        IDemoQueueHandle first = queue.SubmitVisit(new DemoVisitRequest("/d/a.dem", PassLevel.Backlog,
        [
            new TestPass("first")
            {
                Ran = ran,
                OnRun = _ =>
                {
                    running.TrySetResult();
                    release.Wait(TimeSpan.FromSeconds(5));
                }
            }
        ]));
        await running.Task;

        // Queued while a.dem's visit runs: b.dem arrived first, but a.dem's second visit reads inputs the
        // running one does not, so it waits as its own entry.
        IDemoQueueHandle other = Visit(queue, "/d/b.dem", PassLevel.Backlog, ran, orderHint: 100);
        IDemoQueueHandle again = Visit(queue, "/d/a.dem", PassLevel.Backlog, ran, userCommands: true);
        release.Set();
        await Task.WhenAll(first.Completion, other.Completion, again.Completion);

        await Assert.That(ran).IsEquivalentTo(["first", "p:/d/a.dem", "p:/d/b.dem"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    private sealed class LoadedOnly(string path) : IShellDemoLease
    {
        public IHeldParse? TryHold(string demo) => null;

        public string LoadedPath => path;
    }
}
