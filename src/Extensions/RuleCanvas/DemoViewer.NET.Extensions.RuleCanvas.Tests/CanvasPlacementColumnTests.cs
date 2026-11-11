#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     An item added without a drop point (the + Add flyout, the quick starts) lands in the column its kind
///     reads in: under the lowest node of that column, or beside the entries when the column is empty. A
///     table goes right of the stats, where the first placement puts a show node, not under them.
/// </summary>
public class CanvasPlacementColumnTests
{
    private static (PlacementColumn? Column, Rect Box) Node(PlacementColumn? column, double x, double y, double w = 300, double h = 100) =>
        (column, new Rect(x, y, w, h));

    [Test]
    public async Task TheKindsColumn_IsDefineTableOrEntry()
    {
        using (Assert.Multiple())
        {
            await Assert.That(CanvasArranger.ColumnOf(GalleryItemKind.Define)).IsEqualTo(PlacementColumn.Define);
            await Assert.That(CanvasArranger.ColumnOf(GalleryItemKind.Table)).IsEqualTo(PlacementColumn.Show);
            foreach (GalleryItemKind kind in new[] { GalleryItemKind.Stat, GalleryItemKind.Prefab, GalleryItemKind.Highlight, GalleryItemKind.View, GalleryItemKind.Gate })
            {
                await Assert.That(CanvasArranger.ColumnOf(kind)).IsEqualTo(PlacementColumn.Entry).Because(kind.ToString());
            }
        }
    }

    [Test]
    public async Task AnItem_LandsUnderTheLowestNodeOfItsColumn_LevelWithItsLeftEdge()
    {
        List<(PlacementColumn? Column, Rect Box)> nodes =
        [
            Node(null, 0, 0, 140, 30),
            Node(PlacementColumn.Define, 300, 50),
            Node(PlacementColumn.Entry, 800, 0),
            Node(PlacementColumn.Entry, 820, 300, 300, 200),
            Node(PlacementColumn.Show, 1400, 20, 240, 120),
            Node(PlacementColumn.Show, 1450, 400, 240, 90)
        ];
        using (Assert.Multiple())
        {
            await Assert.That(CanvasArranger.NextFreeSpot(PlacementColumn.Entry, nodes)).IsEqualTo(new Point(800, 540));
            await Assert.That(CanvasArranger.NextFreeSpot(PlacementColumn.Show, nodes)).IsEqualTo(new Point(1400, 530));
            await Assert.That(CanvasArranger.NextFreeSpot(PlacementColumn.Define, nodes)).IsEqualTo(new Point(300, 190));
        }
    }

    [Test]
    public async Task AnEmptyColumn_OpensBesideTheEntries_OrBelowEverything_OrAtTheOrigin()
    {
        List<(PlacementColumn? Column, Rect Box)> entriesOnly =
        [
            Node(null, 0, 0, 140, 30),
            Node(PlacementColumn.Entry, 500, 100, 360, 200),
            Node(PlacementColumn.Entry, 520, 400, 300, 100)
        ];
        List<(PlacementColumn? Column, Rect Box)> scaffoldsOnly = [Node(null, 0, 0, 140, 30), Node(null, 20, 100, 140, 30)];
        using (Assert.Multiple())
        {
            await Assert.That(CanvasArranger.NextFreeSpot(PlacementColumn.Show, entriesOnly)).IsEqualTo(new Point(860 + 140, 100))
                .Because("a show node opens one layer right of the entries, at their top");
            await Assert.That(CanvasArranger.NextFreeSpot(PlacementColumn.Define, entriesOnly)).IsEqualTo(new Point(500 - 140 - 360, 100))
                .Because("a define opens one layer left of the entries");
            await Assert.That(CanvasArranger.NextFreeSpot(PlacementColumn.Entry, entriesOnly)).IsEqualTo(new Point(500, 540));
            await Assert.That(CanvasArranger.NextFreeSpot(PlacementColumn.Entry, scaffoldsOnly)).IsEqualTo(new Point(0, 170))
                .Because("with no entries anything lands below what is drawn");
            await Assert.That(CanvasArranger.NextFreeSpot(PlacementColumn.Show, scaffoldsOnly)).IsEqualTo(new Point(0, 170));
            await Assert.That(CanvasArranger.NextFreeSpot(PlacementColumn.Entry, [])).IsEqualTo(new Point(0, 0));
        }
    }

    [Test]
    public async Task OnTheCanvas_ATableGoesRightOfTheStats_ADefineLeft_AndAPrefabUnderThem()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        List<CanvasNodeViewModel> entries = [.. vm.Nodes.Where(n => n.IsEntry && !n.IsDefine)];
        CanvasNodeViewModel table = vm.Nodes.Single(n => n.IsShow);
        double entriesRight = entries.Max(n => n.Location.X + n.EstimatedSize.Width);
        double entriesLeft = entries.Min(n => n.Location.X);
        double entriesBottom = entries.Max(n => n.Location.Y + n.EstimatedSize.Height);

        GalleryItem tableItem = vm.Gallery.Single(g => g.Title == "Surfacing").Items.Single(i => i.Kind == GalleryItemKind.Table);
        GalleryItem defineItem = vm.Gallery.Single(g => g.Title == "Stats").Items.Single(i => i.Kind == GalleryItemKind.Define);
        GalleryItem prefab = vm.Gallery.Single(g => g.Title == "Prefabs").Items.Single(i => i.Name == "enemy_kills");
        using (Assert.Multiple())
        {
            await Assert.That(vm.NextFreeSpot(tableItem)).IsEqualTo(new Point(table.Location.X, table.Location.Y + table.EstimatedSize.Height + 40))
                .Because("the one table is the show column; a new one stacks under it");
            await Assert.That(vm.NextFreeSpot(tableItem).X).IsGreaterThan(entriesRight).Because("right of the stats, never under them");
            await Assert.That(vm.NextFreeSpot(defineItem).X).IsLessThan(entriesLeft).Because("player_round has no define; the column opens left of the entries");
            await Assert.That(vm.NextFreeSpot(prefab)).IsEqualTo(new Point(entriesLeft, entriesBottom + 40));
        }

        vm.AddItemCommand.Execute(tableItem);
        CanvasNodeViewModel added = vm.Nodes.Single(n => n.IsShow && n.Title == "new_table");
        using (Assert.Multiple())
        {
            await Assert.That(added.Location.X).IsEqualTo(table.Location.X);
            await Assert.That(added.Location.X).IsGreaterThan(entriesRight);
            await Assert.That(added.Location.Y).IsGreaterThan(table.Location.Y);
        }

        vm.AddItemCommand.Execute(defineItem);
        CanvasNodeViewModel define = vm.Nodes.Single(n => n.IsDefine);
        await Assert.That(define.Location.X).IsLessThan(entriesLeft);

        vm.AddItemCommand.Execute(defineItem);
        List<CanvasNodeViewModel> defines = [.. vm.Nodes.Where(n => n.IsDefine)];
        await Assert.That(defines.Select(d => d.Location.X).Distinct().Count()).IsEqualTo(1).Because("the second define stacks under the first");
    }
}
