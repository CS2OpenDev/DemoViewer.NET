#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Freeze value as fixture on the tab: the block lands above the entry through the session so Undo takes
///     it back, a second freeze replaces it, and without a run, a track or a writable file the toast says why.
/// </summary>
public class CanvasTabViewModelFixtureTests
{
    private static readonly DateOnly _day = new(2026, 11, 9);

    [Test]
    public async Task Freeze_WritesTheBlockAboveTheEntry_AndUndoTakesItBack()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        vm.Today = () => _day;
        vm.ShowLiveRun(LiveCanned.Run(vm));
        string path = rules.UserPath("player_round");

        vm.FreezeFixture(LiveCanned.Node(vm, "round_kills"));
        string text = File.ReadAllText(path);
        using (Assert.Multiple())
        {
            await Assert.That(text).Contains("  # fixture: demo one, Alice\n  # fixture: final 3, fired 3 times, first tick 100, last tick 900\n  # fixture: taken 2026-11-09\n  round_kills:\n");
            await Assert.That(vm.DocumentText).IsEqualTo(text).Because("the pane shows the file as written");
            await Assert.That(vm.EditStatus).IsEqualTo("");
            await Assert.That(LiveCanned.Node(vm, "round_kills").LiveBadge).IsEqualTo("3").Because("the badges stay through the redraw");
            await Assert.That(vm.CanUndo).IsTrue();
        }

        vm.SelectedInstance = vm.Instances.Single(i => i.Name == "Bob");
        vm.FreezeFixture(LiveCanned.Node(vm, "round_kills"));
        string again = File.ReadAllText(path);
        using (Assert.Multiple())
        {
            await Assert.That(again.Split("# fixture:").Length - 1).IsEqualTo(3).Because("replaced, not stacked");
            await Assert.That(again).Contains("# fixture: demo one, Bob\n");
            await Assert.That(RulesetFixtureComment.Read(again, EntrySection.Stat, "round_kills")[1]).IsEqualTo("final 1, fired 1 times, first tick 500, last tick 500");
        }

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        await Assert.That(File.ReadAllText(path)).DoesNotContain("# fixture:");
    }

    [Test]
    public async Task Freeze_SaysWhyItWroteNothing()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        string path = rules.UserPath("player_round");
        string before = File.ReadAllText(path);

        vm.FreezeFixture(LiveCanned.Node(vm, "round_kills"));
        await Assert.That(vm.Toast?.Message).IsEqualTo("No run yet: evaluate the demo before freezing a fixture.");

        vm.ShowLiveRun(LiveCanned.Run(vm));
        vm.FreezeFixture(LiveCanned.Node(vm, "round_damage"));
        await Assert.That(vm.Toast?.Message).IsEqualTo("round_damage has no data in the run; nothing to freeze.");

        vm.SourceEdited(before + "\n# typing");
        vm.FreezeFixture(LiveCanned.Node(vm, "round_kills"));
        await Assert.That(vm.Toast?.Message).IsEqualTo("Save or revert the YAML tab before freezing a fixture.");
        await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
    }

    [Test]
    public async Task Freeze_OnAShippedRuleset_IsRefused()
    {
        using RulesTree rules = RulesTree.WithShipped("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        vm.ShowLiveRun(LiveCanned.Run(vm));
        string path = rules.ShippedPath("player_round");
        string before = File.ReadAllText(path);
        vm.FreezeFixture(LiveCanned.Node(vm, "round_kills"));
        using (Assert.Multiple())
        {
            await Assert.That(vm.Toast?.Message).IsEqualTo("A shipped ruleset is read only here; Edit a copy first.");
            await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
        }
    }

    [Test]
    public async Task TheRequest_RefusesANodeThatIsNotAnEntry()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
        CanvasNodeViewModel scaffold = vm.Nodes.First(n => n.IsScaffold);
        EditRequest request = CanvasEditSemantics.FreezeFixture(scaffold, ["demo one, Alice"]);
        using (Assert.Multiple())
        {
            await Assert.That(request.IsRefused).IsTrue();
            await Assert.That(request.Refusal).Contains("your own ruleset");
            await Assert.That(CanvasEditSemantics.FreezeFixture(LiveCanned.Node(vm, "round_kills"), ["x"]).Description).IsEqualTo("freeze round_kills as a fixture");
        }
    }
}
