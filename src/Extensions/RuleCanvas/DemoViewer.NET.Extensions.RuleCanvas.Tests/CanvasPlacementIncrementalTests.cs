#region

using System.Diagnostics;
using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Placement after an edit: a node that has a position keeps it without MSAGL running, and only a
///     node without one is placed, below the arranged ones.
/// </summary>
public class CanvasPlacementIncrementalTests
{
    private static RulesetDocumentNodeKey Key(string id) => new("r", id);

    private static readonly IReadOnlyList<CanvasPlacement.Item> Items =
    [
        new(Key("a"), 200, 100),
        new(Key("b"), 200, 100),
        new(Key("c"), 200, 100)
    ];

    private static readonly IReadOnlyList<RulesetDocumentEdge> Edges = [new(Key("a"), Key("b"), "a")];

    [Test]
    public async Task WithEveryPositionKnown_NothingIsPlaced()
    {
        RulesetNodeLayout known = RulesetNodeLayout.From(
        [
            new(Key("a"), new Point(10, 20)),
            new(Key("b"), new Point(400, 20)),
            new(Key("c"), new Point(10, 300))
        ]);

        IReadOnlyDictionary<RulesetDocumentNodeKey, Point> placed = CanvasPlacement.Place(Items, Edges, known);
        using (Assert.Multiple())
        {
            await Assert.That(placed.Count).IsEqualTo(3);
            await Assert.That(placed[Key("a")]).IsEqualTo(new Point(10, 20));
            await Assert.That(placed[Key("b")]).IsEqualTo(new Point(400, 20));
            await Assert.That(placed[Key("c")]).IsEqualTo(new Point(10, 300));
        }
    }

    [Test]
    public async Task ANodeWithoutAPosition_GoesBelowTheArrangedOnes()
    {
        RulesetNodeLayout known = RulesetNodeLayout.From(
        [
            new(Key("a"), new Point(10, 20)),
            new(Key("b"), new Point(400, 50))
        ]);

        IReadOnlyDictionary<RulesetDocumentNodeKey, Point> placed = CanvasPlacement.Place(Items, Edges, known);
        using (Assert.Multiple())
        {
            await Assert.That(placed[Key("a")]).IsEqualTo(new Point(10, 20));
            await Assert.That(placed[Key("b")]).IsEqualTo(new Point(400, 50));
            await Assert.That(placed[Key("c")].Y).IsGreaterThanOrEqualTo(150 + 60).Because("below the lowest arranged node plus the gap");
        }
    }

    [Test]
    public async Task WithNothingKnown_EverythingIsPlaced()
    {
        IReadOnlyDictionary<RulesetDocumentNodeKey, Point> fresh = CanvasPlacement.Place(Items, Edges);
        IReadOnlyDictionary<RulesetDocumentNodeKey, Point> placed = CanvasPlacement.Place(Items, Edges, RulesetNodeLayout.Empty);
        await Assert.That(placed).IsEquivalentTo(fresh);
    }

    [Test]
    [NotInParallel]
    public async Task AFacetWrite_OnPlayerStats_KeepsEveryNodeWhereItWas_WithoutPlacingAgain()
    {
        using RulesTree rules = RulesetCorpusReplaceTests.CorpusWithUser("player_stats");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "player_stats";
        vm.OnActivated(null!);
        await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("player_stats");
        Dictionary<RulesetDocumentNodeKey, Point> before = vm.Nodes.ToDictionary(n => n.Key, n => n.Location);
        CanvasNodeViewModel stat = vm.Nodes.First(n => n.IsAuthorable && n.Kind == "count" && n.AvailableFacets.Contains("headshot"));

        // The path a write took before this change, replayed: every file read, the chain composed, every node placed.
        Stopwatch clock = Stopwatch.StartNew();
        RulesetCorpus whole = rules.Load();
        CanvasGraphBuilder.Build(whole.Compose("player_stats"));
        long oldPathMs = clock.ElapsedMilliseconds;

        clock.Restart();
        stat.FacetToAdd = "headshot";
        long writeMs = clock.ElapsedMilliseconds;

        string note = $"facet write on player_stats ({vm.Nodes.Count} nodes): {oldPathMs}ms before (full load, compose, place), {writeMs}ms after (replace, compose, no placement)";
        Console.WriteLine("[canvas] " + note);
        CanvasNodeViewModel again = vm.Nodes.Single(n => n.Key.Equals(stat.Key));
        using (Assert.Multiple())
        {
            await Assert.That(again.Facets.Select(f => f.Name)).Contains("headshot");
            await Assert.That(vm.Nodes.Count).IsEqualTo(before.Count);
            await Assert.That(vm.Nodes.All(n => before.TryGetValue(n.Key, out Point at) && at == n.Location)).IsTrue()
                .Because("a redraw after an edit keeps every node where it was");
            await Assert.That(writeMs).IsLessThan(5000).Because(note);
        }
    }
}
