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
    // Your defines: the trigger defines of the user's other rulesets, copied in under their own name on a
    // drop; on a stat the copy is interposed and the stat fires on it. The open ruleset's own defines and
    // non-trigger defines stay off the shelf.
    [Test]
    public async Task TheGallery_ShelvesTheUsersOtherDefines_AndADropCopiesOne()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        rules.User("clutch_lib", """
            ruleset: clutch_lib
            title: Clutch lib
            for: each_player

            define:
              post_plant_kill:
                on: kill
                match: { enemy: true }
                while: round.bomb.was_planted
              util_weapons: [hegrenade, inferno]

            stats:
              pp_kills:
                count: post_plant_kill
                per: round
            """);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "player_round");

        GalleryGroup shelf = vm.Gallery.Single(g => g.Title == "Your defines");
        GalleryItem item = shelf.Items.Single();
        using (Assert.Multiple())
        {
            await Assert.That(item.Name).IsEqualTo("post_plant_kill");
            await Assert.That(item.CopiesName).IsTrue();
            await Assert.That(item.Hint).Contains("from clutch_lib");
            await Assert.That(item.Body).Contains("while: round.bomb.was_planted");
        }

        vm.PlaceItem(item, new Avalonia.Point(500, 500));
        string text = File.ReadAllText(rules.UserPath("player_round"));
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.DefineNames(text)).IsEquivalentTo(["post_plant_kill"]);
            await Assert.That(RulesetEdits.DefineField(text, "post_plant_kill", "while")).IsEqualTo("round.bomb.was_planted");
            await Assert.That(vm.Nodes.Any(n => n.IsDefine && n.Title == "post_plant_kill")).IsTrue();
            await Assert.That(vm.Gallery.Any(g => g.Title == "Your defines")).IsTrue().Because("the shelf lists the other ruleset's define whether or not a copy exists here");
        }

        vm.DropOnNode(item, vm.Nodes.Single(n => n.Id == "round_kills"));
        text = File.ReadAllText(rules.UserPath("player_round"));
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.DefineNames(text)).IsEquivalentTo(["post_plant_kill", "post_plant_kill_2"]);
            await Assert.That(RulesetEdits.Field(text, "round_kills", "count")).IsEqualTo("post_plant_kill_2").Because("the stat fires on the fresh copy");
        }

        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "clutch_lib");
        await Assert.That(vm.Gallery.Single(g => g.Title == "Your defines").Items.Select(i => i.Name)).IsEquivalentTo(["post_plant_kill", "post_plant_kill_2"])
            .Because("from the other side the copies are the shelf, and a ruleset's own defines are its nodes");
    }

    [Test]
    public async Task TheGallery_OffersStatsViewsGatesAndPrefabs()
    {
        IReadOnlyList<GalleryGroup> gallery = CanvasGallery.Build(EmbeddedCatalog.Root);
        using (Assert.Multiple())
        {
            await Assert.That(gallery.Select(g => g.Title)).IsEquivalentTo(["Primitives", "Stats", "Views", "Gates", "Prefabs", "Highlights", "Surfacing"]);
            await Assert.That(gallery.Single(g => g.Title == "Views").Items.Select(i => i.Name)).Contains("kill");
            await Assert.That(gallery.Single(g => g.Title == "Gates").Items.Select(i => i.Name)).Contains("round.active");
            await Assert.That(gallery.SelectMany(g => g.Items).Where(i => i.Kind is not (GalleryItemKind.Gate or GalleryItemKind.Event)).All(i => i.Body is not null))
                .IsTrue().Because("everything but a gate or a floating event places an entry, so it needs a body");
        }
    }

    [Test]
    public async Task APrefabDroppedOnTheCanvas_IsAddedWhereItLanded()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
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
    public async Task ATallyDroppedOnTheCanvas_LoadsWithItsThresholds_AndIsDrawn()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        GalleryItem tally = vm.Gallery.Single(g => g.Title == "Stats").Items.Single(i => i.Name == "tally");

        vm.PlaceItem(tally, new Point(300, 300));

        CanvasNodeViewModel? added = vm.Nodes.SingleOrDefault(n => n.Id == "new_tally");
        using (Assert.Multiple())
        {
            await Assert.That(added).IsNotNull().Because("a body the loader rejects leaves no node and no diagnostic");
            await Assert.That(vm.Diagnostics).IsEmpty();
            await Assert.That(added!.Thresholds!.Rows.Select(r => r.Min + ":" + r.Target)).IsEquivalentTo(["3:rounds_3k"]);
            await Assert.That(added.Location).IsEqualTo(new Point(300, 300));
        }
    }

    [Test]
    public async Task AStatKindDroppedTwice_GetsDistinctIds()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
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
        using RulesTree rules = RulesTree.WithUser("player_round");
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
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        GalleryItem death = vm.Gallery.Single(g => g.Title == "Views").Items.Single(i => i.Name == "death");

        vm.DropOnNode(death, vm.Nodes.Single(n => n.Id == "round_kills"));
        await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("player_round")), "round_kills", "count")).IsEqualTo("death");

        vm.DropOnNode(death, vm.Nodes.Single(n => n.Id == "round_kast"));
        await Assert.That(vm.EditStatus).Contains("fires on no view");
    }

    // The gallery flag read round_kills, so a flag placed in a ruleset without one arrived with an error.
    [Test]
    public async Task AFlagPlacedWhereNothingDeclaresRoundKills_StartsOnASurvivedTest_ThatResolves()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("bare", "ruleset: bare\ntitle: Bare\nfor: each_player\n\nstats: {}\n");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "bare";
        vm.OnActivated(null!);
        GalleryItem flag = vm.Gallery.Single(g => g.Title == "Stats").Items.Single(i => i.Name == "flag");

        vm.PlaceItem(flag, new Point(0, 0));
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("bare")), "new_flag", "flag.when")).IsEqualTo("player.survived");
            await Assert.That(vm.DiagnosticEntries).IsEmpty();
        }

        using RulesTree kast = RulesTree.WithUser("player_round");
        CanvasTabViewModel withKills = kast.NewViewModel();
        withKills.OnActivated(null!);
        withKills.PlaceItem(flag, new Point(0, 0));
        await Assert.That(RulesetEdits.Field(File.ReadAllText(kast.UserPath("player_round")), "new_flag", "flag.when")).IsEqualTo("round_kills > 0")
            .Because("a ruleset that declares round_kills keeps the gallery's own example");
    }
}
