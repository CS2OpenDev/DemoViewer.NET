#region

using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Live values on a show node's columns, with the canned run: each column carries the surfaced stat's
///     badge and tip for the selected instance, follows the instance and the playhead with the header
///     badges, greys with them when the run is stale, and clears with them when the run goes.
/// </summary>
public class CanvasTabViewModelShowColumnTests
{
    private static CanvasNodeViewModel Table(CanvasTabViewModel vm) => vm.Nodes.Single(n => n.IsShow && n.Columns.Count > 0);

    private static CanvasColumnViewModel Column(CanvasTabViewModel vm, string stat) => Table(vm).Columns.Single(c => c.Stat == stat);

    [Test]
    public async Task EachColumn_ReadsItsStatsTrack_ForTheSelectedInstance()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        await Assert.That(Table(vm).Columns.All(c => !c.HasLive)).IsTrue();

        vm.ShowLiveRun(LiveCanned.Run(vm));
        using (Assert.Multiple())
        {
            await Assert.That(Column(vm, "round_kills").LiveBadge).IsEqualTo("3");
            await Assert.That(Column(vm, "round_kills").LiveTip).IsEqualTo("Alice: fired 3 times, first tick 100, last tick 900, final 3");
            await Assert.That(Column(vm, "round_deaths").LiveBadge).IsEqualTo("2");
            await Assert.That(Column(vm, "round_kast").LiveBadge).IsEqualTo("off").Because("the surfaced stat is a flag, off at the end of the canned run");
            await Assert.That(Column(vm, "round_damage").HasLive).IsFalse().Because("the run has no track for it, and a column says nothing rather than no data");
            await Assert.That(Column(vm, "round_side").HasLive).IsFalse();
            await Assert.That(Table(vm).HasLive).IsFalse().Because("the table itself has no track; its columns speak");
        }

        vm.SelectedInstance = vm.Instances.Single(i => i.Name == "Bob");
        using (Assert.Multiple())
        {
            await Assert.That(Column(vm, "round_kills").LiveBadge).IsEqualTo("1");
            await Assert.That(Column(vm, "round_deaths").HasLive).IsFalse().Because("Bob has no deaths track");
        }
    }

    [Test]
    public async Task TheColumns_FollowThePlayhead_AndGreyWhenStale_AndClearWithTheRun()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        CanvasColumnViewModel kills = Column(vm, "round_kills");
        shell.MoveTo(300);
        using (Assert.Multiple())
        {
            await Assert.That(kills.LiveBadge).IsEqualTo("1");
            await Assert.That(kills.LiveTip).StartsWith("at tick 300: 1");
            await Assert.That(Column(vm, "round_kast").LiveBadge).IsEqualTo("on");
        }

        shell.MoveTo(50);
        await Assert.That(kills.LiveBadge).IsEqualTo("-").Because("before the first fire");

        LiveCanned.Node(vm, "round_kills").Facets.Single(f => f.Name == "enemy").Toggle = false;
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsLiveStale).IsTrue();
            await Assert.That(Column(vm, "round_kills").IsLiveStale).IsTrue();
            await Assert.That(Column(vm, "round_kills").LiveBadge).IsEqualTo("-");
        }

        vm.ClearLive();
        await Assert.That(Table(vm).Columns.All(c => !c.HasLive && !c.IsLiveStale)).IsTrue();
    }
}
