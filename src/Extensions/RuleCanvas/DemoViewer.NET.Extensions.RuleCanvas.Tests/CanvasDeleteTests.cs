#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Delete: an entry something still names is refused with what names it, and a selection nothing
///     names goes in one write, so one undo brings it all back.
/// </summary>
public class CanvasDeleteTests
{
    private static GalleryItem Item(CanvasTabViewModel vm, string group, string name) =>
        vm.Gallery.Single(g => g.Title == group).Items.Single(i => i.Name == name);

    [Test]
    public async Task Dependants_NameTheReadersAndTheShowEntries()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        RulesetCorpus.Composed composed = rules.Load().Compose("player_round");

        NodeDependants kills = CanvasEditSemantics.Dependants(vm.Nodes.Single(n => n.Id == "round_kills"), composed);
        NodeDependants kast = CanvasEditSemantics.Dependants(vm.Nodes.Single(n => n.Id == "round_kast"), composed);
        using (Assert.Multiple())
        {
            await Assert.That(kills.Readers).IsEquivalentTo(["round_kast"]);
            await Assert.That(kills.Tables).IsEquivalentTo(["player_round"]);
            await Assert.That(kills.Scoreboard).IsFalse();
            await Assert.That(kills.Describe()).IsEqualTo("round_kills is read by round_kast and shown in table player_round");
            await Assert.That(kast.Readers).IsEmpty();
            await Assert.That(kast.Describe()).IsEqualTo("round_kast is shown in table player_round");
        }
    }

    [Test]
    public async Task AHighlightTheScoreboardShows_IsADependant_ThroughItsCount()
    {
        using RulesTree rules = RulesTree.WithUser("highlights_aim");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "highlights_aim";
        vm.OnActivated(null!);
        RulesetCorpus.Composed composed = rules.Load().Compose("highlights_aim");

        NodeDependants noScope = CanvasEditSemantics.Dependants(vm.Nodes.Single(n => n.Id == "no_scope"), composed);
        using (Assert.Multiple())
        {
            await Assert.That(noScope.Scoreboard).IsTrue();
            await Assert.That(noScope.Describe()).IsEqualTo("no_scope is shown on the scoreboard");
        }
    }

    [Test]
    public async Task DeletingAnEntrySomethingNames_IsRefused_AndTheFileIsUntouched()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        string before = File.ReadAllText(rules.UserPath("player_round"));

        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "round_kills"));
        vm.DeleteSelectedCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.EditStatus).IsEqualTo("round_kills is read by round_kast and shown in table player_round");
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
            await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("round_kills");
            await Assert.That(vm.CanUndo).IsFalse();
        }
    }

    [Test]
    public async Task ASelectionNothingNames_GoesInOneWrite()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.PlaceItem(Item(vm, "Prefabs", "enemy_kills"), new Point(0, 0));
        vm.PlaceItem(Item(vm, "Prefabs", "deaths"), new Point(400, 0));
        string both = File.ReadAllText(rules.UserPath("player_round"));

        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "enemy_kills"));
        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "deaths"));
        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "player_death"));
        vm.DeleteSelectedCommand.Execute(null);
        string after = File.ReadAllText(rules.UserPath("player_round"));
        using (Assert.Multiple())
        {
            await Assert.That(vm.EditStatus).IsEqualTo("");
            await Assert.That(after).DoesNotContain("  enemy_kills:");
            await Assert.That(after).DoesNotContain("  deaths:");
            await Assert.That(after).Contains("  round_deaths:");
            await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("player_death").Because("an engine node is not an entry");
        }

        vm.UndoCommand.Execute(null);
        await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(both).Because("one write, one undo");
    }

    [Test]
    public async Task ASelectionThatReadsItself_IsDeletable_ButNotHalfOfIt()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.PlaceItem(Item(vm, "Prefabs", "enemy_kills"), new Point(0, 0));
        vm.PlaceItem(Item(vm, "Highlights", "highlight"), new Point(400, 0));
        vm.Connect(vm.Nodes.Single(n => n.Id == "enemy_kills"), vm.Nodes.Single(n => n.Id == "highlight"));
        await Assert.That(RulesetEdits.HighlightField(File.ReadAllText(rules.UserPath("player_round")), "highlight", "when")).Contains("enemy_kills");

        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "enemy_kills"));
        vm.DeleteSelectedCommand.Execute(null);
        await Assert.That(vm.EditStatus).IsEqualTo("enemy_kills is read by highlight");
        await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("enemy_kills");

        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "highlight"));
        vm.DeleteSelectedCommand.Execute(null);
        string after = File.ReadAllText(rules.UserPath("player_round"));
        using (Assert.Multiple())
        {
            await Assert.That(vm.EditStatus).IsEqualTo("");
            await Assert.That(after).DoesNotContain("enemy_kills:");
            await Assert.That(after).DoesNotContain("  highlight:");
        }
    }

    [Test]
    public async Task Delete_WithNothingAuthorableSelected_IsIgnored()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        string before = File.ReadAllText(rules.UserPath("player_round"));

        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "player_death"));
        vm.DeleteSelectedCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.EditStatus).IsEqualTo("");
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
        }
    }
}
