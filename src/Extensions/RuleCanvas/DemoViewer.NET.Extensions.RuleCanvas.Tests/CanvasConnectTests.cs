#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The connect gesture: a wire from an engine node to a stat writes the field the wire stands for, and
///     removing one takes the field away. The chain redraws from the file, so each case checks both the
///     text and the node that now stands in the chain.
/// </summary>
public class CanvasConnectTests
{
    private static (TempRules Rules, CanvasTabViewModel Vm) Open()
    {
        TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id);

    [Test]
    public async Task AContextFlagWiredIntoAStat_BecomesItsWhile()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.Connect(Node(vm, "MatchLive"), Node(vm, "round_kills"));
            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "round_kills", "while")).IsEqualTo("match.live");
                await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Id == "MatchLive" && c.Target.Owner?.Id == "round_kills" && !c.IsRead))
                    .IsTrue().Because("the gate now stands in front of the stat");
            }
        }
    }

    [Test]
    public async Task AnEnrichmentWiredIntoACount_BindsTheFacetThatReadsIt()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.Connect(Node(vm, "enrich.kill.was_enemy_kill"), Node(vm, "round_deaths"));
            string text = File.ReadAllText(rules.UserPath("player_round"));
            await Assert.That(text).Contains("round_deaths:\n    count: death\n    while: match.live\n    per: round\n    match: { enemy: true }");
            await Assert.That(Node(vm, "round_deaths").Facets.Select(f => f.Name)).Contains("enemy");
        }
    }

    [Test]
    public async Task AStatWiredIntoAFlag_ExtendsItsWhen()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.Connect(Node(vm, "round_damage"), Node(vm, "round_kast"));
            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "round_kast", "flag.when"))
                    .IsEqualTo("round_kills > 0 or round_kast_assists > 0 or player.survived or player.traded or round_damage > 0");
                await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Id == "round_damage" && c.Target.Owner?.Id == "round_kast")).IsTrue();
            }
        }
    }

    [Test]
    public async Task AnEventThatFiresSeveralViews_IsRefusedWithTheChoice()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = File.ReadAllText(rules.UserPath("player_round"));
            vm.Connect(Node(vm, "player_death"), Node(vm, "round_kills"));
            using (Assert.Multiple())
            {
                await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
                await Assert.That(vm.EditStatus).Contains("kill");
                await Assert.That(vm.EditStatus).Contains("death");
            }
        }
    }

    [Test]
    public async Task AWireIntoAnEngineNode_IsRefused()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = File.ReadAllText(rules.UserPath("player_round"));
            vm.Connect(Node(vm, "round_kills"), Node(vm, "MatchLive"));
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
            await Assert.That(vm.EditStatus).Contains("your own ruleset");
        }
    }

    [Test]
    public async Task RemovingTheGateWire_TakesTheWhileWithIt()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasConnectionViewModel gate = vm.Connections.Single(c =>
                c.Source.Owner?.Id == "MatchLive" && c.Target.Owner?.Id == "round_deaths" && !c.IsRead);
            vm.DisconnectCommand.Execute(gate);
            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(text, "round_deaths", "while")).IsNull();
                await Assert.That(vm.Connections.Any(c => c.Target.Owner?.Id == "round_deaths" && c.Source.Owner?.Id == "player_death"))
                    .IsTrue().Because("ungated, the stat fires straight from the event");
            }
        }
    }

    [Test]
    public async Task RemovingAnEnrichmentRead_TakesTheFacetWithIt()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasConnectionViewModel read = vm.Connections.Single(c =>
                c.Source.Owner?.Id == "enrich.kill.was_enemy_kill" && c.Target.Owner?.Id == "round_kills");
            vm.DisconnectCommand.Execute(read);
            string text = File.ReadAllText(rules.UserPath("player_round"));
            await Assert.That(RulesetEdits.Field(text, "round_kills", "match.enemy")).IsNull();
            await Assert.That(Node(vm, "round_kills").Facets).IsEmpty();
        }
    }

    [Test]
    public async Task RemovingATriggerWire_IsRefused()
    {
        (TempRules rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = File.ReadAllText(rules.UserPath("player_round"));
            CanvasConnectionViewModel trigger = vm.Connections.Single(c =>
                c.Source.Owner?.Id == "player_death" && c.Target.Owner?.Id == "round_kills");
            vm.DisconnectCommand.Execute(trigger);
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
            await Assert.That(vm.EditStatus).Contains("own definition");
        }
    }
}
