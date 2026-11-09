#region

using System.Xml.Linq;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The family colour reaches every place a node kind is shown: the face's stripe, pill and header band,
///     the gallery's pills, and the Inspector's heading. Scaffold rows stay quieter, with no band.
/// </summary>
public class CanvasKindColourTests
{
    private static readonly XNamespace _views = "using:DemoViewer.NET.Extensions.RuleCanvas.Views";

    private static string Attribute(XElement element, string name) => element.Attribute(name)?.Value ?? "";

    private static XDocument Load(string file) => XDocument.Load(Path.Combine(ViewsFolder(), file));

    [Test]
    public async Task AGalleryItem_TakesTheFamilyOfTheNodeItPlaces()
    {
        IReadOnlyList<GalleryGroup> gallery = CanvasGallery.Build(EmbeddedCatalog.Root, ["my_flag"]);
        GalleryItem Item(string group, string name) => gallery.Single(g => g.Title == group).Items.Single(i => i.Name == name);
        using (Assert.Multiple())
        {
            foreach (GalleryItem stat in gallery.Single(g => g.Title == "Stats").Items.Where(i => i.Kind == GalleryItemKind.Stat))
            {
                await Assert.That(stat.NodeKind).IsEqualTo(stat.Name);
            }

            await Assert.That(Item("Stats", "define").NodeKind).IsEqualTo("define");
            await Assert.That(Item("Prefabs", "side").NodeKind).IsEqualTo("capture").Because("the prefab's body is a capture");
            await Assert.That(Item("Prefabs", "damage_dealt").NodeKind).IsEqualTo("sum");
            await Assert.That(Item("Prefabs", "deaths").NodeKind).IsEqualTo("count");
            await Assert.That(Item("Highlights", "multi_kill").NodeKind).IsEqualTo("highlight");
            await Assert.That(Item("Surfacing", "table").NodeKind).IsEqualTo("show");
            await Assert.That(Item("Gates", "match.live").NodeKind).IsEqualTo("context").Because("an engine gate is a context flag");
            await Assert.That(Item("Gates", "my_flag").NodeKind).IsEqualTo("flag").Because("the user's own flag");
            await Assert.That(gallery.Single(g => g.Title == "Views").Items[0].NodeKind).IsEqualTo("count").Because("a view places a count");
            await Assert.That(RuleKindToken.KeyFor(Item("Prefabs", "side").NodeKind)).IsEqualTo("RuleKindCapture");
        }
    }

    [Test]
    public async Task TheInspectorHeading_CarriesTheSelectedNodesKind_AndNoneOverTheDocument()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        InspectorViewModel inspector = vm.Inspector;
        bool documentHasKind = inspector.HasHeadingKind;
        CanvasNodeViewModel kills = vm.Nodes.Single(n => n.Id == "round_kills");
        vm.SelectedNodes.Add(kills);
        string? countKind = inspector.HeadingKind;
        vm.SelectedNodes.Clear();
        CanvasNodeViewModel table = vm.Nodes.First(n => n.IsShow);
        vm.SelectedNodes.Add(table);
        string? tableKind = inspector.HeadingKind;
        vm.SelectedNodes.Clear();
        using (Assert.Multiple())
        {
            await Assert.That(documentHasKind).IsFalse();
            await Assert.That(countKind).IsEqualTo("count");
            await Assert.That(tableKind).IsEqualTo(table.Kind);
            await Assert.That(inspector.HeadingKind).IsNull().Because("back on the document");
            await Assert.That(inspector.HasHeadingKind).IsFalse();
        }
    }

    // Every authored face's header carries the band in its family brush, behind the stripe, styled by the theme;
    // the scaffold row has none, so the engine's nodes stay quieter than the author's.
    [Test]
    public async Task EveryAuthoredHeader_HasAFamilyBand_AndTheScaffoldRowHasNone()
    {
        XDocument view = Load("CanvasTabView.axaml");
        List<XElement> bands = [.. view.Descendants().Where(e => e.Name.LocalName == "Border" && Attribute(e, "Classes") == "kindTint")];
        XElement scaffold = view.Descendants().Single(e => e.Name.LocalName == "CanvasNodeTemplateSelector.Scaffold");
        using (Assert.Multiple())
        {
            await Assert.That(bands.Count).IsEqualTo(3).Because("the show, dependency and stat faces");
            foreach (XElement band in bands)
            {
                XElement grid = band.Parent!;
                int columns = Attribute(grid, "ColumnDefinitions").Split(',').Length;
                await Assert.That(band.Attribute(_views + "TokenBrush.Background")?.Value).Contains("RuleKindToken");
                await Assert.That(Attribute(band, "Grid.ColumnSpan")).IsEqualTo(columns.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await Assert.That(grid.Elements().First()).IsSameReferenceAs(band).Because("drawn under the stripe and the text");
                await Assert.That(Attribute(band, "Opacity")).IsEqualTo("").Because("the opacity is the theme's RuleKindTintOpacity");
            }

            await Assert.That(scaffold.Descendants().Any(e => Attribute(e, "Classes") == "kindTint")).IsFalse();
        }
    }

    [Test]
    public async Task TheGalleryPills_AndTheInspectorHeading_AreInTheFamilyColour()
    {
        XDocument view = Load("CanvasTabView.axaml");
        XDocument inspector = Load("InspectorView.axaml");
        List<XElement> pills = [.. view.Descendants().Where(e => e.Name.LocalName == "TextBlock" && Attribute(e, "Text") == "{Binding KindLabel}" && Attribute(e, "FontSize") == "8")];
        XElement stripe = inspector.Descendants().Single(e => Attribute(e, "IsVisible") == "{Binding HasHeadingKind}");
        using (Assert.Multiple())
        {
            await Assert.That(pills.Count).IsEqualTo(2).Because("the add menu's list and the gallery panel");
            foreach (XElement pill in pills)
            {
                await Assert.That(pill.Attribute(_views + "TokenBrush.Foreground")?.Value).IsEqualTo("{Binding NodeKind, Converter={x:Static views:RuleKindToken.Instance}}");
            }

            await Assert.That(stripe.Attribute(_views + "TokenBrush.Background")?.Value).IsEqualTo("{Binding HeadingKind, Converter={x:Static views:RuleKindToken.Instance}}");
        }
    }

    internal static string ViewsFolder()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Extensions", "RuleCanvas", "DemoViewer.NET.Extensions.RuleCanvas", "Views");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("the extension's Views folder is beside the tests in the repo");
    }
}
