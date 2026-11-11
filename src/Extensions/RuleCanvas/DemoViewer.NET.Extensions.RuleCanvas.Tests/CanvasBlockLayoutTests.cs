#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The event-block placement of a big component, measured over the shipped player_stats: the extent
///     reads as a landscape, the structure is blocks between a shared column and the surfaces, the saved
///     arrangement still wins per node, and Reset arrangement comes back to this placement.
///     <para>
///         The crossing count is a ceiling, not a target. Measured as wire pairs
///         whose straight runs (output rail to input row) cross, today's layered placement lands at 1988
///         for player_stats and this one at about 4000: a wide placement sends every scoreboard wire and
///         every shared gate across the blocks between, where the tall single column sent them one layer
///         over. The ceiling here keeps the number from drifting up unnoticed.
///     </para>
/// </summary>
public class CanvasBlockLayoutTests
{
    private const int CrossingsCeiling = 4400;

    private static (IReadOnlyList<CanvasNodeViewModel> Nodes, IReadOnlyList<CanvasConnectionViewModel> Wires) PlayerStats()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        return CanvasGraphBuilder.Build(corpus.Compose("player_stats"));
    }

    [Test]
    [NotInParallel]
    public async Task PlayerStats_LandsAsALandscape_WithoutOverlaps_UnderTheCrossingsCeiling()
    {
        (IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires) = PlayerStats();
        double aspect = LayoutMetrics.AspectOf(nodes);
        int crossings = LayoutMetrics.Crossings(wires);
        Console.WriteLine($"[canvas] player_stats block layout: {nodes.Count} nodes, {wires.Count} wires, aspect {aspect:F2}, crossings {crossings}");

        List<string> overlaps = [];
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                if (LayoutMetrics.Overlap(nodes[i], nodes[j]))
                {
                    overlaps.Add(nodes[i].Id + " over " + nodes[j].Id);
                }
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(nodes.Count).IsGreaterThan(CanvasPlacement.BlockThreshold).Because("player_stats is the big component this layout exists for");
            await Assert.That(aspect).IsGreaterThanOrEqualTo(1.3);
            await Assert.That(aspect).IsLessThanOrEqualTo(3.0);
            await Assert.That(overlaps).IsEmpty();
            await Assert.That(crossings).IsLessThanOrEqualTo(CrossingsCeiling);
        }
    }

    [Test]
    [NotInParallel]
    public async Task PlayerStats_IsBlocks_BetweenTheSharedColumnAndTheSurfaces()
    {
        (IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires) = PlayerStats();
        List<CanvasNodeViewModel> stats = [.. nodes.Where(n => n.IsEntry && !n.IsHighlight)];
        CanvasNodeViewModel show = nodes.Single(n => n.IsShow);
        CanvasNodeViewModel roundActive = nodes.Single(n => n.Ruleset == "~context" && n.Id == "RoundActive");

        using (Assert.Multiple())
        {
            await Assert.That(stats.All(s => s.Location.X + s.EstimatedSize.Width <= show.Location.X)).IsTrue()
                .Because("the scoreboard stands in the last column, right of every stat");
            await Assert.That(nodes.Where(n => n.IsHighlight).All(h => h.Location.X + h.EstimatedSize.Width <= show.Location.X)).IsTrue()
                .Because("highlights share the last columns with the surfaces");
            await Assert.That(stats.All(s => roundActive.Location.X + roundActive.EstimatedSize.Width <= s.Location.X)).IsTrue()
                .Because("a gate shared across blocks stands once in the column at the left");

            // Every event that fires stats stands left of each stat it fires, in the same block.
            foreach (CanvasConnectionViewModel wire in wires.Where(w => w.Source.Owner?.Ruleset == "~event" && w.Target.Owner?.IsEntry == true && !w.IsRead && !w.IsGate))
            {
                CanvasNodeViewModel head = wire.Source.Owner!, stat = wire.Target.Owner!;
                await Assert.That(head.Location.X + head.EstimatedSize.Width).IsLessThanOrEqualTo(stat.Location.X)
                    .Because($"{head.Id} fires {stat.Id} from its left");
            }

            // An enrichment read by one block alone sits in that block's lane, left of its first reader and level with it.
            CanvasNodeViewModel enemyKill = nodes.Single(n => n.Ruleset == "~enrichment" && n.Id == "enrich.kill.was_enemy_kill");
            List<CanvasNodeViewModel> readers = [.. wires.Where(w => ReferenceEquals(w.Source.Owner, enemyKill)).Select(w => w.Target.Owner!)];
            await Assert.That(readers.All(r => enemyKill.Location.X + enemyKill.EstimatedSize.Width <= r.Location.X)).IsTrue();
            await Assert.That(readers.Any(r => enemyKill.Location.Y >= r.Location.Y && enemyKill.Location.Y + enemyKill.EstimatedSize.Height <= r.Location.Y + r.EstimatedSize.Height))
                .IsTrue().Because("the enrichment is seated beside a stat that reads it");
        }
    }

    [Test]
    [NotInParallel]
    public async Task ASavedPosition_StillWins_AndResetArrangement_ComesBackToTheBlockLayout()
    {
        using RulesTree rules = RulesetCorpusReplaceTests.CorpusWithUser("player_stats");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "player_stats";
        vm.OnActivated(null!);
        Dictionary<RulesetDocumentNodeKey, Point> fresh = vm.Nodes.ToDictionary(n => n.Key, n => n.Location);
        CanvasNodeViewModel moved = vm.Nodes.Single(n => n.Id == "TotalEnemyKills");
        moved.Location = new Point(12, 34);
        vm.SaveLayoutCommand.Execute(null);

        CanvasTabViewModel again = rules.NewViewModel();
        again.PreferredRulesetId = "player_stats";
        again.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(again.Nodes.Single(n => n.Id == "TotalEnemyKills").Location).IsEqualTo(new Point(12, 34));
            await Assert.That(again.Nodes.Where(n => n.Id != "TotalEnemyKills").All(n => fresh[n.Key] == n.Location)).IsTrue()
                .Because("every other node keeps the block layout's place");
        }

        again.ResetLayoutCommand.Execute(null);
        await Assert.That(again.Nodes.All(n => fresh[n.Key] == n.Location)).IsTrue().Because("Reset arrangement is the block layout again");
    }

    [Test]
    public async Task ASmallComponent_KeepsTheLayeredPlacement()
    {
        // Thirty one-node chains under the threshold: MSAGL's layers put the source left of the sink and the two sinks level.
        RulesetDocumentNodeKey a = new("r", "a"), b = new("r", "b"), c = new("r", "c");
        IReadOnlyList<CanvasPlacement.Item> items = [new(a, 200, 100), new(b, 200, 100), new(c, 200, 100)];
        IReadOnlyList<RulesetDocumentEdge> edges = [new(a, b, "a"), new(a, c, "a")];
        IReadOnlyDictionary<RulesetDocumentNodeKey, Point> placed = CanvasPlacement.Place(items, edges);
        using (Assert.Multiple())
        {
            await Assert.That(placed[a].X).IsLessThan(placed[b].X);
            await Assert.That(placed[b].X).IsEqualTo(placed[c].X).Because("one layer, one left edge");
        }
    }
}
