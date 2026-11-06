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
    public async Task SelectingANode_JumpsToTheLineThatDeclaresIt()
    {
        CanvasTabViewModel vm = new(_ => true,
            () => RulesetCorpus.Load([(Path.Combine(RepoRoot(), "rules"), "shipped")]), () => null);
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
            () => RulesetCorpus.Load([(Path.Combine(RepoRoot(), "rules"), "shipped")]), () => null);
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

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
