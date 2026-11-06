#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The lowered chain for player_round, which is the picture the Workbench's graph pane draws: the events
///     split out of the root, the gates and enrichments the stats run through, and the wires between them,
///     with the builder's companions folded away.
/// </summary>
public class RulesetChainGraphTests
{
    private static RulesetCorpus.Composed Compose(string id)
    {
        string shipped = RuleSetLocator.ResolveShippedRulesDirectory();
        return RulesetCorpus.Load([(shipped, "shipped")]).Compose(id);
    }

    [Test]
    public async Task PlayerRound_LowersToEventsGatesEnrichmentsAndStats()
    {
        Stopwatch clock = Stopwatch.StartNew();
        RulesetCorpus.Composed composed = Compose("player_round");
        clock.Stop();
        RulesetChainGraph chain = composed.Chain;
        Console.WriteLine($"[chain] nodes={chain.Nodes.Count} edges={chain.Edges.Count} compose={clock.ElapsedMilliseconds}ms");
        foreach (ChainEdge e in chain.Edges)
        {
            Console.WriteLine($"[chain] {e.Source} -> {e.Target} | {e.Label} {e.Effect} read={e.IsRead} | {e.Condition}");
        }

        ChainNode Node(string name) => chain.Nodes.Single(n => n.Name == name);
        using (Assert.Multiple())
        {
            await Assert.That(composed.Diagnostics).IsEmpty();
            await Assert.That(chain.Nodes.Select(n => n.Name)).DoesNotContain("Root").Because("the root is split into its events");
            await Assert.That(chain.Nodes.Where(n => n.Name.StartsWith("__first_tick", StringComparison.Ordinal))).IsEmpty()
                .Because("a first-tick companion is the builder's, not the author's");
            await Assert.That(Node("player_death").Kind).IsEqualTo(ChainNodeKind.Event);
            await Assert.That(Node("MatchLive").Kind).IsEqualTo(ChainNodeKind.Context);
            await Assert.That(Node("Alive").IsPerPlayer).IsTrue();
            await Assert.That(Node("enrich.kill.was_enemy_kill").Kind).IsEqualTo(ChainNodeKind.Enrichment);
            await Assert.That(Node("round_kills").Key).IsEqualTo(new RulesetDocumentNodeKey("player_round", "round_kills"))
                .Because("a declared node keeps its document key, so facets and the jump still find it");
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "MatchLive" && e.Target.Id == "round_deaths" && e.Label == "player_death"))
                .IsTrue().Because("while: match.live puts the gate in front of the stat");
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "player_death" && e.Target.Id == "round_kills"
                                                  && e.Condition!.Contains("was_enemy_kill", StringComparison.Ordinal)))
                .IsTrue().Because("the trigger wire carries the lowered condition");
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "enrich.kill.was_enemy_kill" && e.Target.Id == "round_kills" && e.IsRead))
                .IsTrue().Because("the facet test reads the enrichment");
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "round_kills" && e.Target.Id == "round_kast" && e.Effect == EdgeEffect.Disjunction))
                .IsTrue();
            await Assert.That(chain.Edges.Any(e => e.Source == e.Target)).IsFalse().Because("a round reset is not a wire");
            await Assert.That(chain.Nodes.Where(n => n.Kind == ChainNodeKind.External)).IsEmpty()
                .Because("external state is where the walk stops; everything writes it");
            await Assert.That(chain.Nodes.Count).IsEqualTo(26)
                .Because("the Workbench's 18 non-root nodes plus the eight events the root fires into them");
        }
    }

    [Test]
    public async Task EveryShippedRuleset_LowersWithoutADiagnostic()
    {
        string shipped = RuleSetLocator.ResolveShippedRulesDirectory();
        RulesetCorpus corpus = RulesetCorpus.Load([(shipped, "shipped")]);
        List<string> failed = [];
        Stopwatch clock = Stopwatch.StartNew();
        foreach (RulesetCorpus.Entry entry in corpus.Entries)
        {
            RulesetCorpus.Composed composed = corpus.Compose(entry.Id);
            if (composed.Chain.Nodes.Count == 0 || composed.Diagnostics.Any(d => d.StartsWith("The rule chain", StringComparison.Ordinal)))
            {
                failed.Add(entry.Id + ": " + string.Join("; ", composed.Diagnostics));
            }
        }

        string note = $"{corpus.Entries.Count} rulesets composed with their chains in {clock.ElapsedMilliseconds}ms";
        Console.WriteLine("[chain] " + note);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "rulecanvas-chain-timing.txt"), note);
        await Assert.That(failed).IsEmpty();
    }
}
