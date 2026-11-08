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
    // A wire dropped on empty canvas starts a node: from a stat, a highlight, a flag or a compute reading it;
    // from an event, a count or a define per view; from a define, a count; from a highlight, nothing.
    [Test]
    public async Task AWireDroppedOnEmptyCanvas_OffersNodesThatReadTheSource_AndPlacesThePick()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasNodeViewModel kills = vm.Nodes.Single(n => n.Id == "round_kills");
        IReadOnlyList<GalleryItem> fromStat = vm.WireDropOptions(kills);
        CanvasNodeViewModel death = vm.Nodes.Single(n => n.Ruleset == "~event" && n.Id == "player_death");
        IReadOnlyList<GalleryItem> fromEvent = vm.WireDropOptions(death);
        using (Assert.Multiple())
        {
            await Assert.That(fromStat.Select(o => o.Title)).IsEquivalentTo(["New highlight when round_kills > 0", "New flag when round_kills > 0", "New compute from round_kills"]);
            await Assert.That(fromEvent.Select(o => o.Title)).Contains("New count on kill");
            await Assert.That(fromEvent.Select(o => o.Title)).Contains("New define on death");
            await Assert.That(kills.Outputs.Count).IsEqualTo(1).Because("a stat has its output port before anything reads it");
        }

        CanvasNodeViewModel? dropped = null;
        vm.WireDropped += n => dropped = n;
        vm.ConnectCommand.Execute(((object)kills.Outputs[0], (object?)null));
        await Assert.That(dropped).IsSameReferenceAs(kills);

        vm.PlaceItem(fromStat[0], new Avalonia.Point(900, 300));
        string text = File.ReadAllText(rules.UserPath("player_round"));
        CanvasNodeViewModel highlight = vm.Nodes.Single(n => n.Id == "round_kills_highlight");
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.HighlightField(text, "round_kills_highlight", "when")).IsEqualTo("round_kills > 0");
            await Assert.That(highlight.Inputs.Count).IsEqualTo(1);
            await Assert.That(vm.Connections.Any(w => ReferenceEquals(w.Source.Owner, vm.Nodes.Single(n => n.Id == "round_kills")) && ReferenceEquals(w.Target.Owner, highlight))).IsTrue()
                .Because("the new highlight is wired from the stat it reads");
            await Assert.That(vm.WireDropOptions(highlight)).IsEmpty().Because("nothing starts from a highlight");
        }
    }

    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id);

    [Test]
    public async Task AContextFlagWiredIntoAStat_BecomesItsWhile()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
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
        (RulesTree rules, CanvasTabViewModel vm) = Open();
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
        (RulesTree rules, CanvasTabViewModel vm) = Open();
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
    public async Task AStatWiredIntoAScalarFlag_RewritesItsExpression_AndIntoATriggerFlag_IsRefused()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("flags_lab", """
            ruleset: flags_lab
            title: Flags lab
            for: each_player

            stats:
              kills:
                count: kill
                per: round
              deaths:
                count: death
                per: round
              hot:
                flag: "kills > 0"
                per: round
              armed:
                flag: true
                on: kill
                per: round
            """);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "flags_lab";
        vm.OnActivated(null!);
        string Text() => File.ReadAllText(rules.UserPath("flags_lab"));

        vm.Connect(Node(vm, "deaths"), Node(vm, "hot"));
        using (Assert.Multiple())
        {
            await Assert.That(Text()).Contains("  hot:\n    flag: \"kills > 0 or deaths > 0\"\n    per: round");
            await Assert.That(vm.Diagnostics).IsEmpty();
            await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Id == "deaths" && c.Target.Owner?.Id == "hot")).IsTrue();
        }

        string before = Text();
        vm.Connect(Node(vm, "deaths"), Node(vm, "armed"));
        using (Assert.Multiple())
        {
            await Assert.That(Text()).IsEqualTo(before).Because("a trigger flag has no when: the loader reads");
            await Assert.That(vm.EditStatus).Contains("no when:");
            await Assert.That(CanvasEditSemantics.ConnectPreview(Node(vm, "deaths"), Node(vm, "armed"))).Contains("no when:");
        }
    }

    [Test]
    public async Task RemovingTheGateWire_TakesTheWhileWithIt()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
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
        (RulesTree rules, CanvasTabViewModel vm) = Open();
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
}
