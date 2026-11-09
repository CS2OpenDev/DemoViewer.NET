#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Live values on the tab, with a canned run and no demo: badges follow the selected instance without a
///     recompose, a write marks them stale and keeps them, a ruleset or demo switch drops them, a run for
///     text no longer drawn is ignored, and without a host the strip says no demo is open.
/// </summary>
public class CanvasTabViewModelLiveTests
{
    private const string DemoPath = "/demos/one.dem";

    private static readonly RulesetDocumentNodeKey _kills = new("player_round", "round_kills");
    private static readonly RulesetDocumentNodeKey _kast = new("player_round", "round_kast");
    private static readonly RulesetDocumentNodeKey _damage = new("player_round", "round_damage");

    // The game and two players; Alice has three kills and holds KAST, Bob one kill; round_damage is unbound.
    private static LiveValuesRun CannedRun(string demoPath = DemoPath)
    {
        List<LiveInstance> instances = [new LiveInstance(0, null, null), new LiveInstance(1, 0, "Alice"), new LiveInstance(2, 1, "Bob")];
        Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> nodes = new()
        {
            [(_kills, 1)] = new NodeTrack(3, 100, 900, "3", 3f, true),
            [(_kills, 2)] = new NodeTrack(1, 500, 500, "1", 1f, true),
            [(_kast, 1)] = new NodeTrack(2, 120, 800, "1", 1f, true),
            [(_kast, 2)] = new NodeTrack(0, null, null, "0", 0f, false)
        };
        return new LiveValuesRun(demoPath, "Cs2GotvProfile", 64, 0, 90000, instances, nodes, [_damage],
            TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(7), 0);
    }

    private static ParsedDemo SyntheticDemo()
    {
        Dictionary<int, PlayerInfo> players = new()
        {
            [0] = new PlayerInfo(0, "Alice", 76561198000000001UL, 1, 2, false),
            [1] = new PlayerInfo(1, "Bob", 76561198000000002UL, 2, 3, false)
        };
        return new ParsedDemo([], [], players, null, "de_anytown", 0, 1f / 64f, "test", "test", "csgo", 0, 0, 0,
            "valve_demo_2", "", "", DemoProfile.Unknown);
    }

    private static CanvasTabViewModel WithHost(RulesTree rules, IExtensionJobs jobs, Func<string?> demoPath, Action<Action>? onDemoChanged = null,
        Func<string?>? demoHash = null, Func<string, bool>? selectTab = null)
    {
        CanvasTabViewModel vm = new(selectTab ?? (_ => true), rules.Load, () => rules.OwnFolder, null,
            new LiveValuesHost(() => jobs, demoPath, demoHash ?? (static () => "hash"), static a => a(), onDemoChanged)) { LiveAutoRunDelay = TimeSpan.Zero };
        vm.OnActivated(null!);
        return vm;
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id);

    [Test]
    public async Task ACannedRun_BadgesEveryTrackedNodeForTheFirstPlayer_AndSaysNoDataForAnUnboundOne()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.ShowLiveRun(CannedRun());
        CanvasNodeViewModel kills = Node(vm, "round_kills");
        CanvasConnectionViewModel intoKills = vm.Connections.First(w => w.Target.Owner == kills);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedInstance?.Name).IsEqualTo("Alice").Because("the first player, not the game, is the default");
            await Assert.That(vm.Instances.Select(i => i.Name ?? "game")).IsEquivalentTo(["game", "Alice", "Bob"]);
            await Assert.That(kills.LiveBadge).IsEqualTo("3");
            await Assert.That(kills.HasLive).IsTrue();
            await Assert.That(kills.IsLiveStale).IsFalse();
            await Assert.That(kills.LiveTip).IsEqualTo("Alice: fired 3 times, first tick 100, last tick 900, final 3");
            await Assert.That(Node(vm, "round_kast").LiveBadge).IsEqualTo("on").Because("a flag is a bool");
            await Assert.That(Node(vm, "round_damage").LiveBadge).IsEqualTo(LiveValuesText.NoData);
            await Assert.That(Node(vm, "round_deaths").HasLive).IsFalse().Because("no track and not unbound: the run says nothing about it");
            await Assert.That(vm.Nodes.Where(n => n.IsScaffold).All(n => !n.HasLive)).IsTrue();
            await Assert.That(intoKills.LiveTip).IsEqualTo("fired 3 times, last tick 900");
            await Assert.That(vm.IsLiveStale).IsFalse();
            await Assert.That(vm.HasLiveRun).IsTrue();
        }
    }

    [Test]
    public async Task SwitchingTheInstance_SwapsTheBadges_WithoutRedrawingTheGraph()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.ShowLiveRun(CannedRun());
        int replaced = 0;
        vm.GraphReplaced += () => replaced++;
        CanvasNodeViewModel kills = Node(vm, "round_kills");

        vm.SelectedInstance = vm.Instances.Single(i => i.Name == "Bob");
        using (Assert.Multiple())
        {
            await Assert.That(kills.LiveBadge).IsEqualTo("1");
            await Assert.That(kills.LiveTip).StartsWith("Bob: fired 1 times");
            await Assert.That(Node(vm, "round_kast").LiveBadge).IsEqualTo("off");
            await Assert.That(replaced).IsEqualTo(0);
            await Assert.That(vm.Nodes.Single(n => n.Id == "round_kills")).IsSameReferenceAs(kills).Because("no redraw, the same view models");
        }

        vm.SelectedInstance = vm.Instances.Single(i => i.Slot is null);
        await Assert.That(kills.HasLive).IsFalse().Because("a per-player stat has no value for the game");
    }

    [Test]
    public async Task AWrite_MarksTheRunStale_AndKeepsTheBadges_UntilUndoRestoresTheText()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.ShowLiveRun(CannedRun());

        Node(vm, "round_kills").Facets.Single(f => f.Name == "enemy").Toggle = false;
        CanvasNodeViewModel redrawn = Node(vm, "round_kills");
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsLiveStale).IsTrue();
            await Assert.That(vm.LiveState).IsEqualTo("no demo open").Because("without a host the strip's first word is the demo");
            await Assert.That(redrawn.LiveBadge).IsEqualTo("3").Because("stale values are shown, never hidden");
            await Assert.That(redrawn.IsLiveStale).IsTrue();
            await Assert.That(redrawn.LiveTip).EndsWith(LiveValuesText.StaleLine);
            await Assert.That(vm.Connections.First(w => w.Target.Owner == redrawn).LiveTip).EndsWith(LiveValuesText.StaleLine);
            await Assert.That(vm.PendingLiveRun is null).IsTrue().Because("no demo, nothing to re-run");
        }

        vm.UndoCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsLiveStale).IsFalse().Because("the text is the run's again");
            await Assert.That(Node(vm, "round_kills").IsLiveStale).IsFalse();
        }
    }

    [Test]
    public async Task AFileChangedOnDisk_MarksTheRunStale()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.ShowLiveRun(CannedRun());
        string path = rules.UserPath("player_round");
        File.WriteAllText(path, File.ReadAllText(path).Replace("count: kill", "count: kill\n    label: Kills", StringComparison.Ordinal));
        vm.FilesChanged([path]);
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsLiveStale).IsTrue();
            await Assert.That(Node(vm, "round_kills").LiveBadge).IsEqualTo("3");
        }
    }

    [Test]
    public async Task ARulesetSwitch_ClearsTheRun()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        rules.UserCopyOf("player_stats");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.ShowLiveRun(CannedRun());
        await Assert.That(vm.HasLiveRun).IsTrue();

        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "player_stats");
        using (Assert.Multiple())
        {
            await Assert.That(vm.LiveRun).IsNull();
            await Assert.That(vm.Instances).IsEmpty();
            await Assert.That(vm.SelectedInstance).IsNull();
            await Assert.That(vm.Nodes.All(n => !n.HasLive)).IsTrue();
            await Assert.That(vm.Connections.All(w => !w.HasLiveTip)).IsTrue();
        }
    }

    [Test]
    public async Task AFocusChange_KeepsTheRun_AndPutsTheBadgesOnTheFreshNodes()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.ShowLiveRun(CannedRun());
        CanvasNodeViewModel before = Node(vm, "round_kills");
        vm.FocusNode(before);
        CanvasNodeViewModel after = Node(vm, "round_kills");
        using (Assert.Multiple())
        {
            await Assert.That(after).IsNotSameReferenceAs(before);
            await Assert.That(after.LiveBadge).IsEqualTo("3");
            await Assert.That(vm.IsLiveStale).IsFalse().Because("a focus change recomposes nothing");
        }
    }

    [Test]
    public async Task WithoutAHost_TheStripSaysNoDemo_AndRunDoesNothing()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasDemo).IsFalse();
            await Assert.That(vm.DemoChipText).IsEqualTo("no demo open");
            await Assert.That(vm.LiveState).IsEqualTo("no demo open");
            await Assert.That(vm.CanRunLive).IsFalse();
            await Assert.That(vm.RunLiveCommand.CanExecute(null)).IsFalse();
        }

        await vm.RunLiveCommand.ExecuteAsync(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.LiveRun).IsNull();
            await Assert.That(vm.IsLiveRunning).IsFalse();
            await Assert.That(vm.Nodes.All(n => !n.HasLive)).IsTrue();
        }
    }

    [Test]
    public async Task WithAHost_RunEvaluatesTheOpenDemo_AndTheStripNamesIt()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        InlineJobs jobs = new() { ParseDemo = _ => SyntheticDemo() };
        CanvasTabViewModel vm = WithHost(rules, jobs, () => DemoPath);
        using (Assert.Multiple())
        {
            await Assert.That(vm.DemoChipText).IsEqualTo("one").Because("the chip shows the stem; the full name is its tooltip");
            await Assert.That(vm.DemoFileName).IsEqualTo("one.dem");
            await Assert.That(CanvasTabViewModel.ShortDemoName("003816248937665266002_0544286934")).IsEqualTo("003816248\u2026286934")
                .Because("a long demo id keeps its start and end so two stay distinguishable");
            await Assert.That(vm.LiveState).IsEqualTo("no values yet");
            await Assert.That(vm.CanRunLive).IsTrue();
        }

        await vm.RunLiveCommand.ExecuteAsync(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.LiveRun).IsNotNull();
            await Assert.That(vm.IsLiveRunning).IsFalse();
            await Assert.That(vm.LiveState).IsEqualTo("values ready");
            await Assert.That(vm.Instances.Count).IsEqualTo(1).Because("players materialise from events, and a demo with no frames has none");
            await Assert.That(vm.SelectedInstance?.Slot).IsNull().Because("with no player the game is the default");
            await Assert.That(Node(vm, "round_kills").LiveBadge).IsNotEmpty().Because("a track with no fires still has a final value");
            await Assert.That(jobs.Submitted.Single().Options.Key).IsEqualTo(LiveValuesService.JobKey);
        }
    }

    [Test]
    public async Task AWrite_QueuesAReplacementRun_AfterThePause()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        InlineJobs jobs = new() { ParseDemo = _ => SyntheticDemo() };
        CanvasTabViewModel vm = WithHost(rules, jobs, () => DemoPath);
        // Long enough to see the stale state before the replacement runs; zero would run it inside the write.
        vm.LiveAutoRunDelay = TimeSpan.FromMilliseconds(50);
        vm.ShowLiveRun(CannedRun());

        Node(vm, "round_kills").Facets.Single(f => f.Name == "enemy").Toggle = false;
        Task? pending = vm.PendingLiveRun;
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsLiveStale).IsTrue();
            await Assert.That(pending is not null).IsTrue();
            await Assert.That(jobs.Submitted).IsEmpty().Because("the pause has not passed");
        }

        await pending!;
        using (Assert.Multiple())
        {
            await Assert.That(jobs.Submitted.Count).IsEqualTo(1);
            await Assert.That(vm.IsLiveStale).IsFalse().Because("the replacement evaluated the written text");
            await Assert.That(vm.LiveRun?.DemoPath).IsEqualTo(DemoPath);
            await Assert.That(vm.LiveState).IsEqualTo("values ready");
        }
    }

    [Test]
    public async Task ARunArrivingAfterARulesetSwitch_IsIgnored()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        rules.UserCopyOf("player_stats");
        DeferredJobs jobs = new(SyntheticDemo());
        CanvasTabViewModel vm = WithHost(rules, jobs, () => DemoPath);
        Task run = vm.RunLiveCommand.ExecuteAsync(null);
        await Assert.That(vm.IsLiveRunning).IsTrue();
        await Assert.That(vm.LiveState).IsEqualTo("evaluating");

        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "player_stats");
        jobs.Release();
        await run;
        using (Assert.Multiple())
        {
            await Assert.That(vm.LiveRun).IsNull();
            await Assert.That(vm.IsLiveRunning).IsFalse();
            await Assert.That(vm.Nodes.All(n => !n.HasLive)).IsTrue();
        }
    }

    [Test]
    public async Task ARunArrivingAfterTheDemoChanged_IsIgnored()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        DeferredJobs jobs = new(SyntheticDemo());
        string hash = "first";
        CanvasTabViewModel vm = WithHost(rules, jobs, () => DemoPath, demoHash: () => hash);
        Task run = vm.RunLiveCommand.ExecuteAsync(null);
        hash = "second";
        jobs.Release();
        await run;
        using (Assert.Multiple())
        {
            await Assert.That(vm.LiveRun).IsNull().Because("the run is for the demo that was open when it was queued");
            await Assert.That(vm.IsLiveRunning).IsFalse();
            await Assert.That(vm.LiveState).IsEqualTo("no values yet");
        }
    }

    [Test]
    public async Task TheDemoChip_OpensTheLibraryTab()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        List<string> selected = [];
        CanvasTabViewModel vm = WithHost(rules, new InlineJobs(), () => null, selectTab: id =>
        {
            selected.Add(id);
            return true;
        });
        vm.OpenLibraryCommand.Execute(null);
        await Assert.That(selected).IsEquivalentTo([CanvasTabViewModel.LibraryTabId]);
    }

    [Test]
    public async Task ADemoChange_ClearsTheRun_AndTheStripFollowsTheNewDemo()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        string? path = DemoPath;
        Action? demoChanged = null;
        CanvasTabViewModel vm = WithHost(rules, new InlineJobs(), () => path, h => demoChanged = h);
        vm.ShowLiveRun(CannedRun());
        await Assert.That(demoChanged is not null).IsTrue().Because("the host's demo change is subscribed through the delegate");

        path = null;
        demoChanged!();
        using (Assert.Multiple())
        {
            await Assert.That(vm.LiveRun).IsNull();
            await Assert.That(vm.LiveState).IsEqualTo("no demo open");
            await Assert.That(vm.CanRunLive).IsFalse();
            await Assert.That(vm.Nodes.All(n => !n.HasLive)).IsTrue();
        }
    }

    /// <summary>A queue whose one job waits for <see cref="Release" /> before its work runs and its handle completes.</summary>
    private sealed class DeferredJobs(ParsedDemo demo) : IExtensionJobs
    {
        private readonly TaskCompletionSource<JobResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private JobRequest? _request;

        public IJobHandle Enqueue(JobRequest request)
        {
            _request = request;
            return new Handle(_done.Task);
        }

        public Task RunAsync(string title, Func<IJobContext, Task> work, JobOptions? options = null) => work(new Context());

        public void CancelAll()
        {
        }

        public void Release()
        {
            _request!.DemoWork!(new Context(demo, _request.DemoPath!)).GetAwaiter().GetResult();
            _done.TrySetResult(new JobResult(JobStatus.Completed));
        }

        private sealed class Context(ParsedDemo? parsed = null, string path = "") : IDemoJobContext
        {
            public CancellationToken CancellationToken => CancellationToken.None;

            public string DemoPath => path;

            public ParsedDemo Parsed => parsed!;

            public void Report(int done, int total, string? detail = null)
            {
            }
        }

        private sealed class Handle(Task<JobResult> completion) : IJobHandle
        {
            public JobStatus Status => completion.IsCompleted ? completion.Result.Status : JobStatus.Running;

            public Task<JobResult> Completion => completion;

            public event Action<JobResult>? Completed
            {
                add { }
                remove { }
            }

            public void Cancel()
            {
            }
        }
    }
}
