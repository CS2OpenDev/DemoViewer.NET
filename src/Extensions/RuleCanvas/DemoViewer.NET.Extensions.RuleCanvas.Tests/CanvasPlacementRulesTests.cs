#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Two rules of the first placement, measured on player_round: no face sits on the midpoint of a wire
///     that is not its own, and an enrichment or context flag that feeds one stat alone sits beside that
///     stat rather than in the scaffold column.
/// </summary>
public class CanvasPlacementRulesTests
{
    private static (IReadOnlyList<CanvasNodeViewModel> Nodes, IReadOnlyList<CanvasConnectionViewModel> Wires) PlayerRound()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        return CanvasGraphBuilder.Build(corpus.Compose("player_round"));
    }

    [Test]
    public async Task PlayerRound_HasNoWireMidpoint_InsideAForeignNode()
    {
        (IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires) = PlayerRound();
        List<(CanvasConnectionViewModel Wire, CanvasNodeViewModel Under)> hits = LayoutMetrics.MidpointHits(nodes, wires);
        await Assert.That(hits.Select(h => $"{h.Wire.Source.Owner?.Id} -> {h.Wire.Target.Owner?.Id} under {h.Under.Id}")).IsEmpty();
    }

    [Test]
    public async Task TheMidpointRule_NudgesTheFaceUnderAWire_AndTheColumnBelowIt()
    {
        // a feeds c across b, whose box holds the wire's midpoint; d sits under b in the same column.
        RulesetDocumentNodeKey a = new("r", "a"), b = new("r", "b"), c = new("r", "c"), d = new("r", "d");
        Dictionary<RulesetDocumentNodeKey, CanvasPlacement.Item> items = new()
        {
            [a] = new CanvasPlacement.Item(a, 200, 100),
            [b] = new CanvasPlacement.Item(b, 200, 100),
            [c] = new CanvasPlacement.Item(c, 200, 100),
            [d] = new CanvasPlacement.Item(d, 200, 100)
        };
        Dictionary<RulesetDocumentNodeKey, Point> local = new()
        {
            [a] = new Point(0, 0),
            [b] = new Point(300, 0),
            [c] = new Point(600, 0),
            [d] = new Point(300, 140)
        };

        CanvasPlacement.AvoidMidpoints(local, items, [new RulesetDocumentEdge(a, c, "a")], 40);
        using (Assert.Multiple())
        {
            await Assert.That(local[b]).IsEqualTo(new Point(300, 140)).Because("b moved down by its height plus the row gap");
            await Assert.That(local[d]).IsEqualTo(new Point(300, 280)).Because("the column under b went with it");
            await Assert.That(local[a]).IsEqualTo(new Point(0, 0));
            await Assert.That(local[c]).IsEqualTo(new Point(600, 0));
        }
    }

    [Test]
    public async Task PlayerRound_SeatsSingleFeeders_BesideTheirStat_AndShortensTheScaffoldColumn()
    {
        (IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires) = PlayerRound();
        List<CanvasNodeViewModel> feeders = [.. nodes.Where(n => n.Ruleset is "~enrichment" or "~context")];

        // Every feeder with exactly one reader, and that reader.
        Dictionary<CanvasNodeViewModel, CanvasNodeViewModel> seated = new();
        foreach (CanvasNodeViewModel feeder in feeders)
        {
            List<CanvasNodeViewModel> readers = [.. wires.Where(w => ReferenceEquals(w.Source.Owner, feeder)).Select(w => w.Target.Owner!).Distinct()];
            if (readers.Count == 1 && readers[0].IsEntry)
            {
                seated[feeder] = readers[0];
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(seated.Keys.Select(f => f.Id)).Contains("MatchLive").Because("it gates round_deaths alone");
            await Assert.That(seated.Keys.Select(f => f.Id)).Contains("enrich.hurt.capped_damage");
            await Assert.That(seated.Keys.Select(f => f.Id)).DoesNotContain("enrich.kill.was_enemy_kill").Because("two stats read it");
            foreach ((CanvasNodeViewModel feeder, CanvasNodeViewModel stat) in seated)
            {
                double gap = stat.Location.X - (feeder.Location.X + feeder.EstimatedSize.Width);
                await Assert.That(gap).IsEqualTo(CanvasPlacement.SeatGap).Within(1).Because($"{feeder.Id} sits beside {stat.Id}, positions rounded to the pixel");
                await Assert.That(feeder.Location.Y).IsGreaterThanOrEqualTo(stat.Location.Y);
                await Assert.That(feeder.Location.Y + feeder.EstimatedSize.Height).IsLessThanOrEqualTo(stat.Location.Y + stat.EstimatedSize.Height)
                    .Because($"{feeder.Id} is level with {stat.Id}");
            }

            // The column is what is left in it: the feeders read from more than one place.
            double column = LayoutMetrics.ExtentOf(feeders.Where(f => !seated.ContainsKey(f))).Height;
            double all = LayoutMetrics.ExtentOf(feeders).Height;
            await Assert.That(column).IsLessThanOrEqualTo(all * 2 / 3).Because($"the scaffold column is {column:F0} of {all:F0} tall");
        }
    }

    [Test]
    public async Task SingleFeeders_AreThoseWithOneReaderThatIsAStat()
    {
        RulesetDocumentNodeKey stat = new("r", "s"), other = new("r", "t");
        RulesetDocumentNodeKey one = new("~context", "One"), two = new("~context", "Two"), chain = new("~enrichment", "enrich.x");
        IReadOnlyList<CanvasPlacement.Item> items =
        [
            new(stat, 300, 200), new(other, 300, 200), new(one, 200, 30), new(two, 200, 30), new(chain, 200, 30)
        ];
        IReadOnlyList<RulesetDocumentEdge> edges =
        [
            new(one, stat, "while"), new(two, stat, "reads"), new(two, other, "reads"), new(chain, one, "on")
        ];

        Dictionary<RulesetDocumentNodeKey, List<CanvasPlacement.Item>> seated = CanvasPlacement.SingleFeeders(items, edges);
        using (Assert.Multiple())
        {
            await Assert.That(seated.Keys).IsEquivalentTo([stat]);
            await Assert.That(seated[stat].Select(i => i.Key)).IsEquivalentTo([one]).Because("two is read twice and chain feeds a context, not a stat");
        }
    }
}
