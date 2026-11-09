#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Typed ports: every authored node has one input per kind it takes and one typed output; wires land on
///     the port of their kind; a port decides what a dropped wire may be; a held wire dims the ports that
///     cannot take it.
/// </summary>
public class CanvasPortTests
{
    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id && !n.IsScaffold);

    private static IReadOnlyList<string> InputTitles(CanvasNodeViewModel node) => [.. node.Inputs.Select(i => i.Title)];

    [Test]
    public async Task AuthoredNodes_CarryOnePortPerKindTheyTake_AndATypedOutput()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasNodeViewModel kills = Node(vm, "round_kills");
        CanvasNodeViewModel kast = Node(vm, "round_kast");
        using (Assert.Multiple())
        {
            await Assert.That(InputTitles(kills)).IsEquivalentTo(["on", "while", "reads"]).Because("a count fires on a view, can be gated, and its body can read");
            await Assert.That(kills.Inputs.Select(i => i.Kind)).IsEquivalentTo([PortKind.Trigger, PortKind.Gate, PortKind.Read]);
            await Assert.That(kills.Outputs.Single().Kind).IsEqualTo(PortKind.Value);
            await Assert.That(kills.Outputs.Single().Title).IsEqualTo("int");
            await Assert.That(InputTitles(kast)).IsEquivalentTo(["when", "while", "reads"]).Because("an expression flag has no trigger; its when is the logic input");
            await Assert.That(kast.Outputs.Single().Title).IsEqualTo("bool");
            await Assert.That(vm.Nodes.Single(n => n.Ruleset == "~event" && n.Id == "player_death").Outputs.Single().Kind).IsEqualTo(PortKind.Fire);
            await Assert.That(vm.Nodes.First(n => n.Ruleset == "~context").Outputs.Single().Kind).IsEqualTo(PortKind.Flag);
        }
    }

    [Test]
    public async Task Wires_LandOnThePortOfTheirKind()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasNodeViewModel deaths = Node(vm, "round_deaths");
        CanvasNodeViewModel kast = Node(vm, "round_kast");
        List<CanvasConnectionViewModel> intoDeaths = [.. vm.Connections.Where(w => ReferenceEquals(w.Target.Owner, deaths))];
        List<CanvasConnectionViewModel> intoKast = [.. vm.Connections.Where(w => ReferenceEquals(w.Target.Owner, kast))];
        using (Assert.Multiple())
        {
            await Assert.That(intoDeaths.Single(w => w.IsGate).Target.Kind).IsEqualTo(PortKind.Gate).Because("the match.live gate lands on while");
            await Assert.That(intoDeaths.Single(w => !w.IsGate && !w.IsRead).Target.Kind).IsEqualTo(PortKind.Trigger).Because("the death event lands on on");
            await Assert.That(intoKast.Where(w => w.IsOr).Select(w => w.Target.Kind).Distinct()).IsEquivalentTo([PortKind.Logic]).Because("or inputs land on when");
            await Assert.That(intoKast.Where(w => w.IsOr).Select(w => w.Target).Distinct().Count()).IsEqualTo(1).Because("one port per kind: every or input shares the when port");
        }
    }

    [Test]
    public async Task APort_DecidesWhatADroppedWireMayBe()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasNodeViewModel kills = Node(vm, "round_kills");
        CanvasNodeViewModel kast = Node(vm, "round_kast");
        CanvasNodeViewModel matchLive = vm.Nodes.Single(n => n.Ruleset == "~context" && n.Id == "MatchLive");
        CanvasNodeViewModel death = vm.Nodes.Single(n => n.Ruleset == "~event" && n.Id == "player_death");
        using (Assert.Multiple())
        {
            await Assert.That(CanvasEditSemantics.Connect(matchLive, kills, PortKind.Trigger).Refusal).Contains("takes an event, a view or a define");
            await Assert.That(CanvasEditSemantics.Connect(death, kills, PortKind.Gate).Refusal).Contains("takes a context flag or one of your bool stats");
            await Assert.That(CanvasEditSemantics.Connect(kast, kills, PortKind.Gate).IsRefused).IsFalse().Because("a sibling bool stat is a gate");
            await Assert.That(CanvasEditSemantics.ConnectPreview(kast, kills, PortKind.Gate)).Contains("round_kast");
        }

        vm.Connect(kast, kills, PortKind.Gate);
        await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("player_round")), "round_kills", "while")).IsEqualTo("round_kast")
            .Because("the gate port writes the flag as while:");
    }

    [Test]
    public async Task AHeldWire_DimsThePortsThatCannotTakeIt()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasNodeViewModel kills = Node(vm, "round_kills");
        CanvasConnectorViewModel flag = vm.Nodes.Single(n => n.Ruleset == "~context" && n.Id == "MatchLive").Outputs.Single();

        vm.Pending.Source = flag;
        using (Assert.Multiple())
        {
            await Assert.That(kills.Inputs.Single(i => i.Kind == PortKind.Trigger).IsCompatible).IsFalse();
            await Assert.That(kills.Inputs.Single(i => i.Kind == PortKind.Gate).IsCompatible).IsTrue();
            await Assert.That(kills.Inputs.Single(i => i.Kind == PortKind.Read).IsCompatible).IsTrue();
            await Assert.That(kills.Inputs.Single(i => i.Kind == PortKind.Trigger).Opacity).IsLessThan(1);
        }

        vm.Pending.Source = null;
        await Assert.That(kills.Inputs.All(i => i.IsCompatible)).IsTrue().Because("a dropped or cancelled wire lights every port again");
    }
}
