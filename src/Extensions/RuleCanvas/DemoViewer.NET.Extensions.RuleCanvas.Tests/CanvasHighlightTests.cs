#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Highlights as first-class nodes: placed from the gallery into a document with or without a
///     <c>highlights:</c> section, edited on the face, extended by a wire from a stat, and deleted.
/// </summary>
public class CanvasHighlightTests
{
    private static (TempRules Rules, CanvasTabViewModel Vm) Open()
    {
        TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static GalleryItem Item(CanvasTabViewModel vm, string group, string name) =>
        vm.Gallery.Single(g => g.Title == group).Items.Single(i => i.Name == name);

    private static string Text(TempRules rules) => File.ReadAllText(rules.UserPath("player_round"));

    [Test]
    public async Task AHighlightPrefab_CreatesTheSection_AndStandsInTheChain()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.PlaceItem(Item(vm, "Prefabs", "enemy_kills"), new Point(0, 0));
            vm.PlaceItem(Item(vm, "Highlights", "multi_kill"), new Point(400, 0));
            string text = Text(rules);
            CanvasNodeViewModel node = vm.Nodes.Single(n => n.Id == "multi_kill");
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("\nhighlights:\n  multi_kill:\n    when: enemy_kills >= 3\n    per: round\n    score: 80\n");
                await Assert.That(node.IsHighlight).IsTrue();
                await Assert.That(node.Location).IsEqualTo(new Point(400, 0));
                await Assert.That(node.Lines.Select(l => l.Key)).IsEquivalentTo(["when", "title", "score"]);
                await Assert.That(node.Per).IsEqualTo("round");
                await Assert.That(node.ShowGatePicker).IsFalse();
                await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Id == "enemy_kills" && c.Target.Owner?.Id == "multi_kill"))
                    .IsTrue().Because("the when: reads the stat, which the chain draws as a logic input");
                await Assert.That(vm.Diagnostics).IsEmpty();
            }

            vm.PlaceItem(Item(vm, "Highlights", "multi_kill"), new Point(0, 0));
            await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("multi_kill_2").Because("a second one lands under the section that now exists");
        }
    }

    [Test]
    public async Task TheFace_EditsWhenTitleScoreAndPer()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.PlaceItem(Item(vm, "Highlights", "highlight"), new Point(0, 0));
            CanvasNodeViewModel Node() => vm.Nodes.Single(n => n.Id == "highlight");

            CanvasExpressionViewModel when = Node().Lines.Single(l => l.Key == "when");
            when.Text = "round_kills >= 2";
            when.Commit();
            await Assert.That(RulesetEdits.HighlightField(Text(rules), "highlight", "when")).IsEqualTo("round_kills >= 2");

            CanvasExpressionViewModel score = Node().Lines.Single(l => l.Key == "score");
            score.Text = "";
            score.Commit();
            await Assert.That(RulesetEdits.HighlightField(Text(rules), "highlight", "score")).IsNull();

            CanvasExpressionViewModel title = Node().Lines.Single(l => l.Key == "title");
            title.Text = "{player.name} double";
            title.Commit();
            await Assert.That(RulesetEdits.HighlightField(Text(rules), "highlight", "title")).IsEqualTo("{player.name} double");

            Node().Per = "match";
            await Assert.That(RulesetEdits.HighlightField(Text(rules), "highlight", "per")).IsEqualTo("match");

            string before = Text(rules);
            when = Node().Lines.Single(l => l.Key == "when");
            when.Text = "";
            when.Commit();
            await Assert.That(Text(rules)).IsEqualTo(before);
            await Assert.That(vm.EditStatus).Contains("cannot be empty");
        }
    }

    [Test]
    public async Task AStatWiredIntoAHighlight_JoinsItsWhen()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.PlaceItem(Item(vm, "Highlights", "highlight"), new Point(0, 0));
            vm.Connect(vm.Nodes.Single(n => n.Id == "round_survived"), vm.Nodes.Single(n => n.Id == "highlight"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.HighlightField(Text(rules), "highlight", "when")).IsEqualTo("player.survived and round_survived > 0");
                await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Id == "round_survived" && c.Target.Owner?.Id == "highlight")).IsTrue();
            }

            string before = Text(rules);
            vm.Connect(vm.Nodes.Single(n => n.Id == "MatchLive"), vm.Nodes.Single(n => n.Id == "highlight"));
            await Assert.That(Text(rules)).IsEqualTo(before);
            await Assert.That(vm.EditStatus).Contains("reads stats");
        }
    }

    [Test]
    public async Task DeletingAHighlight_RemovesItsEntry()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.PlaceItem(Item(vm, "Highlights", "highlight"), new Point(0, 0));
            vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "highlight"));
            vm.DeleteSelectedCommand.Execute(null);
            await Assert.That(Text(rules)).DoesNotContain("  highlight:");
            await Assert.That(vm.Nodes.Select(n => n.Id)).DoesNotContain("highlight");
        }
    }
}
