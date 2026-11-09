#region

using System.ComponentModel;
using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Structure on a big canvas through the view model: frames, folds, the zoom level of detail, a tidy
///     of the selection, the search, and an arrangement that follows a renamed node. None of it writes the
///     YAML; every change lives in the sidecar and comes back on the next open, and the layout changes
///     share Undo with the document writes.
/// </summary>
public class CanvasTabViewModelStructureTests
{
    private const string Ruleset = "player_round";

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id);

    private static void Select(CanvasTabViewModel vm, params string[] ids)
    {
        vm.SelectedNodes.Clear();
        foreach (string id in ids)
        {
            vm.SelectedNodes.Add(Node(vm, id));
        }
    }

    private static CanvasTabViewModel Open(RulesTree tree)
    {
        CanvasTabViewModel vm = tree.NewViewModel();
        vm.OnActivated(null!);
        return vm;
    }

    // ---- groups ----

    [Test]
    public async Task GroupingTheSelection_FramesIt_NamedForTheSharedView_AndTheFrameComesBackOnTheNextOpen()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        string text = File.ReadAllText(tree.UserPath(Ruleset));
        Select(vm, "round_assists", "round_kast_assists");
        Rect box = new Rect(Node(vm, "round_assists").Location, Node(vm, "round_assists").EstimatedSize)
            .Union(new Rect(Node(vm, "round_kast_assists").Location, Node(vm, "round_kast_assists").EstimatedSize));

        vm.GroupSelectionCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Groups.Count).IsEqualTo(1);
            await Assert.That(vm.Groups[0].Title).IsEqualTo("assist").Because("both stats fire on the assist view");
            await Assert.That(vm.Groups[0].Members).IsEquivalentTo([Node(vm, "round_assists").Key, Node(vm, "round_kast_assists").Key]);
            await Assert.That(vm.Groups[0].Rect.Contains(box)).IsTrue().Because("the frame is drawn around the nodes with room for a header");
            await Assert.That(vm.Items[0]).IsSameReferenceAs(vm.Groups[0]).Because("frames come first, so they draw under the nodes");
            await Assert.That(vm.Items.Count).IsEqualTo(vm.Nodes.Count + 1);
            await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Count()).IsEqualTo(2).Because("the nodes stay selected");
            await Assert.That(File.ReadAllText(tree.UserPath(Ruleset))).IsEqualTo(text).Because("a frame is layout, not document");
            await Assert.That(vm.HasSavedLayout).IsTrue();
        }

        CanvasTabViewModel again = Open(tree);
        using (Assert.Multiple())
        {
            await Assert.That(again.Groups.Count).IsEqualTo(1);
            await Assert.That(again.Groups[0].Title).IsEqualTo("assist");
            await Assert.That(RulesetLayoutStore.Load(tree.UserPath(Ruleset)).Groups.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task GroupingNodesOfDifferentViews_IsCalledGroup_AndOneNodeIsNotAGroup()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        Select(vm, "round_kills");
        vm.GroupSelectionCommand.Execute(null);
        await Assert.That(vm.Groups).IsEmpty().Because("a frame around one node frames nothing");

        Select(vm, "round_kills", "round_deaths");
        vm.GroupSelectionCommand.Execute(null);
        await Assert.That(vm.Groups.Single().Title).IsEqualTo("Group");
    }

    [Test]
    public async Task UngroupAndDelete_RemoveTheFrameAndKeepTheNodes_AndUndoBringsItBack()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        string text = File.ReadAllText(tree.UserPath(Ruleset));
        Select(vm, "round_kills", "round_deaths");
        vm.GroupSelectionCommand.Execute(null);
        int nodes = vm.Nodes.Count;

        vm.Ungroup(vm.Groups.Single());
        using (Assert.Multiple())
        {
            await Assert.That(vm.Groups).IsEmpty();
            await Assert.That(vm.Nodes.Count).IsEqualTo(nodes);
            await Assert.That(vm.CanUndo).IsTrue().Because("a layout change is on the same Undo as a write");
        }

        vm.UndoCommand.Execute(null);
        await Assert.That(vm.Groups.Count).IsEqualTo(1).Because("Undo puts the frame back");
        vm.UndoCommand.Execute(null);
        await Assert.That(vm.Groups).IsEmpty().Because("and the grouping before it");
        vm.RedoCommand.Execute(null);
        await Assert.That(vm.Groups.Count).IsEqualTo(1);

        vm.SelectedNodes.Clear();
        vm.SelectedNodes.Add(vm.Groups.Single());
        vm.DeleteSelectionCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Groups).IsEmpty().Because("Delete on a frame ungroups");
            await Assert.That(vm.Nodes.Count).IsEqualTo(nodes).Because("and never deletes an entry");
            await Assert.That(File.ReadAllText(tree.UserPath(Ruleset))).IsEqualTo(text);
        }
    }

    [Test]
    public async Task AFrame_SurvivesAWriteAndItsUndo_AndIsRedrawnFromTheSidecar()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        Select(vm, "round_kills", "round_deaths");
        vm.GroupSelectionCommand.Execute(null);
        CanvasGroupViewModel before = vm.Groups.Single();

        Node(vm, "round_kills").FacetToAdd = "headshot";
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Field(File.ReadAllText(tree.UserPath(Ruleset)), "round_kills", "match.headshot")).IsNotNull();
            await Assert.That(vm.Groups.Count).IsEqualTo(1);
            await Assert.That(vm.Groups[0]).IsNotSameReferenceAs(before).Because("the frame is rebuilt from the sidecar on every redraw");
            await Assert.That(vm.Groups[0].Members).IsEquivalentTo(before.Members);
        }

        vm.UndoCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Field(File.ReadAllText(tree.UserPath(Ruleset)), "round_kills", "match.headshot")).IsNull().Because("the write was undone");
            await Assert.That(vm.Groups.Count).IsEqualTo(1).Because("the frame is not document state and stays");
        }

        vm.UndoCommand.Execute(null);
        await Assert.That(vm.Groups).IsEmpty().Because("the grouping, older than the write, is the next layout change back");
    }

    [Test]
    public async Task AMemberDraggedOutOfTheFrame_LeavesTheGroup_WhenTheLayoutIsSaved()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        Select(vm, "round_kills", "round_deaths");
        vm.GroupSelectionCommand.Execute(null);
        Rect frame = vm.Groups.Single().Rect;

        Node(vm, "round_deaths").Location = new Point(frame.Right + 500, frame.Bottom + 500);
        vm.SaveLayoutCommand.Execute(null);
        RulesetNodeLayout saved = RulesetLayoutStore.Load(tree.UserPath(Ruleset));
        using (Assert.Multiple())
        {
            await Assert.That(saved.Groups.Single().Members).IsEquivalentTo([Node(vm, "round_kills").Key]);
            await Assert.That(saved.GroupOf(Node(vm, "round_deaths").Key)).IsNull();
        }

        Node(vm, "round_kills").Location = new Point(frame.Right + 900, frame.Bottom + 900);
        vm.SaveLayoutCommand.Execute(null);
        await Assert.That(RulesetLayoutStore.Load(tree.UserPath(Ruleset)).Groups).IsEmpty().Because("a frame with no member left is gone");
    }

    [Test]
    public async Task AFramesTitle_IsEditedInPlace_AndWritten()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        Select(vm, "round_kills", "round_deaths");
        vm.GroupSelectionCommand.Execute(null);
        CanvasGroupViewModel frame = vm.Groups.Single();

        frame.BeginEditTitleCommand.Execute(null);
        await Assert.That(frame.IsEditingTitle).IsTrue();
        frame.TitleText = "  Round counts ";
        frame.CommitTitle();
        using (Assert.Multiple())
        {
            await Assert.That(frame.IsEditingTitle).IsFalse();
            await Assert.That(frame.Title).IsEqualTo("Round counts");
            await Assert.That(frame.HeaderText).IsEqualTo("Round counts");
            await Assert.That(RulesetLayoutStore.Load(tree.UserPath(Ruleset)).Groups.Single().Title).IsEqualTo("Round counts");
        }

        frame.TitleText = "";
        frame.CommitTitle();
        await Assert.That(frame.HeaderText).IsEqualTo("Group").Because("an unnamed frame still has a header to drag by");
    }

    // ---- collapse ----

    [Test]
    public async Task FoldingANode_KeepsItsPortsOnTheRail_IsStoredPerNode_AndComesBackOnTheNextOpen()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        CanvasNodeViewModel kills = Node(vm, "round_kills");
        CanvasNodeViewModel deaths = Node(vm, "round_deaths");
        await Assert.That(kills.IsEditable).IsTrue();
        await Assert.That(kills.RailInputs).IsEmpty().Because("an open editable face carries its ports on its rows");

        vm.ToggleCollapse(kills);
        using (Assert.Multiple())
        {
            await Assert.That(kills.IsCollapsed).IsTrue();
            await Assert.That(kills.ShowsHeaderOnly).IsTrue();
            await Assert.That(kills.ShowsFace).IsFalse();
            await Assert.That(kills.RailInputs.Count).IsEqualTo(kills.Inputs.Count).Because("the rows are gone, so the rail attaches the wires");
            await Assert.That(kills.CollapseGlyph).IsNotEqualTo(deaths.CollapseGlyph);
            await Assert.That(deaths.IsCollapsed).IsFalse().Because("a fold is per node, not per kind");
            await Assert.That(RulesetLayoutStore.Load(tree.UserPath(Ruleset)).IsCollapsed(kills.Key)).IsTrue();
        }

        CanvasTabViewModel again = Open(tree);
        await Assert.That(Node(again, "round_kills").IsCollapsed).IsTrue();
        await Assert.That(Node(again, "round_deaths").IsCollapsed).IsFalse();

        again.ToggleCollapse(Node(again, "round_kills"));
        await Assert.That(RulesetLayoutStore.Load(tree.UserPath(Ruleset)).IsCollapsed(kills.Key)).IsFalse();
    }

    [Test]
    public async Task AFold_SurvivesAWrite()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        vm.ToggleCollapse(Node(vm, "round_kills"));
        Node(vm, "round_deaths").FacetToAdd = "headshot";
        await Assert.That(Node(vm, "round_kills").IsCollapsed).IsTrue();
    }

    // ---- zoom level of detail ----

    [Test]
    public async Task BelowTheDetailZoom_EveryFaceIsHeaderOnly_AndAUserFoldOutlivesZoomingBackIn()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        CanvasNodeViewModel kills = Node(vm, "round_kills");
        vm.ToggleCollapse(kills);
        int zoomNotifications = 0;
        foreach (CanvasNodeViewModel node in vm.Nodes)
        {
            node.PropertyChanged += (_, e) => zoomNotifications += e.PropertyName == nameof(CanvasNodeViewModel.IsZoomedOut) ? 1 : 0;
        }

        vm.ViewportZoom = 0.9;
        vm.ViewportZoom = 0.7;
        vm.ViewportZoom = 0.55;
        await Assert.That(zoomNotifications).IsEqualTo(0).Because("only the crossing of the threshold reaches the nodes");
        await Assert.That(vm.IsZoomedOut).IsFalse();

        vm.ViewportZoom = 0.45;
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsZoomedOut).IsTrue();
            await Assert.That(vm.Nodes.All(n => n.ShowsHeaderOnly)).IsTrue();
            await Assert.That(zoomNotifications).IsEqualTo(vm.Nodes.Count).Because("one set per node, once");
            await Assert.That(Node(vm, "round_deaths").IsCollapsed).IsFalse().Because("the zoom does not fold anything");
        }

        vm.ViewportZoom = 0.3;
        await Assert.That(zoomNotifications).IsEqualTo(vm.Nodes.Count).Because("deeper below the threshold is not a crossing");

        vm.ViewportZoom = 1;
        using (Assert.Multiple())
        {
            await Assert.That(Node(vm, "round_deaths").ShowsHeaderOnly).IsFalse();
            await Assert.That(kills.ShowsHeaderOnly).IsTrue().Because("the user's fold stays");
            await Assert.That(RulesetLayoutStore.Load(tree.UserPath(Ruleset)).Collapsed).IsEquivalentTo([kills.Key]).Because("the zoom is not stored");
        }
    }

    [Test]
    public async Task ANodeDrawnWhileZoomedOut_StartsHeaderOnly()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        vm.ViewportZoom = 0.4;
        Node(vm, "round_kills").FacetToAdd = "headshot";
        await Assert.That(vm.Nodes.All(n => n.IsZoomedOut)).IsTrue().Because("a redraw reads the zoom the view holds");
    }

    // ---- tidy ----

    [Test]
    public async Task TidyingTheSelection_ReplacesOnlyIt_InsideItsBox_AndOneUndoPutsItBack()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        CanvasNodeViewModel kills = Node(vm, "round_kills");
        CanvasNodeViewModel deaths = Node(vm, "round_deaths");
        CanvasNodeViewModel kast = Node(vm, "round_kast");
        kills.Location = new Point(1000, 1000);
        deaths.Location = new Point(1700, 1050);
        kast.Location = new Point(1300, 1400);
        vm.SaveLayoutCommand.Execute(null);
        Dictionary<RulesetDocumentNodeKey, Point> others = vm.Nodes.Where(n => n.Id is not ("round_kills" or "round_deaths" or "round_kast")).ToDictionary(n => n.Key, n => n.Location);
        Dictionary<string, Point> scattered = new() { ["round_kills"] = kills.Location, ["round_deaths"] = deaths.Location, ["round_kast"] = kast.Location };

        Select(vm, "round_kills", "round_deaths", "round_kast");
        vm.TidySelectionCommand.Execute(null);
        Dictionary<string, Point> tidied = new() { ["round_kills"] = Node(vm, "round_kills").Location, ["round_deaths"] = Node(vm, "round_deaths").Location, ["round_kast"] = Node(vm, "round_kast").Location };
        using (Assert.Multiple())
        {
            await Assert.That(tidied.Values.Min(p => p.X)).IsEqualTo(1000).Because("anchored at the box's left");
            await Assert.That(tidied.Values.Min(p => p.Y)).IsEqualTo(1000).Because("and at its top");
            await Assert.That(tidied).IsNotEquivalentTo(scattered).Because("something moved");
            await Assert.That(Node(vm, "round_kast").Location.X).IsGreaterThan(Node(vm, "round_kills").Location.X)
                .Because("round_kast reads round_kills, so the layering puts it to the right");
            foreach ((RulesetDocumentNodeKey key, Point at) in others)
            {
                await Assert.That(vm.Nodes.Single(n => n.Key == key).Location).IsEqualTo(at).Because(key + " was not selected");
            }

            await Assert.That(vm.SelectedNodes.Count).IsEqualTo(3).Because("the selection is kept through the redraw");
            await Assert.That(RulesetLayoutStore.Load(tree.UserPath(Ruleset)).PositionFor(kast.Key)).IsEqualTo(Node(vm, "round_kast").Location);
        }

        vm.UndoCommand.Execute(null);
        using (Assert.Multiple())
        {
            foreach ((string id, Point at) in scattered)
            {
                await Assert.That(Node(vm, id).Location).IsEqualTo(at).Because(id + " goes back where it was");
            }

            await Assert.That(RulesetLayoutStore.Load(tree.UserPath(Ruleset)).PositionFor(kast.Key)).IsEqualTo(scattered["round_kast"]);
        }

        vm.RedoCommand.Execute(null);
        foreach ((string id, Point at) in tidied)
        {
            await Assert.That(Node(vm, id).Location).IsEqualTo(at).Because(id + " is tidied again");
        }
    }

    [Test]
    public async Task ResetArrangement_IsOneUndoAway()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        Node(vm, "round_kills").Location = new Point(1234, 567);
        vm.SaveLayoutCommand.Execute(null);

        vm.ResetLayoutCommand.Execute(null);
        await Assert.That(vm.HasSavedLayout).IsFalse();
        await Assert.That(Node(vm, "round_kills").Location).IsNotEqualTo(new Point(1234, 567));

        vm.UndoCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(Node(vm, "round_kills").Location).IsEqualTo(new Point(1234, 567));
            await Assert.That(vm.HasSavedLayout).IsTrue();
        }
    }

    // ---- search ----

    [Test]
    public async Task Searching_DimsWhatItDoesNotName_AndEnterWalksTheMatches()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        int fits = 0;
        vm.GraphReplaced += () => fits++;

        vm.CanvasSearch = "round_k";
        List<CanvasNodeViewModel> matches = [.. vm.Nodes.Where(n => n.IsSearchMatch)];
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsSearching).IsTrue();
            await Assert.That(matches.Select(n => n.Id)).IsEquivalentTo(["round_kills", "round_kast_assists", "round_kast"]);
            await Assert.That(Node(vm, "round_deaths").DimOpacity).IsEqualTo(0.25);
            await Assert.That(Node(vm, "round_kills").DimOpacity).IsEqualTo(1);
            await Assert.That(vm.SearchMatches.Count).IsEqualTo(3);
            await Assert.That(vm.SearchSummary).IsEqualTo("3 matches");
        }

        CanvasNodeViewModel? first = vm.AdvanceSearch();
        CanvasNodeViewModel? second = vm.AdvanceSearch();
        vm.AdvanceSearch();
        CanvasNodeViewModel? wrapped = vm.AdvanceSearch();
        using (Assert.Multiple())
        {
            await Assert.That(first).IsSameReferenceAs(vm.SearchMatches[0]);
            await Assert.That(second).IsSameReferenceAs(vm.SearchMatches[1]);
            await Assert.That(wrapped).IsSameReferenceAs(first).Because("Enter cycles");
            await Assert.That(vm.SelectedNodes.Single()).IsSameReferenceAs(first);
            await Assert.That(fits).IsEqualTo(0).Because("the view frames the selection; the graph is not replaced");
        }

        vm.ClearSearchCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Nodes.All(n => n.IsSearchMatch)).IsTrue();
            await Assert.That(vm.SearchSummary).IsEmpty();
            await Assert.That(vm.AdvanceSearch()).IsNull();
        }
    }

    [Test]
    public async Task Searching_ReadsEventNamesLabelsAndDefines_AndSurvivesARedraw()
    {
        using RulesTree tree = RulesTree.New();
        tree.User("search", """
            ruleset: search
            title: Search
            for: each_player
            define:
              enemy_kill:
                on: kill
                match: { enemy: true }
            stats:
              frags:
                count: enemy_kill
                label: Frags on enemies
                per: round
              falls:
                count: death
                per: round
            """);
        CanvasTabViewModel vm = tree.NewViewModel();
        vm.PreferredRulesetId = "search";
        vm.OnActivated(null!);

        vm.CanvasSearch = "enemies";
        await Assert.That(vm.Nodes.Where(n => n.IsSearchMatch).Select(n => n.Id)).IsEquivalentTo(["frags"]).Because("the label matches");

        vm.CanvasSearch = "enemy_kill";
        await Assert.That(vm.Nodes.Where(n => n.IsSearchMatch).Select(n => n.Title)).Contains("enemy_kill").Because("the define by name");
        await Assert.That(vm.Nodes.Where(n => n.IsSearchMatch).Select(n => n.Id)).Contains("frags").Because("and the stat firing on it");

        vm.CanvasSearch = "death";
        List<CanvasNodeViewModel> deaths = [.. vm.Nodes.Where(n => n.IsSearchMatch)];
        using (Assert.Multiple())
        {
            await Assert.That(deaths.Select(n => n.Id)).Contains("falls").Because("the view the stat fires on");
            await Assert.That(deaths.Any(n => n.IsScaffold && n.Id.Contains("death", StringComparison.Ordinal))).IsTrue()
                .Because("and the event node itself");
            await Assert.That(deaths.Select(n => n.Id)).Contains("frags").Because("the kill view's wire event is player_death");
        }

        vm.CanvasSearch = "falls";
        Node(vm, "falls").FacetToAdd = "headshot";
        await Assert.That(Node(vm, "frags").IsSearchMatch).IsFalse().Because("the search applies to the redrawn nodes");
        await Assert.That(Node(vm, "falls").IsSearchMatch).IsTrue();
    }

    // ---- identity across rename ----

    [Test]
    public async Task RenamingANode_CarriesItsPositionFoldAndFrame_ToTheNewKey()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        CanvasNodeViewModel kills = Node(vm, "round_kills");
        kills.Location = new Point(1234, 567);
        vm.SaveLayoutCommand.Execute(null);
        vm.ToggleCollapse(kills);
        Select(vm, "round_kills", "round_deaths");
        vm.GroupSelectionCommand.Execute(null);
        RulesetDocumentNodeKey renamed = new(Ruleset, "frags");

        Select(vm, "round_kills");
        vm.Inspector.Id.Text = "frags";
        vm.Inspector.Id.Commit();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Inspector.Id.Error).IsEmpty();
            await Assert.That(Node(vm, "frags").Location).IsEqualTo(new Point(1234, 567)).Because("the node keeps its place under its new name");
            await Assert.That(Node(vm, "frags").IsCollapsed).IsTrue();
            await Assert.That(vm.Groups.Single().Members).Contains(renamed);
            await Assert.That(vm.Groups.Single().Members).DoesNotContain(kills.Key);
            RulesetNodeLayout saved = RulesetLayoutStore.Load(tree.UserPath(Ruleset));
            await Assert.That(saved.PositionFor(renamed)).IsEqualTo(new Point(1234, 567)).Because("the sidecar learns the new key at once");
            await Assert.That(saved.IsCollapsed(renamed)).IsTrue();
            await Assert.That(saved.GroupOf(renamed)).IsNotNull();
        }

        CanvasTabViewModel again = Open(tree);
        await Assert.That(Node(again, "frags").Location).IsEqualTo(new Point(1234, 567));
        await Assert.That(again.Groups.Single().Members).Contains(renamed);
    }

    [Test]
    public async Task ARefusedRename_CarriesNothing_AndLeavesTheArrangementAlone()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        Node(vm, "round_kills").Location = new Point(1234, 567);
        vm.SaveLayoutCommand.Execute(null);

        Select(vm, "round_kills");
        vm.Inspector.Id.Text = "round_deaths";
        vm.Inspector.Id.Commit();
        await Assert.That(vm.Inspector.Id.Error).Contains("already declared");

        Node(vm, "round_deaths").FacetToAdd = "headshot";
        using (Assert.Multiple())
        {
            await Assert.That(Node(vm, "round_kills").Location).IsEqualTo(new Point(1234, 567));
            await Assert.That(Node(vm, "round_deaths").Location).IsNotEqualTo(new Point(1234, 567)).Because("no carry was recorded for a refused rename");
        }
    }

    // ---- layout history against the session's ----

    [Test]
    public async Task UndoOrdersLayoutChangesAgainstWrites_ByWhenTheyWereMade()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = Open(tree);
        string original = File.ReadAllText(tree.UserPath(Ruleset));

        Select(vm, "round_kills", "round_deaths");
        vm.GroupSelectionCommand.Execute(null);
        Node(vm, "round_kills").FacetToAdd = "headshot";
        string written = File.ReadAllText(tree.UserPath(Ruleset));
        Select(vm, "round_assists", "round_kast_assists");
        vm.GroupSelectionCommand.Execute(null);
        await Assert.That(vm.Groups.Count).IsEqualTo(2);

        vm.UndoCommand.Execute(null);
        await Assert.That(vm.Groups.Count).IsEqualTo(1).Because("the second frame came last");
        await Assert.That(File.ReadAllText(tree.UserPath(Ruleset))).IsEqualTo(written);

        vm.UndoCommand.Execute(null);
        await Assert.That(File.ReadAllText(tree.UserPath(Ruleset))).IsEqualTo(original).Because("then the write");
        await Assert.That(vm.Groups.Count).IsEqualTo(1);

        vm.UndoCommand.Execute(null);
        await Assert.That(vm.Groups).IsEmpty().Because("then the first frame");
        await Assert.That(vm.CanUndo).IsFalse();

        vm.RedoCommand.Execute(null);
        await Assert.That(vm.Groups.Count).IsEqualTo(1);
        vm.RedoCommand.Execute(null);
        await Assert.That(File.ReadAllText(tree.UserPath(Ruleset))).IsEqualTo(written);
        vm.RedoCommand.Execute(null);
        await Assert.That(vm.Groups.Count).IsEqualTo(2);
        await Assert.That(vm.CanRedo).IsFalse();
    }

    [Test]
    public async Task SwitchingRulesets_DropsTheLayoutHistory()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        tree.User("other", "ruleset: other\ntitle: Other\nstats:\n  other_kills:\n    count: kill\n    per: round\n");
        CanvasTabViewModel vm = Open(tree);
        Select(vm, "round_kills", "round_deaths");
        vm.GroupSelectionCommand.Execute(null);
        await Assert.That(vm.CanUndo).IsTrue();

        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "other");
        await Assert.That(vm.CanUndo).IsFalse();
        await Assert.That(vm.Groups).IsEmpty();
    }

    [Test]
    public async Task AFrameIsNotDimmedBySearch_AndNotifiesLikeAnyItem()
    {
        CanvasGroupViewModel frame = new(new CanvasGroup("x", [new RulesetDocumentNodeKey("a", "b")], new Rect(1, 2, 3, 4)));
        List<string> changed = [];
        frame.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        frame.Location = new Point(5, 6);
        frame.Size = new Size(7, 8);
        using (Assert.Multiple())
        {
            await Assert.That(frame.DimOpacity).IsEqualTo(1);
            await Assert.That(frame.Rect).IsEqualTo(new Rect(5, 6, 7, 8));
            await Assert.That(changed).Contains(nameof(CanvasGroupViewModel.Location));
            await Assert.That(changed).Contains(nameof(CanvasGroupViewModel.Size));
        }
    }
}
