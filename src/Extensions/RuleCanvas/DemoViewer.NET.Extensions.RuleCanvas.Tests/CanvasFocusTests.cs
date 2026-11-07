#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Focus: one node's slice of the chain stays bright, the rest goes faint, and the focus survives the
///     redraw an edit causes. The slice is the node, its ancestors and its descendants, not its siblings.
/// </summary>
public class CanvasFocusTests
{
    [Test]
    public async Task FocusingAFlag_DrawsItsInputsAndTheirChains_AndNothingElse()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        int whole = vm.Nodes.Count;

        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "round_kast"));
        vm.FocusSelectedCommand.Execute(null);

        List<string> drawn = [.. vm.Nodes.Select(n => n.Id)];
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasFocus).IsTrue();
            await Assert.That(vm.FocusLabel).IsEqualTo("round_kast");
            await Assert.That(drawn).Contains("round_kast");
            await Assert.That(drawn).Contains("round_kills").Because("an input to the flag");
            await Assert.That(drawn).Contains("enrich.kill.was_enemy_kill").Because("read by an input to the flag");
            await Assert.That(drawn).Contains("player_death").Because("fires an input to the flag");
            await Assert.That(drawn).Contains("Alive").Because("Survived, an input, hangs off Alive");
            await Assert.That(drawn).DoesNotContain("round_damage").Because("fires on player_hurt, which the flag never reads");
            await Assert.That(drawn).DoesNotContain("MatchLive");
            await Assert.That(drawn).DoesNotContain("round_deaths").Because("a sibling reader of player_death is sideways, not upstream");
            await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Id == "round_kills" && c.Target.Owner?.Id == "round_kast")).IsTrue();
            await Assert.That(vm.Nodes.Count).IsLessThan(whole);
        }

        vm.ClearFocusCommand.Execute(null);
        await Assert.That(vm.Nodes.Count).IsEqualTo(whole);
        await Assert.That(vm.HasFocus).IsFalse();
    }

    [Test]
    public async Task Focus_SurvivesAnEdit_AndItsPlacementIsNotSaved()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_kills"));

        vm.Nodes.Single(n => n.Id == "round_kills").FacetToAdd = "headshot";
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasFocus).IsTrue().Because("the redraw after a write keeps the focus");
            await Assert.That(vm.Nodes.Select(n => n.Id)).DoesNotContain("round_damage");
            await Assert.That(vm.Nodes.Single(n => n.Id == "round_kills").Facets.Select(f => f.Name)).Contains("headshot");
        }

        vm.SaveLayoutCommand.Execute(null);
        await Assert.That(vm.HasSavedLayout).IsFalse().Because("a slice's placement is not the chain's arrangement");
    }

    [Test]
    public async Task AGalleryDropWhileFocused_DropsTheFocus_AndLandsWhereItWasDropped()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_kills"));

        vm.PlaceItem(vm.Gallery.Single(g => g.Title == "Prefabs").Items.Single(i => i.Name == "deaths"), new Avalonia.Point(640, 480));
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasFocus).IsFalse().Because("a slice would hide the new entry");
            await Assert.That(vm.Nodes.Single(n => n.Id == "deaths").Location).IsEqualTo(new Avalonia.Point(640, 480));
            await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("round_damage").Because("the whole chain is drawn again");
        }
    }

    [Test]
    public async Task DeletingTheFocusedNode_DropsTheFocus()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        // An added stat, since every shipped one is a show: column and deleting it breaks the composition.
        vm.PlaceItem(vm.Gallery.Single(g => g.Title == "Prefabs").Items.Single(i => i.Name == "enemy_kills"), new Avalonia.Point(0, 0));
        vm.FocusNode(vm.Nodes.Single(n => n.Id == "enemy_kills"));
        await Assert.That(vm.Nodes.Select(n => n.Id)).DoesNotContain("round_damage");

        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "enemy_kills"));
        vm.DeleteSelectedCommand.Execute(null);
        await Assert.That(vm.HasFocus).IsFalse();
        await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("round_damage").Because("the whole chain is back");
    }

    [Test]
    public async Task PlayerStats_ComposesAndPlaces_WithinBudget()
    {
        string shipped = RuleSetLocator.ResolveShippedRulesDirectory();
        RulesetCorpus corpus = RulesetCorpus.Load([(shipped, "shipped")]);
        corpus.Compose("player_round");
        Stopwatch clock = Stopwatch.StartNew();
        RulesetCorpus.Composed composed = corpus.Compose("player_stats");
        long firstMs = clock.ElapsedMilliseconds;
        clock.Restart();
        composed = corpus.Compose("player_stats");
        long composeMs = clock.ElapsedMilliseconds;
        clock.Restart();
        (IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires) = CanvasGraphBuilder.Build(composed);
        long placeMs = clock.ElapsedMilliseconds;
        string note = $"player_stats: {nodes.Count} nodes, {wires.Count} wires; compose {firstMs}ms first, {composeMs}ms warm; build and place {placeMs}ms";
        Console.WriteLine("[canvas] " + note);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "rulecanvas-player-stats-timing.txt"), note);
        await Assert.That(composeMs + placeMs).IsLessThan(5000).Because(note);
    }
}
