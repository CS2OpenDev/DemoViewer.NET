#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     What the face reads after the owner's manual checks on a user copy of player_round, stated exactly so
///     the manual script can say what to expect.
/// </summary>
public class CanvasOwnerChecksTests
{
    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id);

    private static string Text(RulesTree rules) => File.ReadAllText(rules.UserPath("player_round"));

    // The death view binds the player as the victim, so "as victim" is the right chip, not a regression.
    [Test]
    public async Task PickingDeathOnRoundKills_WritesCountDeath_AndTheFaceReadsAsVictim()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel before = Node(vm, "round_kills");
            using (Assert.Multiple())
            {
                await Assert.That(before.Chips).IsEquivalentTo(["on kill", "as killer", "per round"]);
                await Assert.That(before.FixedRows).IsEquivalentTo(["not a suicide"]);
                await Assert.That(before.Trigger!.Selected!.Display).IsEqualTo("kill  (player_death)");
            }

            CanvasTriggerViewModel trigger = before.Trigger!;
            trigger.Selected = trigger.Options.Single(o => o.Written == "death");

            CanvasNodeViewModel after = Node(vm, "round_kills");
            string text = Text(rules);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "round_kills", "count")).IsEqualTo("death");
                await Assert.That(text).Contains("round_kills:\n    count: death\n    match: { enemy: true }\n    per: round\n");
                await Assert.That(after.Trigger!.Selected!.Display).IsEqualTo("death  (player_death)");
                await Assert.That(after.Chips).IsEquivalentTo(["on death", "as victim", "per round"]);
                await Assert.That(after.VisibleChips).IsEquivalentTo(["as victim"]);
                await Assert.That(after.Facets.Select(f => f.Name)).IsEquivalentTo(["enemy"]).Because("the death view knows enemy too, so the facet stays");
                await Assert.That(after.FixedRows).IsEmpty().Because("the not-a-suicide row belongs to the kill view");
                await Assert.That(vm.Diagnostics).IsEmpty();
            }
        }
    }
}
