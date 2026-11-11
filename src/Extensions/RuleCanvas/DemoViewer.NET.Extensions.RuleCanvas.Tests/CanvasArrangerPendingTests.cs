#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     A drop point outlives whatever comes between the drop and the draw that holds its node: a focus
///     redraw that does not draw it, and a write that was refused.
/// </summary>
public class CanvasArrangerPendingTests
{
    [Test]
    public async Task APendingPlacement_WaitsForTheDrawThatHoldsItsKey()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        RulesetCorpus.Composed composed = rules.Load().Compose("player_round");
        CanvasArranger arranger = new(() => null);
        RulesetDocumentNodeKey damage = new("player_round", "round_damage");
        arranger.PlaceNext(damage, new Point(640, 480));

        // The slice around round_kast does not hold round_damage, which fires on player_hurt.
        arranger.FocusKey = new RulesetDocumentNodeKey("player_round", "round_kast");
        Arrangement slice = arranger.Arrange(composed, null, RulesetNodeLayout.Empty);
        using (Assert.Multiple())
        {
            await Assert.That(slice.Composed.Graph.Nodes.Select(n => n.Id)).DoesNotContain("round_damage");
            await Assert.That(slice.Layout.PositionFor(damage)).IsNull();
            await Assert.That(arranger.Pending.ContainsKey(damage)).IsTrue().Because("nothing drew it yet");
        }

        arranger.FocusKey = null;
        Arrangement whole = arranger.Arrange(composed, null, RulesetNodeLayout.Empty);
        using (Assert.Multiple())
        {
            await Assert.That(whole.Layout.PositionFor(damage)).IsEqualTo(new Point(640, 480));
            await Assert.That(arranger.Pending).IsEmpty().Because("spent by the draw that held it");
        }
    }

    [Test]
    public async Task APendingPlacement_WinsOverTheSidecar_AndTheScreen()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        RulesetCorpus.Composed composed = rules.Load().Compose("player_round");
        CanvasArranger arranger = new(() => null);
        RulesetDocumentNodeKey kills = new("player_round", "round_kills");
        string sidecar = RulesetLayoutStore.SidecarPathFor(rules.UserPath("player_round"));
        RulesetLayoutStore.SaveSidecar(sidecar, RulesetNodeLayout.From([new(kills, new Point(1, 1))]));
        RulesetNodeLayout onScreen = RulesetNodeLayout.From([new(kills, new Point(2, 2))]);

        arranger.PlaceNext(kills, new Point(3, 3));
        Arrangement drawn = arranger.Arrange(composed, sidecar, onScreen);
        await Assert.That(drawn.Layout.PositionFor(kills)).IsEqualTo(new Point(3, 3));
    }

    [Test]
    public async Task ARefusedWrite_AndAFocusedRedraw_KeepTheDropPoint_ForTheWriteThatAddsTheEntry()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        GalleryItem kill = vm.Gallery.SelectMany(g => g.Items).Single(i => i.Kind == GalleryItemKind.View && i.Name == "kill");

        // A directory where the atomic write puts its sibling makes the write fail on every platform.
        string blocker = Directory.CreateDirectory(rules.UserPath("player_round") + ".tmp").FullName;
        try
        {
            vm.PlaceItem(kill, new Point(640, 480));
            using (Assert.Multiple())
            {
                await Assert.That(vm.EditStatus).StartsWith("The file could not be written");
                await Assert.That(vm.Nodes.Select(n => n.Id)).DoesNotContain("new_kill");
            }
        }
        finally
        {
            Directory.Delete(blocker);
        }

        // A focus redraw in between draws a slice without the key.
        vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_kills"));
        vm.ClearFocusCommand.Execute(null);

        // The same id through another route: the command adds new_<view>, which is what the drop asked for.
        vm.AddStatCommand.Execute("kill");
        using (Assert.Multiple())
        {
            await Assert.That(vm.EditStatus).IsEqualTo("");
            await Assert.That(vm.Nodes.Single(n => n.Id == "new_kill").Location).IsEqualTo(new Point(640, 480));
        }
    }

    [Test]
    public async Task ADropWhileFocused_PlacesTheNode_WhereItWasDropped()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_kast"));
        await Assert.That(vm.Nodes.Select(n => n.Id)).DoesNotContain("round_damage");

        vm.PlaceItem(vm.Gallery.Single(g => g.Title == "Prefabs").Items.Single(i => i.Name == "deaths"), new Point(777, 333));
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasFocus).IsFalse();
            await Assert.That(vm.EditStatus).IsEqualTo("");
            await Assert.That(vm.Nodes.Single(n => n.Id == "deaths").Location).IsEqualTo(new Point(777, 333));
            await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("round_damage");
        }
    }
}
