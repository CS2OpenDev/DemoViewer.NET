#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The Inspector through the tab: it follows the selection and the redraw, every field writes through the
///     session, a rename rewrites references and keeps the node selected, the document mode edits the header
///     and the surfacing block, Delete cascades or refuses, and New ruleset writes a file and opens it.
/// </summary>
public class InspectorViewModelTests
{
    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static string Text(RulesTree rules) => File.ReadAllText(rules.UserPath("player_round"));

    private static CanvasNodeViewModel Select(CanvasTabViewModel vm, string id)
    {
        CanvasNodeViewModel node = vm.Nodes.Single(n => n.Id == id);
        vm.SelectedNodes.Clear();
        vm.SelectedNodes.Add(node);
        return node;
    }

    [Test]
    public async Task TheInspector_FollowsTheSelection_AndShowsTheEntrysKeys()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            InspectorViewModel inspector = vm.Inspector;
            using (Assert.Multiple())
            {
                await Assert.That(inspector.HasDocument).IsTrue();
                await Assert.That(inspector.ShowsDocument).IsTrue();
                await Assert.That(inspector.Heading).IsEqualTo("RULESET player_round");
                await Assert.That(inspector.DocumentTitle.Text).IsEqualTo("Player Round");
                await Assert.That(inspector.For.Text).IsEqualTo("each_player");
                await Assert.That(inspector.Entries.Count).IsEqualTo(7).Because("the document mode lists every column");
                await Assert.That(inspector.Tables).IsEquivalentTo(["player_round"]);
            }

            CanvasNodeViewModel kills = Select(vm, "round_kills");
            using (Assert.Multiple())
            {
                await Assert.That(inspector.HasNode).IsTrue();
                await Assert.That(inspector.ShowsDocument).IsFalse();
                await Assert.That(inspector.Heading).IsEqualTo("COUNT round_kills");
                await Assert.That(inspector.Id.Text).IsEqualTo("round_kills");
                await Assert.That(inspector.Id.IsEditable).IsTrue();
                await Assert.That(inspector.Label.Text).IsEmpty();
                await Assert.That(inspector.Per.Text).IsEqualTo("round");
                await Assert.That(inspector.Gate.IsVisible).IsTrue();
                await Assert.That(inspector.Format.IsVisible).IsFalse().Because("format is a compute's key");
                await Assert.That(inspector.Keep.IsVisible).IsFalse().Because("keep is a capture's key");
                await Assert.That(inspector.Title.IsVisible).IsFalse();
                await Assert.That(inspector.Surfacing).IsEqualTo("shown: player_round as kills");
                await Assert.That(kills.Surfacing).IsEqualTo("shown: player_round as kills");
                await Assert.That(kills.IsSurfaced).IsTrue();
                await Assert.That(inspector.Entries.Select(e => e.Stat)).IsEquivalentTo(["round_kills"]);
                await Assert.That(inspector.DeleteRefusal).Contains("round_kast").Because("the flag's when: reads it");
                await Assert.That(inspector.CanDelete).IsFalse();
            }

            Select(vm, "round_side");
            using (Assert.Multiple())
            {
                await Assert.That(inspector.Keep.IsVisible).IsTrue();
                await Assert.That(inspector.Keep.Text).IsEmpty();
                await Assert.That(inspector.Surfacing).IsEqualTo("shown: player_round as side");
            }

            CanvasNodeViewModel death = vm.Nodes.Single(n => n.Id == "player_death" && n.IsScaffold);
            vm.SelectedNodes.Clear();
            vm.SelectedNodes.Add(death);
            using (Assert.Multiple())
            {
                await Assert.That(inspector.IsNodeAuthorable).IsFalse();
                await Assert.That(inspector.NodeNote).Contains("engine node");
                await Assert.That(death.Surfacing).IsEmpty();
            }
        }
    }

    [Test]
    public async Task AField_WritesThroughTheSession_AndTheInspectorReflectsTheRedraw()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            InspectorViewModel inspector = vm.Inspector;
            Select(vm, "round_kills");
            inspector.Label.Text = "Frags";
            inspector.Label.Commit();
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "label")).IsEqualTo("Frags");
                await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Single().Id).IsEqualTo("round_kills");
                await Assert.That(inspector.Label.Text).IsEqualTo("Frags");
                await Assert.That(inspector.Label.IsDirty).IsFalse();
                await Assert.That(vm.CanUndo).IsTrue();
            }

            inspector.Gate.Text = "round.active";
            inspector.Gate.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "while")).IsEqualTo("round.active");

            inspector.Gate.Text = "";
            inspector.Gate.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "while")).IsNull();

            inspector.Per.Text = "match";
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "per")).IsEqualTo("match").Because("a picker writes on selection");

            inspector.Label.Text = "";
            inspector.Label.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "label")).IsNull();

            Select(vm, "round_side");
            inspector.Keep.Text = "first";
            await Assert.That(RulesetEdits.Field(Text(rules), "round_side", "keep")).IsEqualTo("first");

            inspector.Keep.Text = null!;
            await Assert.That(RulesetEdits.Field(Text(rules), "round_side", "keep")).IsEqualTo("first")
                .Because("a picker losing its selection is not a removal");
            inspector.Keep.Text = "";
            await Assert.That(RulesetEdits.Field(Text(rules), "round_side", "keep")).IsNull().Because("the empty option is");
        }
    }

    [Test]
    public async Task Rename_RewritesTheReferences_AndKeepsTheNodeSelected_AndRefusesADuplicate()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            InspectorViewModel inspector = vm.Inspector;
            Select(vm, "round_kills");
            inspector.Id.Text = "frags";
            inspector.Id.Commit();
            string text = Text(rules);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.StatIds(text)).Contains("frags");
                await Assert.That(RulesetEdits.StatIds(text)).DoesNotContain("round_kills");
                await Assert.That(text).Contains("when: \"frags > 0 or round_kast_assists > 0 or player.survived or player.traded\"");
                await Assert.That(text).Contains("- { stat: frags,    label: kills }");
                await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Single().Id).IsEqualTo("frags");
                await Assert.That(inspector.Id.Text).IsEqualTo("frags");
                await Assert.That(inspector.Id.Error).IsEmpty();
                await Assert.That(inspector.Surfacing).IsEqualTo("shown: player_round as kills");
                await Assert.That(vm.Diagnostics).IsEmpty();
            }

            inspector.Id.Text = "round_deaths";
            inspector.Id.Commit();
            using (Assert.Multiple())
            {
                await Assert.That(inspector.Id.Error).Contains("already declared");
                await Assert.That(Text(rules)).IsEqualTo(text);
                await Assert.That(vm.EditStatus).Contains("already declared");
            }

            inspector.Id.Text = "1bad";
            inspector.Id.Commit();
            using (Assert.Multiple())
            {
                await Assert.That(inspector.Id.Error).Contains("not a valid id");
                await Assert.That(Text(rules)).IsEqualTo(text);
            }
        }
    }

    [Test]
    public async Task TheDocumentMode_EditsTheHeaderAndTheUses()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            InspectorViewModel inspector = vm.Inspector;
            inspector.Summary.Text = "Rows per round.";
            inspector.Summary.Commit();
            await Assert.That(RulesetEdits.DocumentField(Text(rules), "summary")).IsEqualTo("Rows per round.");

            inspector.For.Text = "match";
            await Assert.That(RulesetEdits.DocumentField(Text(rules), "for")).IsEqualTo("match");
            inspector.For.Text = "each_player";

            inspector.UseToAdd = "kast";
            inspector.AddUseCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(inspector.Uses).IsEquivalentTo(["kast"]);
                await Assert.That(inspector.UseToAdd).IsEmpty();
                await Assert.That(Text(rules)).Contains("for: each_player\nuse: [kast]\n");
            }

            inspector.RemoveUseCommand.Execute("kast");
            using (Assert.Multiple())
            {
                await Assert.That(inspector.Uses).IsEmpty();
                await Assert.That(Text(rules)).DoesNotContain("use:");
            }

            inspector.DocumentTitle.Text = "";
            inspector.DocumentTitle.Commit();
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.DocumentField(Text(rules), "title")).IsNull();
                await Assert.That(vm.SelectedRuleset!.Title).IsEqualTo("player_round").Because("the picker falls back to the id");
            }
        }
    }

    [Test]
    public async Task Surfacing_AddsRemovesAndRelabels_AndTheBadgeFollows()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            InspectorViewModel inspector = vm.Inspector;
            Select(vm, "round_deaths");
            using (Assert.Multiple())
            {
                await Assert.That(inspector.Surfacing).IsEqualTo("shown: player_round as deaths");
                await Assert.That(inspector.DeleteLabel).IsEqualTo("Delete and remove its reference");
                await Assert.That(inspector.CanDelete).IsTrue();
            }

            inspector.AddToScoreboardCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).Contains("  scoreboard:\n    - { stat: round_deaths, label: round_deaths }\n");
                await Assert.That(inspector.Surfacing).IsEqualTo("shown: scoreboard as round_deaths, player_round as deaths");
                await Assert.That(vm.Nodes.Single(n => n.Id == "round_deaths").Surfacing).IsEqualTo("shown: scoreboard as round_deaths, player_round as deaths");
                await Assert.That(inspector.DeleteLabel).IsEqualTo("Delete and remove its 2 references");
                await Assert.That(vm.Diagnostics).IsEmpty();
            }

            string shown = Text(rules);
            inspector.AddToTableCommand.Execute("player_round");
            await Assert.That(Text(rules)).IsEqualTo(shown).Because("a column already there is not written twice");

            Select(vm, "round_kast_assists");
            inspector.AddToTableCommand.Execute("player_round");
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).Contains("        - { stat: round_kast,     label: kast }\n        - { stat: round_kast_assists, label: round_kast_assists }\n");
                await Assert.That(inspector.Surfacing).IsEqualTo("shown: player_round as round_kast_assists");
                await Assert.That(inspector.DeleteRefusal).Contains("round_kast").Because("the flag reads it, so the cascade is not offered");
            }

            Select(vm, "round_deaths");
            ShowEntryViewModel board = inspector.Entries.Single(e => e.Reference.IsScoreboard);
            board.Label.Text = "Deaths";
            board.Label.Commit();
            board = inspector.Entries.Single(e => e.Reference.IsScoreboard);
            board.Group.Text = "round";
            board.Group.Commit();
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).Contains("- { stat: round_deaths, label: Deaths, group: round }");
                await Assert.That(inspector.Surfacing).IsEqualTo("shown: scoreboard as Deaths, player_round as deaths");
            }

            inspector.Entries.Single(e => !e.Reference.IsScoreboard).RemoveCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).DoesNotContain("{ stat: round_deaths,   label: deaths }");
                await Assert.That(inspector.Entries.Count).IsEqualTo(1);
                await Assert.That(inspector.DeleteLabel).IsEqualTo("Delete and remove its reference");
            }

            inspector.DeleteCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.StatIds(Text(rules))).DoesNotContain("round_deaths");
                await Assert.That(Text(rules)).DoesNotContain("scoreboard:").Because("the cascade took the entry and the emptied list");
                await Assert.That(Text(rules)).Contains("- { stat: round_kast,     label: kast }");
                await Assert.That(inspector.ShowsDocument).IsTrue();
                await Assert.That(vm.Diagnostics).IsEmpty();
            }

            vm.UndoCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.StatIds(Text(rules))).Contains("round_deaths").Because("the cascade is one edit");
                await Assert.That(Text(rules)).Contains("scoreboard:");
            }
        }
    }

    [Test]
    public async Task Delete_IsRefused_WhileASiblingReadsTheEntry()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = Text(rules);
            vm.Inspector.DeleteCommand.Execute(null);
            await Assert.That(Text(rules)).IsEqualTo(before).Because("nothing is selected");

            Select(vm, "round_kills");
            vm.Inspector.DeleteCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(vm.Inspector.DeleteRefusal).IsEqualTo("round_kills is read by round_kast; change them first.");
                await Assert.That(Text(rules)).IsEqualTo(before);
            }
        }
    }

    [Test]
    public async Task NewRuleset_WritesAMinimalFile_OpensIt_AndTakesAStat()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            InspectorViewModel inspector = vm.Inspector;
            await Assert.That(inspector.CanCreate).IsTrue();

            inspector.NewRulesetId = "bad id";
            inspector.CreateRulesetCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(vm.EditStatus).Contains("not a valid ruleset id");
                await Assert.That(vm.SelectedRuleset!.Id).IsEqualTo("player_round");
            }

            inspector.NewRulesetId = "player_round";
            inspector.CreateRulesetCommand.Execute(null);
            await Assert.That(vm.EditStatus).Contains("already exists");

            inspector.NewRulesetId = "my_rules";
            inspector.CreateRulesetCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(File.ReadAllText(rules.UserPath("my_rules"))).IsEqualTo("ruleset: my_rules\ntitle: my rules\nfor: each_player\n\nstats: {}\n");
                await Assert.That(vm.EditStatus).IsEmpty();
                await Assert.That(vm.SelectedRuleset!.Id).IsEqualTo("my_rules");
                await Assert.That(vm.Rulesets.Select(r => r.Id)).IsEquivalentTo(["my_rules", "player_round"]);
                await Assert.That(vm.IsEditable).IsTrue();
                await Assert.That(inspector.NewRulesetId).IsEmpty();
                await Assert.That(inspector.ShowsDocument).IsTrue();
                await Assert.That(inspector.Heading).IsEqualTo("RULESET my_rules");
                await Assert.That(inspector.DocumentTitle.Text).IsEqualTo("my rules");
                await Assert.That(vm.Nodes).IsEmpty();
            }

            vm.NewRulesetCommand.Execute("my_rules");
            await Assert.That(vm.EditStatus).Contains("already exists").Because("the tab's own command is the same path");

            vm.AddStatCommand.Execute("kill");
            using (Assert.Multiple())
            {
                await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("new_kill");
                await Assert.That(vm.Diagnostics).IsEmpty();
            }
        }
    }

    [Test]
    public async Task AShippedRuleset_ShowsReadOnly_AndWritesNothing()
    {
        using RulesTree rules = RulesTree.WithShipped("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        InspectorViewModel inspector = vm.Inspector;
        Select(vm, "round_kills");
        using (Assert.Multiple())
        {
            await Assert.That(inspector.IsEditable).IsFalse();
            await Assert.That(inspector.ReadOnlyNote).Contains("shipped");
            await Assert.That(inspector.CanEditNode).IsFalse();
            await Assert.That(inspector.Id.IsEditable).IsFalse();
            await Assert.That(inspector.CanDelete).IsFalse();
        }

        inspector.Label.Text = "x";
        inspector.Label.Commit();
        await Assert.That(File.ReadAllText(rules.ShippedPath("player_round"))).DoesNotContain("label: x");
    }

    [Test]
    public async Task TheDock_ShowsWhileEitherTabIsOn_AndBringsTheTurnedOnTabToTheFront()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            using (Assert.Multiple())
            {
                await Assert.That(vm.ShowInspector).IsTrue();
                await Assert.That(vm.ShowSource).IsTrue();
                await Assert.That(vm.ShowDock).IsTrue();
                await Assert.That(vm.DockTab).IsEqualTo(0);
            }

            vm.ToggleSourceCommand.Execute(null);
            vm.ToggleSourceCommand.Execute(null);
            await Assert.That(vm.DockTab).IsEqualTo(1).Because("turning the YAML tab on brings it to the front");

            vm.ToggleInspectorCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(vm.ShowInspector).IsFalse();
                await Assert.That(vm.ShowDock).IsTrue();
                await Assert.That(vm.DockTab).IsEqualTo(1);
            }

            vm.ToggleSourceCommand.Execute(null);
            await Assert.That(vm.ShowDock).IsFalse();

            vm.ToggleInspectorCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(vm.ShowDock).IsTrue();
                await Assert.That(vm.DockTab).IsEqualTo(0);
            }
        }
    }
}
