#region

using System.Diagnostics;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Every shipped ruleset composes and places with nodes and wires, measured warm and printed slowest
///     first, so a ruleset that starts taking seconds on open is a number that moved, not a feeling. The
///     budget is a gross one: a shared runner has stretched a half-second compose past four seconds, and a
///     wall-clock gate that load alone can trip is not a gate.
/// </summary>
public class ShippedCorpusBudgetTests
{
    // Debug build, player_match (230 nodes, 542 wires) is the slowest at about 0.5 s on an idle M-series
    // laptop and has measured 5 s under a load average of 40.
    private const long BudgetMs = 30_000;

    [Test]
    [NotInParallel]
    public async Task EveryShippedRuleset_ComposesAndPlaces_WithinBudget()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        await Assert.That(corpus.Entries.Count).IsGreaterThan(10);
        CanvasGraphBuilder.Build(corpus.Compose("player_round"));

        List<(string Id, long ComposeMs, long PlaceMs, int Nodes, int Wires)> timings = [];
        foreach (RulesetCorpus.Entry entry in corpus.Entries)
        {
            Stopwatch clock = Stopwatch.StartNew();
            RulesetCorpus.Composed composed = corpus.Compose(entry.Id);
            long composeMs = clock.ElapsedMilliseconds;
            clock.Restart();
            (IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires) = CanvasGraphBuilder.Build(composed);
            timings.Add((entry.Id, composeMs, clock.ElapsedMilliseconds, nodes.Count, wires.Count));
        }

        List<(string Id, long ComposeMs, long PlaceMs, int Nodes, int Wires)> slowest =
            [.. timings.OrderByDescending(t => t.ComposeMs + t.PlaceMs).Take(5)];
        string note = $"slowest five of {timings.Count} shipped rulesets (compose + place, warm): " + string.Join("; ",
            slowest.Select(t => $"{t.Id} {t.ComposeMs + t.PlaceMs}ms ({t.ComposeMs} + {t.PlaceMs}, {t.Nodes} nodes, {t.Wires} wires)"));
        Console.WriteLine("[canvas] " + note);

        using (Assert.Multiple())
        {
            foreach ((string id, long composeMs, long placeMs, int nodes, int wires) in timings)
            {
                await Assert.That(nodes).IsGreaterThan(0).Because($"{id} composes to nothing");
                await Assert.That(wires).IsGreaterThan(0).Because($"{id} composes unwired");
                await Assert.That(composeMs + placeMs).IsLessThan(BudgetMs).Because($"{id}: {note}");
            }
        }
    }
}
