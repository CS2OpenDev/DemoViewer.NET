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
                    .IsEquivalentTo(["Blank stat", "Blank define", "Blank highlight", "Blank flag"]);
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
}
