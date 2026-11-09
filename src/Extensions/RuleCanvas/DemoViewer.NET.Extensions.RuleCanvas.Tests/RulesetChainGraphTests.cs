#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

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
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "player_death" && e.Target.Id == "round_deaths" && e.Label == "player_death"
                                                  && e.Guard == new RulesetDocumentNodeKey("~context", "MatchLive")))
                .IsTrue().Because("the event fires the gated stat, and the wire names the guard");
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "MatchLive" && e.Target.Id == "round_deaths" && e.IsGate && e.Label == "while"))
                .IsTrue().Because("while: match.live is a gate wire beside the event wire");
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "MatchLive" && e.Target.Id == "round_deaths" && !e.IsGate))
                .IsFalse().Because("the guard is not drawn as what fires the stat");
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "round_ended" && e.Target.Id == "round_survived" && e.Label == "round_ended"))
                .IsTrue().Because("the trio behind player.alive merges once the wires run from the events");
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
            await Assert.That(chain.Nodes.Count(n => n.Kind is not (ChainNodeKind.Show or ChainNodeKind.Params))).IsEqualTo(26)
                .Because("the Workbench's 18 non-root nodes plus eight events: the root's seven, and the three round ends "
                         + "that fire round_survived through the Alive guard merged into one round_ended, which also "
                         + "absorbs the cs_win_panel_match that ends MatchLive");
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
    public async Task PlayerStats_EveryFiredStat_HasAWireFromItsEvent()
    {
        RulesetChainGraph chain = Compose("player_stats").Chain;
        HashSet<RulesetDocumentNodeKey> eventKeys = [.. chain.Nodes.Where(n => n.Kind == ChainNodeKind.Event).Select(n => n.Key)];
        List<ChainNode> declared = [.. chain.Nodes.Where(n => n.Kind == ChainNodeKind.Declared)];
        string[] unfired =
        [
            .. declared.Where(n => !chain.Edges.Any(e => e.Target == n.Key && !e.IsRead && !e.IsGate)).Select(n => n.Key.Id)
        ];
        string[] gated = [.. chain.Edges.Where(e => e.Guard is not null).Select(e => e.Target.Id).Distinct()];
        Console.WriteLine($"[chain] player_stats declared={declared.Count} no-write-wire={unfired.Length} gated={gated.Length}: {string.Join(", ", unfired)}");
        using (Assert.Multiple())
        {
            await Assert.That(gated.Length).IsGreaterThanOrEqualTo(40).Because("most player_stats columns carry a while:");
            await Assert.That(chain.Edges.Where(e => e.Guard is not null).All(e => eventKeys.Contains(e.Source))).IsTrue()
                .Because("a gated write runs from its event");
            await Assert.That(chain.Edges.Where(e => e.IsGate)
                    .All(e => chain.Nodes.Any(n => n.Key == e.Source && n.Kind is ChainNodeKind.Context or ChainNodeKind.Declared)))
                .IsTrue().Because("a gate runs from its guard, a context flag or a sibling flag, which stays on the canvas");
            await Assert.That(unfired.Length).IsEqualTo(0)
                .Because($"{unfired.Length} of {declared.Count} declared nodes have no write wire: {string.Join(", ", unfired)}");
        }
    }

    /// <summary>
    ///     The engine wires a <c>while: &lt;sibling flag&gt;</c> trigger from the flag node exactly as it wires one
    ///     from a context flag. aim_rating's contact_shots is <c>count: shot</c> gated on enemy_seen_round.
    /// </summary>
    [Test]
    public async Task ASiblingFlagGate_DrawsLikeAContextGate()
    {
        RulesetChainGraph chain = Compose("aim_rating").Chain;
        RulesetDocumentNodeKey flag = new("aim_rating", "enemy_seen_round");
        RulesetDocumentNodeKey count = new("aim_rating", "contact_shots");
        ChainEdge[] into = [.. chain.Edges.Where(e => e.Target == count)];
        using (Assert.Multiple())
        {
            await Assert.That(into.Any(e => e.Source.Ruleset == "~event" && e.Source.Id == "weapon_fire" && e.Label == "weapon_fire" && e.Guard == flag))
                .IsTrue().Because("the event fires the count, and the wire names the flag as its guard");
            await Assert.That(into.Any(e => e.Source == flag && e.IsGate && e.Label == "while")).IsTrue()
                .Because("while: enemy_seen_round is a dashed gate wire from the flag");
            await Assert.That(into.Any(e => e.Source == flag && !e.IsGate)).IsFalse()
                .Because("the flag is not drawn as what fires the count");
            await Assert.That(into.Any(e => e.Label == "weapon_fire" && e.Source == flag)).IsFalse();
        }
    }

    /// <summary>
    ///     A comparison gate (<c>while: round.number &gt; 0</c>) has no guard node: the engine roots the trigger
    ///     and reads the gate's operands on the edge. aim_rating's enemy_damage carries one, beside enemy_hits
    ///     whose <c>while: round.active</c> is a guard. Both draw as gates.
    /// </summary>
    [Test]
    public async Task AComparisonGate_DrawsAsAGateWire_NotAPlainRead()
    {
        RulesetChainGraph chain = Compose("aim_rating").Chain;
        RulesetDocumentNodeKey damage = new("aim_rating", "enemy_damage");
        ChainEdge[] into = [.. chain.Edges.Where(e => e.Target == damage)];
        ChainEdge[] fromRound = [.. into.Where(e => e.Source.Id == "RoundNumber")];
        using (Assert.Multiple())
        {
            await Assert.That(fromRound.Length).IsEqualTo(1);
            await Assert.That(fromRound[0].IsGate).IsTrue().Because("the gate's read is the gate");
            await Assert.That(fromRound[0].IsRead).IsFalse();
            await Assert.That(fromRound[0].Label).IsEqualTo("while round.number > 0");
            await Assert.That(ChainWiring.WireText(fromRound[0])).IsEqualTo("while round.number > 0");
            await Assert.That(into.Any(e => e.Source.Ruleset == "~event" && !e.IsRead && e.Guard is null)).IsTrue()
                .Because("the trigger runs from the event; a comparison gate has no guard node");
            await Assert.That(into.Any(e => e.Source.Id == "enrich.hurt.was_enemy_damage" && e.IsRead && e.Label == "reads")).IsTrue()
                .Because("the facet's read stays a plain read");
            await Assert.That(chain.Edges.Any(e => e.Target.Id == "enemy_hits" && e.Source.Id == "RoundActive" && e.IsGate && e.Label == "while"))
                .IsTrue().Because("a guard's gate wire keeps its one-word label");
        }
    }

    /// <summary>
    ///     The engine draws a live compute's reads as unlabelled SetValue edges from each node read, with
    ///     <c>live</c> as the condition. They are reads, and the wire says they are live.
    /// </summary>
    [Test]
    public async Task ALiveComputesReads_AreReadWires_SayingLive()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("live_probe", """
            ruleset: live_probe
            title: Live probe
            for: each_player

            stats:
              kills:
                count: kill
                per: round
              live_ratio:
                compute: { value: "kills * 2", live: true }
                per: round
              end_ratio:
                compute: "kills * 3"
                per: round
            """);
        RulesetCorpus.Composed composed = rules.Load().Compose("live_probe");
        RulesetChainGraph chain = composed.Chain;
        ChainEdge[] intoLive = [.. chain.Edges.Where(e => e.Target.Id == "live_ratio")];
        ChainEdge[] intoEnd = [.. chain.Edges.Where(e => e.Target.Id == "end_ratio")];
        using (Assert.Multiple())
        {
            await Assert.That(composed.Diagnostics).IsEmpty();
            await Assert.That(intoLive.Length).IsEqualTo(1);
            await Assert.That(intoLive[0].Source.Id).IsEqualTo("kills");
            await Assert.That(intoLive[0].IsRead).IsTrue().Because("the count is what the compute reads, not what writes it");
            await Assert.That(intoLive[0].Label).IsEqualTo("reads (live)");
            await Assert.That(ChainWiring.WireText(intoLive[0])).IsEqualTo("reads (live)");
            await Assert.That(intoEnd.Any(e => e.IsRead && e.Source.Id == "kills" && e.Label == "reads")).IsTrue();
            await Assert.That(intoEnd.Any(e => !e.IsRead && e.Source.Id == "round_ended")).IsTrue().Because("a round-end compute is fired by the round end");
        }
    }

    [Test]
    public async Task PlayerStats_MergingTwins_DropsTheDuplicateWires()
    {
        RulesetChainGraph chain = Compose("player_stats").Chain;
        int twins = chain.Nodes.Count(n => n.Kind == ChainNodeKind.Event && n.Events.Count > 1);
        int wires = chain.Edges.Count(e => !e.IsGate && !e.Target.IsShow && !e.Source.IsParams);
        Console.WriteLine($"[chain] player_stats nodes={chain.Nodes.Count} edges={chain.Edges.Count} wires={wires} merged-events={twins}");
        using (Assert.Multiple())
        {
            await Assert.That(twins).IsGreaterThanOrEqualTo(1);
            await Assert.That(wires).IsLessThanOrEqualTo(202)
                .Because("measured 245 before the merge, 201 after it, 202 once kast's tally is drawn; gate wires not counted");
        }
    }

    [Test]
    public async Task AHighlightsCount_IsReadAsTheHighlight()
    {
        RulesetChainGraph chain = Compose("player_stats").Chain;
        ChainNode highlight = chain.Nodes.Single(n => n.Key == new RulesetDocumentNodeKey("player_stats", "opening_kill"));
        ChainEdge[] intoDuelWin = [.. chain.Edges.Where(e => e.Target.Id == "DuelWin%" && e.IsRead)];
        using (Assert.Multiple())
        {
            await Assert.That(highlight.Outputs).IsEquivalentTo(["opening_kill.count"]);
            await Assert.That(chain.Nodes.Count(n => n.Key.Id.EndsWith(".count", StringComparison.Ordinal))).IsEqualTo(0)
                .Because("the counter is drawn as the highlight that bumps it");
            await Assert.That(intoDuelWin.Select(e => (e.Source.Id, e.Label)))
                .IsEquivalentTo([("opening_kill", "reads .count"), ("opening_death", "reads .count")])
                .Because("a compute over opening_kill.count reads the highlight, and the wire says which companion");
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "opening_kill" && e.Target.Id == "opening_kill")).IsFalse()
                .Because("the rising edge that bumps the counter is the highlight's own");
        }
    }

    /// <summary>
    ///     A tally has no node of its own in the engine, only its target counters and a seen flag, all owned by
    ///     it; the canvas draws the entry once with the targets as outputs. The resolver does not let a sibling
    ///     read a target counter (resolve.unknown-root), so the only readers of the targets are show: columns.
    /// </summary>
    [Test]
    public async Task ATally_IsOneNode_WithItsTargetsAsOutputs()
    {
        RulesetCorpus.Composed composed = Compose("kast");
        RulesetChainGraph chain = composed.Chain;
        ChainNode tally = chain.Nodes.Single(n => n.Key == new RulesetDocumentNodeKey("kast", "multi_kill_tally"));
        using (Assert.Multiple())
        {
            await Assert.That(composed.Diagnostics).IsEmpty();
            await Assert.That(tally.Kind).IsEqualTo(ChainNodeKind.Declared);
            await Assert.That(tally.Outputs).IsEquivalentTo(["rounds_5k", "rounds_4k", "rounds_3k", "rounds_2k"])
                .Because("the threshold targets in threshold order, for the face");
            await Assert.That(chain.Nodes.Select(n => n.Key.Id)).DoesNotContain("rounds_5k").Because("a target is the tally, not a node");
            await Assert.That(chain.Nodes.Count(n => n.Key == tally.Key)).IsEqualTo(1).Because("four targets and a seen flag fold into one node");
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "kills" && e.Target == tally.Key && e.IsRead && e.Label == "reads")).IsTrue()
                .Because("the tallied stat is read at round end");
            await Assert.That(chain.Edges.Count(e => e.Source.Id == "round_ended" && e.Target == tally.Key && !e.IsRead)).IsEqualTo(1)
                .Because("the bump runs once from the merged round end, not once per target per twin");
        }
    }

    /// <summary>
    ///     Every stat reference in every shipped compute: formula is a read wire into the compute, resolved
    ///     the way the author spells it: a sibling, a qualified ruleset.stat, a highlight's .count or a tally's
    ///     target. Measured 2026-11-07 over 21 rulesets: zero exceptions.
    /// </summary>
    [Test]
    public async Task EveryComputeReference_HasAReadWireInTheChain()
    {
        string root = DemoTestHelper.FindRepoRoot() ?? throw new SkipTestException("repo root not found from the test bin");
        string rulesDir = Path.Combine(root, "rules");
        RuleConfigLoadResult loaded = YamlConfigLoader.TryLoadDirectory(rulesDir);
        RulesetComposition.Result composed = RulesetComposition.Compose(loaded.Rulesets, EmbeddedCatalog.Adapter, 64.0,
            DemoSourceProfileRegistry.DefaultFallback.GetType().Name);
        RulesetCorpus corpus = RulesetCorpus.Load([(rulesDir, "shipped")]);

        int references = 0;
        List<string> missing = [];
        foreach (CheckedRuleset ruleset in composed.Rulesets)
        {
            RulesetChainGraph chain = corpus.Compose(ruleset.Id.Id).Chain;
            foreach (CheckedStat stat in ruleset.Stats.Where(s => s.Kind == RuleNodeKind.Compute))
            {
                RulesetDocumentNodeKey key = new(ruleset.Id.Id, stat.StatId);
                // A compute: formula rides in TriggerCondition; the value selector is for sum: and capture:.
                IEnumerable<string> paths = (stat.TriggerCondition?.References ?? []).Concat(stat.ValueSelector?.References ?? [])
                    .Where(r => r.IsStatReference && r.StatPath is not null)
                    .Select(r => r.StatPath!)
                    .Distinct(StringComparer.Ordinal);
                foreach (string statPath in paths)
                {
                    references++;
                    if (!chain.Edges.Any(e => e.Target == key && e.IsRead && Names(chain, e.Source, statPath)))
                    {
                        missing.Add($"{key} <- {statPath}");
                    }
                }
            }
        }

        Console.WriteLine($"[chain] compute references={references} without a wire={missing.Count}: {string.Join(", ", missing)}");
        using (Assert.Multiple())
        {
            await Assert.That(composed.Rulesets.Count).IsEqualTo(loaded.Rulesets.Count);
            await Assert.That(references).IsGreaterThanOrEqualTo(60).Because("the shipped computes read dozens of siblings");
            await Assert.That(missing.Count).IsEqualTo(0)
                .Because($"{missing.Count} of {references} compute references have no read wire: {string.Join(", ", missing)}");
        }
    }

    /// <summary>Whether the chain node at <paramref name="source" /> is what <paramref name="statPath" /> names.</summary>
    private static bool Names(RulesetChainGraph chain, RulesetDocumentNodeKey source, string statPath)
    {
        ChainNode node = chain.Nodes.Single(n => n.Key == source);
        return statPath == node.Key.Id || statPath == node.Key.ToString() || node.Outputs.Contains(statPath, StringComparer.Ordinal)
               || statPath.StartsWith(node.Key.Id + ".", StringComparison.Ordinal);
    }

    [Test]
    public async Task TwoEntriesWithOneDefinition_EachGetTheirOwnNodeAndWires()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
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

    /// <summary>
    ///     Two stats with one definition share one engine node, so a read of either arrives from that node
    ///     with both as owners. The reader's text names which one it reads; the other owner gets no wire.
    /// </summary>
    [Test]
    public async Task AReadOfOneSharedOwner_WiresFromThatOwnerAlone()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("twins", """
            ruleset: twins
            title: Twins
            for: each_player

            stats:
              a:
                count: kill
                per: round
              b:
                count: kill
                per: round
              saw_a:
                flag:
                  when: "a > 0"
                per: round
            """);
        RulesetCorpus.Composed composed = rules.Load().Compose("twins");
        RulesetChainGraph chain = composed.Chain;
        RulesetDocumentNodeKey flag = new("twins", "saw_a");
        ChainEdge[] fromOwners = [.. chain.Edges.Where(e => e.Target == flag && e.Source.Ruleset == "twins")];
        using (Assert.Multiple())
        {
            await Assert.That(composed.Diagnostics).IsEmpty();
            await Assert.That(chain.Nodes.Select(n => n.Key.Id)).Contains("b").Because("the twin is still drawn");
            await Assert.That(fromOwners.Length).IsEqualTo(1).Because("the flag reads a, not b: " + string.Join(", ", fromOwners.Select(e => e.Source.Id)));
            await Assert.That(fromOwners[0].Source.Id).IsEqualTo("a");
            await Assert.That(fromOwners[0].IsGate).IsFalse();
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "b" && e.Target == flag)).IsFalse();
        }
    }

    [Test]
    public async Task ABrokenReference_StillDrawsEveryEntry_AndPinsTheDiagnostic()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        string path = rules.UserPath("player_round");
        string text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("or player.traded\"", "or player.traded or no_such_stat > 0\"", StringComparison.Ordinal));
        int brokenLine = Array.FindIndex(text.Split('\n'), l => l.StartsWith("  round_kast:", StringComparison.Ordinal)) + 1;

        RulesetCorpus.Composed composed = rules.Load().Compose("player_round");
        RulesetDocumentNode Node(string id) => composed.Graph.Nodes.Single(n => n.Key.Id == id);
        using (Assert.Multiple())
        {
            await Assert.That(composed.Chain.Nodes.All(n => n.Kind == ChainNodeKind.Show)).IsTrue().Because("nothing lowered; the table is drawn from the document");
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
        using RulesTree rules = RulesTree.WithUser("player_stats");
        string root = DemoTestHelper.FindRepoRoot() ?? throw new InvalidOperationException("repo root not found");
        string kast = rules.UserPath("kast");
        File.WriteAllText(kast, File.ReadAllText(Path.Combine(root, "rules", "kast.rules.yaml"))
            .Replace("kast_pct:\n    compute: \"", "kast_pct:\n    compute: \"no_such_stat + ", StringComparison.Ordinal));

        RulesetCorpus corpus = rules.Load();
        RulesetCorpus.Composed composed = corpus.Compose("player_stats");
        using (Assert.Multiple())
        {
            await Assert.That(corpus.Failures).IsEmpty().Because("the edit keeps the file loadable");
            await Assert.That(composed.Chain.Nodes.All(n => n.Kind == ChainNodeKind.Show)).IsTrue().Because(string.Join("; ", composed.Diagnostics));
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

    /// <summary>
    ///     aim_rating gates its contact tier on enemy_spotted, which a demo-less build cannot produce: no
    ///     geometry, no contact scan. The build reports that as coverage, and the canvas says it at the
    ///     entry, so the node carries it and the strip lists it. The shipped catalog binds every view on the
    ///     fallback profile, so this is the one coverage skip the corpus can show.
    /// </summary>
    [Test]
    public async Task ACoverageSkip_IsSaidAtTheEntry_SoTheNodeCarriesIt()
    {
        RulesetCorpus.Composed composed = Compose("aim_rating");
        string[] lines = File.ReadAllLines(Path.Combine(RulesTree.RepoRulesDirectory, "aim_rating.rules.yaml"));
        int line = Array.FindIndex(lines, l => l.StartsWith("  first_contacts:", StringComparison.Ordinal)) + 1;
        RulesetDocumentNodeKey key = new("aim_rating", "first_contacts");
        ILookup<RulesetDocumentNodeKey, string> byNode = NodeFaceBuilder.DiagnosticsByNode(composed);
        using (Assert.Multiple())
        {
            await Assert.That(line).IsGreaterThan(1);
            await Assert.That(composed.Chain.Nodes).IsNotEmpty().Because("a coverage skip is not a failed build");
            await Assert.That(composed.PositionedDiagnostics.Any(d => d.RulesetId == "aim_rating" && d.Line == line
                                                                       && d.Message.StartsWith("first_contacts:", StringComparison.Ordinal)
                                                                       && d.Message.Contains("enemy_spotted", StringComparison.Ordinal)))
                .IsTrue().Because(string.Join("; ", composed.Diagnostics));
            await Assert.That(byNode[key].Any(m => m.Contains("enemy_spotted", StringComparison.Ordinal))).IsTrue();
            await Assert.That(composed.Diagnostics.Any(d => d.StartsWith("enemy_seen_round:", StringComparison.Ordinal))).IsTrue()
                .Because("the flag on enemy_spotted is reported too");
            await Assert.That(composed.Diagnostics.Any(d => d.Contains((char)0x2014))).IsFalse();
            await Assert.That(composed.Graph.Nodes.Any(n => n.Key == key)).IsTrue();
        }
    }

    /// <summary>
    ///     round_facts is <c>for: each_team</c> and captures <c>round.bomb.site</c>, a plant-site round fact. The
    ///     chain tells a side-scoped node from a per-player one, and a round fact from a trigger-driven context.
    /// </summary>
    [Test]
    public async Task ATeamRuleset_MarksItsNodesPerTeam_AndARoundFactAsOne()
    {
        RulesetCorpus.Composed composed = Compose("round_facts");
        RulesetChainGraph chain = composed.Chain;
        ChainNode plantSite = chain.Nodes.Single(n => n.Key == new RulesetDocumentNodeKey("round_facts", "plant_site"));
        ChainNode[] facts = [.. chain.Nodes.Where(n => n.IsRoundFact)];
        Console.WriteLine("[chain] round_facts facts=" + string.Join(", ", facts.Select(n => n.Name)) + " per-team="
                          + chain.Nodes.Count(n => n.IsPerTeam) + " per-player=" + chain.Nodes.Count(n => n.IsPerPlayer));
        using (Assert.Multiple())
        {
            await Assert.That(chain.Nodes).IsNotEmpty().Because(string.Join("; ", composed.Diagnostics));
            await Assert.That(plantSite.IsPerTeam).IsTrue();
            await Assert.That(plantSite.IsPerPlayer).IsFalse();
            await Assert.That(facts.Length).IsEqualTo(1).Because("plant_site reads round.bomb.site and nothing else reads a fact");
            await Assert.That(facts[0].Kind).IsEqualTo(ChainNodeKind.Context);
            await Assert.That(facts[0].Name).Contains("bomb_site");
            await Assert.That(chain.Edges.Any(e => e.Source == facts[0].Key && e.Target == plantSite.Key && e.IsRead)).IsTrue();
            await Assert.That(Compose("player_round").Chain.Nodes.Any(n => n.IsPerTeam || n.IsRoundFact)).IsFalse();
        }
    }

    /// <summary>
    ///     The engine folds a define's body into the trigger wire of every stat firing on it. The canvas cuts
    ///     that wire at a define node: the event fires the define, with the define's condition and gate, and the
    ///     define fires the stat. highlights_clutch's ct_retake_kill carries a match, a while and a where, so its
    ///     enrichment read, its gate wire and its context read all move to the define.
    /// </summary>
    [Test]
    public async Task ADefine_IsANodeBetweenTheEventAndTheStat_CarryingItsOwnCondition()
    {
        RulesetCorpus.Composed composed = Compose("highlights_clutch");
        RulesetChainGraph chain = composed.Chain;
        RulesetDocumentNodeKey define = RulesetDocumentNodeKey.Define("highlights_clutch", "ct_retake_kill");
        RulesetDocumentNodeKey stat = new("highlights_clutch", "retake_kills");
        ChainNode node = chain.Nodes.Single(n => n.Key == define);
        ChainEdge[] intoDefine = [.. chain.Edges.Where(e => e.Target == define)];
        ChainEdge[] intoStat = [.. chain.Edges.Where(e => e.Target == stat)];
        using (Assert.Multiple())
        {
            await Assert.That(composed.Diagnostics).IsEmpty();
            await Assert.That(chain.Nodes.Count(n => n.Kind is not (ChainNodeKind.Show or ChainNodeKind.Params))).IsEqualTo(25).Because("the 24 nodes the engine lowers plus the define");
            await Assert.That(node.Kind).IsEqualTo(ChainNodeKind.Declared);
            await Assert.That(node.Name).IsEqualTo("ct_retake_kill");
            await Assert.That(node.IsPerPlayer).IsTrue().Because("it materializes with the stats that fire on it");
            await Assert.That(chain.Nodes.ToList().FindIndex(n => n.Key == define)).IsLessThan(chain.Nodes.ToList().FindIndex(n => n.Key == stat));
            ChainEdge trigger = intoDefine.Single(e => !e.IsRead && !e.IsGate);
            await Assert.That(trigger.Source).IsEqualTo(new RulesetDocumentNodeKey("~event", "player_death")).Because("the event fires the define");
            await Assert.That(trigger.Label).IsEqualTo("player_death");
            await Assert.That(trigger.Guard).IsEqualTo(new RulesetDocumentNodeKey("~context", "BombWasPlanted")).Because("the define's while: is its guard");
            await Assert.That(trigger.Condition).Contains("was_enemy_kill").Because("the stat adds nothing, so the lowered condition is the define's");
            await Assert.That(trigger.Condition).Contains("player.team == 3");
            await Assert.That(intoDefine.Any(e => e.IsGate && e.Source.Id == "BombWasPlanted" && e.Label == "while")).IsTrue()
                .Because("the define's while: is a dashed gate wire into the define");
            await Assert.That(intoDefine.Any(e => e.IsRead && e.Source.Id == "enrich.kill.was_enemy_kill")).IsTrue()
                .Because("the define's enemy facet reads the enrichment");
            await Assert.That(intoDefine.Any(e => e.IsRead && e.Source.Id == "player_team")).IsTrue()
                .Because("the define's where: reads player.team");
            await Assert.That(intoStat.Select(e => (e.Source, e.Label, e.IsRead, e.IsGate)))
                .IsEquivalentTo([(define, "fires", false, false)]).Because("the stat is fired by the define and nothing else");
            await Assert.That(intoStat[0].Guard).IsNull();
            await Assert.That(intoStat[0].Condition).IsNull();
            await Assert.That(ChainWiring.WireText(intoStat[0])).IsEqualTo("fires");
            await Assert.That(chain.Edges.Any(e => e.Source.Ruleset == "~event" && e.Target == stat)).IsFalse();
            await Assert.That(chain.Edges.Any(e => e.Source == stat && e.Target.Id == "retake_multi")).IsTrue().Because("what the stat feeds is untouched");
        }
    }

    /// <summary>
    ///     Two stats firing on one define share one event wire into it and get one fires wire each. The stat
    ///     face no longer states the define's body; the canvas has the define node for that.
    /// </summary>
    [Test]
    public async Task ADefineTwoStatsFireOn_IsFiredOnce_AndFiresEach()
    {
        RulesetCorpus.Composed composed = Compose("post_plant_double");
        RulesetChainGraph chain = composed.Chain;
        RulesetDocumentNodeKey define = RulesetDocumentNodeKey.Define("post_plant_double", "post_plant_kill");
        IReadOnlyList<NodeFace> faces = NodeFaceBuilder.Build(composed);
        CanvasNodeViewModel kills = CanvasNodeFactory.ReadOnly.Create(faces.Single(f => f.Id == "post_plant_kills"));
        CanvasNodeViewModel ticks = CanvasNodeFactory.ReadOnly.Create(faces.Single(f => f.Id == "kill_ticks"));
        using (Assert.Multiple())
        {
            await Assert.That(composed.Diagnostics).IsEmpty();
            await Assert.That(chain.Nodes.Count(n => n.Kind is not (ChainNodeKind.Show or ChainNodeKind.Params))).IsEqualTo(10).Because("nine lowered nodes plus the define");
            await Assert.That(chain.Edges.Count(e => e.Target == define && !e.IsRead && !e.IsGate)).IsEqualTo(1).Because("one event wire into the define");
            await Assert.That(chain.Edges.Count(e => e.Target == define && e.IsGate)).IsEqualTo(1);
            await Assert.That(chain.Edges.Where(e => e.Source == define).Select(e => (e.Target.Id, e.Label)))
                .IsEquivalentTo([("post_plant_kills", "fires"), ("kill_ticks", "fires")]);
            await Assert.That(chain.Edges.Any(e => e.Target.Id is "post_plant_kills" or "kill_ticks" && e.Source != define)).IsFalse()
                .Because("the gate and the enrichment read belong to the define now");
            await Assert.That(chain.Edges.Any(e => e.Source.Id == "bomb_planted" && e.Target.Id == "plant_tick")).IsTrue()
                .Because("a stat that does not fire on the define keeps its own wire");
            await Assert.That(kills.FixedRows).IsEquivalentTo(["not a suicide"]).Because("the view's own row stays; the define's rows go");
            await Assert.That(kills.Chips).Contains("on post_plant_kill");
            await Assert.That(kills.Chips).DoesNotContain("while round.bomb.was_planted");
            await Assert.That(ticks.FixedRows).IsEquivalentTo(["not a suicide"]);
            await Assert.That(ticks.Chips).Contains("on post_plant_kill");
            await Assert.That(faces.OfType<DefineFace>().Single().Gate).IsEqualTo("round.bomb.was_planted");
        }
    }

    /// <summary>
    ///     A stat that refines the define it fires on (its own facet and where:) keeps what is its own: the
    ///     define wire carries the body spelled from the define, the fires wire the stat's refinements, and the
    ///     define's gate goes to the define. A read both make is drawn to both. A stat gated beside a gated
    ///     define is not a case: the resolver refuses two while: gates as a compound (resolve.while.unsupported).
    /// </summary>
    [Test]
    public async Task AStatThatRefinesItsDefine_KeepsItsOwnReads()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("refine", """
            ruleset: refine
            title: Refine
            for: each_player

            define:
              enemy_kill:
                on: kill
                match: { enemy: true }
                while: round.active

            stats:
              hs:
                count: enemy_kill
                match: { headshot: true }
                where: "enrich.kill.was_enemy_kill == true"
                per: round
              plain:
                count: enemy_kill
                per: round
            """);
        RulesetCorpus.Composed composed = rules.Load().Compose("refine");
        RulesetChainGraph chain = composed.Chain;
        RulesetDocumentNodeKey define = RulesetDocumentNodeKey.Define("refine", "enemy_kill");
        RulesetDocumentNodeKey hs = new("refine", "hs");
        ChainEdge[] intoHs = [.. chain.Edges.Where(e => e.Target == hs)];
        ChainEdge[] intoDefine = [.. chain.Edges.Where(e => e.Target == define)];
        using (Assert.Multiple())
        {
            await Assert.That(composed.Diagnostics).IsEmpty().Because(string.Join("; ", composed.Diagnostics));
            await Assert.That(intoDefine.Count(e => !e.IsRead && !e.IsGate)).IsEqualTo(1).Because("both stats fire through one event wire");
            ChainEdge trigger = intoDefine.Single(e => !e.IsRead && !e.IsGate);
            await Assert.That(trigger.Condition).Contains("was_enemy_kill").Because("plain adds nothing, so its lowered condition is the define's");
            await Assert.That(trigger.Condition).DoesNotContain("Headshot").Because("hs's own facet is not the define's");
            await Assert.That(trigger.Guard).IsEqualTo(new RulesetDocumentNodeKey("~context", "RoundActive"));
            await Assert.That(intoDefine.Any(e => e.IsGate && e.Source.Id == "RoundActive")).IsTrue();
            await Assert.That(intoDefine.Any(e => e.IsRead && e.Source.Id == "enrich.kill.was_enemy_kill")).IsTrue();
            ChainEdge fires = intoHs.Single(e => !e.IsRead && !e.IsGate);
            await Assert.That(fires.Source).IsEqualTo(define);
            await Assert.That(fires.Condition).IsEqualTo("headshot: true; where enrich.kill.was_enemy_kill == true");
            await Assert.That(fires.Guard).IsNull();
            await Assert.That(intoHs.Any(e => e.IsGate)).IsFalse().Because("the define's gate is the define's");
            await Assert.That(intoHs.Any(e => e.IsRead && e.Source.Id == "enrich.kill.was_enemy_kill")).IsTrue()
                .Because("hs's where: reads the enrichment too, so the read is drawn to both");
            ChainEdge[] intoPlain = [.. chain.Edges.Where(e => e.Target.Id == "plain")];
            await Assert.That(intoPlain.Length).IsEqualTo(1);
            await Assert.That(intoPlain[0].Source).IsEqualTo(define);
            await Assert.That(intoPlain[0].Label).IsEqualTo("fires");
            await Assert.That(intoPlain[0].Condition).IsNull();
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
        await Assert.That(failed).IsEmpty();
    }
}
