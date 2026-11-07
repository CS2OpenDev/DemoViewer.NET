#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The surface around the graph: the gallery's order, search and the user's own flags; the diagnostics
///     list; the toast; the empty, failed and new-ruleset states; and the readable fit.
/// </summary>
public class CanvasSurfaceTests
{
    [Test]
    public async Task TheGallery_IsInTaskOrder_WithEveryStatKind_AndViewsFoldedByEvent()
    {
        IReadOnlyList<GalleryGroup> gallery = CanvasGallery.Build(EmbeddedCatalog.Root);
        using (Assert.Multiple())
        {
            await Assert.That(string.Join(",", gallery.Select(g => g.Title))).IsEqualTo("Prefabs,Highlights,Stats,Gates,Views");
            await Assert.That(gallery.Single(g => g.Title == "Stats").Items.Select(i => i.Name))
                .IsEquivalentTo(["count", "sum", "capture", "flag", "compute", "tally", "streak", "bucket", "rate", "burst"]);
            GalleryGroup views = gallery.Single(g => g.Title == "Views");
            await Assert.That(views.IsExpanded).IsFalse().Because("the views are the long tail");
            await Assert.That(views.Sections.Count).IsGreaterThan(1).Because("views are grouped by the event that fires them");
            await Assert.That(views.Sections.All(s => s.Title is not null)).IsTrue();
            await Assert.That(views.Sections.SelectMany(s => s.Items).Count()).IsEqualTo(views.Items.Count);
            await Assert.That(gallery.Where(g => g.Title != "Views").All(g => g.IsExpanded)).IsTrue();
            await Assert.That(gallery.SelectMany(g => g.Items).All(i => i.Hint.Length > 0)).IsTrue().Because("every item has its second line");
            await Assert.That(gallery.Single(g => g.Title == "Gates").Sections.Count).IsEqualTo(1).Because("no ruleset, no flags of the user's own");
        }
    }

    [Test]
    public async Task TheGallery_OffersTheRulesetsOwnFlagsAsGates_AndTheyGateAStat()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        GalleryGroup gates = vm.Gallery.Single(g => g.Title == "Gates");
        GallerySection own = gates.Sections.Single(s => s.Title == "Your flags");
        using (Assert.Multiple())
        {
            await Assert.That(own.Items.Select(i => i.Name)).Contains("round_kast");
            await Assert.That(own.Items.All(i => i.Kind == GalleryItemKind.Gate)).IsTrue();
            await Assert.That(gates.Items.Select(i => i.Name)).Contains("round_kast").Because("the flat list carries them for a drop");
        }

        vm.DropOnNode(own.Items.Single(i => i.Name == "round_kast"), vm.Nodes.Single(n => n.Id == "round_kills"));
        await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("player_round")), "round_kills", "while")).IsEqualTo("round_kast");
    }

    [Test]
    public async Task TheGallerySearch_NarrowsEveryGroup_ByTitleNameAndHint_AndOpensWhatItShows()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        await Assert.That(vm.VisibleGallery).IsEquivalentTo(vm.Gallery).Because("an empty search is the gallery itself");

        vm.GallerySearch = "weapon";
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsGallerySearching).IsTrue();
            await Assert.That(vm.VisibleGallery.All(g => g.IsExpanded)).IsTrue();
            await Assert.That(vm.VisibleGallery.All(g => g.Items.Count > 0)).IsTrue();
            await Assert.That(vm.VisibleGallery.SelectMany(g => g.Items).Select(i => i.Name)).Contains("bucket").Because("the hint says weapon");
            await Assert.That(vm.VisibleGallery.SelectMany(g => g.Items).Select(i => i.Name)).DoesNotContain("count");
        }

        vm.GallerySearch = "Opening";
        await Assert.That(vm.VisibleGallery.SelectMany(g => g.Items).Select(i => i.Title)).Contains("Opening kills").Because("the title matches, case aside");

        vm.GallerySearch = "zzz-nothing";
        await Assert.That(vm.VisibleGallery).IsEmpty();

        vm.ClearGallerySearchCommand.Execute(null);
        await Assert.That(vm.IsGallerySearching).IsFalse();
    }

    [Test]
    public async Task AddItem_PlacesUnderTheAuthoredNodes_AndQuickStartsPlaceTheirItems()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        double lowest = vm.Nodes.Where(n => n.IsAuthorable).Max(n => n.Location.Y + n.EstimatedSize.Height);
        GalleryItem flag = vm.Gallery.Single(g => g.Title == "Stats").Items.Single(i => i.Name == "flag");

        vm.AddItemCommand.Execute(flag);
        CanvasNodeViewModel added = vm.Nodes.Single(n => n.Id == "new_flag");
        using (Assert.Multiple())
        {
            await Assert.That(added.Location.Y).IsGreaterThanOrEqualTo(lowest).Because("it lands under the lowest authored node");
            await Assert.That(added.Kind).IsEqualTo("flag");
        }

        vm.QuickStartCommand.Execute("highlight");
        await Assert.That(vm.Nodes.Any(n => n.Id == "highlight" && n.IsHighlight)).IsTrue();
        vm.QuickStartCommand.Execute("prefab");
        await Assert.That(vm.Nodes.Any(n => n.Id == "enemy_kills")).IsTrue();
    }

    [Test]
    public async Task AddItem_OnAShippedRuleset_SaysSoOnTheToast()
    {
        using RulesTree rules = RulesTree.WithShipped("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        string before = File.ReadAllText(rules.ShippedPath("player_round"));

        vm.AddItemCommand.Execute(vm.Gallery.Single(g => g.Title == "Stats").Items[0]);
        using (Assert.Multiple())
        {
            await Assert.That(File.ReadAllText(rules.ShippedPath("player_round"))).IsEqualTo(before);
            await Assert.That(vm.Toast?.Message).Contains("read only");
        }
    }

    [Test]
    public async Task ARefusal_ArrivesOnTheToast_EvenWhenRepeated()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        GalleryItem gate = vm.Gallery.Single(g => g.Title == "Gates").Items[0];

        vm.PlaceItem(gate, new Point(0, 0));
        CanvasToast? first = vm.Toast;
        vm.PlaceItem(gate, new Point(0, 0));
        CanvasToast? second = vm.Toast;
        using (Assert.Multiple())
        {
            await Assert.That(first?.Message).Contains("onto a stat");
            await Assert.That(second?.Message).IsEqualTo(first?.Message);
            await Assert.That(second?.Sequence).IsNotEqualTo(first?.Sequence).Because("the same refusal said twice is shown twice");
            await Assert.That(second?.At).IsNull().Because("no drop point was given, so the toast sits by the strip");
        }

        vm.Notify("dropped here", new Point(12, 34));
        await Assert.That(vm.Toast?.At).IsEqualTo(new Point(12, 34));
    }

    [Test]
    public async Task TheDiagnosticsList_NamesTheNode_AndSelectingALineSelectsIt()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("broken", """
            ruleset: broken
            title: Broken
            for: each_player

            stats:
              kills:
                count: kill
                per: round
              ratio:
                compute: "kills / no_such_stat"
                per: match

            """);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "broken";
        vm.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(vm.DiagnosticCount).IsGreaterThan(0);
            await Assert.That(vm.DiagnosticEntries.Any(d => d.Message.Contains("no_such_stat", StringComparison.Ordinal))).IsTrue();
            await Assert.That(vm.DiagnosticEntries.All(d => d.Token == "AccentError")).IsTrue();
        }

        CanvasDiagnostic? inNode = vm.DiagnosticEntries.FirstOrDefault(d => d.Node is not null);
        if (inNode is not null)
        {
            vm.SelectDiagnosticCommand.Execute(inNode);
            await Assert.That(vm.SelectedNodes).Contains(inNode.Node!);
            await Assert.That(inNode.Where).IsEqualTo(inNode.Node!.Id);
        }

        vm.ShowDiagnosticsList = false;
        vm.ToggleDiagnosticsListCommand.Execute(null);
        await Assert.That(vm.ShowDiagnosticsList).IsTrue();
    }

    [Test]
    public async Task AnEmptyTree_ShowsTheNoRulesetsCard_AndNewRulesetFillsIt()
    {
        using RulesTree rules = RulesTree.New();
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(vm.EmptyState).IsEqualTo(CanvasEmptyState.NoRulesets);
            await Assert.That(vm.IsNoRulesets).IsTrue();
            await Assert.That(vm.CanCreateRuleset).IsTrue();
            await Assert.That(vm.RulesFolder).IsEqualTo(rules.UserDirectory);
        }

        vm.NewRulesetCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(File.Exists(rules.UserPath("my_rules"))).IsTrue();
            await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("my_rules");
            await Assert.That(vm.EmptyState).IsEqualTo(CanvasEmptyState.None);
            await Assert.That(vm.Nodes.Any(n => n.Id == "enemy_kills")).IsTrue().Because("the starter composes");
            await Assert.That(vm.IsEditable).IsTrue();
        }

        vm.NewRulesetCommand.Execute(null);
        await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("my_rules_2").Because("a second one takes a fresh id");
    }

    [Test]
    public async Task ARulesetWithNothingDeclared_ShowsTheWatermark()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("bare", """
            ruleset: bare
            title: Bare
            for: each_player

            stats: {}

            """);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("bare");
            await Assert.That(vm.EmptyState).IsEqualTo(CanvasEmptyState.EmptyRuleset);
            await Assert.That(vm.IsEmptyRuleset).IsTrue();
            await Assert.That(vm.HasEmptyState).IsTrue();
        }
    }

    [Test]
    public async Task AFileThatStopsLoading_ShowsTheFailureCard_NamingFileAndReason()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        await Assert.That(vm.EmptyState).IsEqualTo(CanvasEmptyState.None);

        // Another surface breaks the file; the next activation sees the change on disk and reloads it.
        await Task.Delay(20);
        RulesTree.Touch(rules.UserPath("player_round"), "ruleset: [not a ruleset\n");
        File.SetLastWriteTimeUtc(rules.UserPath("player_round"), DateTime.UtcNow.AddMinutes(1));
        vm.OnActivated(null!);

        using (Assert.Multiple())
        {
            await Assert.That(vm.EmptyState).IsEqualTo(CanvasEmptyState.LoadFailed);
            await Assert.That(vm.EmptyStateFile).IsEqualTo("player_round.rules.yaml");
            await Assert.That(vm.EmptyStateReason).IsNotEmpty();
            await Assert.That(vm.Toast?.Message).Contains("no longer loads");
        }
    }

    [Test]
    public async Task AFileThatStopsLoading_StaysOpenOnItsText_AndASaveFromThePaneLoadsItBack()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        string path = rules.UserPath("player_round");
        vm.Nodes.Single(n => n.Id == "round_kills").FacetToAdd = "headshot";
        string good = File.ReadAllText(path);
        await Assert.That(vm.CanUndo).IsTrue();

        const string broken = "ruleset: [not a ruleset\n";
        RulesTree.Touch(path, broken);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        vm.FilesChanged([path]);
        using (Assert.Multiple())
        {
            await Assert.That(vm.EmptyState).IsEqualTo(CanvasEmptyState.LoadFailed);
            await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("player_round");
            await Assert.That(vm.DocumentText).IsEqualTo(broken).Because("the pane shows what is on disk, not what loaded last");
            await Assert.That(vm.SourceDirty).IsFalse();
            await Assert.That(vm.CanUndo).IsFalse().Because("the break was another writer's, so the history is gone");
            await Assert.That(vm.Status).DoesNotContain("Undo is still available");
            await Assert.That(vm.IsEditable).IsTrue();
        }

        vm.SourceEdited(good);
        vm.SaveSourceCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(File.ReadAllText(path)).IsEqualTo(good);
            await Assert.That(vm.SourceDirty).IsFalse();
            await Assert.That(vm.EmptyState).IsEqualTo(CanvasEmptyState.None);
            await Assert.That(vm.Nodes.Any(n => n.Id == "round_kills")).IsTrue().Because("the file loads and draws again");
            await Assert.That(vm.EditStatus).IsEqualTo("");
            await Assert.That(vm.HasEditStatus).IsFalse();
        }
    }

    [Test]
    public async Task AFileFixedOutside_DropsTheNoLongerLoadsLine()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        string path = rules.UserPath("player_round");
        string good = File.ReadAllText(path);

        RulesTree.Touch(path, "ruleset: [not a ruleset\n");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        vm.FilesChanged([path]);
        await Assert.That(vm.EmptyState).IsEqualTo(CanvasEmptyState.LoadFailed);
        await Assert.That(vm.EditStatus).Contains("no longer loads");

        RulesTree.Touch(path, good);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(4));
        vm.FilesChanged([path]);
        using (Assert.Multiple())
        {
            await Assert.That(vm.EmptyState).IsEqualTo(CanvasEmptyState.None);
            await Assert.That(vm.Nodes.Count).IsGreaterThan(0);
            await Assert.That(vm.EditStatus).IsEqualTo("").Because("the strip stops saying the file no longer loads once it does");
            await Assert.That(vm.HasEditStatus).IsFalse();
        }
    }

    [Test]
    public async Task TheReadableFit_ClampsTheZoom_AndFramesTheAuthoredNodesWhenTheChainIsTooWide()
    {
        Size viewport = new(1000, 600);
        List<(Point, Size, bool)> small = [(new Point(0, 0), new Size(200, 100), true), (new Point(300, 0), new Size(200, 100), false)];
        ReadableFit fits = ReadableFit.Of(small, viewport);
        using (Assert.Multiple())
        {
            await Assert.That(fits.Zoom).IsEqualTo(1.0).Because("a small chain is never zoomed past 1");
            await Assert.That(fits.Location).IsEqualTo(new Point(-ReadableFit.Pad, -ReadableFit.Pad));
            await Assert.That(fits.FramesAuthoredOnly).IsFalse();
        }

        List<(Point, Size, bool)> wide =
        [
            (new Point(0, 0), new Size(200, 100), false),
            (new Point(600, 0), new Size(360, 200), true),
            (new Point(1000, 300), new Size(360, 200), true),
            (new Point(3000, 0), new Size(200, 100), false)
        ];
        ReadableFit framed = ReadableFit.Of(wide, viewport);
        using (Assert.Multiple())
        {
            await Assert.That(framed.FramesAuthoredOnly).IsTrue();
            await Assert.That(framed.Zoom).IsEqualTo(ReadableFit.MaxZoom).Because("the authored frame fits, so it is shown at full size");
            await Assert.That(framed.Location.X).IsEqualTo(600 - ReadableFit.Pad).Because("anchored to the left-most authored column");
            await Assert.That(framed.Location.Y).IsEqualTo(-ReadableFit.Pad);
        }

        List<(Point, Size, bool)> scaffoldOnly = [(new Point(0, 0), new Size(200, 100), false), (new Point(5000, 0), new Size(200, 100), false)];
        ReadableFit floor = ReadableFit.Of(scaffoldOnly, viewport);
        await Assert.That(floor.Zoom).IsEqualTo(ReadableFit.MinZoom).Because("with nothing authored the whole chain is framed at the floor");
        await Assert.That(ReadableFit.Of([], viewport).Zoom).IsEqualTo(ReadableFit.MaxZoom);
    }

    [Test]
    public async Task ThePanelToggles_Flip()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        bool inspector = vm.ShowInspector;
        bool minimap = vm.ShowMinimap;
        vm.ToggleInspectorCommand.Execute(null);
        vm.ToggleMinimapCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.ShowInspector).IsEqualTo(!inspector);
            await Assert.That(vm.ShowMinimap).IsEqualTo(!minimap);
        }
    }
}
