#region

using System.Diagnostics;
using System.Globalization;
using System.Text;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Stage 0 of live values: the engine reports per-entry fire counts, ticks and final values for a
///     shipped ruleset over the reference demo, within the time and memory the canvas can afford, and the
///     chain the demo build draws has the same declared keys as the demo-less one the canvas shows.
///     Skips without the demo. One heavy parse machine-wide, so not in parallel.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class LiveValuesProbeTests
{
    private const string Demo = "003816248937665266002_0544286934.dem";

    private static readonly string Report = Path.Combine(Path.GetTempPath(), "rule-canvas-live-values-stage0.md");

    [Test]
    [Arguments("player_round")]
    [Arguments("player_stats")]
    public async Task TheReferenceDemo_YieldsATrackPerDeclaredEntry_WithinBudget(string rulesetId)
    {
        string path = DemoTestHelper.RequireDemo(Demo);
        Stopwatch parse = Stopwatch.StartNew();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        parse.Stop();

        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        RulesetCorpus.Composed drawn = corpus.Compose(rulesetId);
        List<RulesetDoc> docs = [.. drawn.Documents.Values];

        Process process = Process.GetCurrentProcess();
        GC.Collect();
        long workingBefore = process.WorkingSet64;
        long managedBefore = GC.GetTotalMemory(true);

        Stopwatch build = Stopwatch.StartNew();
        var built = DemoAnalysis.Build(demo, docs);
        build.Stop();
        Stopwatch evaluate = Stopwatch.StartNew();
        AnalysisRun run = DemoAnalysis.Evaluate(demo, built, new AnalysisOptions { CaptureSnapshots = true });
        evaluate.Stop();

        RulesetComposition.Result composed = RulesetComposition.Compose(docs, EmbeddedCatalog.Adapter, demo.TickRate, built.Profile.GetType().Name);
        process.Refresh();
        long peak = Math.Max(0, process.WorkingSet64 - workingBefore);
        long managed = Math.Max(0, GC.GetTotalMemory(false) - managedBefore);
        LiveValuesRun values = LiveValuesReducer.Reduce(run, composed.Rulesets, path, demo.TickRate, build.Elapsed, evaluate.Elapsed, peak);

        // Key drift: the chain the canvas draws without a demo against the chain this demo's build gives.
        RulesetChainGraph demoChain = RulesetChainGraph.Build(built, composed.Rulesets, drawn.Documents[rulesetId]);
        HashSet<string> drawnKeys = [.. drawn.Chain.Nodes.Select(n => n.Key.ToString())];
        HashSet<string> demoKeys = [.. demoChain.Nodes.Select(n => n.Key.ToString())];
        List<string> onlyDrawn = [.. drawnKeys.Except(demoKeys).Order(StringComparer.Ordinal)];
        List<string> onlyDemo = [.. demoKeys.Except(drawnKeys).Order(StringComparer.Ordinal)];

        IEnumerable<(RulesetDocumentNodeKey Key, int Instance)> fired = values.Nodes.Where(kv => kv.Value.FireCount > 0).Select(kv => kv.Key);
        List<string> busiest = [.. values.Nodes.Where(kv => kv.Key.Key.Ruleset == rulesetId).OrderByDescending(kv => kv.Value.FireCount).Take(8)
            .Select(kv => $"{kv.Key.Key.Id}@{values.Instances[kv.Key.Instance].Name ?? "game"}: {kv.Value.FireCount} fires, last tick {kv.Value.LastTick}, final {kv.Value.FinalDisplay ?? kv.Value.FinalNumeric?.ToString(CultureInfo.InvariantCulture) ?? "-"}")];

        StringBuilder note = new();
        note.AppendLine(CultureInfo.InvariantCulture, $"## {rulesetId} over {Path.GetFileName(path)}");
        note.AppendLine(CultureInfo.InvariantCulture, $"parse {parse.Elapsed.TotalSeconds:F1} s (cached across tests), build {build.Elapsed.TotalSeconds:F2} s, evaluate {evaluate.Elapsed.TotalSeconds:F1} s, reduce {values.ReduceTime.TotalMilliseconds:F0} ms");
        note.AppendLine(CultureInfo.InvariantCulture, $"working set +{peak / 1_048_576} MB, managed +{managed / 1_048_576} MB, messages {run.Snapshots?.Messages.Count ?? 0}, tracked nodes {run.Snapshots?.FinalTrackedNodes.Count ?? 0}");
        note.AppendLine(CultureInfo.InvariantCulture, $"instances {values.Instances.Count}, tracks {values.Nodes.Count}, fired {fired.Count()}, unbound {values.Unbound.Count}: {string.Join(", ", values.Unbound.Select(k => k.Id))}");
        note.AppendLine(CultureInfo.InvariantCulture, $"profile {values.ProfileId}, ticks {values.FirstTick}..{values.LastTick}");
        note.AppendLine(CultureInfo.InvariantCulture, $"keys only in the drawn chain: {string.Join(", ", onlyDrawn)}");
        note.AppendLine(CultureInfo.InvariantCulture, $"keys only in the demo chain: {string.Join(", ", onlyDemo)}");
        foreach (string line in busiest)
        {
            note.AppendLine("  " + line);
        }

        Console.WriteLine(note.ToString());
        await File.AppendAllTextAsync(Report, note.ToString() + "\n");

        HashSet<RulesetDocumentNodeKey> declared = [.. drawn.Chain.Nodes.Where(n => n.Kind == ChainNodeKind.Declared && n.Key.Ruleset == rulesetId && !n.Key.IsDefine).Select(n => n.Key)];
        HashSet<RulesetDocumentNodeKey> tracked = [.. values.Nodes.Keys.Select(k => k.Key)];
        using (Assert.Multiple())
        {
            await Assert.That(declared.Except(tracked).Except(values.Unbound)).IsEmpty().Because("every declared entry is tracked or named as unbound");
            await Assert.That(fired.Any()).IsTrue().Because("a real demo fires something");
            await Assert.That(values.Nodes.Values.Where(t => t.LastTick is not null).All(t => t.LastTick <= values.LastTick)).IsTrue();
            await Assert.That(values.Nodes.Values.Where(t => t.FirstTick is not null).All(t => t.FirstTick <= t.LastTick)).IsTrue();
            await Assert.That(build.Elapsed + evaluate.Elapsed + values.ReduceTime).IsLessThan(TimeSpan.FromSeconds(30)).Because("the budget the plan set for the reference demo");
            await Assert.That(peak).IsLessThan(1_500L * 1_048_576).Because("1.5 GB transient is the line for a 16 GB box with the parse already resident");
            await Assert.That(onlyDrawn.Where(k => !k.StartsWith('~'))).IsEmpty().Because("every declared key the canvas draws exists in the demo build");
        }
    }

    // Stage 2: every drawn wire has a track for the players that materialised, a gate's open count never
    // exceeds the trigger's message count, a flag's on count is seen at its fires, the series follow the
    // fire ticks, and a define sums the stats that fire on it.
    [Test]
    [Arguments("player_round")]
    [Arguments("highlights_clutch")]
    public async Task TheReferenceDemo_TracksEveryWire_GatesHonestly_AndSumsDefines(string rulesetId)
    {
        string path = DemoTestHelper.RequireDemo(Demo);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        RulesetCorpus.Composed drawn = corpus.Compose(rulesetId);
        List<RulesetDoc> docs = [.. drawn.Documents.Values];
        var built = DemoAnalysis.Build(demo, docs);
        AnalysisRun run = DemoAnalysis.Evaluate(demo, built, new AnalysisOptions { CaptureSnapshots = true });
        RulesetComposition.Result composed = RulesetComposition.Compose(docs, EmbeddedCatalog.Adapter, demo.TickRate, built.Profile.GetType().Name);
        LiveValuesRun values = LiveValuesReducer.Reduce(run, composed.Rulesets, path, demo.TickRate, TimeSpan.Zero, TimeSpan.Zero, 0, drawn.Documents[rulesetId]);

        HashSet<LiveWireKey> drawnWires = [.. drawn.Chain.Edges.Select(e => new LiveWireKey(e.Source, e.Target, LiveValuesReducer.KindOf(e)))];
        List<LiveWireKey> tracked = [.. values.Wires.Keys.Select(k => k.Wire).Distinct()];
        List<LiveWireKey> untracked = [.. drawnWires.Where(w => !tracked.Contains(w) && w.Kind != LiveWireKind.Read && w.Kind != LiveWireKind.Logic)];
        List<KeyValuePair<(LiveWireKey Wire, int Instance), WireTrack>> gates = [.. values.Wires.Where(kv => kv.Key.Wire.Kind == LiveWireKind.Gate)];
        List<NodeTrack> fired = [.. values.Nodes.Values.Where(t => t.FireCount > 0)];
        List<NodeTrack> flags = [.. values.Nodes.Where(kv => kv.Key.Key.Id == "round_kast").Select(kv => kv.Value)];
        List<KeyValuePair<(RulesetDocumentNodeKey Key, int Instance), NodeTrack>> defines = [.. values.Nodes.Where(kv => kv.Key.Key.IsDefine)];

        Console.WriteLine($"[live] {rulesetId}: {tracked.Count} of {drawnWires.Count} drawn wires tracked; untracked triggers/gates: {string.Join(", ", untracked.Select(w => $"{w.Source.Id}->{w.Target.Id}/{w.Kind}"))}");
        foreach (KeyValuePair<(LiveWireKey Wire, int Instance), WireTrack> gate in gates.Take(4))
        {
            Console.WriteLine($"[live] gate {gate.Key.Wire.Source.Id}->{gate.Key.Wire.Target.Id}@{values.Instances[gate.Key.Instance].Name ?? "game"}: {LiveValuesText.WireTip(gate.Value, LiveWireKind.Gate, false)}");
        }

        foreach (KeyValuePair<(RulesetDocumentNodeKey Key, int Instance), NodeTrack> define in defines.Take(4))
        {
            Console.WriteLine($"[live] define {define.Key.Key.Name}@{values.Instances[define.Key.Instance].Name ?? "game"}: {LiveValuesText.Tip(define.Value, values.Instances[define.Key.Instance], false, false)}");
        }

        using (Assert.Multiple())
        {
            await Assert.That(tracked).IsNotEmpty();
            await Assert.That(tracked.Where(w => !drawnWires.Contains(w))).IsEmpty().Because("every track sits on a drawn wire");
            await Assert.That(untracked).IsEmpty().Because("every trigger and gate wire the canvas draws has an engine edge in the demo build");
            await Assert.That(gates).IsNotEmpty();
            foreach (KeyValuePair<(LiveWireKey Wire, int Instance), WireTrack> gate in gates)
            {
                await Assert.That(gate.Value.TriggerCount is null || gate.Value.FireCount <= gate.Value.TriggerCount).IsTrue()
                    .Because($"{gate.Key.Wire.Source.Id}->{gate.Key.Wire.Target.Id}: open on {gate.Value.FireCount} of {gate.Value.TriggerCount}");
            }

            foreach (NodeTrack track in fired)
            {
                await Assert.That(track.Series.Count).IsGreaterThan(0).Because("a node that fired has a series");
                await Assert.That(track.Series.Ticks[0]).IsEqualTo(track.FirstTick!.Value);
                await Assert.That(track.Series.Ticks[^1]).IsEqualTo(track.LastTick!.Value);
                await Assert.That(track.Series.IsSampled || track.Series.Count == track.FireCount || track.Series.Count <= track.FireCount).IsTrue();
            }

            foreach (WireTrack track in values.Wires.Values.Where(t => t.FireCount > 0))
            {
                await Assert.That(track.Ticks.Count).IsGreaterThan(0);
                await Assert.That(track.Ticks.Count <= track.FireCount).IsTrue();
                await Assert.That(track.Ticks.IsSampled || track.Ticks.Count == track.FireCount).IsTrue().Because("an unsampled list has one tick per fire");
            }

            if (rulesetId == "player_round")
            {
                await Assert.That(flags.Any(f => f.OnCount > 0)).IsTrue().Because("KAST comes on for someone");
                await Assert.That(flags.All(f => f.OnCount + f.OffCount <= f.Series.Count)).IsTrue();
            }
            else
            {
                await Assert.That(defines).IsNotEmpty().Because("ct_retake_kill fires retake_kills");
                foreach (KeyValuePair<(RulesetDocumentNodeKey Key, int Instance), NodeTrack> define in defines)
                {
                    int sum = RulesetDocumentGraph.StatsFiringOn(drawn.Documents[rulesetId], define.Key.Key.Name)
                        .Sum(stat => values.Nodes.GetValueOrDefault((new RulesetDocumentNodeKey(rulesetId, stat), define.Key.Instance))?.FireCount ?? 0);
                    await Assert.That(define.Value.FireCount).IsEqualTo(sum);
                    await Assert.That(define.Value.ViaStats).IsEqualTo(RulesetDocumentGraph.StatsFiringOn(drawn.Documents[rulesetId], define.Key.Key.Name).Count);
                }
            }
        }
    }
}
