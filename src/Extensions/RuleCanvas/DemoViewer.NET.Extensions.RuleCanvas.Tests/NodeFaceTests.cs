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
        // Wave 1 changed three things on purpose. The trigger chip carries the author's spelling, so a raw
        // event keeps its raw. prefix (round_side). A second chip after the trigger says whom the view binds
        // (as killer, as victim, binds nobody), which the face hid before. A sum or a capture shows its value
        // as the expression (= enrich.hurt.capped_damage), where it showed nothing. The kind's keys are rows
        // (Fields, Choices), not chips, so they are checked apart below.
        (string Id, string[] Chips, string? Expression, string[] Lines)[] expected =
        [
            ("round_side", ["on raw.round_freeze_end", "per round"], "= player.team", ["where"]),
            ("round_kills", ["on kill", "as killer", "per round"], null, ["where"]),
            ("round_deaths", ["on death", "as victim", "while match.live", "per round"], null, ["where"]),
            ("round_assists", ["on assist", "as assister", "per round"], "where event.AssistedFlash == false", ["where"]),
            ("round_damage", ["on damage_dealt", "as attacker", "per round"], "= enrich.hurt.capped_damage", ["where"]),
            ("round_survived", ["on round_ended", "binds nobody", "while player.alive", "per round"], null, ["where"]),
            ("round_kast_assists", ["on assist", "as assister", "per round"], "where enrich.kill.was_enemy_kill == true", ["where"]),
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

            // The trigger chip's tip names the resolved view and the wire event; the actor chip's names the role.
            await Assert.That(Node("round_side").ChipTips["on raw.round_freeze_end"]).Contains("round_freeze_end");
            await Assert.That(Node("round_kills").ChipTips["on kill"]).Contains("player_death");
            await Assert.That(Node("round_kills").ChipTips["as killer"]).Contains("killer");
            await Assert.That(Node("round_kills").Trigger!.Key).IsEqualTo("count");
            await Assert.That(Node("round_damage").Trigger!.Key).IsEqualTo("on");
            await Assert.That(Node("round_kast").Trigger).IsNull().Because("an expression flag is its own trigger");
            await Assert.That(Node("round_damage").Fields.Single(f => f.Key == "sum").Original).IsEqualTo("enrich.hurt.capped_damage");
            await Assert.That(Node("round_side").Choices.Single(c => c.Key == "keep").IsDefault).IsTrue();
            await Assert.That(Node("round_kills").FixedRows).IsEquivalentTo(["not a suicide"]).Because("the kill view bakes Attacker != UserId in");
            await Assert.That(Node("round_deaths").FixedRows).IsEmpty().Because("the death view keeps the suicide");
            await Assert.That(Node("round_kills").ValueType).IsEqualTo("int");
            await Assert.That(Node("round_kast").ValueType).IsEqualTo("bool");
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

    /// <summary>
    ///     highlights_clutch's retake_kills is <c>count: ct_retake_kill</c>, a define whose body carries the
    ///     match, the gate and the where. The resolver runs all three; the face states them as rows the stat
    ///     cannot edit, named for the define, and stops offering the define's facets as unset.
    /// </summary>
    [Test]
    public async Task ADefineTriggeredStat_StatesTheDefinesBody_AsFixedRows()
    {
        IReadOnlyList<NodeFace> faces = NodeFaceBuilder.Build(Compose("highlights_clutch"));
        StatFace retake = (StatFace)faces.Single(f => f.Id == "retake_kills");
        StatFace clutch = (StatFace)faces.Single(f => f.Id == "clutch_1v2_round");
        CanvasNodeViewModel node = CanvasNodeFactory.ReadOnly.Create(retake);
        using (Assert.Multiple())
        {
            await Assert.That(retake.Define?.Name).IsEqualTo("ct_retake_kill");
            await Assert.That(retake.View).IsEqualTo("kill");
            await Assert.That(clutch.Define).IsNull().Because("round_won is a view, not a define");
            await Assert.That(node.Chips).Contains("on ct_retake_kill");
            await Assert.That(node.FixedRows)
                .IsEquivalentTo(["not a suicide", "enemy: true", "while round.bomb.was_planted", "where player.team == 3"]);
            await Assert.That(node.AvailableFacets).DoesNotContain("enemy").Because("the resolver refuses the same key on the define and the site");
            await Assert.That(node.AvailableFacets).Contains("headshot");
            await Assert.That(node.Facets).IsEmpty().Because("the stat's own match: is empty");
            await Assert.That(node.FixedRowItems.Single(r => r.Text == "enemy: true").Tip).Contains("ct_retake_kill");
            await Assert.That(node.FixedRowItems.Single(r => r.Text == "where player.team == 3").Tip).Contains("define:");
            await Assert.That(node.FixedRowItems.Single(r => r.Text == "not a suicide").Tip).Contains("event.Attacker != event.UserId");
            await Assert.That(node.Gate).IsEmpty().Because("the gate editor is the stat's own while:, which is unset");
        }
    }

    /// <summary>
    ///     round_won and round_lost bind a team, not a role: the engine lowers the result to a test of this
    ///     player's team against the round's winner. The face says so, states the test, and offers the actor
    ///     toggle, since the engine honours <c>actor: any</c> on a team view too.
    /// </summary>
    [Test]
    public async Task ATeamBoundView_SaysWhichTeamItBinds_NotNobody()
    {
        IReadOnlyList<NodeFace> clutch = NodeFaceBuilder.Build(Compose("highlights_clutch"));
        IReadOnlyList<NodeFace> stats = NodeFaceBuilder.Build(Compose("player_stats"));
        StatFace won = (StatFace)clutch.Single(f => f.Id == "clutch_1v2_round");
        StatFace lost = (StatFace)stats.Single(f => f.Id == "CTLosses");
        StatFace ended = (StatFace)stats.Single(f => f.Id == "rounds_survived");
        CanvasNodeViewModel wonNode = CanvasNodeFactory.ReadOnly.Create(won);
        CanvasNodeViewModel lostNode = CanvasNodeFactory.ReadOnly.Create(lost);
        wonNode.IsEditable = true;
        using (Assert.Multiple())
        {
            await Assert.That(won.View).IsEqualTo("round_won");
            await Assert.That(won.ActorText).IsEqualTo("as winning team");
            await Assert.That(lost.View).IsEqualTo("round_lost");
            await Assert.That(lost.ActorText).IsEqualTo("as losing team");
            await Assert.That(ended.ActorText).IsEqualTo("binds nobody").Because("round_ended binds neither a role nor a team");
            await Assert.That(wonNode.Chips).DoesNotContain("binds nobody");
            await Assert.That(wonNode.ChipTips["as winning team"]).Contains("enrich.round.winner_team == player.team");
            await Assert.That(lostNode.ChipTips["as losing team"]).Contains("winner_team != player.team");
            await Assert.That(wonNode.FixedRows).Contains("round won by this player's team");
            await Assert.That(lostNode.FixedRows).Contains("round lost by this player's team");
            await Assert.That(wonNode.ShowActorToggle).IsTrue().Because("actor: any switches a team binding off too");
            await Assert.That(wonNode.BindsActor == true).IsTrue();
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

        // The two sequence nodes the engine synthesises for player_event's per: player_event tables are the
        // only chain nodes no stats: or highlights: entry backs; any other document-less face is a regression.
        await Assert.That(undeclared).IsEquivalentTo(["player_event.#seq_player_kills", "player_event.#seq_player_deaths"])
            .Because("undeclared: " + string.Join(", ", undeclared));
    }
}
