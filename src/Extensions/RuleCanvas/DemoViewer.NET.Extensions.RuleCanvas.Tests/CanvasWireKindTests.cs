#region

using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     A wire carries its meaning as flags the template turns into classes: read, gate, and, or. The theme
///     colours and and or wires, so a logic input must say which connective it is or it draws as a write.
/// </summary>
public class CanvasWireKindTests
{
    [Test]
    public async Task ALogicInput_SaysItsConnective_AndNoWireSaysTwo()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasConnectionViewModel orWire = vm.Connections.Single(c => c.Source.Owner?.Id == "round_kills" && c.Target.Owner?.Id == "round_kast");
        using (Assert.Multiple())
        {
            await Assert.That(orWire.IsOr).IsTrue().Because("round_kast disjoins round_kills, and the label already says or");
            await Assert.That(orWire.Text).StartsWith("or ");
            await Assert.That(orWire.IsAnd || orWire.IsRead || orWire.IsGate).IsFalse();
            await Assert.That(vm.Connections.Count(c => c.IsOr)).IsEqualTo(vm.Connections.Count(c => c.Text.StartsWith("or ", StringComparison.Ordinal)))
                .Because("every wire labelled or carries the class, and only those");
            await Assert.That(vm.Connections.Count(c => c.IsAnd)).IsEqualTo(vm.Connections.Count(c => c.Text.StartsWith("and ", StringComparison.Ordinal)));
            await Assert.That(vm.Connections.Any(c => c.IsAnd && c.IsOr)).IsFalse();
            await Assert.That(vm.Connections.Any(c => (c.IsAnd || c.IsOr) && (c.IsRead || c.IsGate))).IsFalse()
                .Because("the connective classes are exclusive with read and gate, so one style wins");
            await Assert.That(vm.Connections.Where(c => c.IsRead)).IsNotEmpty();
        }
    }
}
