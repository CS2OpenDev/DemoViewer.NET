#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Selecting a node names the line that declares it, in the text the source pane shows: the line the
///     resolver recorded on the checked stat is the line the YAML file has the entry on.
/// </summary>
public class CanvasTabViewModelJumpTests
{
    [Test]
    public async Task SelectingAFoldedDependency_JumpsToItsUseLine()
    {
        CanvasTabViewModel vm = new(_ => true,
            () => RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]), () => null);
        vm.PreferredRulesetId = "player_stats";
        List<int> jumps = [];
        vm.JumpRequested += jumps.Add;
        vm.OnActivated(null!);

        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.IsDependency));
        string[] lines = vm.DocumentText.Split('\n');
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedLine).IsGreaterThan(0);
            await Assert.That(lines[vm.SelectedLine - 1]).StartsWith("use: [ kast ]");
            await Assert.That(jumps).IsEquivalentTo([vm.SelectedLine]);
            await Assert.That(RulesetEntryText.UseLineOf("use:\n  - a\n  - kast\n", "kast")).IsEqualTo(3);
            await Assert.That(RulesetEntryText.UseLineOf("ruleset: x\n", "kast")).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ADefine_ListsTheStatsFiringOnIt_AndEachSelectsItsStat()
    {
        CanvasTabViewModel vm = new(_ => true,
            () => RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]), () => null);
        vm.PreferredRulesetId = "highlights_clutch";
        vm.OnActivated(null!);

        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.IsDefine && n.Title == "ct_retake_kill"));
        using (Assert.Multiple())
        {
            await Assert.That(vm.Inspector.HasFiredBy).IsTrue();
            await Assert.That(vm.Inspector.FiredBy).Contains("retake_kills");
            await Assert.That(vm.Inspector.FiredByHeading).IsEqualTo(vm.Inspector.FiredBy.Count == 1 ? "fired by 1 stat" : $"fired by {vm.Inspector.FiredBy.Count} stats");
        }

        vm.Inspector.SelectFiringCommand.Execute("retake_kills");
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Select(n => n.Id)).IsEquivalentTo(["retake_kills"]);
            await Assert.That(vm.Inspector.HasFiredBy).IsFalse().Because("a stat lists nothing firing on it");
        }
    }

    [Test]
    public async Task SelectingANode_JumpsToTheLineThatDeclaresIt()
    {
        CanvasTabViewModel vm = new(_ => true,
            () => RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]), () => null);
        vm.PreferredRulesetId = "player_round";
        List<int> jumps = [];
        vm.JumpRequested += jumps.Add;
        vm.OnActivated(null!);

        CanvasNodeViewModel kills = vm.Nodes.Single(n => n.Id == "round_kills");
        vm.SelectedNodes.Add(kills);

        string[] lines = vm.DocumentText.Split('\n');
        using (Assert.Multiple())
        {
            await Assert.That(vm.DocumentName).IsEqualTo("player_round.rules.yaml");
            await Assert.That(jumps).IsEquivalentTo([kills.SourceLine]);
            await Assert.That(vm.SelectedLine).IsEqualTo(kills.SourceLine);
            await Assert.That(kills.SourceLine).IsGreaterThan(0);
            await Assert.That(lines[kills.SourceLine - 1].Trim()).StartsWith("round_kills:")
                .Because("the position the resolver kept is the entry's own line in the file");
        }

        vm.SelectedNodes.Clear();
        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "round_kast"));
        await Assert.That(lines[vm.SelectedLine - 1].Trim()).StartsWith("round_kast:");
    }

    [Test]
    public async Task ChangingTheRuleset_ClearsTheSelectionAndTheText()
    {
        CanvasTabViewModel vm = new(_ => true,
            () => RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]), () => null);
        vm.PreferredRulesetId = "player_round";
        vm.OnActivated(null!);
        vm.SelectedNodes.Add(vm.Nodes[0]);
        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "weapon_stats");
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedNodes).IsEmpty();
            await Assert.That(vm.SelectedLine).IsEqualTo(0);
            await Assert.That(vm.DocumentName).IsEqualTo("weapon_stats.rules.yaml");
            await Assert.That(vm.DocumentText).Contains("ruleset: weapon_stats");
        }
    }
}
