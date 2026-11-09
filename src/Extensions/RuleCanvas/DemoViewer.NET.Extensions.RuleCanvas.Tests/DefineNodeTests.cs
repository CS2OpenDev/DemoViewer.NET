#region

using CS2DemoKit.Analysis.Rules.Hashing;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     A trigger-bodied <c>define:</c> of the open document is a node of its own, keyed <c>define.&lt;name&gt;</c>,
///     with a position for the sidecar and the jump, wired to the stats that fire on it; its face carries the
///     body. A dependency's define is not drawn, since the resolver cannot fire a stat on it.
/// </summary>
public class DefineNodeTests
{
    private static RulesetCorpus.Composed Compose(string id) =>
        RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]).Compose(id);

    [Test]
    public async Task ADefineKey_IsTheNamePrefixed_AndNamesItselfBack()
    {
        RulesetDocumentNodeKey key = RulesetDocumentNodeKey.Define("r", "blind_kill");
        RulesetDocumentNodeKey stat = new("r", "kills");
        using (Assert.Multiple())
        {
            await Assert.That(key.Id).IsEqualTo("define.blind_kill");
            await Assert.That(key.IsDefine).IsTrue();
            await Assert.That(key.Name).IsEqualTo("blind_kill");
            await Assert.That(key.ToString()).IsEqualTo("r.define.blind_kill").Because("the sidecar key keeps the prefix");
            await Assert.That(stat.IsDefine).IsFalse();
            await Assert.That(stat.Name).IsEqualTo("kills");
        }
    }

    [Test]
    public async Task HighlightsClutch_DrawsItsDefine_WiredToTheStatThatFiresOnIt()
    {
        RulesetCorpus.Composed composed = Compose("highlights_clutch");
        RulesetDocumentNodeKey key = RulesetDocumentNodeKey.Define("highlights_clutch", "ct_retake_kill");
        RulesetDocumentNode define = composed.Graph.Nodes.Single(n => n.Key == key);
        string[] lines = File.ReadAllLines(Path.Combine(RulesTree.RepoRulesDirectory, "highlights_clutch.rules.yaml"));
        int line = Array.FindIndex(lines, l => l.StartsWith("  ct_retake_kill:", StringComparison.Ordinal)) + 1;
        using (Assert.Multiple())
        {
            await Assert.That(composed.Diagnostics).IsEmpty();
            await Assert.That(define.IsDefine).IsTrue();
            await Assert.That(define.IsAuthorable).IsTrue();
            await Assert.That(define.IsHighlight).IsFalse();
            await Assert.That(define.Kind).IsEqualTo(RuleNodeKind.None);
            await Assert.That(define.Position.Line).IsEqualTo(line).Because("the jump lands on the define's own line");
            await Assert.That(define.View).IsEqualTo("kill").Because("the resolver gave retake_kills the view the define names");
            await Assert.That(define.Events).IsEquivalentTo(["player_death"]);
            await Assert.That(composed.Graph.Edges.Where(e => e.Source == key).Select(e => (e.Target.Id, e.Reference)))
                .IsEquivalentTo([("retake_kills", "fires")]);
            await Assert.That(composed.Graph.Nodes.Count(n => n.IsDefine)).IsEqualTo(1);
            await Assert.That(RulesetDocumentGraph.StatsFiringOn(composed.Documents["highlights_clutch"], "ct_retake_kill")).IsEquivalentTo(["retake_kills"]);
        }
    }

    [Test]
    public async Task PostPlantDouble_WiresItsDefine_ToBothStatsThatFireOnIt()
    {
        RulesetCorpus.Composed composed = Compose("post_plant_double");
        RulesetDocumentNodeKey key = RulesetDocumentNodeKey.Define("post_plant_double", "post_plant_kill");
        using (Assert.Multiple())
        {
            await Assert.That(composed.Graph.Nodes.Any(n => n.Key == key)).IsTrue();
            await Assert.That(composed.Graph.Edges.Where(e => e.Source == key).Select(e => e.Target.Id))
                .IsEquivalentTo(["post_plant_kills", "kill_ticks"]).Because("a count and a capture both fire on the define");
            await Assert.That(composed.Graph.Nodes.Any(n => n.Id == "define.min_kills")).IsFalse().Because("a param is not a define");
        }
    }

    [Test]
    public async Task ADefineFace_CarriesTheBody_AndTheNodeShowsIt()
    {
        IReadOnlyList<NodeFace> faces = NodeFaceBuilder.Build(Compose("highlights_clutch"));
        DefineFace define = faces.OfType<DefineFace>().Single();
        StatFace retake = (StatFace)faces.Single(f => f.Id == "retake_kills");
        CanvasNodeViewModel node = CanvasNodeFactory.ReadOnly.Create(define);
        using (Assert.Multiple())
        {
            await Assert.That(define.Kind).IsEqualTo("define");
            await Assert.That(define.Name).IsEqualTo("ct_retake_kill");
            await Assert.That(define.Id).IsEqualTo("define.ct_retake_kill");
            await Assert.That(define.IsDefine).IsTrue();
            await Assert.That(define.IsScaffold).IsFalse();
            await Assert.That(define.TriggerSpelling).IsEqualTo("kill");
            await Assert.That(define.View).IsEqualTo("kill");
            await Assert.That(define.Events).IsEquivalentTo(["player_death"]);
            await Assert.That(define.Gate).IsEqualTo("round.bomb.was_planted");
            await Assert.That(define.Trigger.Where).IsEqualTo("player.team == 3");
            await Assert.That(define.Form.Set.Select(r => r.Name)).IsEquivalentTo(["enemy"]);
            await Assert.That(define.Form.Rows.Where(r => !r.IsSet).Select(r => r.Name)).Contains("headshot");
            await Assert.That(define.SourceLine).IsGreaterThan(0);
            await Assert.That(define.Label).IsNull();
            await Assert.That(define.Per).IsEmpty();
            await Assert.That(retake.Define).IsNotNull();
            await Assert.That(retake.Define!.Key).IsEqualTo(define.Key).Because("the stat names the node it fires through");
            await Assert.That(faces.ToList().FindIndex(f => f.Key == define.Key)).IsLessThan(faces.ToList().FindIndex(f => f.Key == retake.Key))
                .Because("the document order puts define: before stats:");
            await Assert.That(node.Id).IsEqualTo("define.ct_retake_kill");
            await Assert.That(node.Title).IsEqualTo("ct_retake_kill");
            await Assert.That(node.Kind).IsEqualTo("define");
            await Assert.That(node.KindLabel).IsEqualTo("DEFINE");
            await Assert.That(node.IsDefine).IsTrue();
            await Assert.That(node.IsScaffold).IsFalse();
            await Assert.That(node.View).IsEqualTo("kill");
            await Assert.That(node.Chips).IsEquivalentTo(["on kill", "while round.bomb.was_planted"]);
            await Assert.That(node.Expression).IsEqualTo("where player.team == 3");
            await Assert.That(node.Label).IsNull();
        }
    }

    [Test]
    public async Task ADependencysDefine_IsNotDrawn_AndADefineNothingFiresOn_TakesTheCatalogsView()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("base", """
            ruleset: base
            title: Base
            for: each_player

            define:
              base_kill:
                on: kill
                match: { enemy: true }

            stats:
              base_kills:
                count: base_kill
                per: round
            """);
        rules.User("top", """
            ruleset: top
            title: Top
            for: each_player
            use: [base]

            define:
              lonely:
                on: death
              listed: [awp, ssg08]

            stats:
              kills:
                count: kill
                per: round
            """);
        RulesetCorpus.Composed composed = rules.Load().Compose("top");
        RulesetDocumentNode lonely = composed.Graph.Nodes.Single(n => n.IsDefine);
        using (Assert.Multiple())
        {
            await Assert.That(composed.Diagnostics).IsEmpty();
            await Assert.That(lonely.Key).IsEqualTo(RulesetDocumentNodeKey.Define("top", "lonely"));
            await Assert.That(lonely.View).IsEqualTo("death").Because("nothing fires on it, so the catalog answers for the spelling");
            await Assert.That(lonely.Events).IsEquivalentTo(["player_death"]);
            await Assert.That(composed.Graph.Edges.Any(e => e.Source == lonely.Key)).IsFalse();
            await Assert.That(composed.Graph.Nodes.Any(n => n.Id == "define.base_kill")).IsFalse().Because("a use: dependency's define cannot trigger a stat here");
            await Assert.That(composed.Graph.Nodes.Any(n => n.Id == "define.listed")).IsFalse().Because("a list define is not a trigger");
            await Assert.That(composed.Graph.Nodes.Single(n => n.Id == "base_kills").IsAuthorable).IsFalse();
        }
    }

    private const string Lab = """
        ruleset: define_lab
        title: Define lab
        for: each_player

        define:
          post_plant_kill:
            on: kill                         # the retake frag
            match: { enemy: true }
            while: round.bomb.was_planted

        stats:
          late_kills:
            count: post_plant_kill
            per: round
          seen:
            flag:
              when: "late_kills > 0"
            per: round
        """;

    private static (RulesTree Rules, CanvasTabViewModel Vm) OpenLab()
    {
        RulesTree rules = RulesTree.New();
        rules.User("define_lab", Lab);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "define_lab";
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Define(CanvasTabViewModel vm) => vm.Nodes.Single(n => n.IsDefine);

    /// <summary>
    ///     The define node carries a count's editors less the actor toggle, the scope and the label: the trigger
    ///     picker, the facet rows and their add picker, the gate editor and the where rows. Each writes under
    ///     define: through its own helpers, and the stats firing on it follow by name.
    /// </summary>
    [Test]
    public async Task ADefineNode_HasACountsEditors_LessActorScopeAndLabel()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenLab();
        using (rules)
        {
            CanvasNodeViewModel define = Define(vm);
            CanvasNodeViewModel count = vm.Nodes.Single(n => n.Id == "late_kills");
            using (Assert.Multiple())
            {
                await Assert.That(vm.Diagnostics).IsEmpty();
                await Assert.That(define.IsEditable).IsTrue();
                await Assert.That(define.Title).IsEqualTo("post_plant_kill");
                await Assert.That(define.ShowTriggerPicker).IsTrue();
                await Assert.That(define.Trigger!.Key).IsEqualTo("on");
                await Assert.That(define.Trigger.Spelling).IsEqualTo("kill");
                await Assert.That(define.Trigger.Options.Any(o => o.Group == "define")).IsFalse().Because("a define cannot fire on a define");
                await Assert.That(define.Trigger.Options.Any(o => o.Written == "raw.player_death")).IsTrue();
                await Assert.That(define.ShowFieldPickers).IsTrue();
                await Assert.That(define.ShowPerPicker).IsFalse().Because("a define has no scope; the stats firing on it keep theirs");
                await Assert.That(define.ShowGatePicker).IsTrue();
                await Assert.That(define.GateChoice).IsEqualTo("round.bomb.was_planted");
                await Assert.That(define.GateChoices).Contains("seen").Because("a sibling flag can gate a define");
                await Assert.That(define.CanEditLabel).IsFalse();
                await Assert.That(define.ShowActorToggle).IsFalse();
                await Assert.That(define.ShowFacetPicker).IsTrue();
                await Assert.That(define.Facets.Select(f => f.Name)).IsEquivalentTo(["enemy"]);
                await Assert.That(define.AvailableFacets).Contains("headshot");
                await Assert.That(define.Lines.Select(l => l.Key)).IsEquivalentTo(["where"]);
                await Assert.That(define.VisibleChips).IsEmpty().Because("the trigger picker and the listed gate stand in for the chips");
                await Assert.That(define.Surfacing).IsEmpty().Because("show: cannot surface a define");
                await Assert.That(count.ShowPerPicker).IsTrue();
                await Assert.That(count.Chips).Contains("on post_plant_kill");
                await Assert.That(count.AvailableFacets).DoesNotContain("enemy");
                await Assert.That(CanvasEditSemantics.TriggerKeyFor(define)).IsEqualTo("on");
                await Assert.That(CanvasEditSemantics.WriteField(define, "per", "match").IsRefused).IsTrue().Because("a define has no per:");
                await Assert.That(CanvasEditSemantics.WriteField(define, "per", "match").Refusal).IsNull();
                await Assert.That(CanvasEditSemantics.WriteField(define, "on", null).Refusal).Contains("needs a trigger");
            }
        }
    }

    [Test]
    public async Task ADefineNodesEditors_WriteUnderDefine_AndTheStatsFollowByName()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenLab();
        using (rules)
        {
            string Text() => File.ReadAllText(rules.UserPath("define_lab"));

            CanvasTriggerViewModel trigger = Define(vm).Trigger!;
            trigger.Selected = trigger.Options.Single(o => o.Written == "death");
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.DefineField(Text(), "post_plant_kill", "on")).IsEqualTo("death");
                await Assert.That(Text()).Contains("on: death                         # the retake frag");
                await Assert.That(RulesetEdits.DefineFacetKeys(Text(), "post_plant_kill")).IsEquivalentTo(["enemy"]).Because("death knows enemy too");
                await Assert.That(vm.Diagnostics).IsEmpty();
                await Assert.That(Define(vm).View).IsEqualTo("death");
                await Assert.That(vm.Nodes.Single(n => n.Id == "late_kills").View).IsEqualTo("death").Because("the stat fires on the define by name");
                await Assert.That(vm.Connections.Any(c => c.Source.Owner?.IsDefine == true && c.Target.Owner?.Id == "late_kills" && c.Text == "fires")).IsTrue();
            }

            Define(vm).FacetToAdd = "headshot";
            await Assert.That(RulesetEdits.DefineFacetKeys(Text(), "post_plant_kill")).IsEquivalentTo(["enemy", "headshot"]);
            await Assert.That(Define(vm).Facets.Select(f => f.Name)).IsEquivalentTo(["enemy", "headshot"]);

            Define(vm).Facets.Single(f => f.Name == "enemy").RemoveCommand.Execute(null);
            await Assert.That(RulesetEdits.DefineFacetKeys(Text(), "post_plant_kill")).IsEquivalentTo(["headshot"]);

            CanvasNodeViewModel gated = Define(vm);
            gated.GateText = "round.active";
            gated.CommitGate();
            await Assert.That(RulesetEdits.DefineField(Text(), "post_plant_kill", "while")).IsEqualTo("round.active");
            await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Id == "RoundActive" && c.Target.Owner?.IsDefine == true && c.IsGate)).IsTrue()
                .Because("the gate wire runs into the define");

            CanvasNodeViewModel ungated = Define(vm);
            ungated.GateText = "";
            ungated.CommitGate();
            await Assert.That(RulesetEdits.DefineField(Text(), "post_plant_kill", "while")).IsNull();

            CanvasExpressionViewModel where = Define(vm).Lines.Single(l => l.Key == "where");
            where.Text = "player.team == 3";
            where.Commit();
            await Assert.That(RulesetEdits.DefineField(Text(), "post_plant_kill", "where")).IsEqualTo("player.team == 3");
            await Assert.That(vm.Diagnostics).IsEmpty();
            await Assert.That(Define(vm).Lines.Single(l => l.Key == "where").Text).IsEqualTo("player.team == 3");

            where = Define(vm).Lines.Single(l => l.Key == "where");
            where.Text = "";
            where.Commit();
            await Assert.That(RulesetEdits.DefineField(Text(), "post_plant_kill", "where")).IsNull();
            await Assert.That(vm.CanUndo).IsTrue();
        }
    }

    [Test]
    public async Task WhenTheCompositionFails_TheDefineIsStillDrawn_FromTheDocument()
    {
        using RulesTree rules = RulesTree.WithUser("post_plant_double");
        string path = rules.UserPath("post_plant_double");
        File.WriteAllText(path, File.ReadAllText(path).Replace("when: post_plant_kills >= params.min_kills", "when: no_such_stat >= 1", StringComparison.Ordinal));
        RulesetCorpus.Composed composed = rules.Load().Compose("post_plant_double");
        RulesetDocumentNodeKey key = RulesetDocumentNodeKey.Define("post_plant_double", "post_plant_kill");
        IReadOnlyList<NodeFace> faces = NodeFaceBuilder.Build(composed);
        using (Assert.Multiple())
        {
            await Assert.That(composed.Chain.Nodes.All(n => n.Kind is ChainNodeKind.Show or ChainNodeKind.Params)).IsTrue()
                .Because("nothing lowered; only the document's own show: and params: blocks are drawn");
            await Assert.That(composed.Graph.Nodes.Single(n => n.Key == key).View).IsEqualTo("kill").Because("the on: names a catalog view");
            await Assert.That(composed.Graph.Edges.Where(e => e.Source == key).Select(e => e.Target.Id)).IsEquivalentTo(["post_plant_kills", "kill_ticks"]);
            await Assert.That(faces.OfType<DefineFace>().Single().Gate).IsEqualTo("round.bomb.was_planted");
            IReadOnlyList<WireSpec> wires = ChainWiring.Wires(composed, composed.Graph.Nodes.Select(n => n.Key).ToHashSet());
            await Assert.That(wires.Where(w => w.Source == key).All(w => !w.IsRead && w.Text == "fires")).IsTrue()
                .Because("a define fires its stats; it is not read by them");
        }
    }
}
