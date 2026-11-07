#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The write decisions as data: each gesture or face edit answers with the transform it would run and
///     the words for it, or a refusal with the reason, and none of them touches a file. The transforms run
///     here over the shipped player_round text, so the text each one produces is checked without a view model.
/// </summary>
public class CanvasEditSemanticsTests
{
    private static readonly string _playerRound = File.ReadAllText(Path.Combine(TempRules.RepoRulesDirectory, "player_round.rules.yaml"));

    private static CanvasNodeViewModel Stat(string id, string kind, string? view = null, bool authorable = true) => new()
    {
        Id = id, Kind = kind, Ruleset = "player_round", View = view, IsAuthorable = authorable
    };

    private static CanvasNodeViewModel Engine(string ruleset, string id, string kind) => new()
    {
        Id = id, Kind = kind, Ruleset = ruleset, IsAuthorable = false
    };

    private static CanvasNodeViewModel Highlight(string id) => new() { Id = id, Kind = "highlight", Ruleset = "player_round" };

    private static string Run(EditRequest request) => request.Transform!(_playerRound);

    [Test]
    public async Task TheGateTable_NamesEveryGateOnceUnderBothSpellings()
    {
        using (Assert.Multiple())
        {
            await Assert.That(CanvasEditSemantics.GatePaths.Select(g => g.Path)).IsEquivalentTo(CanvasGallery.GatePaths);
            await Assert.That(CanvasEditSemantics.GatePaths.Select(g => g.EngineName)).IsEquivalentTo(
                ["RoundActive", "MatchLive", "Alive", "NoDeathsYet", "BombWasPlanted", "Traded", "Survived"]);
            await Assert.That(CanvasEditSemantics.GatePathOf("MatchLive")).IsEqualTo("match.live");
            await Assert.That(CanvasEditSemantics.GatePathOf("player_team")).IsNull();
            await Assert.That(CanvasNodeViewModel.GateOptions[0]).IsEmpty().Because("the picker offers none first");
            await Assert.That(CanvasNodeViewModel.GateOptions.Skip(1)).IsEquivalentTo(CanvasGallery.GatePaths);
        }
    }

    [Test]
    public async Task AContextFlag_GatesTheTarget_AndAnUnnamedOneIsRefused()
    {
        EditRequest gate = CanvasEditSemantics.Connect(Engine("~context", "MatchLive", "context"), Stat("round_kills", "count", "kill"));
        EditRequest unnamed = CanvasEditSemantics.Connect(Engine("~context", "player_team", "context"), Stat("round_kills", "count", "kill"));
        using (Assert.Multiple())
        {
            await Assert.That(gate.Description).IsEqualTo("gate round_kills on match.live");
            await Assert.That(RulesetEdits.Field(Run(gate), "round_kills", "while")).IsEqualTo("match.live");
            await Assert.That(unnamed.IsRefused).IsTrue();
            await Assert.That(unnamed.Refusal).Contains("not a gate");
        }
    }

    [Test]
    public async Task AnEventThatFiresSeveralViews_IsRefusedWithTheChoice()
    {
        EditRequest request = CanvasEditSemantics.Connect(Engine("~event", "player_death", "event"), Stat("round_kills", "count", "kill"));
        using (Assert.Multiple())
        {
            await Assert.That(request.IsRefused).IsTrue();
            await Assert.That(request.Refusal).Contains("kill");
            await Assert.That(request.Refusal).Contains("death");
        }
    }

    [Test]
    public async Task AnEventThatFiresOneView_RetargetsACount_AndACompute_IsRefused()
    {
        string eventName = EmbeddedCatalog.Root.Views.First(v => v.Name == "damage_dealt").Event;
        IReadOnlyList<string> views = CanvasEditSemantics.ViewsFiredBy(eventName);
        EditRequest count = CanvasEditSemantics.Connect(Engine("~event", eventName, "event"), Stat("round_kills", "count", "kill"));
        EditRequest compute = CanvasEditSemantics.Connect(Engine("~event", eventName, "event"), Stat("round_kast", "compute"));
        using (Assert.Multiple())
        {
            await Assert.That(views.Count).IsGreaterThanOrEqualTo(1);
            if (views.Count == 1)
            {
                await Assert.That(RulesetEdits.Field(Run(count), "round_kills", "count")).IsEqualTo(views[0]);
            }

            await Assert.That(compute.IsRefused).IsTrue();
        }
    }

    [Test]
    public async Task AWireIntoAnEngineNode_IsRefused()
    {
        EditRequest request = CanvasEditSemantics.Connect(Stat("round_kills", "count", "kill"), Engine("~context", "MatchLive", "context"));
        await Assert.That(request.IsRefused).IsTrue();
        await Assert.That(request.Refusal).Contains("your own ruleset");
    }

    [Test]
    public async Task AnEnrichment_BindsTheBoolFacetThatReadsIt_OrIsRefused()
    {
        EditRequest bound = CanvasEditSemantics.Connect(Engine("~enrichment", "enrich.kill.was_enemy_kill", "enrich"),
            Stat("round_deaths", "count", "death"));
        EditRequest none = CanvasEditSemantics.Connect(Engine("~enrichment", "enrich.nothing.reads_this", "enrich"),
            Stat("round_deaths", "count", "death"));
        using (Assert.Multiple())
        {
            await Assert.That(bound.Description).IsEqualTo("set round_deaths.match.enemy");
            await Assert.That(Run(bound)).Contains("round_deaths:\n    count: death\n    while: match.live\n    per: round\n    match: { enemy: true }");
            await Assert.That(none.IsRefused).IsTrue();
            await Assert.That(none.Refusal).Contains("where:");
        }
    }

    [Test]
    public async Task AStatIntoAFlag_ExtendsItsWhen_AndIntoAnythingElse_IsRefused()
    {
        EditRequest flag = CanvasEditSemantics.Connect(Stat("round_damage", "sum", "damage_dealt"), Stat("round_kast", "flag"));
        EditRequest count = CanvasEditSemantics.Connect(Stat("round_damage", "sum", "damage_dealt"), Stat("round_kills", "count", "kill"));
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Field(Run(flag), "round_kast", "flag.when"))
                .IsEqualTo("round_kills > 0 or round_kast_assists > 0 or player.survived or player.traded or round_damage > 0");
            await Assert.That(count.IsRefused).IsTrue();
            await Assert.That(count.Refusal).Contains("flag's when:");
        }
    }

    [Test]
    public async Task AStatIntoAHighlight_JoinsItsWhen_AndAnEngineNode_IsRefused()
    {
        string withHighlight = RulesetEdits.AddHighlight(_playerRound, "h", "when: player.survived\nper: round");
        EditRequest join = CanvasEditSemantics.Connect(Stat("round_survived", "count"), Highlight("h"));
        EditRequest scaffold = CanvasEditSemantics.Connect(Engine("~context", "MatchLive", "context"), Highlight("h"));
        EditRequest foreign = CanvasEditSemantics.Connect(Engine("kast", "round_kast", "flag"), Highlight("h"));
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.HighlightField(join.Transform!(withHighlight), "h", "when")).IsEqualTo("player.survived and round_survived > 0");
            await Assert.That(scaffold.IsRefused).IsTrue();
            await Assert.That(scaffold.Refusal).Contains("reads stats");
            await Assert.That(RulesetEdits.HighlightField(foreign.Transform!(withHighlight), "h", "when"))
                .IsEqualTo("player.survived and kast.round_kast > 0").Because("a stat of another ruleset is read by its qualified path");
        }
    }

    [Test]
    public async Task Disconnect_UngatesOrUnbinds_AndRefusesATriggerWire()
    {
        CanvasNodeViewModel matchLive = Engine("~context", "MatchLive", "context");
        CanvasNodeViewModel enrichment = Engine("~enrichment", "enrich.kill.was_enemy_kill", "enrich");
        CanvasNodeViewModel death = Engine("~event", "player_death", "event");
        CanvasNodeViewModel deaths = Stat("round_deaths", "count", "death");
        CanvasNodeViewModel kills = Stat("round_kills", "count", "kill");
        CanvasNodeViewModel foreign = Stat("round_kast", "flag", authorable: false);

        static CanvasConnectionViewModel Wire(CanvasNodeViewModel from, CanvasNodeViewModel to, bool isRead = false) =>
            new(new CanvasConnectorViewModel { Title = "", Owner = from }, new CanvasConnectorViewModel { Title = "", Owner = to }, isRead: isRead);

        EditRequest ungate = CanvasEditSemantics.Disconnect(Wire(matchLive, deaths));
        EditRequest unbind = CanvasEditSemantics.Disconnect(Wire(enrichment, kills, isRead: true));
        EditRequest trigger = CanvasEditSemantics.Disconnect(Wire(death, kills));
        EditRequest ignored = CanvasEditSemantics.Disconnect(Wire(matchLive, foreign));
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Field(Run(ungate), "round_deaths", "while")).IsNull();
            await Assert.That(RulesetEdits.Field(Run(unbind), "round_kills", "match.enemy")).IsNull();
            await Assert.That(trigger.IsRefused).IsTrue();
            await Assert.That(trigger.Refusal).Contains("own definition");
            await Assert.That(ignored.IsRefused).IsTrue();
            await Assert.That(ignored.Refusal).IsNull().Because("a wire into another ruleset's node is not reported, there is nothing to say");
        }
    }

    [Test]
    public async Task DropOnNode_GatesRetargetsOrRefuses()
    {
        GalleryItem gate = new(GalleryItemKind.Gate, "round.active", "round.active", "");
        GalleryItem view = new(GalleryItemKind.View, "death", "death", "", "count: death\nper: round");
        GalleryItem prefab = new(GalleryItemKind.Prefab, "deaths", "Deaths", "", "count: death\nper: round");
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Field(Run(CanvasEditSemantics.DropOnNode(gate, Stat("round_kills", "count", "kill"))), "round_kills", "while"))
                .IsEqualTo("round.active");
            await Assert.That(RulesetEdits.Field(Run(CanvasEditSemantics.DropOnNode(view, Stat("round_kills", "count", "kill"))), "round_kills", "count"))
                .IsEqualTo("death");
            await Assert.That(RulesetEdits.Field(Run(CanvasEditSemantics.DropOnNode(view, Stat("round_damage", "sum", "damage_dealt"))), "round_damage", "on"))
                .IsEqualTo("death");
            await Assert.That(CanvasEditSemantics.DropOnNode(view, Stat("round_kast", "flag")).Refusal).Contains("fires on no view");
            await Assert.That(CanvasEditSemantics.DropOnNode(prefab, Stat("round_kills", "count", "kill")).Refusal).Contains("on the canvas");
            await Assert.That(CanvasEditSemantics.DropOnNode(gate, Highlight("h")).Refusal).Contains("stat of your own ruleset");
            await Assert.That(CanvasEditSemantics.DropOnNode(gate, Stat("round_kills", "count", "kill", authorable: false)).IsRefused).IsTrue();
        }
    }

    [Test]
    public async Task PlaceItem_IndentsTheBody_NamesTheEntry_AndRefusesAGate()
    {
        GalleryItem prefab = new(GalleryItemKind.Prefab, "opening_kills", "Opening kills", "",
            "count: kill\nmatch: { enemy: true }\nwhile: round.no_deaths_yet\nper: match");
        GalleryItem highlight = new(GalleryItemKind.Highlight, "multi_kill", "Multi-kill", "", "when: round_kills >= 3\nper: round");
        GalleryItem count = new(GalleryItemKind.Stat, "count", "count", "", "count: kill\nper: round");
        GalleryItem gate = new(GalleryItemKind.Gate, "round.active", "round.active", "");
        using (Assert.Multiple())
        {
            await Assert.That(CanvasEditSemantics.PlacedIdFor(prefab)).IsEqualTo("opening_kills");
            await Assert.That(CanvasEditSemantics.PlacedIdFor(highlight)).IsEqualTo("multi_kill");
            await Assert.That(CanvasEditSemantics.PlacedIdFor(count)).IsEqualTo("new_count");
            await Assert.That(CanvasEditSemantics.FreshId("new_count", ["new_count", "new_count_2"])).IsEqualTo("new_count_3");
            await Assert.That(Run(CanvasEditSemantics.PlaceItem(prefab, "opening_kills")))
                .Contains("opening_kills:\n    count: kill\n    match: { enemy: true }\n    while: round.no_deaths_yet\n    per: match");
            await Assert.That(Run(CanvasEditSemantics.PlaceItem(highlight, "multi_kill")))
                .Contains("\nhighlights:\n  multi_kill:\n    when: round_kills >= 3\n    per: round\n");
            await Assert.That(Run(CanvasEditSemantics.AddStat("kill", "new_kill"))).Contains("new_kill:\n    count: kill\n    per: round");
            await Assert.That(CanvasEditSemantics.PlaceItem(gate, "x").Refusal).Contains("onto a stat");
        }
    }

    [Test]
    public async Task WriteField_SetsRemovesOrRefuses_PerKey()
    {
        CanvasNodeViewModel kast = Stat("round_kast", "flag");
        CanvasNodeViewModel kills = Stat("round_kills", "count", "kill");
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Field(Run(CanvasEditSemantics.WriteField(kills, "per", "match")), "round_kills", "per")).IsEqualTo("match");
            await Assert.That(RulesetEdits.Field(Run(CanvasEditSemantics.WriteField(kills, "while", "round.active")), "round_kills", "while")).IsEqualTo("round.active");
            await Assert.That(RulesetEdits.Field(Run(CanvasEditSemantics.WriteField(Stat("round_deaths", "count", "death"), "while", null)), "round_deaths", "while")).IsNull();
            await Assert.That(RulesetEdits.Field(Run(CanvasEditSemantics.WriteField(kast, "when", "round_kills > 1")), "round_kast", "flag.when")).IsEqualTo("round_kills > 1");
            await Assert.That(CanvasEditSemantics.WriteField(kast, "when", null).Refusal).Contains("cannot be empty");
            await Assert.That(CanvasEditSemantics.WriteField(Stat("c", "compute"), "compute", null).Refusal).Contains("cannot be empty");
            await Assert.That(CanvasEditSemantics.WriteField(kills, "per", null).IsRefused).IsTrue().Because("a scope cannot be removed");
            await Assert.That(CanvasEditSemantics.WriteHighlightField(Highlight("h"), "when", null).Refusal).Contains("highlight's when cannot be empty");
            await Assert.That(CanvasEditSemantics.WriteHighlightField(Highlight("h"), "score", null).Description).IsEqualTo("remove h.score");
            await Assert.That(CanvasEditSemantics.WriteField(Highlight("h"), "title", "t").Description).IsEqualTo("set h.title");
        }
    }

    [Test]
    public async Task WriteFacet_SetsOrRemoves()
    {
        CanvasNodeViewModel kills = Stat("round_kills", "count", "kill");
        using (Assert.Multiple())
        {
            await Assert.That(Run(CanvasEditSemantics.WriteFacet(kills, "headshot", "true"))).Contains("match: { enemy: true, headshot: true }");
            await Assert.That(RulesetEdits.Field(Run(CanvasEditSemantics.WriteFacet(kills, "enemy", null)), "round_kills", "match.enemy")).IsNull();
            await Assert.That(CanvasEditSemantics.WriteFacet(kills, "enemy", null).Description).IsEqualTo("remove round_kills.match.enemy");
        }
    }
}
