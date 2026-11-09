#region

using Avalonia.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Seek to a fire, with a canned run and a fake shell clock: the menu items and the bracket keys move
///     the shell's clock to the next, previous or first fire of the selected instance's track, say why
///     they cannot, say when the list was thinned, and route an event node through the host's navigator.
/// </summary>
public class CanvasTabViewModelSeekTests
{
    [Test]
    public async Task NextPreviousAndFirst_SeekTheShell_FromThePlayhead()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        CanvasNodeViewModel kills = LiveCanned.Node(vm, "round_kills");
        shell.MoveTo(300);

        vm.NextFireCommand.Execute(kills);
        vm.PreviousFireCommand.Execute(kills);
        vm.FirstFireCommand.Execute(kills);
        await Assert.That(shell.Seeks).IsEquivalentTo([500, 100, 100]);

        shell.Seeks.Clear();
        shell.MoveTo(500);
        vm.NextFireCommand.Execute(kills);
        vm.PreviousFireCommand.Execute(kills);
        await Assert.That(shell.Seeks).IsEquivalentTo([900, 100]).Because("a fire at the playhead is neither next nor previous");

        shell.Seeks.Clear();
        shell.MoveTo(900);
        vm.NextFireCommand.Execute(kills);
        await Assert.That(shell.Seeks).IsEmpty().Because("nothing after the last fire");

        shell.Seeks.Clear();
        shell.MoveTo(400);
        vm.NextFireCommand.Execute(LiveCanned.GateInto(vm, "round_deaths"));
        vm.NextFireCommand.Execute(LiveCanned.TriggerInto(vm, "round_deaths"));
        await Assert.That(shell.Seeks).IsEquivalentTo([700, 700]).Because("a wire seeks its own ticks, or its target's when it has none");
    }

    [Test]
    public async Task TheBracketKeys_SeekOnTheSelection()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        shell.MoveTo(300);
        using (Assert.Multiple())
        {
            await Assert.That(CanvasKeyMap.Resolve(Key.OemCloseBrackets, KeyModifiers.None)).IsEqualTo(CanvasKeyAction.NextFire);
            await Assert.That(CanvasKeyMap.Resolve(Key.OemOpenBrackets, KeyModifiers.None)).IsEqualTo(CanvasKeyAction.PreviousFire);
            await Assert.That(CanvasKeyMap.Resolve(Key.OemCloseBrackets, KeyModifiers.Control)).IsEqualTo(CanvasKeyAction.None);
        }

        vm.NextFireCommand.Execute(null);
        await Assert.That(shell.Seeks).IsEmpty().Because("nothing selected, nothing to seek on");

        vm.SelectedNodes.Add(LiveCanned.Node(vm, "round_kills"));
        vm.NextFireCommand.Execute(null);
        vm.PreviousFireCommand.Execute(null);
        await Assert.That(shell.Seeks).IsEquivalentTo([500, 100]);

        vm.SelectedNodes.Clear();
        vm.SelectedWires.Add(LiveCanned.GateInto(vm, "round_deaths"));
        shell.Seeks.Clear();
        vm.NextFireCommand.Execute(null);
        await Assert.That(shell.Seeks).IsEquivalentTo([700]);
    }

    [Test]
    public async Task TheSeekMenu_SaysWhyAnItemIsOff()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules);
        CanvasNodeViewModel kills = LiveCanned.Node(vm, "round_kills");
        vm.RefreshSeekMenu(kills);
        using (Assert.Multiple())
        {
            await Assert.That(kills.SeekMenu.NextText).IsEqualTo("Next fire (no run yet)");
            await Assert.That(kills.SeekMenu.CanNext).IsFalse();
            await Assert.That(kills.SeekMenu.CanPrevious).IsFalse();
            await Assert.That(kills.SeekMenu.FirstText).IsEqualTo("First fire (no run yet)");
        }

        vm.ShowLiveRun(LiveCanned.Run(vm));
        shell.MoveTo(900);
        vm.RefreshSeekMenu(kills);
        using (Assert.Multiple())
        {
            await Assert.That(kills.SeekMenu.NextText).IsEqualTo("Next fire (none after tick 900)");
            await Assert.That(kills.SeekMenu.CanNext).IsFalse();
            await Assert.That(kills.SeekMenu.PreviousText).IsEqualTo("Previous fire");
            await Assert.That(kills.SeekMenu.CanPrevious).IsTrue();
            await Assert.That(kills.SeekMenu.FirstText).IsEqualTo("First fire");
            await Assert.That(kills.SeekMenu.CanFirst).IsTrue();
        }

        shell.MoveTo(100);
        vm.RefreshSeekMenu(kills);
        await Assert.That(kills.SeekMenu.PreviousText).IsEqualTo("Previous fire (none before tick 100)");

        CanvasNodeViewModel damage = LiveCanned.Node(vm, "round_damage");
        vm.RefreshSeekMenu(damage);
        await Assert.That(damage.SeekMenu.NextText).IsEqualTo("Next fire (no data)").Because("the run says nothing about it");

        CanvasConnectionViewModel gate = LiveCanned.GateInto(vm, "round_deaths");
        shell.MoveTo(300);
        vm.RefreshSeekMenu(gate);
        using (Assert.Multiple())
        {
            await Assert.That(gate.SeekMenu.NextText).IsEqualTo("Next fire");
            await Assert.That(gate.SeekMenu.PreviousText).IsEqualTo("Previous fire (none before tick 300)");
        }

        vm.SelectedInstance = vm.Instances.Single(i => i.Name == "Bob");
        vm.RefreshSeekMenu(LiveCanned.Node(vm, "round_deaths"));
        await Assert.That(LiveCanned.Node(vm, "round_deaths").SeekMenu.FirstText).IsEqualTo("First fire (no data)").Because("Bob has no deaths track");
    }

    [Test]
    public async Task ASampledTrack_SaysSoInTheMenu()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules);
        LiveValuesRun run = LiveCanned.Run(vm);
        Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> nodes = new(run.Nodes)
        {
            [(LiveCanned.Kills, 1)] = new NodeTrack(5000, 0, 49_990, "5000", 5000f, true)
            {
                Series = ValueSeries.From([.. Enumerable.Range(0, 5000).Select(i => new ValuePoint(i * 10, true, i + 1, null))])
            }
        };
        vm.ShowLiveRun(run with { Nodes = nodes });
        CanvasNodeViewModel kills = LiveCanned.Node(vm, "round_kills");
        shell.MoveTo(25_000);
        vm.RefreshSeekMenu(kills);
        using (Assert.Multiple())
        {
            await Assert.That(kills.SeekMenu.NextText).IsEqualTo("Next fire (sampled)");
            await Assert.That(kills.SeekMenu.CanNext).IsTrue();
            await Assert.That(kills.SeekMenu.FirstText).IsEqualTo("First fire (sampled)");
        }

        vm.FirstFireCommand.Execute(kills);
        await Assert.That(shell.Seeks).IsEquivalentTo([0]).Because("the first fire survives the thinning");
    }

    [Test]
    public async Task WithoutADemo_EveryItemIsOff_AndNothingSeeks()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules, false);
        CanvasNodeViewModel kills = LiveCanned.Node(vm, "round_kills");
        vm.RefreshSeekMenu(kills);
        vm.NextFireCommand.Execute(kills);
        using (Assert.Multiple())
        {
            await Assert.That(kills.SeekMenu.NextText).IsEqualTo("Next fire (no demo open)");
            await Assert.That(kills.SeekMenu.CanNext).IsFalse();
            await Assert.That(kills.SeekMenu.CanFirst).IsFalse();
            await Assert.That(shell.Seeks).IsEmpty();
        }
    }

    [Test]
    public async Task AnEventNode_GoesThroughTheHostsEventNavigator_WithoutARun()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        FakeModuleContext context = new();
        (CanvasTabViewModel vm, FakeShell shell) = LiveCanned.Open(rules, context: context);
        CanvasNodeViewModel death = vm.Nodes.Single(n => n.IsScaffold && n.Id == "player_death");
        vm.RefreshSeekMenu(death);
        using (Assert.Multiple())
        {
            await Assert.That(death.Events).IsEquivalentTo(["player_death"]);
            await Assert.That(death.SeekMenu.NextText).IsEqualTo("Next event");
            await Assert.That(death.SeekMenu.CanNext).IsTrue();
            await Assert.That(death.SeekMenu.PreviousText).IsEqualTo("Previous event");
            await Assert.That(death.SeekMenu.FirstText).IsEqualTo("First fire (events are not tracked)");
            await Assert.That(death.SeekMenu.CanFirst).IsFalse();
        }

        vm.NextFireCommand.Execute(death);
        vm.PreviousFireCommand.Execute(death);
        vm.FirstFireCommand.Execute(death);
        using (Assert.Multiple())
        {
            await Assert.That(context.NextEvents.Select(n => string.Join(",", n))).IsEquivalentTo(["player_death"]);
            await Assert.That(context.PrevEvents.Select(n => string.Join(",", n))).IsEquivalentTo(["player_death"]);
            await Assert.That(shell.Seeks).IsEmpty().Because("an event seek is the navigator's, not a tick");
        }

        CanvasTabViewModel bare = rules.NewViewModel();
        bare.OnActivated(null!);
        CanvasNodeViewModel noHost = bare.Nodes.Single(n => n.IsScaffold && n.Id == "player_death");
        bare.RefreshSeekMenu(noHost);
        await Assert.That(noHost.SeekMenu.NextText).IsEqualTo("Next event (no host navigator)");
    }
}
