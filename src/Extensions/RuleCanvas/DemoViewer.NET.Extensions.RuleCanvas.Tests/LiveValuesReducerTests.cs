#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The joins behind a wire's track. The chain graph names the engine edge(s) each drawn wire was cut
///     from: a guard's gate and the trigger it guards share one, a define's two halves share the whole's,
///     and a twin-merged event's wire carries every twin's. Over a demo with no frames the reducer still
///     answers, with nothing materialised and nothing fired; the real demo proves the counts, in the probe.
/// </summary>
public class LiveValuesReducerTests
{
    private static RulesetCorpus.Composed Compose(string id) => RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]).Compose(id);

    private static ParsedDemo SyntheticDemo()
    {
        Dictionary<int, PlayerInfo> players = new()
        {
            [0] = new PlayerInfo(0, "Alice", 76561198000000001UL, 1, 2, false),
            [1] = new PlayerInfo(1, "Bob", 76561198000000002UL, 2, 3, false)
        };
        return new ParsedDemo([], [], players, null, "de_anytown", 0, 1f / 64f, "test", "test", "csgo", 0, 0, 0,
            "valve_demo_2", "", "", DemoProfile.Unknown);
    }

    [Test]
    public async Task EveryDrawnWire_NamesTheEngineEdgesItWasCutFrom()
    {
        RulesetChainGraph chain = Compose("player_round").Chain;
        using (Assert.Multiple())
        {
            await Assert.That(chain.Edges).IsNotEmpty();
            await Assert.That(chain.Edges.Where(e => !chain.EngineEdges.ContainsKey(e))).IsEmpty().Because("a wire with no edge behind it could never carry a track");
            await Assert.That(chain.EngineEdges.Values.All(list => list.Count > 0)).IsTrue();
            await Assert.That(chain.EngineEdges.Values.SelectMany(l => l).Any(e => e.Descriptor.Edge is not null)).IsTrue()
                .Because("the preview materialises the template, so its rows carry real engine edges");
            await Assert.That(chain.Slice(new RulesetDocumentNodeKey("player_round", "round_kills")).EngineEdges).IsEmpty().Because("a slice is drawn, not evaluated");
            await Assert.That(RulesetChainGraph.Empty.EngineEdges).IsEmpty();
        }
    }

    [Test]
    public async Task AGateWire_AndTheTriggerItGuards_ShareTheirEngineEdge()
    {
        RulesetChainGraph chain = Compose("player_round").Chain;
        ChainEdge trigger = chain.Edges.Single(e => e.Target.Id == "round_deaths" && !e.IsRead && !e.IsGate && e.Source.Ruleset == "~event");
        ChainEdge gate = chain.Edges.Single(e => e.Target.Id == "round_deaths" && e.IsGate);
        using (Assert.Multiple())
        {
            await Assert.That(trigger.Guard).IsEqualTo(gate.Source);
            await Assert.That(chain.EngineEdges[gate].Select(e => e.Key)).IsEquivalentTo(chain.EngineEdges[trigger].Select(e => e.Key))
                .Because("the engine wires one gated edge; the canvas draws it as a trigger and a gate");
            await Assert.That(LiveValuesReducer.KindOf(gate)).IsEqualTo(LiveWireKind.Gate);
            await Assert.That(LiveValuesReducer.KindOf(trigger)).IsEqualTo(LiveWireKind.Trigger);
            await Assert.That(LiveValuesReducer.KindOf(chain.Edges.First(e => e.Target.Id == "round_kast" && !e.IsRead && !e.IsGate && e.Source.Ruleset == "player_round")))
                .IsEqualTo(LiveWireKind.Logic).Because("a when: term is a logic input");
        }
    }

    [Test]
    public async Task ADefinesTwoHalves_ShareTheWholesEngineEdge()
    {
        RulesetChainGraph chain = Compose("highlights_clutch").Chain;
        RulesetDocumentNodeKey define = RulesetDocumentNodeKey.Define("highlights_clutch", "ct_retake_kill");
        RulesetDocumentNodeKey stat = new("highlights_clutch", "retake_kills");
        ChainEdge fires = chain.Edges.Single(e => e.Source == define && e.Target == stat);
        List<ChainEdge> intoDefine = [.. chain.Edges.Where(e => e.Target == define && !e.IsRead && !e.IsGate)];
        using (Assert.Multiple())
        {
            await Assert.That(intoDefine).IsNotEmpty().Because("the event fires the define");
            await Assert.That(chain.EngineEdges.ContainsKey(fires)).IsTrue();
            foreach (ChainEdge half in intoDefine)
            {
                await Assert.That(chain.EngineEdges[half].Select(e => e.Key)).IsEquivalentTo(chain.EngineEdges[fires].Select(e => e.Key))
                    .Because("both halves of a cut wire stand for the engine edge the whole did");
            }
        }
    }

    [Test]
    public async Task ATwinMergedEventsWire_CarriesEveryTwinsEdge()
    {
        RulesetChainGraph chain = Compose("player_round").Chain;
        ChainNode merged = chain.Nodes.Single(n => n.Kind == ChainNodeKind.Event && n.Events.Count > 1);
        List<ChainEdge> wires = [.. chain.Edges.Where(e => e.Source == merged.Key && e.Label == merged.Name)];
        using (Assert.Multiple())
        {
            await Assert.That(wires).IsNotEmpty().Because($"{merged.Name} merges {string.Join(", ", merged.Events)} and carries a shared wire");
            foreach (ChainEdge wire in wires)
            {
                await Assert.That(chain.EngineEdges[wire].Count).IsGreaterThanOrEqualTo(merged.Events.Count)
                    .Because($"{wire.Source} -> {wire.Target} stands for one engine edge per twin");
            }
        }
    }

    [Test]
    public async Task OverADemoWithNoFrames_TheReducerAnswers_WithNothingMaterialisedAndNothingFired()
    {
        RulesetCorpus.Composed drawn = Compose("highlights_clutch");
        List<RulesetDoc> docs = [.. drawn.Documents.Values];
        ParsedDemo demo = SyntheticDemo();
        BuildResult built = DemoAnalysis.Build(demo, docs);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, built, new AnalysisOptions { CaptureSnapshots = true });
        RulesetComposition.Result composed = RulesetComposition.Compose(docs, EmbeddedCatalog.Adapter, demo.TickRate, built.Profile.GetType().Name);
        RulesetDoc open = drawn.Documents["highlights_clutch"];
        LiveValuesRun values = LiveValuesReducer.Reduce(run, composed.Rulesets, "/demos/one.dem", demo.TickRate, TimeSpan.Zero, TimeSpan.Zero, 0, open);
        RulesetDocumentNodeKey define = RulesetDocumentNodeKey.Define("highlights_clutch", "ct_retake_kill");
        RulesetDocumentNodeKey stat = new("highlights_clutch", "retake_kills");
        HashSet<LiveWireKey> drawnWires = [.. drawn.Chain.Edges.Select(e => new LiveWireKey(e.Source, e.Target, LiveValuesReducer.KindOf(e)))];
        using (Assert.Multiple())
        {
            await Assert.That(values.Instances.Count).IsEqualTo(1).Because("players materialise from events; none arrive");
            await Assert.That(values.Nodes.Values.All(t => t.FireCount == 0 && t.Series.Count == 0)).IsTrue();
            await Assert.That(values.Wires.Keys.Select(k => k.Wire).Where(k => !drawnWires.Contains(k))).IsEmpty().Because("a track with no wire to sit on would never be seen");
            await Assert.That(values.Wires.Values.All(t => t.FireCount == 0 && t.Ticks.Count == 0)).IsTrue();
            await Assert.That(values.Nodes.Keys.Where(k => k.Key == define).Select(k => k.Instance))
                .IsEquivalentTo(values.Nodes.Keys.Where(k => k.Key == stat).Select(k => k.Instance))
                .Because("the define has a track wherever its stat has one");
            foreach (NodeTrack track in values.Nodes.Where(kv => kv.Key.Key == define).Select(kv => kv.Value))
            {
                await Assert.That(track.ViaStats).IsEqualTo(1);
                await Assert.That(track.IsDefine).IsTrue();
            }
        }
    }
}
