#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The canvas's edits as text to text: a facet set, added or removed in the flow form the corpus writes
///     and in the block form, a stat added and removed, each leaving every other byte of the file alone.
/// </summary>
public class RulesetEditsTests
{
    private const string Flow = """
        # header comment stays
        ruleset: t
        for: each_player

        stats:
          kills:
            count: kill                 # trailing comment stays
            match: { enemy: true, headshot: true, bullet: true }
            per: round

          deaths:
            count: death
            per: round
        """;

    [Test]
    public async Task SetFacet_ReplacesTheValue_InAFlowBlock_AndKeepsEverythingElse()
    {
        string after = RulesetEdits.SetFacet(Flow, "kills", "headshot", "false");
        using (Assert.Multiple())
        {
            await Assert.That(after).Contains("match: { enemy: true, headshot: false, bullet: true }");
            await Assert.That(after).Contains("# header comment stays");
            await Assert.That(after).Contains("count: kill                 # trailing comment stays");
            await Assert.That(after.Length).IsEqualTo(Flow.Length + 1).Because("false is one character longer than true and nothing else moved");
        }
    }

    [Test]
    public async Task SetFacet_AddsANewKey_InsideTheBraces()
    {
        string after = RulesetEdits.SetFacet(Flow, "kills", "no_scope", "true");
        await Assert.That(after).Contains("match: { enemy: true, headshot: true, bullet: true, no_scope: true }");
    }

    [Test]
    public async Task SetFacet_OnAStatWithNoMatch_AddsTheBlock()
    {
        string after = RulesetEdits.SetFacet(Flow, "deaths", "enemy", "true");
        using (Assert.Multiple())
        {
            await Assert.That(after).Contains("match: { enemy: true }");
            await Assert.That(RulesetEdits.StatIds(after)).IsEquivalentTo(["kills", "deaths"]);
        }
    }

    [Test]
    [Arguments("enemy", "match: { headshot: true, bullet: true }")]
    [Arguments("headshot", "match: { enemy: true, bullet: true }")]
    [Arguments("bullet", "match: { enemy: true, headshot: true }")]
    public async Task RemoveFacet_FromAFlowBlock_TakesItsSeparatorWithIt(string facet, string expected)
    {
        string after = RulesetEdits.RemoveFacet(Flow, "kills", facet);
        await Assert.That(after).Contains(expected);
    }

    [Test]
    public async Task RemoveFacet_TheLastOne_RemovesTheMatchLine()
    {
        string one = RulesetEdits.RemoveFacet(RulesetEdits.RemoveFacet(Flow, "kills", "enemy"), "kills", "headshot");
        string none = RulesetEdits.RemoveFacet(one, "kills", "bullet");
        using (Assert.Multiple())
        {
            await Assert.That(one).Contains("match: { bullet: true }");
            await Assert.That(none).DoesNotContain("match:");
            await Assert.That(none).Contains("count: kill                 # trailing comment stays");
            await Assert.That(none).Contains("per: round");
        }
    }

    [Test]
    public async Task RemoveFacet_FromABlockMapping_GoesThroughTheWriter()
    {
        const string block = """
            ruleset: t
            for: each_player
            stats:
              kills:
                count: kill
                match:
                  enemy: true
                  headshot: true
                per: round
            """;
        string after = RulesetEdits.RemoveFacet(block, "kills", "enemy");
        using (Assert.Multiple())
        {
            await Assert.That(after).DoesNotContain("enemy: true");
            await Assert.That(after).Contains("      headshot: true");
        }
    }

    [Test]
    public async Task AddStat_AppendsUnderStats_AndRemoveStat_TakesTheEntryWhole()
    {
        string added = RulesetEdits.AddStat(Flow, "assists:\n  count: assist\n  per: round\n");
        await Assert.That(RulesetEdits.StatIds(added)).IsEquivalentTo(["kills", "deaths", "assists"]);
        string removed = RulesetEdits.RemoveStat(added, "kills");
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.StatIds(removed)).IsEquivalentTo(["deaths", "assists"]);
            await Assert.That(removed).DoesNotContain("headshot");
            await Assert.That(removed).Contains("# header comment stays");
        }
    }

    [Test]
    public async Task AnUnknownStat_IsRefused_NotGuessed()
    {
        await Assert.That(() => RulesetEdits.SetFacet(Flow, "nope", "enemy", "true")).Throws<InvalidOperationException>();
        await Assert.That(() => RulesetEdits.RemoveFacet(Flow, "kills", "nope")).Throws<InvalidOperationException>();
    }
}
