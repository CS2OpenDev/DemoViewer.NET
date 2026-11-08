#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The edits a define node makes, text to text: its trigger, gate, where and facets set and removed, a
///     define added (with its block, above <c>stats:</c>, when the document has none) and removed, and a
///     rename that follows every stat firing on it. Every comment and every other byte stays.
/// </summary>
public class RulesetEditsDefineTests
{
    private const string Doc = """
        # header stays
        ruleset: t
        title: T
        for: each_player

        define:
          # the retake frag
          post_plant_kill:
            on: kill                        # view of player_death
            match: { enemy: true }
            while: round.bomb.was_planted
          utility: [hegrenade, inferno]

        stats:
          kills:
            count: post_plant_kill
            per: round
          ticks:
            capture: event.tick
            on: post_plant_kill
            keep: list
            per: round
          armed:
            flag: true
            on: bomb_planted
            off: post_plant_kill
            per: round
          plain:
            count: kill
            per: round

        highlights:
          double:
            when: kills >= 2
            per: round
            title: "{player.name} double"

        show:
          scoreboard:
            - { stat: kills, label: Kills }
        """;

    private const string NoDefines = """
        ruleset: t
        title: T
        for: each_player

        # the counters
        stats:
          kills:
            count: kill
            per: round
        """;

    [Test]
    public async Task SetDefineField_SetsOnWhileAndWhere_AndRemoveTakesThemAway()
    {
        string on = RulesetEdits.SetDefineField(Doc, "post_plant_kill", "on", "death");
        string where = RulesetEdits.SetDefineField(Doc, "post_plant_kill", "where", "player.team == 3");
        string ungated = RulesetEdits.RemoveDefineFieldIfPresent(Doc, "post_plant_kill", "while");
        using (Assert.Multiple())
        {
            await Assert.That(on).Contains("    on: death                        # view of player_death").Because("the trailing comment stays");
            await Assert.That(RulesetEdits.DefineField(on, "post_plant_kill", "on")).IsEqualTo("death");
            await Assert.That(where).Contains("    while: round.bomb.was_planted\n    where: player.team == 3\n");
            await Assert.That(RulesetEdits.DefineField(where, "post_plant_kill", "where")).IsEqualTo("player.team == 3");
            await Assert.That(ungated).DoesNotContain("while: round.bomb.was_planted");
            await Assert.That(ungated).Contains("    match: { enemy: true }\n  utility:");
            await Assert.That(RulesetEdits.RemoveDefineFieldIfPresent(Doc, "post_plant_kill", "where")).IsEqualTo(Doc);
            await Assert.That(on).Contains("# the retake frag");
            await Assert.That(on).Contains("# header stays");
            await Assert.That(() => RulesetEdits.SetDefineField(Doc, "utility", "on", "kill")).Throws<InvalidOperationException>()
                .Because("a list define is not a trigger");
            await Assert.That(() => RulesetEdits.SetDefineField(Doc, "nope", "on", "kill")).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task SetDefineFacet_AndRemoveDefineFacet_EditTheMatchBlock()
    {
        string added = RulesetEdits.SetDefineFacet(Doc, "post_plant_kill", "headshot", "true");
        string changed = RulesetEdits.SetDefineFacet(Doc, "post_plant_kill", "enemy", "false");
        string removed = RulesetEdits.RemoveDefineFacet(Doc, "post_plant_kill", "enemy");
        string fresh = RulesetEdits.SetDefineFacet(RulesetEdits.AddDefine(Doc, "bare", "on: kill"), "bare", "enemy", "true");
        using (Assert.Multiple())
        {
            await Assert.That(added).Contains("match: { enemy: true, headshot: true }");
            await Assert.That(changed).Contains("match: { enemy: false }");
            await Assert.That(removed).DoesNotContain("match:").Because("the last facet takes the block with it");
            await Assert.That(removed).Contains("    on: kill                        # view of player_death\n    while: round.bomb.was_planted");
            await Assert.That(RulesetEdits.DefineFacetKeys(added, "post_plant_kill")).IsEquivalentTo(["enemy", "headshot"]);
            await Assert.That(RulesetEdits.DefineFacetKeys(Doc, "utility")).IsEmpty();
            await Assert.That(fresh).Contains("  bare:\n    on: kill\n    match: { enemy: true }\n");
            await Assert.That(() => RulesetEdits.RemoveDefineFacet(Doc, "post_plant_kill", "headshot")).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task AddDefine_AppendsUnderTheBlock_OrOpensOneAboveStats()
    {
        string appended = RulesetEdits.AddDefine(Doc, "blind_kill", "on: kill\nwhere: event.AttackerBlind == true");
        string opened = RulesetEdits.AddDefine(NoDefines, "blind_kill", "on: kill");
        using (Assert.Multiple())
        {
            await Assert.That(appended).Contains("  utility: [hegrenade, inferno]\n  blind_kill:\n    on: kill\n    where: event.AttackerBlind == true\n\nstats:");
            await Assert.That(RulesetEdits.DefineNames(appended)).IsEquivalentTo(["post_plant_kill", "utility", "blind_kill"]);
            await Assert.That(opened).IsEqualTo("""
                ruleset: t
                title: T
                for: each_player

                define:
                  blind_kill:
                    on: kill

                # the counters
                stats:
                  kills:
                    count: kill
                    per: round
                """).Because("the block lands in the loader's order, above stats:, and the comment stays on stats:");
            await Assert.That(RulesetEdits.EntryIds(appended)).Contains("blind_kill").Because("a define name shares the id namespace");
            await Assert.That(() => RulesetEdits.AddDefine(Doc, "kills", "on: kill")).Throws<InvalidOperationException>().Because("a stat has the name");
            await Assert.That(() => RulesetEdits.AddDefine(Doc, "utility", "on: kill")).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEdits.AddDefine(Doc, "1bad", "on: kill")).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task AddDefine_IntoADocumentWithNoBlankLineBeforeStats_StillSeparatesTheBlocks()
    {
        string opened = RulesetEdits.AddDefine("ruleset: t\nfor: each_player\nstats:\n  kills:\n    count: kill\n", "blind_kill", "on: kill");
        await Assert.That(opened).IsEqualTo("ruleset: t\nfor: each_player\n\ndefine:\n  blind_kill:\n    on: kill\n\nstats:\n  kills:\n    count: kill\n");
    }

    [Test]
    public async Task RemoveDefine_TakesTheEntry_AndTheLastOneTakesTheBlock()
    {
        string one = RulesetEdits.RemoveDefine(Doc, "utility");
        string both = RulesetEdits.RemoveDefine(one, "post_plant_kill");
        using (Assert.Multiple())
        {
            await Assert.That(one).DoesNotContain("utility:");
            await Assert.That(one).Contains("define:\n  # the retake frag\n  post_plant_kill:");
            await Assert.That(both).DoesNotContain("define:");
            await Assert.That(both).DoesNotContain("post_plant_kill:\n");
            await Assert.That(both).Contains("for: each_player\n\n  # the retake frag\n\nstats:\n  kills:")
                .Because("the emptied block goes whole; a comment above an entry is the author's and stays, as the writer's rule says");
            await Assert.That(RulesetEdits.StatIds(both)).IsEquivalentTo(["kills", "ticks", "armed", "plain"]);
            await Assert.That(() => RulesetEdits.RemoveDefine(Doc, "nope")).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task RenameDefine_MovesTheKey_AndEveryTriggerThatSpellsIt()
    {
        string after = RulesetEdits.RenameDefine(Doc, "post_plant_kill", "late_kill");
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.DefineNames(after)).IsEquivalentTo(["late_kill", "utility"]);
            await Assert.That(after).Contains("  kills:\n    count: late_kill\n");
            await Assert.That(after).Contains("    capture: event.tick\n    on: late_kill\n");
            await Assert.That(after).Contains("    on: bomb_planted\n    off: late_kill\n").Because("a flag's deactivator spells it too");
            await Assert.That(after).Contains("    count: kill\n").Because("a view of the same event is not the define");
            await Assert.That(after).Contains("# the retake frag");
            await Assert.That(after).Contains("# view of player_death");
            await Assert.That(after).Contains("- { stat: kills, label: Kills }");
            await Assert.That(RulesetEdits.StatsFiringOn(Doc, "post_plant_kill")).IsEquivalentTo(["kills", "ticks", "armed"]);
            await Assert.That(RulesetEdits.StatsFiringOn(after, "late_kill")).IsEquivalentTo(["kills", "ticks", "armed"]);
            await Assert.That(RulesetEdits.StatsFiringOn(Doc, "kill")).IsEquivalentTo(["plain"]);
            await Assert.That(RulesetEdits.RenameDefine(Doc, "post_plant_kill", "post_plant_kill")).IsEqualTo(Doc);
            await Assert.That(() => RulesetEdits.RenameDefine(Doc, "post_plant_kill", "kills")).Throws<InvalidOperationException>()
                .Because("a stat has the name");
            await Assert.That(() => RulesetEdits.RenameDefine(Doc, "post_plant_kill", "a-b")).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEdits.RenameDefine(Doc, "nope", "x")).Throws<InvalidOperationException>();
        }
    }
}
