#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Renaming an entry: the key moves and so does every reference token in sibling expressions and in
///     <c>show:</c>, while a label, a comment, a string literal, a prefixed id and an event field of the
///     same name stay where they are. Invalid and duplicate ids are refused before anything is written.
/// </summary>
public class RulesetEditsRenameTests
{
    private const string Doc = """
        # header stays
        ruleset: t
        title: T
        for: each_player

        stats:
          kills:
            count: kill
            match: { enemy: true }
            per: round
            label: Kills
          deaths:
            count: death
            per: round
          seen:
            flag: true
            on: enemy_spotted
            per: round
          kd:
            compute: kills / max(deaths, 1)   # kills read here
            per: match
          gated:
            count: kill
            while: seen
            where: "event.kills > 0 && kills >= 2 && event.Weapon == \"kills\""
            per: round
          tallied:
            tally: kills
            thresholds:
              - { min: 2, target: rounds_2k }
            per: match
          kills_2:
            count: kill
            per: round
          edge:
            flag:
              when: 'kills.set and t.kills > 1'
            per: round

        highlights:
          ace:
            when: kills >= 5 and t.kills > 0
            per: round
            title: "{player.name} ace"

        show:
          scoreboard:
            - { stat: kills, label: Kills, group: round }
            - { stat: ace.count, label: Aces }
          tables:
            rounds:
              per: player_round
              columns:
                - { stat: kills, label: kills }
                - { stat: t.kills_2, label: more }
        """;

    [Test]
    public async Task RenameStat_MovesTheKey_AndEveryReferenceToken()
    {
        string after = RulesetEdits.RenameStat(Doc, "kills", "frags");
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.StatIds(after)).IsEquivalentTo(["frags", "deaths", "seen", "kd", "gated", "tallied", "kills_2", "edge"]);
            await Assert.That(after).Contains("compute: frags / max(deaths, 1)   # kills read here").Because("the comment is not a reference");
            await Assert.That(after).Contains("where: \"event.kills > 0 && frags >= 2 && event.Weapon == \\\"kills\\\"\"")
                .Because("event.kills is a field and the literal is text; the quoting is kept");
            await Assert.That(after).Contains("tally: frags");
            await Assert.That(after).Contains("when: 'frags.set and t.frags > 1'").Because("a pseudo-member and the self-qualified spelling both move");
            await Assert.That(after).Contains("when: frags >= 5 and t.frags > 0");
            await Assert.That(after).Contains("- { stat: frags, label: Kills, group: round }").Because("a label is text");
            await Assert.That(after).Contains("- { stat: frags, label: kills }");
            await Assert.That(after).Contains("- { stat: t.kills_2, label: more }").Because("kills_2 is another id");
            await Assert.That(after).Contains("    label: Kills");
            await Assert.That(after).Contains("# header stays");
            await Assert.That(after).Contains("target: rounds_2k");
        }
    }

    [Test]
    public async Task RenameStat_RewritesAGate_ThatNamesASiblingFlag()
    {
        string after = RulesetEdits.RenameStat(Doc, "seen", "spotted");
        using (Assert.Multiple())
        {
            await Assert.That(after).Contains("  spotted:\n    flag: true");
            await Assert.That(after).Contains("while: spotted");
        }
    }

    [Test]
    public async Task RenameHighlight_MovesItsCountReads()
    {
        string after = RulesetEdits.RenameHighlight(Doc, "ace", "five_k");
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.HighlightIds(after)).IsEquivalentTo(["five_k"]);
            await Assert.That(after).Contains("- { stat: five_k.count, label: Aces }");
            await Assert.That(after).Contains("title: \"{player.name} ace\"").Because("a title template is text");
        }
    }

    /// <summary>
    ///     The engine expands bare stat ids inside a title's <c>{holes}</c> and renders an unresolvable hole as
    ///     its literal text, with no diagnostic. A rename moves the holes and leaves the prose, even where the
    ///     prose spells the id.
    /// </summary>
    [Test]
    public async Task RenameStat_MovesTheHolesOfAHighlightTitle_AndLeavesItsProse()
    {
        const string doc = """
            ruleset: t
            title: T
            for: each_player

            stats:
              kills:
                count: kill
                per: round

            highlights:
              big:
                when: kills >= 3
                per: round
                title: "{player.name} got {kills} kills, {t.kills} as t.kills, {kills.count} never"
              plain:
                when: kills >= 1
                per: round
                title: kills on a low buy
            """;
        string after = RulesetEdits.RenameStat(doc, "kills", "frags");
        using (Assert.Multiple())
        {
            await Assert.That(after).Contains("title: \"{player.name} got {frags} kills, {t.frags} as t.kills, {frags.count} never\"");
            await Assert.That(after).Contains("title: kills on a low buy").Because("prose outside a hole is text");
            await Assert.That(after).Contains("when: frags >= 3");
            await Assert.That(RulesetEdits.SiblingReaders(doc, "kills")).IsEquivalentTo(["big", "plain"]);
            await Assert.That(RulesetEdits.RewriteTitleHoles("{a} and {b", "b", "c", "t")).IsEqualTo("{a} and {b").Because("an unclosed hole is text");
            await Assert.That(RulesetEdits.RewriteTitleHoles("{kills} {x.kills}", "kills", "frags", "x")).IsEqualTo("{frags} {x.frags}");
        }
    }

    [Test]
    [Arguments("deaths")]
    [Arguments("ace")]
    [Arguments("1abc")]
    [Arguments("a-b")]
    [Arguments("")]
    public async Task RenameStat_RefusesADuplicateOrAnInvalidId(string wanted)
    {
        await Assert.That(() => RulesetEdits.RenameStat(Doc, "kills", wanted)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RenameStat_OfAnUnknownId_IsRefused_AndTheSameId_IsNoChange()
    {
        using (Assert.Multiple())
        {
            await Assert.That(() => RulesetEdits.RenameStat(Doc, "nope", "x")).Throws<InvalidOperationException>();
            await Assert.That(RulesetEdits.RenameStat(Doc, "kills", "kills")).IsEqualTo(Doc);
        }
    }

    [Test]
    public async Task SiblingReaders_NamesTheEntriesWhoseExpressionsReadAnId_NotShow()
    {
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.SiblingReaders(Doc, "kills")).IsEquivalentTo(["kd", "gated", "tallied", "edge", "ace"]);
            await Assert.That(RulesetEdits.SiblingReaders(Doc, "deaths")).IsEquivalentTo(["kd"]);
            await Assert.That(RulesetEdits.SiblingReaders(Doc, "seen")).IsEquivalentTo(["gated"]);
            await Assert.That(RulesetEdits.SiblingReaders(Doc, "kills_2")).IsEmpty().Because("show: is not a sibling");
        }
    }

    [Test]
    [Arguments("kills", "frags")]
    [Arguments("kills.count + kills_2", "frags.count + kills_2")]
    [Arguments("event.kills > 0", "event.kills > 0")]
    [Arguments("t.kills > 0 and u.kills", "t.frags > 0 and u.kills")]
    [Arguments("x == \"kills\" or y == 'kills'", "x == \"kills\" or y == 'kills'")]
    [Arguments("3.5 * kills", "3.5 * frags")]
    [Arguments("{kills}", "{frags}")]
    public async Task RewriteReferences_TouchesOnlyWholeReferenceTokens(string text, string expected)
    {
        await Assert.That(RulesetEdits.RewriteReferences(text, "kills", "frags", "t")).IsEqualTo(expected);
    }

    [Test]
    [Arguments("kills", true)]
    [Arguments("_x1", true)]
    [Arguments("1abc", false)]
    [Arguments("a-b", false)]
    [Arguments("a b", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task IsValidId_AcceptsAWordOfLettersDigitsAndUnderscores(string? id, bool expected)
    {
        await Assert.That(RulesetEdits.IsValidId(id)).IsEqualTo(expected);
    }
}
