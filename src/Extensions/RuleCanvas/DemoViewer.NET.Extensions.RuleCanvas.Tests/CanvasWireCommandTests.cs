#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The commands the keys and the menus call: Delete falls back to the selected wires, a cut folds its
///     refusals into one line, Duplicate copies an entry as written, Select all and Escape act on the
///     selection, and the canvas menu's Add items land where the click was.
/// </summary>
public class CanvasWireCommandTests
{
    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id);

    private static CanvasConnectionViewModel GateWire(CanvasTabViewModel vm, string target) =>
        vm.Connections.Single(c => c.Source.Owner?.Id == "MatchLive" && c.Target.Owner?.Id == target && !c.IsRead);

    private static CanvasConnectionViewModel ReadWire(CanvasTabViewModel vm, string source, string target) =>
        vm.Connections.Single(c => c.Source.Owner?.Id == source && c.Target.Owner?.Id == target);

    [Test]
    public async Task DeleteWithOnlyAWireSelected_RemovesIt_AndClearsTheWireSelection()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.SelectedWires.Add(GateWire(vm, "round_deaths"));
            vm.DeleteSelectionCommand.Execute(null);
            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "round_deaths", "while")).IsNull();
                await Assert.That(vm.SelectedWires).IsEmpty();
                await Assert.That(vm.EditStatus).IsEmpty();
                await Assert.That(RulesetEdits.StatIds(text)).Contains("round_deaths").Because("the wire went, not the stat");
            }
        }
    }

    [Test]
    public async Task DeleteWithANodeAndAWireSelected_DeletesTheNode()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.SelectedNodes.Add(Node(vm, "round_side"));
            vm.SelectedWires.Add(GateWire(vm, "round_deaths"));
            vm.DeleteSelectionCommand.Execute(null);
            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.StatIds(text)).DoesNotContain("round_side");
                await Assert.That(RulesetEdits.Field(text, "round_deaths", "while")).IsEqualTo("match.live").Because("the node won");
            }
        }
    }

    [Test]
    public async Task ACutAcrossAGateAndADefinitionWire_RemovesTheGate_AndSaysWhyTheOtherStayed()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasConnectionViewModel gate = GateWire(vm, "round_deaths");
            CanvasConnectionViewModel definition = ReadWire(vm, "round_kills", "round_kast");
            string before = File.ReadAllText(rules.UserPath("player_round"));

            vm.CuttingStartedCommand.Execute(null);
            vm.DisconnectCommand.Execute(gate);
            vm.DisconnectCommand.Execute(definition);
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before).Because("nothing is written while the line is drawn");

            vm.CuttingCompletedCommand.Execute(null);
            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "round_deaths", "while")).IsNull();
                await Assert.That(RulesetEdits.Field(text, "round_kast", "flag.when")).Contains("round_kills > 0");
                await Assert.That(vm.EditStatus).IsEqualTo("1 wire removed; 1 wire kept: That wire is part of the stat's own definition; change the stat instead.");
            }
        }
    }

    [Test]
    public async Task ACutThatRemovesEverythingItCrossed_LeavesNoStatus()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.CuttingStartedCommand.Execute(null);
            vm.DisconnectCommand.Execute(GateWire(vm, "round_deaths"));
            vm.CuttingCompletedCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("player_round")), "round_deaths", "while")).IsNull();
                await Assert.That(vm.EditStatus).IsEmpty();
            }
        }
    }

    [Test]
    public async Task DisconnectOutsideACut_StillWritesAtOnce()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.CuttingStartedCommand.Execute(null);
            vm.CuttingCompletedCommand.Execute(null);
            vm.DisconnectCommand.Execute(GateWire(vm, "round_deaths"));
            await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("player_round")), "round_deaths", "while")).IsNull();
        }
    }

    [Test]
    public async Task Duplicate_CopiesTheEntryAsWritten_UnderAFreshId_AndSelectsTheCopy()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = File.ReadAllText(rules.UserPath("player_round"));
            vm.SelectedNodes.Add(Node(vm, "round_assists"));
            vm.DuplicateCommand.Execute(null);
            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.StatIds(text)).Contains("round_assists_copy");
                await Assert.That(RulesetEntryText.BodyOf(text, "round_assists_copy", highlight: false))
                    .IsEqualTo(RulesetEntryText.BodyOf(before, "round_assists", highlight: false));
                await Assert.That(RulesetEdits.Field(text, "round_assists_copy", "where")).IsEqualTo("event.AssistedFlash == false");
                await Assert.That(vm.Nodes.Any(n => n.Id == "round_assists_copy")).IsTrue();
                await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Select(n => n.Id)).IsEquivalentTo(["round_assists_copy"]);
                await Assert.That(vm.CanUndo).IsTrue();
            }

            vm.DuplicateCommand.Execute(null);
            await Assert.That(RulesetEdits.StatIds(File.ReadAllText(rules.UserPath("player_round")))).Contains("round_assists_copy_copy");
        }
    }

    [Test]
    public async Task Duplicate_OfAnEngineNode_OrOnAShippedRuleset_WritesNothing()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = File.ReadAllText(rules.UserPath("player_round"));
            vm.SelectedNodes.Add(Node(vm, "MatchLive"));
            vm.DuplicateCommand.Execute(null);
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
            await Assert.That(CanvasEditSemantics.Duplicate(Node(vm, "MatchLive"), "x").IsRefused).IsTrue();
        }

        using RulesTree shipped = RulesTree.WithShipped("player_round");
        CanvasTabViewModel readOnly = shipped.NewViewModel();
        readOnly.OnActivated(null!);
        string shippedBefore = File.ReadAllText(shipped.ShippedPath("player_round"));
        readOnly.DuplicateNode(readOnly.Nodes.Single(n => n.Id == "round_kills"));
        readOnly.SelectedWires.Add(readOnly.Connections.Single(c => c.Source.Owner?.Id == "MatchLive" && c.Target.Owner?.Id == "round_deaths" && !c.IsRead));
        readOnly.DeleteWiresCommand.Execute(null);
        await Assert.That(File.ReadAllText(shipped.ShippedPath("player_round"))).IsEqualTo(shippedBefore);
    }

    [Test]
    public async Task DuplicateOfAHighlight_LandsUnderHighlights()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            GalleryItem highlight = vm.Gallery.Single(g => g.Title == "Highlights").Items.Single(i => i.Name == "multi_kill");
            vm.PlaceItem(highlight, new Point(400, 0));
            string placed = File.ReadAllText(rules.UserPath("player_round"));

            vm.DuplicateNode(Node(vm, "multi_kill"));
            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEntryText.BodyOf(text, "multi_kill_copy", highlight: true))
                    .IsEqualTo(RulesetEntryText.BodyOf(placed, "multi_kill", highlight: true));
                await Assert.That(RulesetEdits.HighlightField(text, "multi_kill_copy", "when")).IsEqualTo(RulesetEdits.HighlightField(placed, "multi_kill", "when"));
                await Assert.That(RulesetEdits.StatIds(text)).DoesNotContain("multi_kill_copy").Because("a highlight's copy is not a stat");
                await Assert.That(Node(vm, "multi_kill_copy").IsHighlight).IsTrue();
            }
        }
    }

    [Test]
    public async Task EntryText_IsTheBodyAtZeroIndentation_CommentsIncluded_AndAMissingEntryThrows()
    {
        string text = File.ReadAllText(Path.Combine(RulesTree.RepoRulesDirectory, "player_round.rules.yaml"));
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEntryText.BodyOf(text, "round_kills", highlight: false)).IsEqualTo("count: kill\nmatch: { enemy: true }\nper: round");
            await Assert.That(RulesetEntryText.BodyOf(text, "round_kast", highlight: false))
                .IsEqualTo("flag:\n  when: \"round_kills > 0 or round_kast_assists > 0 or player.survived or player.traded\"\nper: round");
            await Assert.That(() => RulesetEntryText.BodyOf(text, "nope", highlight: false)).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEntryText.BodyOf(text, "round_kills", highlight: true)).Throws<InvalidOperationException>();
        }

        string commented = "stats:\n  a:\n    count: kill\n    # kept with the entry\n    per: round\n\n  b:\n    count: death\n";
        await Assert.That(RulesetEntryText.BodyOf(commented, "a", highlight: false)).IsEqualTo("count: kill\n# kept with the entry\nper: round");
    }

    [Test]
    public async Task SelectAll_SelectsEveryNode_AndEscape_ClearsFocusAndSelection()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.SelectAllCommand.Execute(null);
            await Assert.That(vm.SelectedNodes.Count).IsEqualTo(vm.Nodes.Count);

            vm.FocusNode(Node(vm, "round_kast"));
            vm.SelectedNodes.Clear();
            vm.SelectedNodes.Add(Node(vm, "round_kast"));
            vm.SelectedWires.Add(vm.Connections.First());
            await Assert.That(vm.HasFocus).IsTrue();

            vm.ClearFocusAndSelectionCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(vm.HasFocus).IsFalse();
                await Assert.That(vm.SelectedNodes).IsEmpty();
                await Assert.That(vm.SelectedWires).IsEmpty();
            }
        }
    }

    [Test]
    public async Task TheCanvasMenu_AddsAStatOnAView_AndAHighlight_WhereTheClickWas()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.AddStatAt("death", new Point(640, 480));
            vm.AddHighlightAt(new Point(100, 900));
            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "new_death", "count")).IsEqualTo("death");
                await Assert.That(Node(vm, "new_death").Location).IsEqualTo(new Point(640, 480));
                await Assert.That(RulesetEdits.HighlightField(text, "highlight", "when")).IsNotNull();
                await Assert.That(Node(vm, "highlight").Location).IsEqualTo(new Point(100, 900));
            }

            string before = text;
            vm.AddStatAt("no_such_view", new Point(0, 0));
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
        }
    }
}
