#region

using System.Diagnostics;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Live values stage 2 on the tab, with a canned run, a fake shell clock and no demo parse: wires read
///     their own tracks in their kind's words, the badges follow the playhead without a redraw, the watch
///     lives from activation to deactivation, and one playhead update over the largest shipped canvas
///     stays under a millisecond.
/// </summary>
public class CanvasTabViewModelPlayheadTests
{
    [Test]
    public async Task AWire_ReadsItsOwnTrack_InItsKindsWords_AndFallsBackToANodeWithoutOne()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        CanvasConnectionViewModel kills = LiveCanned.TriggerInto(vm, "round_kills");
        CanvasConnectionViewModel gate = LiveCanned.GateInto(vm, "round_deaths");
        CanvasConnectionViewModel logic = vm.Connections.First(w => w.Source.Owner?.Id == "round_kills" && w.Target.Owner?.Id == "round_kast");
        using (Assert.Multiple())
        {
            await Assert.That(kills.LiveTip).IsEqualTo("fired 3 times, first tick 100, last tick 900");
            await Assert.That(gate.LiveTip).IsEqualTo("open on 2 of the trigger's 40 fires");
            await Assert.That(logic.LiveKind).IsEqualTo(LiveWireKind.Logic);
            await Assert.That(logic.LiveTip).IsEqualTo("target fired 2 times, first tick 120, last tick 800").Because("no edge of its own: the target's fires, said as the target's");
            await Assert.That(LiveCanned.TriggerInto(vm, "round_deaths").LiveTip).StartsWith("target fired 2 times").Because("no track for the trigger wire itself");
            await Assert.That(LiveCanned.Node(vm, "round_kast").LiveTip).Contains("on 1 times, off 1 times");
        }
    }

    [Test]
    public async Task AReadWireWithNoEdgeOfItsOwn_ShowsItsSourcesLastValue()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        // The enrichment round_kills reads: the engine folds the read into the stat's trigger edge, so the wire has none of its own.
        CanvasConnectionViewModel read = vm.Connections.First(w => w.LiveKind == LiveWireKind.Read && w.Target.Owner?.Id == "round_kills");
        Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> nodes = new()
        {
            [(read.Source.Owner!.Key, 1)] = new NodeTrack(17, 100, 900, "17", 17f, true)
        };
        vm.ShowLiveRun(new LiveValuesRun(LiveCanned.DemoPath, "Cs2GotvProfile", 64, 1, 90000, [new LiveInstance(0, null, null), new LiveInstance(1, 0, "Alice")], nodes, [],
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0));
        await Assert.That(read.LiveTip).IsEqualTo("value at last fire: 17");
    }

    [Test]
    public async Task ThePlayhead_MovesTheBadges_WithoutRedrawingTheGraph()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        int replaced = 0;
        vm.GraphReplaced += () => replaced++;
        CanvasNodeViewModel kills = LiveCanned.Node(vm, "round_kills");
        CanvasConnectionViewModel wire = LiveCanned.TriggerInto(vm, "round_kills");
        await Assert.That(vm.LiveState).IsEqualTo("values ready, final").Because("the shell's clock starts at 0, before the run");

        shell.MoveTo(600);
        using (Assert.Multiple())
        {
            await Assert.That(vm.PlayheadTick).IsEqualTo(600);
            await Assert.That(kills.LiveBadge).IsEqualTo("2");
            await Assert.That(kills.LiveTip).StartsWith("at tick 600: 2\n");
            await Assert.That(LiveCanned.Node(vm, "round_kast").LiveBadge).IsEqualTo("on");
            await Assert.That(wire.LiveTip).StartsWith("2 fires by tick 600\n");
            await Assert.That(vm.LiveState).IsEqualTo("values ready, at tick 600");
            await Assert.That(replaced).IsEqualTo(0);
            await Assert.That(vm.Nodes.Single(n => n.Id == "round_kills")).IsSameReferenceAs(kills).Because("no redraw, the same view models");
        }

        shell.MoveTo(50);
        using (Assert.Multiple())
        {
            await Assert.That(kills.LiveBadge).IsEqualTo(LiveValuesText.NotYet);
            await Assert.That(kills.LiveTip).StartsWith("not yet fired at tick 50");
        }

        shell.MoveTo(100_000);
        using (Assert.Multiple())
        {
            await Assert.That(kills.LiveBadge).IsEqualTo("3").Because("outside the run the badges show the final value");
            await Assert.That(kills.LiveTip).DoesNotContain("at tick");
            await Assert.That(vm.LiveState).IsEqualTo("values ready, final");
            await Assert.That(replaced).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Deactivation_DropsTheWatch_AndActivationTakesItAgain()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        await Assert.That(shell.Subscribers.Count).IsEqualTo(1);
        await Assert.That(vm.IsWatchingPlayhead).IsTrue();

        vm.OnDeactivated();
        shell.MoveTo(600);
        using (Assert.Multiple())
        {
            await Assert.That(shell.Subscribers).IsEmpty();
            await Assert.That(vm.IsWatchingPlayhead).IsFalse();
            await Assert.That(vm.PlayheadTick).IsNull();
            await Assert.That(LiveCanned.Node(vm, "round_kills").LiveBadge).IsEqualTo("3").Because("an inactive tab shows final values");
            await Assert.That(vm.LiveState).IsEqualTo("values ready");
        }

        vm.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(shell.Subscribers.Count).IsEqualTo(1);
            await Assert.That(vm.PlayheadTick).IsEqualTo(600).Because("activation reads the clock at once");
            await Assert.That(LiveCanned.Node(vm, "round_kills").LiveBadge).IsEqualTo("2");
        }

        vm.Dispose();
        await Assert.That(shell.Subscribers).IsEmpty();
    }

    [Test]
    public async Task WithoutAClock_TheStripSaysNothingAboutPosition()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsWatchingPlayhead).IsFalse();
            await Assert.That(vm.PlayheadTick).IsNull();
            await Assert.That(vm.LiveState).IsEqualTo("no demo open");
        }
    }

    // One playhead update walks every badged node and wire of player_match with its dependencies, the
    // largest shipped canvas: a binary search each, and a string only where the fire shown changed.
    // Stepping the tick so most of them change every time.
    // A Stopwatch budget belongs to the Budget tier: the standard lane runs on a shared box and must not
    // fail on timing. Measured 0.32 ms alone and 1.05 ms during a four-way merge build.
    [Test]
    [Category("Budget")]
    [NotInParallel]
    public async Task OnePlayheadUpdate_OverPlayerMatch_StaysUnderAMillisecond()
    {
        using RulesTree rules = RulesTree.WithUser("player_match");
        rules.UserCopyOf("player_stats");
        rules.UserCopyOf("kast");
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules);
        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "player_match");
        const int lastTick = 90_000;
        List<LiveInstance> instances = [new LiveInstance(0, null, null), new LiveInstance(1, 0, "Alice")];
        Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> nodes = [];
        // Every node, scaffolds included: the reducer never tracks a scaffold, but the walk is per badged node.
        foreach (CanvasNodeViewModel node in vm.Nodes)
        {
            List<ValuePoint> points = [.. Enumerable.Range(0, ValueSeries.Cap).Select(i => new ValuePoint(i * (lastTick / ValueSeries.Cap), true, i + 1, null))];
            nodes[(node.Key, 1)] = new NodeTrack(points.Count, points[0].Tick, points[^1].Tick, "512", 512f, true) { Series = ValueSeries.From(points) };
        }

        Dictionary<(LiveWireKey, int), WireTrack> wires = [];
        foreach (CanvasConnectionViewModel wire in vm.Connections.Where(w => w.Source.Owner is not null && w.Target.Owner is not null))
        {
            List<int> ticks = [.. Enumerable.Range(0, TickList.Cap).Select(i => i * (lastTick / TickList.Cap))];
            wires[(LiveCanned.KeyOf(wire), 1)] = new WireTrack(ticks.Count, ticks[0], ticks[^1], TickList.From(ticks)) { TriggerCount = 4000 };
        }

        LiveValuesRun run = new(LiveCanned.DemoPath, "Cs2GotvProfile", 64, 0, lastTick, instances, nodes, [],
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0) { Wires = wires };
        vm.ShowLiveRun(run);
        int badged = vm.Nodes.Count(n => n.HasLive);
        using (Assert.Multiple())
        {
            // A folded dependency is badged from its entries' tracks, which this canned run does not hold.
            await Assert.That(badged).IsEqualTo(vm.Nodes.Count(n => !n.IsDependency)).Because("every drawn node has a canned track");
            await Assert.That(badged).IsGreaterThan(100).Because($"player_match with its dependencies is the largest shipped canvas; {badged} are badged");
            await Assert.That(wires.Count).IsGreaterThan(100).Because($"{wires.Count} wires tracked; the chain composed");
        }

        // Warm the paths once, then measure.
        shell.MoveTo(1);
        shell.MoveTo(2);
        const int iterations = 200;
        Stopwatch clock = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            shell.MoveTo(1000 + i * (lastTick / iterations));
        }

        clock.Stop();
        double average = clock.Elapsed.TotalMilliseconds / iterations;
        Console.WriteLine($"[canvas] playhead update over {badged} nodes and {wires.Count} wires: {average:F3} ms average over {iterations}");
        await Assert.That(average).IsLessThan(1.0).Because($"{average:F3} ms per playhead update over {badged} nodes and {wires.Count} wires");
    }
}
