#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Pinned viewers on the tab, with the canned run and a fake clock: a pin lands in the sidecar and comes
///     back on reopen, the panel is drawn from the run for the selected instance and follows the playhead,
///     a node without a track says so, a stale run dims it, a fold keeps it, and an unpin takes it away.
/// </summary>
public class CanvasTabViewModelViewerTests
{
    private static RulesetNodeLayout Sidecar(RulesTree rules) => RulesetLayoutStore.Load(rules.UserPath("player_round"));

    [Test]
    public async Task PinViewer_ShowsAPanelThatSaysNoRunYet_AndWritesTheSidecar()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        CanvasNodeViewModel kills = LiveCanned.Node(vm, "round_kills");
        await Assert.That(kills.ShowsViewer).IsFalse();

        vm.ToggleViewer(kills);
        using (Assert.Multiple())
        {
            await Assert.That(kills.IsViewerPinned).IsTrue();
            await Assert.That(kills.ViewerMenuText).IsEqualTo("Unpin viewer");
            await Assert.That(kills.ShowsViewer).IsTrue();
            await Assert.That(kills.VisibleViewer).IsSameReferenceAs(kills.LiveViewer);
            await Assert.That(kills.LiveViewer!.Message).IsEqualTo(LiveValuesText.NoRun);
            await Assert.That(kills.LiveViewer.HasSeries).IsFalse();
            await Assert.That(Sidecar(rules).IsPinned(LiveCanned.Kills)).IsTrue();
            await Assert.That(LiveCanned.Node(vm, "round_deaths").LiveViewer).IsNull().Because("only the pinned node gets a panel");
        }
    }

    [Test]
    public async Task APinnedViewer_IsDrawnFromTheRun_ForTheSelectedInstance_AndFollowsThePlayhead()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules);
        CanvasNodeViewModel kills = LiveCanned.Node(vm, "round_kills");
        vm.ToggleViewer(kills);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        CanvasLiveViewerViewModel viewer = kills.LiveViewer!;
        using (Assert.Multiple())
        {
            await Assert.That(viewer.HasMessage).IsFalse();
            await Assert.That(viewer.Fires).IsEqualTo("fired 3 times, first tick 100, last tick 900");
            await Assert.That(viewer.FinalText).IsEqualTo("final 3");
            await Assert.That(viewer.HasSeries).IsTrue();
            await Assert.That(viewer.IsSampled).IsFalse();
            await Assert.That(viewer.SampledNote).IsEqualTo("");
            await Assert.That(viewer.IsStale).IsFalse();
            await Assert.That(viewer.HasPlayhead).IsFalse().Because("the clock at 0 is outside a run that starts at 1");
            await Assert.That(viewer.Breakdown.Select(b => b.Name)).IsEquivalentTo(["Alice", "Bob"]).Because("largest final first");
            await Assert.That(viewer.Breakdown[0].IsSelected).IsTrue();
            await Assert.That(viewer.Breakdown[0].Value).IsEqualTo("3");
            await Assert.That(viewer.Breakdown[1].IsSelected).IsFalse();
            await Assert.That(viewer.Breakdown[1].Fraction).IsEqualTo(1.0 / 3).Within(0.001);
        }

        shell.MoveTo(500);
        using (Assert.Multiple())
        {
            await Assert.That(kills.LiveViewer).IsSameReferenceAs(viewer).Because("a playhead move sets a marker; it rebuilds nothing");
            await Assert.That(viewer.HasPlayhead).IsTrue();
            await Assert.That(viewer.PlayheadMargin.Left).IsGreaterThan(0);
        }

        vm.SelectedInstance = vm.Instances.Single(i => i.Name == "Bob");
        CanvasLiveViewerViewModel bobs = kills.LiveViewer!;
        using (Assert.Multiple())
        {
            await Assert.That(bobs).IsNotSameReferenceAs(viewer);
            await Assert.That(bobs.FinalText).IsEqualTo("final 1");
            await Assert.That(bobs.Breakdown.Single(b => b.IsSelected).Name).IsEqualTo("Bob");
        }
    }

    [Test]
    public async Task APinnedNodeWithoutATrack_SaysNoData_AndAStaleRunDimsTheViewer()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        vm.ToggleViewer(LiveCanned.Node(vm, "round_damage"));
        vm.ToggleViewer(LiveCanned.Node(vm, "round_kills"));
        vm.ShowLiveRun(LiveCanned.Run(vm));
        using (Assert.Multiple())
        {
            await Assert.That(LiveCanned.Node(vm, "round_damage").LiveViewer!.Message).IsEqualTo(LiveValuesText.NoData);
            await Assert.That(LiveCanned.Node(vm, "round_kills").LiveViewer!.IsStale).IsFalse();
        }

        LiveCanned.Node(vm, "round_kills").Facets.Single(f => f.Name == "enemy").Toggle = false;
        CanvasNodeViewModel redrawn = LiveCanned.Node(vm, "round_kills");
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsLiveStale).IsTrue();
            await Assert.That(redrawn.IsViewerPinned).IsTrue().Because("a write redraws; the pin is in the sidecar");
            await Assert.That(redrawn.LiveViewer!.IsStale).IsTrue();
            await Assert.That(redrawn.LiveViewer.FinalText).IsEqualTo("final 3").Because("stale values are shown, never hidden");
            await Assert.That(LiveCanned.Node(vm, "round_damage").LiveViewer!.IsStale).IsTrue();
        }
    }

    [Test]
    public async Task ASampledSeries_SaysSo()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        vm.ToggleViewer(LiveCanned.Node(vm, "round_kills"));
        int[] ticks = [.. Enumerable.Range(1, ValueSeries.Cap + 100).Select(i => i * 10)];
        Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> nodes = new()
        {
            [(LiveCanned.Kills, 1)] = new NodeTrack(ticks.Length, ticks[0], ticks[^1], null, ticks.Length, true) { Series = LiveCanned.Counting(ticks) }
        };
        vm.ShowLiveRun(new LiveValuesRun(LiveCanned.DemoPath, "p", 64, 1, 90000, [new LiveInstance(0, null, null), new LiveInstance(1, 0, "Alice")], nodes, [],
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0));
        CanvasLiveViewerViewModel viewer = LiveCanned.Node(vm, "round_kills").LiveViewer!;
        using (Assert.Multiple())
        {
            await Assert.That(viewer.IsSampled).IsTrue();
            await Assert.That(viewer.SampledNote).IsEqualTo(LiveValuesText.Sampled);
            await Assert.That(viewer.SparkPoints.Count).IsLessThanOrEqualTo(ValueSeries.Cap * 2 + 1);
        }
    }

    [Test]
    public async Task AFoldKeepsTheViewer_AndAReopenRestoresThePin()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        CanvasNodeViewModel kills = LiveCanned.Node(vm, "round_kills");
        vm.ToggleViewer(kills);
        vm.ToggleCollapse(kills);
        using (Assert.Multiple())
        {
            await Assert.That(kills.IsCollapsed).IsTrue();
            await Assert.That(kills.ShowsFace).IsFalse();
            await Assert.That(kills.ShowsViewer).IsTrue().Because("the viewer is the footer, not part of the face");
            await Assert.That(Sidecar(rules).IsCollapsed(LiveCanned.Kills)).IsTrue();
            await Assert.That(Sidecar(rules).IsPinned(LiveCanned.Kills)).IsTrue();
        }

        CanvasTabViewModel again = rules.NewViewModel();
        again.OnActivated(null!);
        CanvasNodeViewModel reopened = LiveCanned.Node(again, "round_kills");
        using (Assert.Multiple())
        {
            await Assert.That(reopened.IsViewerPinned).IsTrue();
            await Assert.That(reopened.IsCollapsed).IsTrue();
            await Assert.That(reopened.LiveViewer!.Message).IsEqualTo(LiveValuesText.NoRun).Because("no run in a fresh tab, but the panel is there");
        }
    }

    [Test]
    public async Task UnpinViewer_RemovesThePanel_AndTheSidecarEntry()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        CanvasNodeViewModel kills = LiveCanned.Node(vm, "round_kills");
        vm.ToggleViewer(kills);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        vm.ToggleViewer(kills);
        using (Assert.Multiple())
        {
            await Assert.That(kills.IsViewerPinned).IsFalse();
            await Assert.That(kills.ViewerMenuText).IsEqualTo("Pin viewer");
            await Assert.That(kills.LiveViewer).IsNull();
            await Assert.That(kills.ShowsViewer).IsFalse();
            await Assert.That(kills.LiveBadge).IsEqualTo("3").Because("the badge is not the viewer's");
            await Assert.That(Sidecar(rules).IsPinned(LiveCanned.Kills)).IsFalse();
        }
    }

    [Test]
    public async Task TheOverflowItems_PinAndUnpinTheSelection()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        vm.SelectedNodes.Add(LiveCanned.Node(vm, "round_kills"));
        vm.SelectedNodes.Add(LiveCanned.Node(vm, "round_deaths"));
        vm.PinSelectedViewersCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(LiveCanned.Node(vm, "round_kills").IsViewerPinned).IsTrue();
            await Assert.That(LiveCanned.Node(vm, "round_deaths").IsViewerPinned).IsTrue();
            await Assert.That(LiveCanned.Node(vm, "round_kast").IsViewerPinned).IsFalse();
            await Assert.That(Sidecar(rules).Pinned.Count).IsEqualTo(2);
        }

        vm.UnpinSelectedViewersCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Nodes.Any(n => n.IsViewerPinned)).IsFalse();
            await Assert.That(Sidecar(rules).Pinned).IsEmpty();
        }
    }
}
