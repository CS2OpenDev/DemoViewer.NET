#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Editing a condition where it is looked at: on the wire. The semantics map each wire kind to the key
///     and the operand row it edits, or to the reason it edits nothing; the editor rewrites that one row
///     and nothing else of the file; a comparison gate's text is written and removed through the wire; a
///     gallery item dropped on a wire acts on the wire's target.
/// </summary>
public class CanvasWireEditTests
{
    private const string Lab = """
        ruleset: wires
        title: Wires
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
          gated_deaths:
            count: death
            while: round.number > 0
            per: round
          seen:
            flag:
              when: "deaths > 0"
            per: round
          nested:
            flag:
              when: "(deaths > 0 or late_kills > 0) and deaths < 5"
            per: round

        highlights:
          ace:
            when: late_kills >= 5
            per: round
          both:
            when: "deaths > 0 and late_kills>0"
            per: round
        """;

    private static (RulesTree Rules, CanvasTabViewModel Vm) OpenLab()
    {
        RulesTree rules = RulesTree.New();
        rules.User("wires", Lab);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "wires";
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static (RulesTree Rules, CanvasTabViewModel Vm) OpenPlayerRound()
    {
        RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasConnectionViewModel Wire(CanvasTabViewModel vm, string source, string target, Func<CanvasConnectionViewModel, bool>? where = null) =>
        vm.Connections.Single(c => (c.Source.Owner?.Title == source || c.Source.Owner?.Id == source) && c.Target.Owner?.Id == target && (where is null || where(c)));

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id && !n.IsScaffold);

    private static GalleryItem Item(CanvasTabViewModel vm, string group, string name) =>
        vm.Gallery.Single(g => g.Title == group).Items.Single(i => i.Name == name);

    [Test]
    public async Task EachWireKind_MapsToItsEditTarget_OrSaysWhyItHasNone()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenPlayerRound();
        using (rules)
        {
            CanvasConnectionViewModel first = Wire(vm, "round_kills", "round_kast");
            CanvasConnectionViewModel second = Wire(vm, "round_kast_assists", "round_kast");
            CanvasConnectionViewModel survived = Wire(vm, "Survived", "round_kast");
            CanvasConnectionViewModel gate = Wire(vm, "MatchLive", "round_deaths", c => !c.IsRead);
            CanvasConnectionViewModel read = vm.Connections.First(c => c.IsRead && c.Target.Owner?.Id == "round_damage");
            CanvasConnectionViewModel trigger = vm.Connections.Single(c => c.Source.Owner?.Ruleset == "~event" && c.Target.Owner?.Id == "round_kills");
            using (Assert.Multiple())
            {
                await Assert.That(first.ConditionEdit).IsEqualTo(new WireEdit(Node(vm, "round_kast"), "when", 0, null));
                await Assert.That(first.CanEditCondition).IsTrue();
                await Assert.That(first.ConditionEditTip).IsEqualTo(CanvasConnectionViewModel.EditHint);
                await Assert.That(second.ConditionEdit!.Row).IsEqualTo(1);
                await Assert.That(survived.ConditionEdit!.Row).IsEqualTo(2).Because("a context read is a bare row, found by its path");
                await Assert.That(gate.ConditionEdit!.IsEnabled).IsFalse();
                await Assert.That(gate.ConditionEdit!.Reason).Contains("this gate is a flag");
                await Assert.That(gate.CanEditCondition).IsFalse();
                await Assert.That(gate.ConditionEditTip).IsEqualTo(gate.ConditionEdit!.Reason);
                await Assert.That(read.ConditionEdit!.Reason).IsEqualTo("a read has nothing to edit");
                await Assert.That(trigger.ConditionEdit!.Reason).IsEqualTo("a trigger wire has no condition; add a where row on round_kills");
                await Assert.That(vm.Connections.All(c => c.ConditionEdit is not null)).IsTrue().Because("every drawn wire is decided once");
            }
        }

        using RulesTree shipped = RulesTree.WithShipped("player_round");
        CanvasTabViewModel readOnly = shipped.NewViewModel();
        readOnly.OnActivated(null!);
        CanvasConnectionViewModel shippedWire = Wire(readOnly, "round_kills", "round_kast");
        using (Assert.Multiple())
        {
            await Assert.That(shippedWire.ConditionEdit!.IsEnabled).IsTrue().Because("the semantics do not know the file is shipped");
            await Assert.That(shippedWire.CanEditCondition).IsFalse();
            await Assert.That(shippedWire.ConditionEditTip).Contains("read only");
        }
    }

    [Test]
    public async Task ADefinesFiresWire_AComparisonGate_AHighlightOperand_AndANestedWhen_AreMapped()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenLab();
        using (rules)
        {
            CanvasConnectionViewModel fires = Wire(vm, "post_plant_kill", "late_kills");
            CanvasConnectionViewModel gate = Wire(vm, "RoundNumber", "gated_deaths");
            CanvasConnectionViewModel ace = Wire(vm, "late_kills", "ace");
            CanvasConnectionViewModel both = Wire(vm, "late_kills", "both");
            CanvasConnectionViewModel nested = Wire(vm, "deaths", "nested", c => c.Condition!.Contains("> 0", StringComparison.Ordinal));
            using (Assert.Multiple())
            {
                await Assert.That(fires.ConditionEdit!.Reason).IsEqualTo("post_plant_kill is what late_kills fires on; pick another trigger on late_kills");
                await Assert.That(gate.IsGate).IsTrue();
                await Assert.That(gate.ConditionEdit).IsEqualTo(new WireEdit(Node(vm, "gated_deaths"), "while", null, null));
                await Assert.That(ace.ConditionEdit).IsEqualTo(new WireEdit(Node(vm, "ace"), "when", 0, null));
                await Assert.That(both.ConditionEdit!.Row).IsEqualTo(1).Because("late_kills>0 is the second row, matched without its spacing");
                await Assert.That(nested.ConditionEdit).IsEqualTo(new WireEdit(Node(vm, "nested"), "when", null, null))
                    .Because("a grouped when: has no rows to point at and is edited whole");
            }
        }
    }

    [Test]
    public async Task CommittingARowThroughTheWire_RewritesThatOperandOnly_AndUndoesAsOneStep()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenPlayerRound();
        using (rules)
        {
            string before = File.ReadAllText(rules.UserPath("player_round"));
            CanvasConnectionViewModel wire = Wire(vm, "round_kast_assists", "round_kast");
            int opened = 0;
            vm.WireEditorOpened += _ => opened++;

            vm.BeginEditWire(wire);
            CanvasWireEditorViewModel editor = vm.WireEditor!;
            using (Assert.Multiple())
            {
                await Assert.That(opened).IsEqualTo(1);
                await Assert.That(editor.IsRow).IsTrue();
                await Assert.That(editor.Title).IsEqualTo("when: on round_kast");
                await Assert.That(editor.Row!.Path).IsEqualTo("round_kast_assists");
                await Assert.That(editor.Row.Comparator).IsEqualTo(">");
                await Assert.That(editor.Row.Literal).IsEqualTo("0");
                await Assert.That(editor.HasNote).IsFalse();
            }

            editor.Row!.Literal = "2";
            editor.Commit();
            string after = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(after).IsEqualTo(before.Replace("round_kast_assists > 0 or", "round_kast_assists > 2 or", StringComparison.Ordinal))
                    .Because("one operand changed and every other byte stayed");
                await Assert.That(vm.WireEditor).IsNull().Because("the write closes the editor");
                await Assert.That(vm.EditStatus).IsEmpty();
                await Assert.That(Wire(vm, "round_kast_assists", "round_kast").Text).EndsWith("> 2").Because("the label redraws from the file");
                await Assert.That(Wire(vm, "round_kast_assists", "round_kast").ConditionEdit!.Row).IsEqualTo(1);
                await Assert.That(vm.CanUndo).IsTrue();
            }

            vm.UndoCommand.Execute(null);
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before).Because("one write, one undo");
        }
    }

    [Test]
    public async Task CancellingTheEditor_WritesNothing_AndAnUnchangedCommitWritesNothing()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenPlayerRound();
        using (rules)
        {
            string before = File.ReadAllText(rules.UserPath("player_round"));
            vm.BeginEditWire(Wire(vm, "round_kills", "round_kast"));
            vm.WireEditor!.Row!.Literal = "9";
            vm.CancelWireEditorCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(vm.WireEditor).IsNull();
                await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
                await Assert.That(Node(vm, "round_kast").Lines.Single(l => l.Key == "when").Rows[0].Literal).IsEqualTo("0")
                    .Because("the editor's rows are its own; the face's never moved");
            }

            vm.BeginEditWire(Wire(vm, "round_kills", "round_kast"));
            vm.CommitWireEditorCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(vm.WireEditor).IsNull();
                await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
                await Assert.That(vm.CanUndo).IsFalse();
            }
        }
    }

    [Test]
    public async Task AWireWithNoCondition_OpensWithTheReason_AndItsCommitWritesNothing()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenPlayerRound();
        using (rules)
        {
            string before = File.ReadAllText(rules.UserPath("player_round"));
            vm.BeginEditWire(Wire(vm, "MatchLive", "round_deaths", c => !c.IsRead));
            CanvasWireEditorViewModel editor = vm.WireEditor!;
            using (Assert.Multiple())
            {
                await Assert.That(editor.IsDisabled).IsTrue();
                await Assert.That(editor.Reason).Contains("this gate is a flag");
                await Assert.That(editor.Title).IsEqualTo("round_deaths");
            }

            editor.Commit();
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
            await Assert.That(vm.WireEditor).IsNull();
        }
    }

    [Test]
    public async Task AComparisonGate_IsEditedAsText_WrittenToWhile_AndRemovedWhenEmptied()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenLab();
        using (rules)
        {
            vm.BeginEditWire(Wire(vm, "RoundNumber", "gated_deaths"));
            CanvasWireEditorViewModel editor = vm.WireEditor!;
            using (Assert.Multiple())
            {
                await Assert.That(editor.IsText).IsTrue();
                await Assert.That(editor.Text).IsEqualTo("round.number > 0");
                await Assert.That(editor.Title).IsEqualTo("while: on gated_deaths");
                await Assert.That(editor.Note).Contains("empty removes it");
            }

            editor.Text = "round.number > 3";
            editor.Commit();
            string text = File.ReadAllText(rules.UserPath("wires"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "gated_deaths", "while")).IsEqualTo("round.number > 3");
                await Assert.That(vm.Diagnostics).IsEmpty();
                await Assert.That(Wire(vm, "RoundNumber", "gated_deaths").Text).IsEqualTo("while round.number > 3");
            }

            vm.BeginEditWire(Wire(vm, "RoundNumber", "gated_deaths"));
            vm.WireEditor!.Text = "";
            vm.WireEditor.Commit();
            text = File.ReadAllText(rules.UserPath("wires"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "gated_deaths", "while")).IsNull();
                await Assert.That(vm.Connections.Any(c => c.Target.Owner?.Id == "gated_deaths" && c.IsGate)).IsFalse();
            }
        }
    }

    [Test]
    public async Task ANestedWhen_IsEditedWholeAsText_AndAHighlightRow_WritesItsWhen()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenLab();
        using (rules)
        {
            vm.BeginEditWire(Wire(vm, "deaths", "nested", c => c.Condition!.Contains("> 0", StringComparison.Ordinal)));
            CanvasWireEditorViewModel editor = vm.WireEditor!;
            using (Assert.Multiple())
            {
                await Assert.That(editor.IsText).IsTrue();
                await Assert.That(editor.Text).IsEqualTo("(deaths > 0 or late_kills > 0) and deaths < 5");
                await Assert.That(editor.Note).Contains("not a flat list");
            }

            editor.Text = "(deaths > 1 or late_kills > 0) and deaths < 5";
            editor.Commit();
            await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("wires")), "nested", "flag.when")).IsEqualTo("(deaths > 1 or late_kills > 0) and deaths < 5");

            vm.BeginEditWire(Wire(vm, "late_kills", "both"));
            vm.WireEditor!.Row!.Literal = "2";
            vm.WireEditor.Commit();
            await Assert.That(RulesetEdits.HighlightField(File.ReadAllText(rules.UserPath("wires")), "both", "when")).IsEqualTo("deaths > 0 and late_kills>2")
                .Because("the row keeps its own spacing and the other row its bytes");
        }
    }

    [Test]
    public async Task ASelectedWire_ShowsItsEditorInTheInspector_AndTheWriteGoesThroughIt()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenPlayerRound();
        using (rules)
        {
            string before = File.ReadAllText(rules.UserPath("player_round"));
            await Assert.That(vm.Inspector.ShowsWire).IsFalse();
            await Assert.That(vm.Inspector.Heading).IsEqualTo("RULESET player_round");

            vm.SelectedWires.Add(Wire(vm, "round_kills", "round_kast"));
            using (Assert.Multiple())
            {
                await Assert.That(vm.Inspector.ShowsWire).IsTrue();
                await Assert.That(vm.Inspector.ShowsDocument).IsFalse();
                await Assert.That(vm.Inspector.Heading).IsEqualTo("WIRE round_kills to round_kast");
                await Assert.That(vm.Inspector.WireText).StartsWith("or ");
                await Assert.That(vm.Inspector.Wire!.IsRow).IsTrue();
                await Assert.That(vm.Inspector.Wire.Row!.Path).IsEqualTo("round_kills");
            }

            // Escape puts the file's row back without a write.
            vm.Inspector.Wire.Row.Literal = "7";
            vm.Inspector.Wire.Cancel();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Inspector.Wire!.Row!.Literal).IsEqualTo("0");
                await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
            }

            vm.Inspector.Wire.Row.Literal = "1";
            vm.Inspector.Wire.Commit();
            using (Assert.Multiple())
            {
                await Assert.That(File.ReadAllText(rules.UserPath("player_round")))
                    .IsEqualTo(before.Replace("\"round_kills > 0 or", "\"round_kills > 1 or", StringComparison.Ordinal));
                await Assert.That(vm.SelectedWires).IsEmpty().Because("the redraw replaced the wires, so the selection is gone");
                await Assert.That(vm.Inspector.ShowsWire).IsFalse();
                await Assert.That(vm.Inspector.ShowsDocument).IsTrue();
            }

            // A node selected alongside a wire wins the Inspector.
            vm.SelectedWires.Add(Wire(vm, "round_kills", "round_kast"));
            vm.SelectedNodes.Add(Node(vm, "round_deaths"));
            using (Assert.Multiple())
            {
                await Assert.That(vm.Inspector.ShowsWire).IsFalse();
                await Assert.That(vm.Inspector.Heading).IsEqualTo("COUNT round_deaths");
            }
        }
    }

    [Test]
    public async Task AWriteOnTheFace_ClosesAnOpenWireEditor()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenPlayerRound();
        using (rules)
        {
            vm.BeginEditWire(Wire(vm, "round_kills", "round_kast"));
            Node(vm, "round_deaths").GateText = "";
            Node(vm, "round_deaths").CommitGate();
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("player_round")), "round_deaths", "while")).IsNull();
                await Assert.That(vm.WireEditor).IsNull().Because("the redraw replaced the wire the editor sat on");
            }
        }
    }

    [Test]
    public async Task DroppingOnAWire_InterposesADefine_GatesTheTarget_RetargetsIt_OrPlacesTheItem()
    {
        (RulesTree rules, CanvasTabViewModel vm) = OpenLab();
        using (rules)
        {
            CanvasConnectionViewModel trigger = vm.Connections.Single(c => c.Source.Owner?.Ruleset == "~event" && c.Target.Owner?.Id == "deaths");
            await Assert.That(CanvasEditSemantics.IsTriggerWire(trigger)).IsTrue();

            vm.DropOnWire(Item(vm, "Stats", "define"), trigger, new Point(0, 0));
            string text = File.ReadAllText(rules.UserPath("wires"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.DefineField(text, "new_define", "on")).IsEqualTo("death").Because("the define takes the wire's trigger");
                await Assert.That(RulesetEdits.Field(text, "deaths", "count")).IsEqualTo("new_define");
                await Assert.That(vm.Diagnostics).IsEmpty();
            }

            CanvasConnectionViewModel logic = Wire(vm, "deaths", "seen");
            vm.DropOnWire(Item(vm, "Gates", "match.live"), logic, new Point(0, 0));
            await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("wires")), "seen", "while")).IsEqualTo("match.live")
                .Because("a gate on any wire gates the wire's target");

            CanvasConnectionViewModel fires = Wire(vm, "new_define", "deaths");
            vm.DropOnWire(Item(vm, "Views", "kill"), fires, new Point(0, 0));
            await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("wires")), "deaths", "count")).IsEqualTo("kill")
                .Because("a view retargets the trigger wire's target");

            string before = File.ReadAllText(rules.UserPath("wires"));
            vm.DropOnWire(Item(vm, "Stats", "define"), Wire(vm, "deaths", "seen"), new Point(0, 0));
            await Assert.That(File.ReadAllText(rules.UserPath("wires"))).IsEqualTo(before);
            await Assert.That(vm.EditStatus).Contains("drop it on the wire that fires seen");

            vm.DropOnWire(Item(vm, "Views", "kill"), Wire(vm, "deaths", "seen"), new Point(0, 0));
            await Assert.That(File.ReadAllText(rules.UserPath("wires"))).IsEqualTo(before);
            await Assert.That(vm.EditStatus).Contains("drop kill on the wire that fires seen");

            vm.DropOnWire(Item(vm, "Prefabs", "enemy_kills"), Wire(vm, "deaths", "seen"), new Point(300, 700));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.StatIds(File.ReadAllText(rules.UserPath("wires")))).Contains("enemy_kills").Because("an item with no wire meaning is placed as on bare canvas");
                await Assert.That(Node(vm, "enemy_kills").Location).IsEqualTo(new Point(300, 700));
            }
        }
    }
}
