#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     A logic wire is labelled with the operand as the author wrote it, not as the engine lowers it. The
///     engine's text stays on the edge for the hover, and the editor opens the row the label shows.
/// </summary>
public class CanvasWireLabelTests
{
    [Test]
    public async Task ALogicWire_ReadsTheAuthoredRow_AndKeepsTheLoweringForTheHover()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasConnectionViewModel kills = vm.Connections.Single(c => c.Source.Owner?.Id == "round_kills" && c.Target.Owner?.Id == "round_kast");
        CanvasConnectionViewModel survived = vm.Connections.Single(c => c.Source.Owner?.Id == "Survived" && c.Target.Owner?.Id == "round_kast");
        RulesetChainGraph chain = RulesetCorpus.Load([(rules.ShippedDirectory, "shipped"), (rules.UserDirectory, "user")]).Compose("player_round").Chain;
        ChainEdge edge = chain.Edges.Single(e => e.Source.Id == "round_kills" && e.Target.Id == "round_kast");
        using (Assert.Multiple())
        {
            await Assert.That(kills.Text).IsEqualTo("or round_kills > 0").Because("the when: names the sibling, the engine says value");
            await Assert.That(survived.Text).IsEqualTo("or player.survived").Because("the context flag is spelled by its gate path");
            await Assert.That(edge.Authored).IsEqualTo("round_kills > 0");
            await Assert.That(edge.Condition).IsNotNull().And.IsNotEqualTo(edge.Authored).Because("the hover keeps the engine's lowering");
            await Assert.That(kills.Condition).IsEqualTo(edge.Condition);
            await Assert.That(vm.Connections.Where(c => c.IsOr || c.IsAnd).Select(c => c.Text))
                .Contains(t => !t.Contains("value", StringComparison.Ordinal))
                .Because("no logic wire into the open document shows the lowered value spelling");
        }
    }

    [Test]
    public async Task AuthoredRow_PrefersTheSpelledRow_ThenThePathWithTheSameTest_ThenThePath()
    {
        const string when = "round_kills > 2 or round_kills > 5 or player.survived";
        using (Assert.Multiple())
        {
            await Assert.That(ChainWiring.AuthoredRow(when, "round_kills", "round_kills > 5")?.Text).IsEqualTo("round_kills > 5");
            await Assert.That(ChainWiring.AuthoredRow(when, "round_kills", "value > 5")?.Text).IsEqualTo("round_kills > 5");
            await Assert.That(ChainWiring.AuthoredRow(when, "round_kills", "value >= 9")?.Text).IsEqualTo("round_kills > 2").Because("the first row on the path");
            await Assert.That(ChainWiring.AuthoredRow(when, "player.survived", "active")?.Text).IsEqualTo("player.survived");
            await Assert.That(ChainWiring.AuthoredRow(when, "round_deaths", "value > 0")).IsNull();
            await Assert.That(ChainWiring.AuthoredRow("(a > 1 or b > 2) and c", "a", "value > 1")).IsNull().Because("a nested when: has no rows");
            await Assert.That(ChainWiring.AuthoredRow("", "a", "value > 1")).IsNull();
        }
    }
}
