#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The gallery: what it offers, and what a drop does to the file. A stat, a view or a prefab dropped on
///     the canvas becomes an entry placed where it landed; a view or a gate dropped on a stat retargets it.
/// </summary>
public class CanvasGalleryTests
{
    [Test]
    public async Task TheGallery_OffersStatsViewsGatesAndPrefabs()
    {
        IReadOnlyList<GalleryGroup> gallery = CanvasGallery.Build(EmbeddedCatalog.Root);
        using (Assert.Multiple())
        {
            await Assert.That(gallery.Select(g => g.Title)).IsEquivalentTo(["Stats", "Views", "Gates", "Prefabs"]);
            await Assert.That(gallery.Single(g => g.Title == "Views").Items.Select(i => i.Name)).Contains("kill");
            await Assert.That(gallery.Single(g => g.Title == "Gates").Items.Select(i => i.Name)).Contains("round.active");
            await Assert.That(gallery.SelectMany(g => g.Items).Where(i => i.Kind != GalleryItemKind.Gate).All(i => i.Body is not null))
                .IsTrue().Because("everything but a gate places an entry, so it needs a body");
        }
    }

    [Test]
    public async Task APrefabDroppedOnTheCanvas_IsAddedWhereItLanded()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        GalleryItem prefab = vm.Gallery.Single(g => g.Title == "Prefabs").Items.Single(i => i.Name == "opening_kills");

        vm.PlaceItem(prefab, new Point(900, 700));

        string text = File.ReadAllText(rules.UserPath("player_round"));
        CanvasNodeViewModel added = vm.Nodes.Single(n => n.Id == "opening_kills");
        using (Assert.Multiple())
        {
            await Assert.That(text).Contains("opening_kills:\n    count: kill\n    match: { enemy: true }\n    while: round.no_deaths_yet\n    per: match");
            await Assert.That(added.Location).IsEqualTo(new Point(900, 700)).Because("the entry lands where it was dropped");
            await Assert.That(added.Chips).Contains("while round.no_deaths_yet");
            await Assert.That(vm.Nodes.Any(n => n.Id == "no_deaths_yet" || n.Id.Contains("NoDeaths", StringComparison.Ordinal)))
                .IsTrue().Because("the gate the prefab declares now stands in the chain");
            await Assert.That(vm.CanUndo).IsTrue();
        }
    }

    [Test]
    public async Task AStatKindDroppedTwice_GetsDistinctIds()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        GalleryItem count = vm.Gallery.Single(g => g.Title == "Stats").Items.Single(i => i.Name == "count");

        vm.PlaceItem(count, new Point(10, 10));
        vm.PlaceItem(count, new Point(10, 200));

        await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("new_count");
        await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("new_count_2");
    }

    [Test]
    public async Task AGateDroppedOnAStat_BecomesItsWhile_AndOnTheCanvasIsRefused()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        GalleryItem gate = vm.Gallery.Single(g => g.Title == "Gates").Items.Single(i => i.Name == "round.active");
        string before = File.ReadAllText(rules.UserPath("player_round"));

        vm.PlaceItem(gate, new Point(0, 0));
        await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
        await Assert.That(vm.EditStatus).Contains("onto a stat");

        vm.DropOnNode(gate, vm.Nodes.Single(n => n.Id == "round_kills"));
        string after = File.ReadAllText(rules.UserPath("player_round"));
        using (Assert.Multiple())
        {
            await Assert.That(after).Contains("round_kills:\n    count: kill\n    match: { enemy: true }\n    per: round\n    while: round.active");
            await Assert.That(vm.Nodes.Single(n => n.Id == "round_kills").Chips).Contains("while round.active");
            await Assert.That(vm.Nodes.Any(n => n.Id == "RoundActive")).IsTrue().Because("the gate node now stands before the stat");
        }
    }

    [Test]
    public async Task AViewDroppedOnACount_RetargetsIt_AndOnAFlagIsRefused()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        GalleryItem death = vm.Gallery.Single(g => g.Title == "Views").Items.Single(i => i.Name == "death");

        vm.DropOnNode(death, vm.Nodes.Single(n => n.Id == "round_kills"));
        await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("player_round")), "round_kills", "count")).IsEqualTo("death");

        vm.DropOnNode(death, vm.Nodes.Single(n => n.Id == "round_kast"));
        await Assert.That(vm.EditStatus).Contains("fires on no view");
    }
}
