#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The scope and gate pickers and the expression lines on a node face: each writes its field through
///     the splice writer, an emptied where: or gate removes the field, and a formula or a flag's test cannot
///     be emptied. A shipped ruleset's nodes carry the editors switched off.
/// </summary>
public class CanvasFaceEditTests
{
    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id);

    private static string Text(RulesTree rules) => File.ReadAllText(rules.UserPath("player_round"));

    [Test]
    public async Task TheFace_CarriesTheScopeTheGateAndTheLines()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel deaths = Node(vm, "round_deaths");
            CanvasNodeViewModel kast = Node(vm, "round_kast");
            using (Assert.Multiple())
            {
                await Assert.That(deaths.Per).IsEqualTo("round");
                await Assert.That(deaths.Gate).IsEqualTo("match.live");
                await Assert.That(deaths.ShowFieldPickers).IsTrue();
                await Assert.That(deaths.VisibleChips).IsEquivalentTo(["on death"]).Because("the pickers stand in for the per and while chips");
                await Assert.That(deaths.Lines.Select(l => l.Key)).IsEquivalentTo(["where"]);
                await Assert.That(kast.Lines.Select(l => l.Key)).IsEquivalentTo(["when", "where"]);
                await Assert.That(kast.Lines.Single(l => l.Key == "when").Text).StartsWith("round_kills > 0");
                await Assert.That(Node(vm, "MatchLive").ShowFieldPickers).IsFalse();
            }
        }
    }

    [Test]
    public async Task ChangingTheScope_WritesPer()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            Node(vm, "round_kills").Per = "match";
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "per")).IsEqualTo("match");
            await Assert.That(Node(vm, "round_kills").Chips).Contains("per match");
        }
    }

    [Test]
    public async Task AClearedScopePicker_WritesNothing()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = Text(rules);
            Node(vm, "round_kills").Per = null;
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).IsEqualTo(before);
                await Assert.That(vm.EditStatus).IsEmpty();
                await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "per")).IsEqualTo("round");
            }
        }
    }

    [Test]
    public async Task PickingAGate_WritesWhile_AndClearingItRemovesIt()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            Node(vm, "round_kills").Gate = "round.active";
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "while")).IsEqualTo("round.active");
                await Assert.That(vm.Nodes.Any(n => n.Id == "RoundActive")).IsTrue().Because("the gate now stands in the chain");
            }

            Node(vm, "round_deaths").Gate = "";
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(Text(rules), "round_deaths", "while")).IsNull();
                await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Id == "player_death" && c.Target.Owner?.Id == "round_deaths"))
                    .IsTrue().Because("ungated, the stat fires straight from the event");
            }
        }
    }

    [Test]
    public async Task EditingWhere_WritesIt_AndEmptyingItRemovesIt()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasExpressionViewModel where = Node(vm, "round_assists").Lines.Single(l => l.Key == "where");
            where.Text = "event.AssistedFlash == true";
            where.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_assists", "where")).IsEqualTo("event.AssistedFlash == true");

            where = Node(vm, "round_assists").Lines.Single(l => l.Key == "where");
            where.Text = "";
            where.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_assists", "where")).IsNull();

            where = Node(vm, "round_kills").Lines.Single(l => l.Key == "where");
            where.Text = "event.Weapon == \"awp\"";
            where.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "where")).IsEqualTo("event.Weapon == \"awp\"")
                .Because("a stat with no where: gets one");
        }
    }

    [Test]
    public async Task EditingAFlagsWhen_WritesIt_AndEmptyingItIsRefused()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasExpressionViewModel when = Node(vm, "round_kast").Lines.Single(l => l.Key == "when");
            when.Text = "round_kills > 1";
            when.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kast", "flag.when")).IsEqualTo("round_kills > 1");

            string before = Text(rules);
            when = Node(vm, "round_kast").Lines.Single(l => l.Key == "when");
            when.Text = "";
            when.Commit();
            await Assert.That(Text(rules)).IsEqualTo(before);
            await Assert.That(vm.EditStatus).Contains("cannot be empty");
        }
    }

    [Test]
    public async Task EditingACompute_WritesTheFormula()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.PlaceItem(vm.Gallery.Single(g => g.Title == "Stats").Items.Single(i => i.Name == "compute"), new Point(0, 0));
            CanvasExpressionViewModel formula = Node(vm, "new_compute").Lines.Single(l => l.Key == "compute");
            formula.Text = "round_kills * 2";
            formula.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "new_compute", "compute")).IsEqualTo("round_kills * 2");
            await Assert.That(Node(vm, "new_compute").Lines.Select(l => l.Key)).IsEquivalentTo(["compute"]).Because("a compute has no trigger to filter");
        }
    }

    [Test]
    public async Task AShippedRuleset_CarriesTheEditorsSwitchedOff()
    {
        using RulesTree rules = RulesTree.WithShipped("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasNodeViewModel deaths = Node(vm, "round_deaths");
        string before = File.ReadAllText(rules.ShippedPath("player_round"));
        deaths.Gate = "round.active";
        deaths.Lines.Single().Text = "1 == 1";
        deaths.Lines.Single().Commit();
        using (Assert.Multiple())
        {
            await Assert.That(deaths.ShowFieldPickers).IsFalse();
            await Assert.That(deaths.VisibleChips).Contains("while match.live");
            await Assert.That(File.ReadAllText(rules.ShippedPath("player_round"))).IsEqualTo(before);
        }
    }
}
