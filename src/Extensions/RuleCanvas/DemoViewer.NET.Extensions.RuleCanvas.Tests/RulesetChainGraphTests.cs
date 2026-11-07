#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.TestSupport;

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
    public async Task RoundEndTwins_MergeIntoOneEventNode_NamedByTheView()
    {
        RulesetChainGraph chain = Compose("player_stats").Chain;
        ChainNode roundEnded = chain.Nodes.Single(n => n.Name == "round_ended");
        ChainEdge[] intoClutch = [.. chain.Edges.Where(e => e.Target.Id == "clutch_won" && !e.IsRead)];
        using (Assert.Multiple())
        {
            await Assert.That(roundEnded.Kind).IsEqualTo(ChainNodeKind.Event);
            await Assert.That(roundEnded.Events).IsEquivalentTo(["round_officially_ended", "cs_win_panel_match", "round_end_terminal"])
                .Because("the wire events stay on the node for hover text");
            await Assert.That(chain.Nodes.Select(n => n.Name)).DoesNotContain("round_officially_ended")
                .Because("the three round ends write the same nodes the same way, so they are one node");
            await Assert.That(chain.Nodes.Single(n => n.Name == "player_death").Events).IsEquivalentTo(["player_death"]);
            await Assert.That(intoClutch.Length).IsEqualTo(1).Because("a wire every twin carried is drawn once");
            await Assert.That(intoClutch[0].Source).IsEqualTo(roundEnded.Key);
            await Assert.That(intoClutch[0].Label).IsEqualTo("round_ended");
            await Assert.That(chain.Edges.Any(e => e.Source == roundEnded.Key && e.Target.Id == "MatchLive" && e.Label == "cs_win_panel_match"
                                                  && e.Effect == EdgeEffect.Deactivate))
                .IsTrue().Because("a wire only one twin carried keeps that twin's name");
            await Assert.That(chain.Nodes.Where(n => n.Kind == ChainNodeKind.Event).Select(n => n.Name))
                .Contains("player_team_observed").Because("an event that writes no declared node is not merged with its neighbour");
        }
    }

    [Test]
    public async Task PlayerStats_MergingTwins_DropsTheDuplicateWires()
    {
        RulesetChainGraph chain = Compose("player_stats").Chain;
        int twins = chain.Nodes.Count(n => n.Kind == ChainNodeKind.Event && n.Events.Count > 1);
        Console.WriteLine($"[chain] player_stats nodes={chain.Nodes.Count} edges={chain.Edges.Count} merged-events={twins}");
        using (Assert.Multiple())
        {
            await Assert.That(twins).IsGreaterThanOrEqualTo(1);
            await Assert.That(chain.Edges.Count).IsLessThanOrEqualTo(201).Because("measured 201 after the merge, 245 before");
        }
    }

    [Test]
    public async Task TwoEntriesWithOneDefinition_EachGetTheirOwnNodeAndWires()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        string path = rules.UserPath("player_round");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\nshow:",
            "\n  enemy_kills:\n    count: kill\n    match: { enemy: true }\n    per: round\n\nshow:", StringComparison.Ordinal));
        RulesetChainGraph chain = rules.Load().Compose("player_round").Chain;

        ChainEdge[] Into(string id) => [.. chain.Edges.Where(e => e.Target.Id == id)];
        using (Assert.Multiple())
        {
            await Assert.That(chain.Nodes.Select(n => n.Key.Id)).Contains("enemy_kills")
                .Because("the engine shares one node between the two, the canvas does not");
            await Assert.That(Into("enemy_kills").Select(e => (e.Source, e.Label, e.IsRead)))
                .IsEquivalentTo(Into("round_kills").Select(e => (e.Source, e.Label, e.IsRead)));
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "round_kills" && e.Target.Id == "enemy_kills")).IsFalse()
                .Because("sharing a node is not a wire between the two");
        }
    }

    [Test]
    public async Task ABrokenReference_StillDrawsEveryEntry_AndPinsTheDiagnostic()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        string path = rules.UserPath("player_round");
        string text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("or player.traded\"", "or player.traded or no_such_stat > 0\"", StringComparison.Ordinal));
        int brokenLine = Array.FindIndex(text.Split('\n'), l => l.StartsWith("  round_kast:", StringComparison.Ordinal)) + 1;

        RulesetCorpus.Composed composed = rules.Load().Compose("player_round");
        RulesetDocumentNode Node(string id) => composed.Graph.Nodes.Single(n => n.Key.Id == id);
        using (Assert.Multiple())
        {
            await Assert.That(composed.Chain.Nodes).IsEmpty().Because("nothing lowered");
            await Assert.That(composed.Graph.Nodes.Count).IsEqualTo(8).Because("every stats: entry of the document is still a node");
            await Assert.That(composed.Graph.Edges).IsEmpty().Because("no checked model, so no resolved read to draw");
            await Assert.That(composed.Diagnostics.Any(d => d.Contains("no_such_stat", StringComparison.Ordinal))).IsTrue();
            await Assert.That(composed.PositionedDiagnostics.Any(d => d.RulesetId == "player_round" && d.Line >= brokenLine
                                                                       && d.Message.Contains("no_such_stat", StringComparison.Ordinal)))
                .IsTrue().Because("the diagnostic pins to the entry whose line it names");
            await Assert.That(Node("round_kast").Kind).IsEqualTo(RuleNodeKind.Flag);
            await Assert.That(Node("round_kast").IsAuthorable).IsTrue();
            await Assert.That(Node("round_kast").Position.Line).IsEqualTo(brokenLine);
            await Assert.That(Node("round_kills").View).IsEqualTo("kill").Because("count: kill names a catalog view");
            await Assert.That(Node("round_kills").Events).IsEquivalentTo(["kill"]);
            await Assert.That(Node("round_damage").Events).IsEquivalentTo(["damage_dealt"]).Because("the on: as written");
            await Assert.That(Node("round_side").View).IsNull().Because("raw.round_freeze_end is no view");
            await Assert.That(Node("round_kast").Events).IsEmpty();
        }
    }

    [Test]
    public async Task ABrokenDependency_StillDrawsBothDocuments_AndNamesTheDependency()
    {
        using TempRules rules = TempRules.WithUser("player_stats");
        string root = DemoTestHelper.FindRepoRoot() ?? throw new InvalidOperationException("repo root not found");
        string kast = rules.UserPath("kast");
        File.WriteAllText(kast, File.ReadAllText(Path.Combine(root, "rules", "kast.rules.yaml"))
            .Replace("kast_pct:\n    compute: \"", "kast_pct:\n    compute: \"no_such_stat + ", StringComparison.Ordinal));

        RulesetCorpus corpus = rules.Load();
        RulesetCorpus.Composed composed = corpus.Compose("player_stats");
        using (Assert.Multiple())
        {
            await Assert.That(corpus.Failures).IsEmpty().Because("the edit keeps the file loadable");
            await Assert.That(composed.Chain.Nodes).IsEmpty().Because(string.Join("; ", composed.Diagnostics));
            await Assert.That(composed.Graph.Nodes.Count(n => n.Key.Ruleset == "player_stats")).IsGreaterThanOrEqualTo(50)
                .Because("the open document's entries are all drawn");
            await Assert.That(composed.Graph.Nodes.Count(n => n.Key.Ruleset == "kast")).IsGreaterThanOrEqualTo(15)
                .Because("the dependency's entries are drawn too, read-only");
            await Assert.That(composed.Graph.Nodes.Where(n => n.Key.Ruleset == "kast").All(n => !n.IsAuthorable)).IsTrue();
            await Assert.That(composed.Graph.Nodes.Any(n => n.Key.Ruleset == "kast" && n.Kind == RuleNodeKind.Highlight)).IsTrue();
            await Assert.That(composed.PositionedDiagnostics.Any(d => d.RulesetId == "kast"
                                                                       && d.Message.Contains("no_such_stat", StringComparison.Ordinal)))
                .IsTrue().Because("a dependency's diagnostic carries the dependency's id, so it pins to its own entry");
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
