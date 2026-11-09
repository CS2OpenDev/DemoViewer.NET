#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The Primitives group: blank entries placed first and finished on the canvas. Each one writes an entry
///     that loads unfinished, is drawn with its ports, and is kept out of the composition with a warning.
/// </summary>
public class CanvasPrimitivesTests
{
    private const string Header = "ruleset: lab\ntitle: Lab\nfor: each_player\n";

    private static (RulesTree Rules, CanvasTabViewModel Vm) Open(string body = "stats:\n  kills:\n    count: kill\n    per: round\n")
    {
        RulesTree rules = RulesTree.New();
        rules.User("lab", Header + body);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "lab";
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static GalleryItem Item(CanvasTabViewModel vm, string title) =>
        vm.Gallery.Single(g => g.Title == "Primitives").Items.Single(i => i.Title == title);

    private static string Text(RulesTree rules) => File.ReadAllText(rules.UserPath("lab"));

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id && n.IsEntry);

    [Test]
    public async Task ThePrimitives_ComeFirst_AndKeepThePrefabs()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            using (Assert.Multiple())
            {
                await Assert.That(vm.Gallery[0].Title).IsEqualTo("Primitives");
                await Assert.That(vm.Gallery[0].Items.Select(i => i.Title))
                    .IsEquivalentTo(["Blank stat", "Blank event", "Blank define", "Blank highlight", "Blank flag"]);
                await Assert.That(vm.Gallery.Single(g => g.Title == "Prefabs").Items.Select(i => i.Name)).Contains("enemy_kills");
                await Assert.That(vm.Gallery.Single(g => g.Title == "Highlights").Items.Single(i => i.Name == "highlight").Title).IsEqualTo("Survived highlight");
            }
        }
    }

    [Test]
    public async Task ABlankStat_WritesNoKindAndNoTrigger_AndIsDrawnUnfinished()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.PlaceItem(Item(vm, "Blank stat"), new Point(300, 0));
            string text = Text(rules);
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("  new_stat:\n    per: round\n");
                await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("new_stat").Because("an entry with no kind is drawn, not lost");
                await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("kills").Because("the rest of the ruleset still composes");
                await Assert.That(vm.DiagnosticEntries.Where(d => d.IsError).Select(d => d.Message)).IsEquivalentTo(["new_stat is unfinished: pick a kind"])
                    .Because("a stat with no kind cannot run, and the loader's own no-kind line is not repeated");
                await Assert.That(Node(vm, "new_stat").IsDraft).IsTrue();
            }
        }
    }

    [Test]
    public async Task ABlankDefine_WritesAnEmptyOn_AndDrawsAsADefine()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.PlaceItem(Item(vm, "Blank define"), new Point(300, 0));
            CanvasNodeViewModel define = vm.Nodes.Single(n => n.IsDefine && n.Title == "new_define");
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).Contains("define:\n  new_define:\n    on:");
                await Assert.That(define.Port(PortKind.Trigger)).IsNotNull().Because("an empty on: is a trigger define, ready to wire");
                await Assert.That(define.Diagnostics.Select(d => d.Message)).Contains("nothing fires on define new_define: set a stat's trigger to it, or drop it on a trigger wire");
            }
        }
    }

    [Test]
    public async Task ABlankHighlight_WritesAnEmptyWhen_WithTheLogicPortReady()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.PlaceItem(Item(vm, "Blank highlight"), new Point(300, 0));
            CanvasNodeViewModel highlight = Node(vm, CanvasGallery.BlankHighlightName);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.HighlightField(Text(rules), CanvasGallery.BlankHighlightName, "when")).IsEqualTo("");
                await Assert.That(highlight.IsHighlight).IsTrue();
                await Assert.That(highlight.LogicPort).IsNotNull();
                await Assert.That(highlight.Diagnostics.Select(d => d.Message)).Contains($"{CanvasGallery.BlankHighlightName} has a blank when: type the test, or wire a stat into its when port");
                await Assert.That(vm.ErrorCount).IsEqualTo(0);
            }
        }
    }

    [Test]
    public async Task ABlankFlag_WritesAnEmptyWhen_WithTheLogicPortReady()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.PlaceItem(Item(vm, "Blank flag"), new Point(300, 0));
            CanvasNodeViewModel flag = Node(vm, "new_flag");
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.FlagWhen(Text(rules), "new_flag")).IsEqualTo("");
                await Assert.That(flag.Kind).IsEqualTo("flag");
                await Assert.That(flag.LogicPort).IsNotNull();
                await Assert.That(flag.Diagnostics.Select(d => d.Message)).Contains("new_flag has an empty when: type the test, or wire a stat into its when port");
                await Assert.That(vm.ErrorCount).IsEqualTo(0);
            }
        }
    }

    [Test]
    public async Task AStatWithNoKind_IsDrawnFromTheText_AsADraftWithAKindPicker()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open("stats:\n  kills:\n    count: kill\n    per: round\n  raw:\n    label: Raw\n    per: match\n");
        using (rules)
        {
            CanvasNodeViewModel draft = Node(vm, "raw");
            CanvasDiagnostic error = vm.DiagnosticEntries.Single(d => d.IsError);
            using (Assert.Multiple())
            {
                await Assert.That(draft.IsDraft).IsTrue();
                await Assert.That(draft.Kind).IsEqualTo(DraftFace.KindName).Because("the stat family colour");
                await Assert.That(draft.Label).IsEqualTo("Raw");
                await Assert.That(draft.Per).IsEqualTo("match");
                await Assert.That(draft.Choices.Single().Key).IsEqualTo("kind");
                await Assert.That(draft.Choices.Single().Options.Skip(1).Select(o => o.Value)).IsEquivalentTo(DraftFace.PickableKinds);
                await Assert.That(error.Message).IsEqualTo("raw is unfinished: pick a kind");
                await Assert.That(error.Node).IsEqualTo(draft);
                await Assert.That(vm.DiagnosticEntries.Any(d => d.Message.Contains("has no kind", StringComparison.Ordinal))).IsFalse();
                await Assert.That(Node(vm, "kills").IsDraft).IsFalse().Because("the rest of the ruleset composes");
            }

            int framed = 0;
            vm.FrameRequested += () => framed++;
            vm.SelectDiagnosticCommand.Execute(error);
            using (Assert.Multiple())
            {
                await Assert.That(vm.SelectedNodes.Single()).IsEqualTo(draft);
                await Assert.That(framed).IsEqualTo(1).Because("the error frames the draft node");
            }
        }
    }

    [Test]
    [Arguments("count", PortKind.Trigger, "raw has no trigger: give count: a view, such as kill")]
    [Arguments("sum", PortKind.Trigger, "raw has no trigger: add on: with a view, such as damage_dealt")]
    [Arguments("flag", PortKind.Logic, "raw has an empty when: type the test, or wire a stat into its when port")]
    [Arguments("compute", PortKind.Read, "raw has no formula: type one under compute:, or wire a stat into its reads port")]
    public async Task PickingAKind_WritesItEmpty_AndTheStatLoadsUnfinishedOfThatKind(string kind, PortKind port, string warning)
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open("stats:\n  raw:\n    per: round\n");
        using (rules)
        {
            CanvasChoiceViewModel picker = Node(vm, "raw").Choices.Single(c => c.Key == "kind");
            picker.Selected = picker.Options.Single(o => o.Value == kind);
            CanvasNodeViewModel stat = Node(vm, "raw");
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(Text(rules), "raw", kind)).IsEqualTo("");
                await Assert.That(stat.IsDraft).IsFalse();
                await Assert.That(stat.Kind).IsEqualTo(kind);
                await Assert.That(stat.Port(port)).IsNotNull();
                await Assert.That(stat.Diagnostics.Select(d => d.Message)).IsEquivalentTo([warning]);
                await Assert.That(vm.ErrorCount).IsEqualTo(0);
            }
        }
    }

    [Test]
    public async Task PickingAKind_OnAStatWithTwo_LeavesTheOneKind()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open("stats:\n  raw:\n    count: kill\n    capture: player.team\n    per: round\n");
        using (rules)
        {
            await Assert.That(Node(vm, "raw").IsDraft).IsTrue().Because("two kinds load as none");
            CanvasChoiceViewModel picker = Node(vm, "raw").Choices.Single(c => c.Key == "kind");
            picker.Selected = picker.Options.Single(o => o.Value == "count");
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).Contains("  raw:\n    per: round\n    count: \"\"\n");
                await Assert.That(Node(vm, "raw").Kind).IsEqualTo("count");
            }
        }
    }

    [Test]
    public async Task AnUnfinishedCount_TakesItsTrigger_FromAnEventWire_OrAViewDrop()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open("stats:\n  plants:\n    count: bomb_planted\n    per: round\n  a:\n    count:\n    per: round\n  b:\n    count:\n    per: round\n");
        using (rules)
        {
            CanvasNodeViewModel planted = vm.Nodes.Single(n => n.Ruleset == "~event" && n.Id == "bomb_planted");
            await Assert.That(Node(vm, "a").Port(PortKind.Trigger)).IsNotNull().Because("an unfinished count is drawn with its on port");
            vm.ConnectCommand.Execute(((object)planted.Outputs[0], (object?)Node(vm, "a").Port(PortKind.Trigger)));
            vm.DropOnNode(vm.Gallery.Single(g => g.Title == "Views").Items.Single(i => i.Name == "kill"), Node(vm, "b"));
            vm.PlaceItem(Item(vm, "Blank define"), new Point(0, 400));
            planted = vm.Nodes.Single(n => n.Ruleset == "~event" && n.Id == "bomb_planted");
            vm.ConnectCommand.Execute(((object)planted.Outputs[0], (object?)vm.Nodes.Single(n => n.IsDefine).Port(PortKind.Trigger)));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.DefineField(Text(rules), "new_define", "on")).IsEqualTo("bomb_planted");
                await Assert.That(RulesetEdits.Field(Text(rules), "a", "count")).IsEqualTo("bomb_planted");
                await Assert.That(RulesetEdits.Field(Text(rules), "b", "count")).IsEqualTo("kill");
                await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Id == "bomb_planted" && c.Target.Owner?.Id == "a")).IsTrue();
                await Assert.That(vm.DiagnosticEntries.Select(d => d.Message).Where(m => m.Contains("no trigger", StringComparison.Ordinal))).IsEmpty();
            }
        }
    }

    [Test]
    public async Task SettingAKeyWrittenWithNothingAfterItsColon_FillsItInPlace()
    {
        const string text = Header + "define:\n  d:\n    on:\n    while: round.active\nstats:\n  a:\n    count:\n    per: round\nhighlights:\n  h:\n    when:\n    title: t\n";
        string written = RulesetEdits.SetField(text, "a", "count", "kill");
        written = RulesetEdits.SetDefineField(written, "d", "on", "kill");
        written = RulesetEdits.SetHighlightField(written, "h", "when", "a > 0");
        await Assert.That(written).IsEqualTo(Header + "define:\n  d:\n    on: kill\n    while: round.active\nstats:\n  a:\n    count: kill\n    per: round\n"
                                             + "highlights:\n  h:\n    when: a > 0\n    title: t\n");
    }

    [Test]
    public async Task SettingAnEmptyQuotedValue_WritesThePlainValue()
    {
        const string text = Header + "stats:\n  a:\n    per: round\n    count: \"\"\nhighlights:\n  h:\n    when: ''\n    title: t\n";
        string written = RulesetEdits.SetField(text, "a", "count", "kill");
        written = RulesetEdits.SetHighlightField(written, "h", "when", "a > 0");
        await Assert.That(written).IsEqualTo(Header + "stats:\n  a:\n    per: round\n    count: kill\nhighlights:\n  h:\n    when: a > 0\n    title: t\n");
    }

    [Test]
    public async Task AStatWiredIntoABlankWhen_WritesTheFirstRow_AndTheWireEditorSetsIt()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.PlaceItem(Item(vm, "Blank highlight"), new Point(400, 0));
            vm.PlaceItem(Item(vm, "Blank flag"), new Point(400, 300));
            CanvasWalkthrough.WireInto(vm, Node(vm, "kills"), Node(vm, CanvasGallery.BlankHighlightName).LogicPort!);
            CanvasWalkthrough.WireInto(vm, Node(vm, "kills"), Node(vm, "new_flag").LogicPort!);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.HighlightField(Text(rules), CanvasGallery.BlankHighlightName, "when")).IsEqualTo("kills > 0");
                await Assert.That(RulesetEdits.FlagWhen(Text(rules), "new_flag")).IsEqualTo("kills > 0");
                await Assert.That(vm.WarningCount).IsEqualTo(0).Because(string.Join("; ", vm.DiagnosticEntries.Select(d => d.Message)));
            }

            vm.BeginEditWire(vm.Connections.Single(c => c.Source.Owner?.Id == "kills" && c.Target.Owner?.Id == CanvasGallery.BlankHighlightName));
            vm.WireEditor!.Row!.Literal = "2";
            vm.WireEditor.Commit();
            await Assert.That(RulesetEdits.HighlightField(Text(rules), CanvasGallery.BlankHighlightName, "when")).IsEqualTo("kills > 2");
        }
    }

    private const string PlantsOnly = "stats:\n  plants:\n    count: bomb_planted\n    per: round\n  a:\n    count:\n    per: round\n";

    private static RulesetDocumentNodeKey PlayerDeath => new("~event", "player_death");

    [Test]
    public async Task TheBlankEvent_AsksWhichView_WhereItWasDropped()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open(PlantsOnly);
        using (rules)
        {
            Point? asked = null;
            vm.EventPickRequested += at => asked = at;
            string before = Text(rules);
            vm.PlaceItem(Item(vm, "Blank event"), new Point(-300, 40));
            GalleryItem kill = vm.BlankEventOptions.Single(o => o.Name == "kill");
            using (Assert.Multiple())
            {
                await Assert.That(asked).IsEqualTo(new Point(-300, 40));
                await Assert.That(kill.Kind).IsEqualTo(GalleryItemKind.Event);
                await Assert.That(kill.Hint).IsEqualTo("fires on player_death");
                await Assert.That(Text(rules)).IsEqualTo(before).Because("nothing is placed until a view is picked");
            }
        }
    }

    [Test]
    public async Task APlacedEvent_FloatsInTheSidecar_AndSurvivesADragAndAReload()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open(PlantsOnly);
        using (rules)
        {
            string before = Text(rules);
            vm.PlaceItem(vm.BlankEventOptions.Single(o => o.Name == "kill"), new Point(-300, 40));
            CanvasNodeViewModel floating = vm.Nodes.Single(n => n.Key == PlayerDeath);
            string sidecar = RulesetLayoutStore.SidecarPathFor(rules.UserPath("lab"));
            using (Assert.Multiple())
            {
                await Assert.That(floating.IsFloating).IsTrue();
                await Assert.That(floating.FloatingView).IsEqualTo("kill");
                await Assert.That(floating.Location).IsEqualTo(new Point(-300, 40));
                await Assert.That(floating.Outputs.Single().Kind).IsEqualTo(PortKind.Fire).Because("a wire can start from it before it has any");
                await Assert.That(Text(rules)).IsEqualTo(before).Because("a floating event is layout, not YAML");
                await Assert.That(RulesetLayoutStore.Load(rules.UserPath("lab")).Scaffolds).IsEquivalentTo([new CanvasScaffold("kill", new Point(-300, 40))]);
            }

            floating.Location = new Point(-500, 90);
            vm.SaveLayoutCommand.Execute(null);
            CanvasTabViewModel reopened = rules.NewViewModel();
            reopened.PreferredRulesetId = "lab";
            reopened.OnActivated(null!);
            CanvasNodeViewModel again = reopened.Nodes.Single(n => n.Key == PlayerDeath);
            using (Assert.Multiple())
            {
                await Assert.That(again.FloatingView).IsEqualTo("kill");
                await Assert.That(again.Location).IsEqualTo(new Point(-500, 90));
                await Assert.That(File.ReadAllText(sidecar)).Contains("\"scaffolds\"");
            }

            vm.PlaceItem(vm.BlankEventOptions.Single(o => o.Name == "death"), new Point(0, 0));
            await Assert.That(vm.Nodes.Count(n => n.Key == PlayerDeath)).IsEqualTo(1).Because("one event is one node");
        }
    }

    [Test]
    public async Task WiringAFloatingEvent_WritesItsView_AndTheChainsEventTakesItsPlace()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open(PlantsOnly);
        using (rules)
        {
            vm.PlaceItem(vm.BlankEventOptions.Single(o => o.Name == "kill"), new Point(-300, 40));
            CanvasNodeViewModel floating = vm.Nodes.Single(n => n.Key == PlayerDeath);
            CanvasWalkthrough.WireInto(vm, floating, Node(vm, "a").Port(PortKind.Trigger)!);
            CanvasNodeViewModel real = vm.Nodes.Single(n => n.Key == PlayerDeath);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(Text(rules), "a", "count")).IsEqualTo("kill").Because("player_death fires three views; the floating node names one");
                await Assert.That(real.IsFloating).IsFalse();
                await Assert.That(real.Location).IsEqualTo(new Point(-300, 40)).Because("the chain's node keeps the spot");
                await Assert.That(vm.Connections.Any(c => c.Source.Owner == real && c.Target.Owner?.Id == "a")).IsTrue();
            }
        }
    }

    [Test]
    public async Task DeletingAFloatingEvent_TakesItOutOfTheSidecar_AndUndoBringsItBack()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open(PlantsOnly);
        using (rules)
        {
            vm.PlaceItem(vm.BlankEventOptions.Single(o => o.Name == "kill"), new Point(-300, 40));
            vm.SelectedNodes.Clear();
            vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Key == PlayerDeath));
            vm.DeleteSelectedCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(vm.Nodes.Any(n => n.Key == PlayerDeath)).IsFalse();
                await Assert.That(RulesetLayoutStore.Load(rules.UserPath("lab")).Scaffolds).IsEmpty();
            }

            vm.UndoCommand.Execute(null);
            await Assert.That(vm.Nodes.Single(n => n.Key == PlayerDeath).FloatingView).IsEqualTo("kill");
        }
    }

    [Test]
    public async Task TheSidecar_HoldsAScaffoldAlone_AndReadsItBack()
    {
        RulesetNodeLayout layout = RulesetNodeLayout.Empty.WithScaffold(new CanvasScaffold("kill", new Point(1.5, -2)));
        string json = RulesetLayoutStore.Serialize(layout);
        using RulesTree rules = RulesTree.New();
        string path = rules.User("lab", Header);
        using (Assert.Multiple())
        {
            await Assert.That(layout.IsEmpty).IsFalse();
            await Assert.That(RulesetLayoutStore.Parse(json).Scaffolds).IsEquivalentTo([new CanvasScaffold("kill", new Point(1.5, -2))]);
            await Assert.That(RulesetLayoutStore.Save(path, layout)).IsTrue();
            await Assert.That(File.Exists(RulesetLayoutStore.SidecarPathFor(path))).IsTrue().Because("a layout holding only a scaffold is not empty");
            await Assert.That(layout.WithoutScaffold("kill").IsEmpty).IsTrue();
            await Assert.That(layout.With(new RulesetDocumentNodeKey("lab", "x"), new Point(0, 0)).Scaffolds.Count).IsEqualTo(1)
                .Because("every copy carries the scaffolds");
        }
    }

    private const string KillsAndDamage = "define:\n  d:\n    on: kill\nstats:\n  kills:\n    count: kill\n    per: round\n  deaths:\n    count: death\n    per: round\n"
                                          + "  dmg:\n    sum: event.DmgHealth\n    on: damage_dealt\n    per: round\n  via_d:\n    count: d\n    per: round\n";

    private static CanvasConnectionViewModel TriggerWire(CanvasTabViewModel vm, string source, string target) =>
        vm.Connections.Single(c => c.Source.Owner?.Id == source && c.Target.Owner?.Id == target && CanvasEditSemantics.IsTriggerWire(c));

    [Test]
    public async Task RemovingATriggerWire_DetachesTheTrigger_AndTheEntryLoadsUnfinished()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open(KillsAndDamage);
        using (rules)
        {
            // Alt+click on the wire.
            vm.DisconnectCommand.Execute(TriggerWire(vm, "player_death", "kills"));
            // Delete with the wire selected.
            vm.SelectedNodes.Clear();
            vm.SelectedWires.Add(TriggerWire(vm, "player_hurt", "dmg"));
            vm.DeleteSelectionCommand.Execute(null);
            vm.DisconnectCommand.Execute(TriggerWire(vm, "player_death", "define.d"));
            string text = Text(rules);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "kills", "count")).IsEqualTo("");
                await Assert.That(RulesetEdits.Field(text, "dmg", "on")).IsEqualTo("");
                await Assert.That(RulesetEdits.Field(text, "dmg", "sum")).IsEqualTo("event.DmgHealth").Because("only the trigger goes");
                await Assert.That(RulesetEdits.DefineField(text, "d", "on")).IsEqualTo("").Because("an empty on: keeps it a trigger define");
                await Assert.That(Node(vm, "kills").Port(PortKind.Trigger)).IsNotNull();
                await Assert.That(Node(vm, "dmg").Port(PortKind.Trigger)).IsNotNull();
                await Assert.That(vm.Nodes.Single(n => n.IsDefine).Port(PortKind.Trigger)).IsNotNull();
                await Assert.That(vm.DiagnosticEntries.Select(d => d.Message)).Contains("kills has no trigger: give count: a view, such as kill");
                await Assert.That(vm.ErrorCount).IsEqualTo(0);
            }
        }
    }

    [Test]
    public async Task DeletingAnEventSomethingFiresOn_IsRefused_WithADetachAction()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open(KillsAndDamage);
        using (rules)
        {
            string before = Text(rules);
            vm.SelectedNodes.Clear();
            vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Ruleset == "~event" && n.Id == "player_death"));
            vm.DeleteSelectionCommand.Execute(null);
            CanvasToast toast = vm.Toast!;
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).IsEqualTo(before);
                await Assert.That(toast.ActionLabel).IsEqualTo("Detach 3 entries").Because("kills, deaths and the define fire on it");
                await Assert.That(toast.Message).Contains("player_death drives");
            }

            toast.Action!();
            string text = Text(rules);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "kills", "count")).IsEqualTo("");
                await Assert.That(RulesetEdits.Field(text, "deaths", "count")).IsEqualTo("");
                await Assert.That(RulesetEdits.DefineField(text, "d", "on")).IsEqualTo("");
                await Assert.That(RulesetEdits.Field(text, "dmg", "on")).IsEqualTo("damage_dealt").Because("another event's stat is untouched");
                await Assert.That(vm.Nodes.Any(n => n.Ruleset == "~event" && n.Id == "player_death")).IsFalse();
            }

            vm.UndoCommand.Execute(null);
            await Assert.That(Text(rules)).IsEqualTo(before).Because("the detach is one write");
        }
    }

    [Test]
    public async Task TheDetachLabel_CountsStats_OrEntries()
    {
        CanvasNodeViewModel stat = new() { Id = "a", Ruleset = "lab", Kind = "count", IsAuthorable = true };
        CanvasNodeViewModel define = new() { Id = "define.d", Ruleset = "lab", Kind = "define", IsAuthorable = true };
        using (Assert.Multiple())
        {
            await Assert.That(CanvasTabViewModel.DetachLabel([stat])).IsEqualTo("Detach 1 stat");
            await Assert.That(CanvasTabViewModel.DetachLabel([stat, stat])).IsEqualTo("Detach 2 stats");
            await Assert.That(CanvasTabViewModel.DetachLabel([stat, define])).IsEqualTo("Detach 2 entries");
        }
    }

    [Test]
    public async Task AWiredFloatingEvent_LeavesTheSidecar()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open(PlantsOnly);
        using (rules)
        {
            vm.PlaceItem(vm.BlankEventOptions.Single(o => o.Name == "kill"), new Point(-300, 40));
            CanvasWalkthrough.WireInto(vm, vm.Nodes.Single(n => n.Key == PlayerDeath), Node(vm, "a").Port(PortKind.Trigger)!);
            RulesetNodeLayout saved = RulesetLayoutStore.Load(rules.UserPath("lab"));
            using (Assert.Multiple())
            {
                await Assert.That(saved.Scaffolds).IsEmpty().Because("the chain draws the event now");
                await Assert.That(saved.PositionFor(PlayerDeath)).IsEqualTo(new Point(-300, 40));
            }
        }
    }
}
