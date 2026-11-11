#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The live values service goes through the host's queue with the declared kind, the one key and the
///     user's priority, and answers null rather than throwing for every way a run can fail to happen: no
///     queue, no demo, a rejected or cancelled job, a throw inside it, or another demo open by the end.
/// </summary>
public class LiveValuesServiceTests
{
    private const string DemoPath = "/demos/one.dem";

    private static IReadOnlyList<RulesetDoc> PlayerRoundDocs() =>
        [.. RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]).Compose("player_round").Documents.Values];

    // Two players and no frames: the engine builds and evaluates it to a run that fired nothing.
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

    [Test]
    public async Task WithoutADemo_NothingIsQueued_AndTheAnswerIsNull()
    {
        InlineJobs jobs = new() { ParseDemo = _ => SyntheticDemo() };
        LiveValuesService service = new(() => jobs, () => null, () => null);
        LiveValuesRun? run = await service.RunAsync("player_round", PlayerRoundDocs(), CancellationToken.None);
        using (Assert.Multiple())
        {
            await Assert.That(run).IsNull();
            await Assert.That(jobs.Submitted).IsEmpty();
        }
    }

    [Test]
    public async Task WithoutAQueue_TheAnswerIsNull()
    {
        LiveValuesService service = new(() => null, () => DemoPath, () => "abc");
        await Assert.That(await service.RunAsync("player_round", PlayerRoundDocs(), CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ARun_GoesThroughTheQueue_WithTheKindTheKeyAndTheUsersPriority()
    {
        InlineJobs jobs = new() { ParseDemo = _ => SyntheticDemo() };
        LiveValuesService service = new(() => jobs, () => DemoPath, () => "abc");
        LiveValuesRun? run = await service.RunAsync("player_round", PlayerRoundDocs(), CancellationToken.None);
        (string title, JobOptions options) = jobs.Submitted.Single();
        using (Assert.Multiple())
        {
            await Assert.That(title).IsEqualTo("Rule Canvas: values for player_round");
            await Assert.That(options.Kind).IsEqualTo(LiveValuesService.JobKindId);
            await Assert.That(options.Key).IsEqualTo(LiveValuesService.JobKey);
            await Assert.That(options.Priority).IsEqualTo(JobPriority.UserRequested);
            await Assert.That(run).IsNotNull();
            await Assert.That(run!.DemoPath).IsEqualTo(DemoPath);
            await Assert.That(run.Instances[0].Slot).IsNull().Because("the game instance comes first");
            await Assert.That(run.Nodes.Values.All(t => t.FireCount == 0)).IsTrue().Because("no frames, no fires");
        }
    }

    [Test]
    public async Task TheDeclaredKind_IsThePacksOnlyJobKind()
    {
        RuleCanvasPack pack = new();
        ExtensionJobKind kind = pack.JobKinds.Single();
        using (Assert.Multiple())
        {
            await Assert.That(kind.Id).IsEqualTo(LiveValuesService.JobKindId);
            await Assert.That(kind.Id).StartsWith(RuleCanvasPack.PackId);
            await Assert.That(kind.IsLight).IsFalse().Because("an evaluation holds the parse slot");
        }
    }

    [Test]
    public async Task ARejectedJob_AnswersNull()
    {
        InlineJobs jobs = new();
        LiveValuesService service = new(() => jobs, () => DemoPath, () => "abc");
        await Assert.That(await service.RunAsync("player_round", PlayerRoundDocs(), CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ACancelledJob_AnswersNull()
    {
        InlineJobs jobs = new() { ParseDemo = _ => SyntheticDemo(), CancelBeforeRun = true };
        LiveValuesService service = new(() => jobs, () => DemoPath, () => "abc");
        await Assert.That(await service.RunAsync("player_round", PlayerRoundDocs(), CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task AThrowInsideTheJob_AnswersNull()
    {
        InlineJobs jobs = new() { ParseDemo = _ => throw new IOException("unreadable") };
        LiveValuesService service = new(() => jobs, () => DemoPath, () => "abc");
        await Assert.That(await service.RunAsync("player_round", PlayerRoundDocs(), CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ARunForADemoNoLongerOpen_IsDropped()
    {
        InlineJobs jobs = new() { ParseDemo = _ => SyntheticDemo() };
        int reads = 0;
        LiveValuesService service = new(() => jobs, () => DemoPath, () => "demo-" + reads++);
        await Assert.That(await service.RunAsync("player_round", PlayerRoundDocs(), CancellationToken.None)).IsNull();
    }
}
