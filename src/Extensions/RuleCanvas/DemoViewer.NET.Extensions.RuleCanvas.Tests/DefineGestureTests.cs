#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The define on the canvas as a gesture target and source: placed from the gallery, put in front of a
///     stat, wired into one, retargeted and gated from the engine nodes, duplicated, deleted, and shown in the
///     Inspector. Each case checks the file and the chain that redraws from it.
/// </summary>
public class DefineGestureTests
{
    private const string Lab = """
        ruleset: gestures
        title: Gestures
        for: each_player

        define:
          post_plant_kill:
            on: kill
            match: { enemy: true }
            while: round.bomb.was_planted

        stats:
          late_kills:
            count: post_plant_kill
            per: round
          deaths:
            count: death
            per: round
          enemy_kills:
            count: kill
            match: { enemy: true }
            per: round
          live_deaths:
            count: death
            while: match.live
            per: round
          seen:
            flag:
              when: "deaths > 0"
            per: round

        highlights:
          ace:
            when: late_kills >= 5
            per: round
        """;

    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.New();
        rules.User("gestures", Lab);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "gestures";
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id && !n.IsScaffold);

    private static CanvasNodeViewModel Define(CanvasTabViewModel vm, string name) => vm.Nodes.Single(n => n.Key.IsDefine && n.Title == name);

    private static GalleryItem Item(CanvasTabViewModel vm, string group, string name) =>
        vm.Gallery.Single(g => g.Title == group).Items.Single(i => i.Name == name);

    private static string Text(RulesTree rules) => File.ReadAllText(rules.UserPath("gestures"));

    [Test]
    public async Task TheGallery_OffersADefine_InTheStatsGroup()
    {
        GalleryItem define = CanvasGallery.Build(EmbeddedCatalog.Root).Single(g => g.Title == "Stats").Items.Single(i => i.Kind == GalleryItemKind.Define);
        using (Assert.Multiple())
        {
            await Assert.That(define.Name).IsEqualTo("define");
            await Assert.That(define.KindLabel).IsEqualTo("DEFINE");
            await Assert.That(define.Hint).IsEqualTo("A named trigger: a view with its own match, while and where, that stats fire on.");
            await Assert.That(define.Body).IsEqualTo("on: kill");
            await Assert.That(CanvasEditSemantics.PlacedIdFor(define)).IsEqualTo("new_define");
            await Assert.That(CanvasEditSemantics.PlacedKeyFor(define, "r", "new_define")).IsEqualTo(RulesetDocumentNodeKey.Define("r", "new_define"));
        }
    }

    [Test]
    public async Task ADefineDroppedOnTheCanvas_IsWrittenAboveStats_AndPlacedWhereItLanded()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        GalleryItem define = Item(vm, "Stats", "define");

        vm.PlaceItem(define, new Point(120, 640));
        vm.PlaceItem(define, new Point(120, 900));

        string text = File.ReadAllText(rules.UserPath("player_round"));
        CanvasNodeViewModel added = vm.Nodes.Single(n => n.Key.IsDefine && n.Title == "new_define");
        using (Assert.Multiple())
        {
            await Assert.That(vm.DiagnosticEntries.All(d => !d.IsError)).IsTrue().Because(vm.Diagnostics);
            await Assert.That(vm.DiagnosticEntries.Count(d => d.Message.Contains("nothing fires on define", StringComparison.Ordinal))).IsEqualTo(2)
                .Because("a define nothing fires on yet is a warning at the define, not a bare node");
            await Assert.That(text).Contains("define:\n  new_define:\n    on: kill\n  new_define_2:\n    on: kill\n");
            await Assert.That(text.IndexOf("define:", StringComparison.Ordinal)).IsLessThan(text.IndexOf("\nstats:", StringComparison.Ordinal))
                .Because("the block opens above stats:, as the loader orders them");
            await Assert.That(added.Location).IsEqualTo(new Point(120, 640)).Because("the entry lands where it was dropped");
            await Assert.That(added.Id).IsEqualTo("define.new_define");
            await Assert.That(added.IsEditable).IsTrue();
            await Assert.That(added.Chips).IsEquivalentTo(["on kill"]);
            await Assert.That(vm.Nodes.Any(n => n.Key.IsDefine && n.Title == "new_define_2")).IsTrue().Because("the second drop gets a fresh name");
            await Assert.That(vm.Connections.Any(c => c.Target.Owner == added)).IsFalse().Because("nothing fires on it yet, and the engine lowers no event for it");
            await Assert.That(vm.CanUndo).IsTrue();
        }
    }

    [Test]
    public async Task ADefineDroppedOnAStat_StandsInFrontOfIt_WithTheStatsOwnTrigger()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.DropOnNode(Item(vm, "Stats", "define"), Node(vm, "deaths"));
            string text = Text(rules);
            using (Assert.Multiple())
            {
                await Assert.That(vm.Diagnostics).IsEmpty();
                await Assert.That(RulesetEdits.DefineField(text, "new_define", "on")).IsEqualTo("death").Because("the define takes the stat's trigger");
                await Assert.That(RulesetEdits.Field(text, "deaths", "count")).IsEqualTo("new_define").Because("the stat now fires on it");
                await Assert.That(RulesetEdits.DefineNames(text)).IsEquivalentTo(["post_plant_kill", "new_define"]);
                await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Title == "new_define" && c.Target.Owner?.Id == "deaths" && c.Text == "fires")).IsTrue();
                await Assert.That(Node(vm, "deaths").Chips).Contains("on new_define");
            }

            string before = Text(rules);
            vm.DropOnNode(Item(vm, "Stats", "define"), Node(vm, "late_kills"));
            await Assert.That(Text(rules)).IsEqualTo(before).Because("a stat already firing on a define keeps it");
            await Assert.That(vm.EditStatus).Contains("already fires on post_plant_kill");

            vm.DropOnNode(Item(vm, "Stats", "define"), Node(vm, "seen"));
            await Assert.That(vm.EditStatus).Contains("fires on no view");

            vm.DropOnNode(Item(vm, "Stats", "define"), Define(vm, "post_plant_kill"));
            await Assert.That(vm.EditStatus).Contains("cannot fire on a define");

            vm.DropOnNode(Item(vm, "Stats", "define"), Node(vm, "ace"));
            await Assert.That(vm.EditStatus).Contains("only be dropped on a define or a stat");
        }
    }

    // The interposed define used to land wherever the arranger's free spot was, often a screen away.
    [Test]
    public async Task ADefinePutInFrontOfAStat_LandsJustLeftOfIt()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            Node(vm, "deaths").Location = new Point(900, 300);
            vm.SaveLayoutCommand.Execute(null);
            vm.DropOnNode(Item(vm, "Stats", "define"), Node(vm, "deaths"));
            using (Assert.Multiple())
            {
                await Assert.That(Define(vm, "new_define").Location).IsEqualTo(new Point(900 - CanvasTabViewModel.InterposedDefineOffset, 300));
                await Assert.That(Node(vm, "deaths").Location).IsEqualTo(new Point(900, 300)).Because("the stat stays where it was");
            }
        }
    }

    [Test]
    public async Task AWireFromADefineToAStat_SetsTheStatsTrigger_AndThePreviewSaysSo()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel define = Define(vm, "post_plant_kill");
            using (Assert.Multiple())
            {
                await Assert.That(CanvasEditSemantics.ConnectPreview(define, Node(vm, "deaths"))).IsEqualTo("fires deaths on post_plant_kill");
                await Assert.That(CanvasEditSemantics.ConnectPreview(define, Node(vm, "ace"))).Contains("reads stats");
                await Assert.That(CanvasEditSemantics.ConnectPreview(define, Node(vm, "seen"))).Contains("fires on no view");
            }

            vm.Connect(define, Node(vm, "deaths"));
            string text = Text(rules);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "deaths", "count")).IsEqualTo("post_plant_kill");
                await Assert.That(vm.Diagnostics).IsEmpty();
                await Assert.That(vm.Connections.Count(c => c.Source.Owner?.IsDefine == true && c.Text == "fires")).IsEqualTo(2)
                    .Because("the define now fires late_kills and deaths");
                await Assert.That(vm.Connections.Count(c => c.Target.Owner?.IsDefine == true && !c.IsRead && !c.IsGate)).IsEqualTo(1)
                    .Because("one event wire into the define");
            }

            string before = Text(rules);
            vm.Connect(define, Node(vm, "ace"));
            await Assert.That(Text(rules)).IsEqualTo(before);
            vm.Connect(Node(vm, "deaths"), define);
            await Assert.That(Text(rules)).IsEqualTo(before);
            await Assert.That(vm.EditStatus).Contains("fires on an event or a view");
        }
    }

    [Test]
    public async Task EngineNodesWiredIntoADefine_RetargetItGateItAndBindItsFacets()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel define = Define(vm, "post_plant_kill");
            // An event that fires one view names the trigger outright; the lab's chain holds the round end.
            CanvasNodeViewModel single = vm.Nodes.First(n => n.IsScaffold && n.Kind == "event" && n.Id != "player_death"
                                                             && CanvasEditSemantics.ViewsFiredBy(n.Id).Count == 1);
            string view = CanvasEditSemantics.ViewsFiredBy(single.Id)[0];
            bool keepsEnemy = FaceVocabulary.ViewOf(view)!.Facets.Any(f => f.Name == "enemy");
            string[] expectedFacets = keepsEnemy ? ["enemy"] : [];
            await Assert.That(CanvasEditSemantics.ConnectPreview(single, define)).IsEqualTo("sets on: " + view);
            vm.Connect(single, define);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.DefineField(Text(rules), "post_plant_kill", "on")).IsEqualTo(view);
                await Assert.That(RulesetEdits.DefineFacetKeys(Text(rules), "post_plant_kill")).IsEquivalentTo(expectedFacets)
                    .Because("a facet the new view lacks goes with the retarget");
                await Assert.That(vm.Diagnostics).IsEmpty();
            }

            vm.Connect(Define(vm, "post_plant_kill"), Node(vm, "deaths"));
            vm.Connect(vm.Nodes.Single(n => n.IsScaffold && n.Id == "player_death"), Define(vm, "post_plant_kill"));
            await Assert.That(vm.EditStatus).Contains("drop the view you mean").Because("player_death fires several views");

            vm.DropOnNode(Item(vm, "Views", "kill"), Define(vm, "post_plant_kill"));
            await Assert.That(RulesetEdits.DefineField(Text(rules), "post_plant_kill", "on")).IsEqualTo("kill");
            if (!keepsEnemy)
            {
                vm.Connect(vm.Nodes.Single(n => n.IsScaffold && n.Id == "enrich.kill.was_enemy_kill"), Define(vm, "post_plant_kill"));
            }

            await Assert.That(RulesetEdits.DefineFacetKeys(Text(rules), "post_plant_kill")).IsEquivalentTo(["enemy"]);

            CanvasNodeViewModel matchLive = vm.Nodes.Single(n => n.IsScaffold && n.Id == "MatchLive");
            await Assert.That(CanvasEditSemantics.ConnectPreview(matchLive, Define(vm, "post_plant_kill"))).IsEqualTo("sets while: match.live");
            vm.Connect(matchLive, Define(vm, "post_plant_kill"));
            await Assert.That(RulesetEdits.DefineField(Text(rules), "post_plant_kill", "while")).IsEqualTo("match.live");
            await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Id == "MatchLive" && c.Target.Owner?.IsDefine == true && c.IsGate)).IsTrue();

            CanvasConnectionViewModel gate = vm.Connections.Single(c => c.Source.Owner?.Id == "MatchLive" && c.Target.Owner?.IsDefine == true && !c.IsRead);
            vm.DisconnectCommand.Execute(gate);
            await Assert.That(RulesetEdits.DefineField(Text(rules), "post_plant_kill", "while")).IsNull();

            CanvasConnectionViewModel read = vm.Connections.Single(c => c.Source.Owner?.Id == "enrich.kill.was_enemy_kill" && c.Target.Owner?.IsDefine == true);
            vm.DisconnectCommand.Execute(read);
            await Assert.That(RulesetEdits.DefineFacetKeys(Text(rules), "post_plant_kill")).IsEmpty();

            CanvasConnectionViewModel fires = vm.Connections.First(c => c.Source.Owner?.IsDefine == true && c.Text == "fires");
            string detached = fires.Target.Owner!.Id;
            vm.DisconnectCommand.Execute(fires);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(Text(rules), detached, "count")).IsEqualTo("").Because("the fires wire is the stat's trigger, and removing it detaches the stat");
                await Assert.That(RulesetEdits.DefineNames(Text(rules))).Contains("post_plant_kill").Because("the define stays");
            }
        }
    }

    [Test]
    public async Task AGateDroppedOnADefine_SetsItsWhile_AndTwoGatesOnOneTrigger_AreRefused()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.DropOnNode(Item(vm, "Gates", "round.active"), Define(vm, "post_plant_kill"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.DefineField(Text(rules), "post_plant_kill", "while")).IsEqualTo("round.active");
                await Assert.That(vm.Diagnostics).IsEmpty();
                await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Id == "RoundActive" && c.Target.Owner?.IsDefine == true && c.IsGate)).IsTrue();
            }

            // The resolver reads a define's while: and a firing stat's as one compound gate and refuses it.
            string before = Text(rules);
            vm.DropOnNode(Item(vm, "Gates", "match.live"), Node(vm, "late_kills"));
            await Assert.That(Text(rules)).IsEqualTo(before).Because("late_kills fires on a gated define");
            await Assert.That(vm.EditStatus).Contains("already gated on round.active");

            vm.Connect(vm.Nodes.Single(n => n.IsScaffold && n.Id == "MatchLive"), Node(vm, "late_kills"));
            await Assert.That(Text(rules)).IsEqualTo(before);

            Node(vm, "late_kills").GateText = "match.live";
            Node(vm, "late_kills").CommitGate();
            await Assert.That(Text(rules)).IsEqualTo(before).Because("the gate editor is refused the same way");

            // The other way round: a define cannot take a gate while a stat firing on it has its own.
            vm.Connect(Define(vm, "post_plant_kill"), Node(vm, "live_deaths"));
            await Assert.That(vm.EditStatus).IsEmpty().Because("retargeting is allowed; the compound is the gate's problem: " + vm.Diagnostics);
            Define(vm, "post_plant_kill").GateText = "";
            Define(vm, "post_plant_kill").CommitGate();
            await Assert.That(RulesetEdits.DefineField(Text(rules), "post_plant_kill", "while")).IsNull();
            before = Text(rules);
            vm.DropOnNode(Item(vm, "Gates", "round.active"), Define(vm, "post_plant_kill"));
            await Assert.That(Text(rules)).IsEqualTo(before);
            await Assert.That(vm.EditStatus).Contains("live_deaths already carry a while:");
        }
    }

    [Test]
    public async Task DeletingADefine_IsRefusedWhileAStatFiresOnIt_AndGoesWithThem()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = Text(rules);
            vm.SelectedNodes.Add(Define(vm, "post_plant_kill"));
            vm.DeleteSelectedCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(vm.EditStatus).IsEqualTo("post_plant_kill is fired on by late_kills; retarget them first");
                await Assert.That(Text(rules)).IsEqualTo(before);
                await Assert.That(vm.Inspector.DeleteRefusal).Contains("fired on by late_kills");
                await Assert.That(vm.Inspector.CanDelete).IsFalse();
                await Assert.That(vm.Inspector.Heading).IsEqualTo("DEFINE post_plant_kill");
                await Assert.That(vm.Inspector.ShowsSurfacing).IsFalse();
                await Assert.That(vm.Inspector.Id.IsVisible).IsTrue();
                await Assert.That(vm.Inspector.Gate.IsVisible).IsTrue();
                await Assert.That(vm.Inspector.Label.IsVisible).IsFalse();
                await Assert.That(vm.Inspector.Per.IsVisible).IsFalse();
            }

            // The define and the stat firing on it go together: nothing outside the selection names the define.
            vm.SelectedNodes.Add(Node(vm, "late_kills"));
            vm.DeleteSelectedCommand.Execute(null);
            await Assert.That(vm.EditStatus).IsEqualTo("late_kills is read by ace").Because("the highlight still reads the stat");

            vm.SelectedNodes.Add(Node(vm, "ace"));
            vm.DeleteSelectedCommand.Execute(null);
            string after = Text(rules);
            using (Assert.Multiple())
            {
                await Assert.That(vm.EditStatus).IsEmpty();
                await Assert.That(after).DoesNotContain("define:").Because("the last define takes the block with it");
                await Assert.That(after).DoesNotContain("late_kills:");
                await Assert.That(after).DoesNotContain("  ace:");
                await Assert.That(vm.Nodes.Any(n => n.IsDefine)).IsFalse();
            }

            vm.UndoCommand.Execute(null);
            await Assert.That(Text(rules)).IsEqualTo(before).Because("one write, one undo");
        }
    }

    [Test]
    public async Task ADefine_IsDuplicatedWithItsBody_AndRenamedFromTheInspector()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.DuplicateNode(Define(vm, "post_plant_kill"));
            string text = Text(rules);
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("  post_plant_kill_copy:\n    on: kill\n    match: { enemy: true }\n    while: round.bomb.was_planted\n");
                await Assert.That(vm.DiagnosticEntries.All(d => !d.IsError)).IsTrue().Because(vm.Diagnostics);
                await Assert.That(vm.Nodes.Single(n => n.Title == "post_plant_kill_copy").Diagnostics.Select(d => d.Weight)).IsEquivalentTo([DiagnosticWeight.Warning])
                    .Because("the copy fires nothing yet, which its face says");
                await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Single().Title).IsEqualTo("post_plant_kill_copy");
                await Assert.That(RulesetEdits.StatsFiringOn(text, "post_plant_kill_copy")).IsEmpty();
            }

            vm.SelectedNodes.Clear();
            vm.SelectedNodes.Add(Define(vm, "post_plant_kill"));
            vm.Inspector.Id.Text = "retake_kill";
            vm.Inspector.Id.Commit();
            text = Text(rules);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.DefineNames(text)).IsEquivalentTo(["retake_kill", "post_plant_kill_copy"]);
                await Assert.That(RulesetEdits.Field(text, "late_kills", "count")).IsEqualTo("retake_kill").Because("the stat follows the rename");
                await Assert.That(vm.DiagnosticEntries.All(d => !d.IsError)).IsTrue().Because(vm.Diagnostics);
                await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Single().Key).IsEqualTo(RulesetDocumentNodeKey.Define("gestures", "retake_kill"))
                    .Because("the Inspector follows the renamed node");
            }

            vm.Inspector.Id.Text = "deaths";
            vm.Inspector.Id.Commit();
            await Assert.That(vm.Inspector.Id.Error).Contains("already declared");

            vm.Inspector.Gate.Text = "round.active";
            vm.Inspector.Gate.Commit();
            await Assert.That(RulesetEdits.DefineField(Text(rules), "retake_kill", "while")).IsEqualTo("round.active");
        }
    }
}
