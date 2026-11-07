#region

using System.Diagnostics;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Every shipped ruleset composes and places inside one budget, measured warm and printed slowest
///     first, so a ruleset that starts taking seconds on open is a number that moved, not a feeling.
/// </summary>
public class ShippedCorpusBudgetTests
{
    // Debug build, player_stats measured at about 1.3 s on an M-series laptop with two other builds running.
    private const long BudgetMs = 4000;

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
            foreach ((string id, long composeMs, long placeMs, _, _) in timings)
            {
                await Assert.That(composeMs + placeMs).IsLessThan(BudgetMs).Because($"{id}: {note}");
            }
        }
    }
}
