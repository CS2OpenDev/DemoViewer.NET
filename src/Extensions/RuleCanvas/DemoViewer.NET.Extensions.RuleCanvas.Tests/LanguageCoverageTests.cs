#region

using System.Text.Json;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Nothing the file holds is hidden from the canvas: a key no face row renders is counted on a chip that
///     jumps to the YAML, a <c>for_each:</c> axis is a chip with an Inspector editor, the <c>params:</c> block
///     is a node wired to every entry that reads it, and <c>exports:</c> sits beside <c>use:</c> in the
///     document Inspector. The corpus test holds the faces to the language: every key the schema gives an
///     entry is rendered by some face or listed by the chip.
/// </summary>
public class LanguageCoverageTests
{
    private const string Odd = """
        ruleset: odd
        for: each_player
        params:
          min_kills: { type: int, default: 2, min: 1, max: 5 }
          ratio: { type: float, default: 0.5 }
          window: { type: duration, default: 10s }
        stats:
          kills:
            count: kill
            match: { enemy: true }
            per: round
            label: Kills
            foo: 1          # not a key the language has
          kills_{weapon}:
            count: kill
            where: event.Weapon == "{weapon}"
            per: round
            for_each: { weapon: [ak47, awp] }
          big:
            flag:
              when: "kills >= params.min_kills"
            per: round
        highlights:
          ace:
            when: kills >= 5
            per: round
            title: "{player.name} ace"
            bar: baz        # not a key the language has
        """;

    private const string EveryKind = """
        ruleset: kinds
        for: each_player
        stats:
          c:
            count: kill
            per: round
          s:
            sum: enrich.hurt.capped_damage
            on: damage_dealt
            per: round
          cap:
            capture: player.team
            on: round_freeze_end
            keep: first
            per: round
          f:
            flag:
              when: "c > 0"
            per: round
          comp:
            compute: "c + 1"
            format: F0
            per: match
          t:
            tally: c
            thresholds:
              - { min: 3, target: rounds_3k }
            per: match
          st:
            streak: kill
            window: 640
            min_streak: 2
            per: match
          b:
            bucket: kill
            key: event.Weapon
            per: match
          b2:
            bucket: shot
            key: event.Weapon
            per: match
          r:
            rate: { of: b, per: b2 }
            per: match
          bu:
            burst: kill
            window: "3s"
            min_streak: 2
        """;

    private static (RulesTree Rules, CanvasTabViewModel Vm) OpenOdd()
    {
        RulesTree rules = RulesTree.New();
        rules.User("odd", Odd);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "odd");
        return (rules, vm);
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id && !n.IsScaffold);

    [Test]
    public async Task AKeyNoRowRenders_IsCountedOnAChip_WhoseClickJumpsToTheYaml()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenOdd();
        using (rules)
        {
            CanvasNodeViewModel kills = Node(vm, "kills");
            CanvasNodeViewModel ace = Node(vm, "ace");
            CanvasNodeViewModel big = Node(vm, "big");
            using (Assert.Multiple())
            {
                await Assert.That(kills.UnrenderedKeys).IsEquivalentTo(["foo: 1"]);
                await Assert.That(kills.UnrenderedChip).IsEqualTo("+1 in YAML");
                await Assert.That(kills.UnrenderedTip).Contains("foo: 1");
                await Assert.That(ace.UnrenderedKeys).IsEquivalentTo(["bar: baz"]);
                await Assert.That(big.UnrenderedKeys).IsEmpty().Because("flag, per and the when inside the flag are all rendered");
                await Assert.That(big.HasUnrendered).IsFalse();
                await Assert.That(vm.Nodes.Where(n => n.IsScaffold).All(n => !n.HasUnrendered)).IsTrue();
            }

            vm.ShowSource = false;
            vm.JumpToYaml(kills);
            using (Assert.Multiple())
            {
                await Assert.That(vm.ShowSource).IsTrue();
                await Assert.That(vm.DockTab).IsEqualTo(1);
                await Assert.That(vm.SelectedNodes).Contains(kills);
                await Assert.That(vm.SelectedLine).IsEqualTo(kills.SourceLine);
            }
        }
    }

    [Test]
    public async Task EveryKeyTheLanguageGivesAnEntry_IsRenderedByAFace_OrListedOnTheChip()
    {
        // The schema beside the shipped rules is the language; a key it adds that no face covers is the
        // chip's by construction, and this test says so by name rather than letting it pass silently.
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RulesTree.RepoRulesDirectory, "cs2demokit-rules.schema.json")));
        JsonElement defs = schema.RootElement.GetProperty("$defs");
        HashSet<string> statKeys = [.. defs.GetProperty("stat").GetProperty("properties").EnumerateObject().Select(p => p.Name)];
        HashSet<string> highlightKeys = [.. defs.GetProperty("highlight").GetProperty("properties").EnumerateObject().Select(p => p.Name)];
        HashSet<string> triggerKeys = [.. defs.GetProperty("trigger").GetProperty("properties").EnumerateObject().Select(p => p.Name)];

        // The shipped corpus plus one ruleset of every stat kind, so a kind the corpus happens not to use
        // (rate, today) still has its face's keys counted.
        using RulesTree rules = RulesTree.New();
        foreach (string file in Directory.EnumerateFiles(RulesTree.RepoRulesDirectory, "*.rules.yaml"))
        {
            File.Copy(file, Path.Combine(rules.ShippedDirectory, Path.GetFileName(file)));
        }

        rules.User("kinds", EveryKind);
        RulesetCorpus corpus = rules.Load();
        HashSet<string> statCovered = new(StringComparer.Ordinal);
        HashSet<string> highlightCovered = new(StringComparer.Ordinal);
        HashSet<string> defineCovered = new(StringComparer.Ordinal);
        List<string> unrendered = [];
        int entries = 0;
        foreach (RulesetCorpus.Entry entry in corpus.Entries)
        {
            string text = File.ReadAllText(entry.Path);
            RulesetCorpus.Composed composed = corpus.Compose(entry.Id);
            foreach (NodeFace face in NodeFaceBuilder.Build(composed).Where(f => f.IsAuthorable && !f.IsScaffold && !f.IsShow && !f.IsParams))
            {
                entries++;
                HashSet<string> covered = (face is DefineFace ? defineCovered : face.IsHighlight ? highlightCovered : statCovered);
                covered.UnionWith(face.CoveredKeys);
                EntryCoverage coverage = EntryCoverage.Of(text, face);
                IReadOnlyList<EntryKey> keys = RulesetEntryText.KeysOf(text, RulesetEntryText.SectionOf(face.Key, face.IsHighlight), coverage.EntryId.Length > 0 ? coverage.EntryId : face.Key.Name);
                foreach (EntryKey key in keys)
                {
                    bool rendered = face.CoveredKeys.Contains(key.Key) || key.Key == "for_each";
                    bool listed = coverage.Unrendered.Any(u => u.Key == key.Key);
                    await Assert.That(rendered || listed).IsTrue().Because($"{face.Key}: {key.Key} is neither rendered nor listed");
                    if (listed)
                    {
                        unrendered.Add($"{face.Key}.{key.Key}");
                    }
                }
            }
        }

        Console.WriteLine($"[coverage] {entries} entries; unrendered: {(unrendered.Count == 0 ? "none" : string.Join(", ", unrendered))}");
        using (Assert.Multiple())
        {
            await Assert.That(entries).IsGreaterThan(300);
            await Assert.That(unrendered).IsEmpty().Because("every key the shipped corpus writes has a row on its face; a new one lands here until it does");
            await Assert.That(statKeys.Except(statCovered).Except(["for_each"])).IsEmpty().Because("a stat key the schema has and no stat face covers");
            await Assert.That(highlightKeys.Except(highlightCovered).Except(["for_each"])).IsEmpty().Because("a highlight key the schema has and no highlight face covers");
            await Assert.That(triggerKeys.Except(defineCovered)).IsEmpty().Because("a trigger key the schema has and the define face does not cover");
            // burst: is a kind the loader takes that the shipped schema file does not list yet.
            await Assert.That(statCovered.Except(statKeys).Except(["burst"])).IsEmpty().Because("a stat face claims a key the schema does not have");
            await Assert.That(highlightCovered.Except(highlightKeys)).IsEmpty();
        }
    }

    [Test]
    public async Task AnExpandedEntry_WearsItsAxisAsAChip_AndTheInspectorEditsTheTemplatesForEach()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenOdd();
        using (rules)
        {
            CanvasNodeViewModel ak = Node(vm, "kills_ak47");
            using (Assert.Multiple())
            {
                await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("kills_awp").Because("the loader multiplies the template");
                await Assert.That(ak.TextChips.Select(c => c.Text)).IsEquivalentTo(["for each weapon"]);
                await Assert.That(ak.TextChips[0].Tip).Contains("ak47, awp");
                await Assert.That(ak.TextChips[0].Tip).Contains("kills_{weapon}");
                await Assert.That(ak.VisibleChipItems.Select(c => c.Text)).Contains("for each weapon");
                await Assert.That(ak.UnrenderedKeys).IsEmpty().Because("for_each is the chip, not an unrendered key");
                await Assert.That(Node(vm, "kills").TextChips).IsEmpty();
            }

            InspectorViewModel inspector = vm.Inspector;
            vm.SelectedNodes.Add(ak);
            await Assert.That(inspector.ForEach.IsVisible).IsTrue();
            await Assert.That(inspector.ForEach.Text).IsEqualTo("weapon: ak47, awp");

            inspector.ForEach.Text = "weapon: ak47, awp, m4a1";
            inspector.ForEach.Commit();
            string text = File.ReadAllText(rules.UserPath("odd"));
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("    for_each: { weapon: [ak47, awp, m4a1] }\n");
                await Assert.That(text).Contains("    foo: 1          # not a key the language has\n").Because("the comment beside the stray key stays");
                await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("kills_m4a1");
                await Assert.That(inspector.ForEach.Error).IsEmpty();
            }

            inspector.ForEach.Text = "side: t, ct";
            inspector.ForEach.Commit();
            using (Assert.Multiple())
            {
                await Assert.That(inspector.ForEach.Error).Contains("{side}");
                await Assert.That(File.ReadAllText(rules.UserPath("odd"))).IsEqualTo(text).Because("an axis the id has no hole for is refused before anything is written");
            }

            inspector.ForEach.Text = "nonsense";
            inspector.ForEach.Commit();
            await Assert.That(inspector.ForEach.Error).Contains("key: value");
        }
    }

    [Test]
    public async Task ForEach_IsWrittenAsAFlowMapping_AndRemovedWhole()
    {
        string text = "ruleset: t\nstats:\n  k_{w}:\n    count: kill\n    per: round\n";
        string set = RulesetEdits.SetForEach(text, EntrySection.Stat, "k_{w}", RulesetEdits.ParseForEach("w: ak47, \"a b\", 7"));
        using (Assert.Multiple())
        {
            await Assert.That(set).IsEqualTo("ruleset: t\nstats:\n  k_{w}:\n    count: kill\n    per: round\n    for_each: { w: [ak47, \"a b\", 7] }\n");
            await Assert.That(RulesetEdits.ForEachOf(set, EntrySection.Stat, "k_{w}").Select(a => a.Key + "=" + string.Join("|", a.Values))).IsEquivalentTo(["w=ak47|a b|7"]);
            await Assert.That(RulesetEdits.SpellForEach(RulesetEdits.ForEachOf(set, EntrySection.Stat, "k_{w}"))).IsEqualTo("w: ak47, a b, 7");
            await Assert.That(RulesetEdits.RemoveForEach(set, EntrySection.Stat, "k_{w}")).IsEqualTo(text);
            await Assert.That(RulesetEdits.RemoveForEach(text, EntrySection.Stat, "k_{w}")).IsEqualTo(text);
            await Assert.That(RulesetEntryText.TemplateOf(set, EntrySection.Stat, "k_ak47")).IsEqualTo("k_{w}");
            await Assert.That(RulesetEntryText.TemplateOf(set, EntrySection.Stat, "k_m4a1")).IsNull();
            await Assert.That(() => RulesetEdits.SetForEach(text, EntrySection.Stat, "k_{w}", [])).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEdits.SetForEach(text, EntrySection.Stat, "k_{w}", [("w", [])])).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEdits.SetForEach(text, EntrySection.Stat, "k_{w}", [("1x", ["a"])])).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task TheParamsBlock_IsANode_WiredToEveryEntryThatReadsIt_AndItsDefaultsAreEditedInPlace()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenOdd();
        using (rules)
        {
            CanvasNodeViewModel parameters = vm.Nodes.Single(n => n.IsParams);
            CanvasNodeViewModel big = Node(vm, "big");
            using (Assert.Multiple())
            {
                await Assert.That(parameters.Kind).IsEqualTo("params");
                await Assert.That(parameters.Title).IsEqualTo("params");
                await Assert.That(parameters.Fields.Select(f => f.Label)).IsEquivalentTo(["min_kills (int 1..5)", "ratio (float)", "window (duration)"]);
                await Assert.That(parameters.Fields.Select(f => f.Original)).IsEquivalentTo(["2", "0.5", "10s"]);
                await Assert.That(parameters.Outputs.Count).IsEqualTo(1);
                await Assert.That(parameters.Inputs).IsEmpty();
                await Assert.That(vm.Connections.Count(w => ReferenceEquals(w.Source.Owner, parameters))).IsEqualTo(1).Because("only big reads a param");
                await Assert.That(vm.Connections.Single(w => ReferenceEquals(w.Source.Owner, parameters)).Target.Owner).IsSameReferenceAs(big);
                await Assert.That(vm.Connections.Single(w => ReferenceEquals(w.Source.Owner, parameters)).Text).IsEqualTo("reads params.min_kills");
                await Assert.That(parameters.Location.X).IsLessThan(big.Location.X).Because("a read source sits left of its reader");
            }

            CanvasFieldViewModel minKills = parameters.Fields.Single(f => f.Key == "param.min_kills");
            minKills.Text = "7";
            minKills.Commit();
            await Assert.That(vm.EditStatus).IsEqualTo("min_kills is at most 5.");
            minKills.Text = "x";
            minKills.Commit();
            await Assert.That(vm.EditStatus).Contains("whole number");

            minKills.Text = "3";
            minKills.Commit();
            string text = File.ReadAllText(rules.UserPath("odd"));
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("  min_kills: { type: int, default: 3, min: 1, max: 5 }\n");
                await Assert.That(vm.Nodes.Single(n => n.IsParams).Fields.Single(f => f.Key == "param.min_kills").Original).IsEqualTo("3");
                await Assert.That(vm.EditStatus).IsEmpty();
            }

            CanvasFieldViewModel window = vm.Nodes.Single(n => n.IsParams).Fields.Single(f => f.Key == "param.window");
            window.Text = "ten";
            window.Commit();
            await Assert.That(vm.EditStatus).Contains("duration");
            window.Text = "500ms";
            window.Commit();
            await Assert.That(File.ReadAllText(rules.UserPath("odd"))).Contains("  window: { type: duration, default: 500ms }\n");

            InspectorViewModel inspector = vm.Inspector;
            vm.SelectedNodes.Add(vm.Nodes.Single(n => n.IsParams));
            using (Assert.Multiple())
            {
                await Assert.That(inspector.Heading).IsEqualTo("PARAMS params");
                await Assert.That(inspector.CanDelete).IsFalse();
                await Assert.That(inspector.DeleteRefusal).Contains("YAML tab");
                await Assert.That(inspector.NodeFields.All(f => !f.IsVisible)).IsTrue().Because("the defaults are rows on the node");
            }
        }
    }

    [Test]
    public async Task TheShippedCorpus_WiresEachParamsBlock_ToTheEntriesReadingIt()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        RulesetCorpus.Composed plant = corpus.Compose("post_plant_double");
        RulesetCorpus.Composed round = corpus.Compose("player_round");
        RulesetDocumentNodeKey parameters = RulesetDocumentNodeKey.Params("post_plant_double");
        using (Assert.Multiple())
        {
            await Assert.That(plant.Chain.Nodes.Count(n => n.Kind == ChainNodeKind.Params)).IsEqualTo(1);
            await Assert.That(plant.Chain.Edges.Where(e => e.Source == parameters).Select(e => e.Target.Id)).IsEquivalentTo(["post_plant_double"])
                .Because("the highlight's when: reads params.min_kills");
            await Assert.That(round.Chain.Nodes.Any(n => n.Kind == ChainNodeKind.Params)).IsFalse().Because("player_round has no params");
            foreach (RulesetCorpus.Entry entry in corpus.Entries)
            {
                RulesetCorpus.Composed composed = corpus.Compose(entry.Id);
                int expected = composed.Open!.Params.Count > 0 ? 1 : 0;
                await Assert.That(composed.Chain.Nodes.Count(n => n.Kind == ChainNodeKind.Params)).IsEqualTo(expected).Because(entry.Id);
            }
        }

        RulesetDoc doc = plant.Open!;
        await Assert.That(SurfaceNodes.ParamsRead(["a >= params.min_kills && params.min_kills > params.other", null, "params.min_kills"]))
            .IsEquivalentTo(["min_kills", "other"]).Because("each name once, in first-use order");
        await Assert.That(doc.Params.Select(p => p.Name)).IsEquivalentTo(["min_kills"]);
    }

    [Test]
    public async Task ExportsAndUses_AreEditedInTheDocumentInspector()
    {
        using RulesTree rules = RulesTree.WithUser("kast");
        rules.UserCopyOf("player_stats");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "kast");
        InspectorViewModel inspector = vm.Inspector;
        using (Assert.Multiple())
        {
            await Assert.That(inspector.Exports).IsEquivalentTo(["kast_pct"]);
            await Assert.That(inspector.HasExports).IsTrue();
            await Assert.That(inspector.ExportsNote).Contains("kast.<id>");
            await Assert.That(inspector.UsesSummary).IsEqualTo("uses no other ruleset");
        }

        inspector.ExportToAdd = "kast";
        inspector.AddExportCommand.Execute(null);
        string text = File.ReadAllText(rules.UserPath("kast"));
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Exports(text)).IsEquivalentTo(["kast_pct", "kast"]);
            await Assert.That(text).Contains("exports: [ kast_pct, kast ]");
            await Assert.That(inspector.Exports).IsEquivalentTo(["kast_pct", "kast"]);
            await Assert.That(inspector.ExportToAdd).IsEmpty();
        }

        inspector.RemoveExportCommand.Execute("kast");
        inspector.RemoveExportCommand.Execute("kast_pct");
        text = File.ReadAllText(rules.UserPath("kast"));
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.HasExports(text)).IsFalse().Because("the last one takes the key, which exports everything again");
            await Assert.That(inspector.ExportsNote).StartsWith("Every stat and highlight is exported");
        }

        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "player_stats");
        await Assert.That(inspector.UsesSummary).IsEqualTo("uses 1 ruleset: kast");
    }

    [Test]
    public async Task AddExport_OpensTheListAfterTheHeader()
    {
        string text = "ruleset: t\ntitle: T\nuse: [a]\n\nstats:\n  kills:\n    count: kill\n";
        string added = RulesetEdits.AddExport(text, "kills");
        using (Assert.Multiple())
        {
            await Assert.That(added).IsEqualTo("ruleset: t\ntitle: T\nuse: [a]\nexports: [kills]\n\nstats:\n  kills:\n    count: kill\n");
            await Assert.That(RulesetEdits.AddExport(added, "kills")).IsEqualTo(added);
            await Assert.That(RulesetEdits.RemoveExport(added, "kills")).IsEqualTo(text);
            await Assert.That(() => RulesetEdits.AddExport(text, "no.dots")).Throws<InvalidOperationException>();
        }
    }
}
