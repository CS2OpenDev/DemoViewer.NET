#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The faces of player_round: typed per kind from the document, and rendered by the factory into the
///     chips, expression and editor lines the canvas shows. The texts here are the anchor a kind's own
///     template has to keep: a face that drops a chip or reorders them changes what the hub render shows.
/// </summary>
public class NodeFaceTests
{
    private static RulesetCorpus.Composed Compose(string id) =>
        RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]).Compose(id);

    [Test]
    public async Task PlayerRound_FacesAreTypedPerKind()
    {
        IReadOnlyList<NodeFace> faces = NodeFaceBuilder.Build(Compose("player_round"));
        NodeFace Face(string id) => faces.Single(f => f.Id == id);
        using (Assert.Multiple())
        {
            await Assert.That(faces.Count).IsEqualTo(26).Because("eight declared nodes and the chain's eighteen engine nodes");
            await Assert.That(Face("round_kills")).IsTypeOf<CountFace>();
            await Assert.That(Face("round_damage")).IsTypeOf<SumFace>();
            await Assert.That(Face("round_side")).IsTypeOf<CaptureFace>();
            await Assert.That(Face("round_kast")).IsTypeOf<FlagFace>();
            await Assert.That(Face("MatchLive")).IsTypeOf<ScaffoldFace>();
            await Assert.That(faces.Take(8).All(f => f is StatFace { IsAuthorable: true })).IsTrue().Because("declared faces come first");

            CountFace kills = (CountFace)Face("round_kills");
            await Assert.That(kills.View).IsEqualTo("kill");
            await Assert.That(kills.Per).IsEqualTo("round");
            await Assert.That(kills.Gate).IsEmpty();
            await Assert.That(kills.KindArg).IsEqualTo("kill");
            await Assert.That(kills.Form.Set.Select(r => r.Name)).IsEquivalentTo(["enemy"]);
            await Assert.That(kills.Form.Rows.Where(r => !r.IsSet).Select(r => r.Name)).Contains("headshot");
            await Assert.That(kills.SourceLine).IsGreaterThan(0);

            CountFace deaths = (CountFace)Face("round_deaths");
            await Assert.That(deaths.Gate).IsEqualTo("match.live");
            await Assert.That(deaths.Trigger!.While).IsEqualTo("match.live");

            FlagFace kast = (FlagFace)Face("round_kast");
            await Assert.That(kast.KindArg).StartsWith("round_kills > 0");
            await Assert.That(kast.View).IsNull();
            await Assert.That(kast.OffTrigger).IsNull();

            CountFace assists = (CountFace)Face("round_assists");
            await Assert.That(assists.Trigger!.Where).IsEqualTo("event.AssistedFlash == false");

            ScaffoldFace alive = (ScaffoldFace)Face("Alive");
            await Assert.That(alive.Kind).IsEqualTo("context");
            await Assert.That(alive.IsPerPlayer).IsTrue();
            await Assert.That(alive.IsScaffold).IsTrue();
            await Assert.That(alive.IsAuthorable).IsFalse();
            await Assert.That(Face("player_death").Kind).IsEqualTo("event");
            await Assert.That(Face("enrich.kill.was_enemy_kill").Kind).IsEqualTo("enrich");
        }
    }

    [Test]
    public async Task PlayerRound_RendersTheSameChipsExpressionAndLines()
    {
        IReadOnlyList<NodeFace> faces = NodeFaceBuilder.Build(Compose("player_round"));
        // Ids repeat across engine kinds (player_team is a context value and an entity pull), so the key is the key.
        Dictionary<RulesetDocumentNodeKey, CanvasNodeViewModel> byKey = faces.ToDictionary(f => f.Key, CanvasNodeFactory.ReadOnly.Create);
        CanvasNodeViewModel Node(string id) => byKey.Single(kv => kv.Key.Id == id && !kv.Key.Ruleset.StartsWith("~entity", StringComparison.Ordinal)).Value;
        (string Id, string[] Chips, string? Expression, string[] Lines)[] expected =
        [
            ("round_side", ["on round_freeze_end", "per round"], null, ["where"]),
            ("round_kills", ["on kill", "per round"], null, ["where"]),
            ("round_deaths", ["on death", "while match.live", "per round"], null, ["where"]),
            ("round_assists", ["on assist", "per round"], "where event.AssistedFlash == false", ["where"]),
            ("round_damage", ["on damage_dealt", "per round"], null, ["where"]),
            ("round_survived", ["on round_ended", "while player.alive", "per round"], null, ["where"]),
            ("round_kast_assists", ["on assist", "per round"], "where enrich.kill.was_enemy_kill == true", ["where"]),
            ("round_kast", ["per round"], "when round_kills > 0 or round_kast_assists > 0 or player.survived or player.traded", ["when", "where"]),
            ("MatchLive", [], null, []),
            ("Alive", ["each player"], null, []),
            ("player_death", [], null, []),
        ];
        using (Assert.Multiple())
        {
            foreach ((string id, string[] chips, string? expression, string[] lines) in expected)
            {
                CanvasNodeViewModel node = Node(id);
                await Assert.That(node.Chips).IsEquivalentTo(chips).Because(id);
                await Assert.That(node.Expression).IsEqualTo(expression).Because(id);
                await Assert.That(node.Lines.Select(l => l.Key)).IsEquivalentTo(lines).Because(id);
                await Assert.That(node.Face).IsSameReferenceAs(faces.Single(f => f.Key == node.Key));
            }

            await Assert.That(Node("round_kills").Facets.Single().Value).IsEqualTo("true");
            await Assert.That(Node("round_kills").Per).IsEqualTo("round");
            await Assert.That(Node("round_deaths").Gate).IsEqualTo("match.live");
            await Assert.That(Node("round_kast").Lines.Single(l => l.Key == "when").Text).StartsWith("round_kills > 0");
            await Assert.That(Node("round_kast").IsScaffold).IsFalse();
            await Assert.That(Node("MatchLive").IsScaffold).IsTrue();
        }
    }

    [Test]
    public async Task AUseDependency_IsDrawnReadOnly_WithItsRulesetOnTheFace()
    {
        IReadOnlyList<NodeFace> faces = NodeFaceBuilder.Build(Compose("player_stats"));
        NodeFace foreign = faces.First(f => f is StatFace { IsAuthorable: false });
        CanvasNodeViewModel node = CanvasNodeFactory.ReadOnly.Create(foreign);
        using (Assert.Multiple())
        {
            await Assert.That(node.Chips).Contains("from " + foreign.Ruleset);
            await Assert.That(node.IsAuthorable).IsFalse();
            await Assert.That(faces.OfType<ScaffoldFace>().All(f => !f.IsAuthorable)).IsTrue();
        }
    }

    [Test]
    public async Task EveryShippedRuleset_BuildsAFaceForEveryNode()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        List<string> undeclared = [];
        foreach (RulesetCorpus.Entry entry in corpus.Entries)
        {
            RulesetCorpus.Composed composed = corpus.Compose(entry.Id);
            IReadOnlyList<NodeFace> faces = NodeFaceBuilder.Build(composed);
            await Assert.That(faces.Select(f => f.Key).Distinct().Count()).IsEqualTo(faces.Count).Because(entry.Id + " keys are distinct");
            await Assert.That(faces.Count(f => !f.IsScaffold)).IsEqualTo(composed.Graph.Nodes.Count).Because(entry.Id);
            undeclared.AddRange(faces.OfType<UndeclaredFace>().Select(f => f.Key.ToString()));
        }

        Console.WriteLine("[faces] undeclared: " + string.Join(", ", undeclared));
    }
}
