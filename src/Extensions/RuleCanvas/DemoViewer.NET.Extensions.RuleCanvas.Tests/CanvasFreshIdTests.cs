#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The id a new entry gets is free in the document itself: a key outside the focused slice, or one the
///     loader dropped and the canvas does not draw, is taken as much as a drawn node's.
/// </summary>
public class CanvasFreshIdTests
{
    private static GalleryItem KillView(CanvasTabViewModel vm) =>
        vm.Gallery.SelectMany(g => g.Items).Single(i => i.Kind == GalleryItemKind.View && i.Name == "kill");

    private static string Text(RulesTree rules) => File.ReadAllText(rules.UserPath("player_round"));

    [Test]
    [Arguments("new_kill:\n  count: kill\n  per: round\n", true)]
    [Arguments("new_kill: 5\n", false)]
    public async Task DroppingAView_WhoseIdIsTakenOffScreen_PicksTheNextFreeId(string taken, bool focus)
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        string path = rules.UserPath("player_round");
        File.WriteAllText(path, RulesetEdits.AddStat(Text(rules), taken));
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        Point? existing = vm.Nodes.SingleOrDefault(n => n.Id == "new_kill")?.Location;
        await Assert.That(existing is null).IsEqualTo(!focus).Because("a dropped entry draws no node");
        if (focus)
        {
            vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_deaths"));
            await Assert.That(vm.Nodes.Any(n => n.Id == "new_kill")).IsFalse().Because("the slice leaves new_kill out");
        }

        Point drop = new(4321, 1234);
        vm.PlaceItem(KillView(vm), drop);

        string text = Text(rules);
        using (Assert.Multiple())
        {
            await Assert.That(vm.EditStatus).IsEmpty();
            await Assert.That(RulesetEdits.EntryIds(text)).Contains("new_kill_2");
            await Assert.That(vm.Nodes.Single(n => n.Id == "new_kill_2").Location).IsEqualTo(drop);
        }

        if (existing is { } was)
        {
            await Assert.That(vm.Nodes.Single(n => n.Id == "new_kill").Location).IsEqualTo(was).Because("the existing entry stays where it was");
        }
    }

    [Test]
    [Arguments("highlight")]
    [Arguments("stat")]
    public async Task AWriteUnderATakenId_IsRefused_AndTheFileIsUnchanged(string kind)
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        string path = rules.UserPath("player_round");
        File.WriteAllText(path, RulesetEdits.AddHighlight(Text(rules), "clutch", "when: round_kills > 0"));
        string before = Text(rules);
        RulesetDocumentSession session = new();
        session.Open(rules.Load().Entries.Single(e => e.Id == "player_round"));

        EditOutcome outcome = kind == "highlight"
            ? session.Apply(CanvasEditSemantics.PlaceItem(new GalleryItem(GalleryItemKind.Highlight, "clutch", "clutch", "", "when: round_kills > 0"), "clutch"))
            : session.Apply(CanvasEditSemantics.AddStat("kill", "round_kills"));
        using (Assert.Multiple())
        {
            await Assert.That(outcome.IsRefused).IsTrue();
            await Assert.That(outcome.Message).Contains("Duplicate key");
            await Assert.That(Text(rules)).IsEqualTo(before);
        }
    }
}
