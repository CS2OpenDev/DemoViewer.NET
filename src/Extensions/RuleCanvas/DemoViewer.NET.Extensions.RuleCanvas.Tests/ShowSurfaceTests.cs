#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The surfacing model answers, for every entry, whether and where <c>show:</c> references it, by any of
///     the spellings the corpus writes: the id, a highlight's <c>.count</c>, and the self-qualified form.
/// </summary>
public class ShowSurfaceTests
{
    private const string Doc = """
        ruleset: t
        for: each_player
        stats:
          kills:
            count: kill
            per: round
          deaths:
            count: death
            per: round
          assists:
            count: assist
            per: round
        highlights:
          ace:
            when: kills >= 5
            per: round
        show:
          scoreboard:
            - { stat: kills, label: Kills, group: round }
            - { stat: ace.count, label: Aces }
            - { stat: t.deaths }
          tables:
            rounds:
              per: player_round
              columns:
                - { stat: kills, label: kills }
                - { stat: ace, label: aces }
        """;

    private static RulesetDoc Load() => RulesetDocumentLoader.Load(Doc, "/x/t.rules.yaml").Doc
                                        ?? throw new InvalidOperationException("the fixture does not load");

    [Test]
    public async Task ReferencesTo_FindsEverySpelling_InDocumentOrder()
    {
        ShowSurface surface = ShowSurface.Of(Load());
        using (Assert.Multiple())
        {
            await Assert.That(surface.ReferencesTo("kills").Select(r => r.Surface)).IsEquivalentTo(["scoreboard", "rounds"]);
            await Assert.That(surface.ReferencesTo("ace").Select(r => r.Stat)).IsEquivalentTo(["ace.count", "ace"]);
            await Assert.That(surface.ReferencesTo("deaths").Select(r => r.Index)).IsEquivalentTo([2]);
            await Assert.That(surface.ReferencesTo("assists")).IsEmpty();
            await Assert.That(surface.Scoreboard[0].Group).IsEqualTo("round");
            await Assert.That(surface.Tables.Single().Per).IsEqualTo("player_round");
            await Assert.That(surface.All.Count()).IsEqualTo(5);
        }
    }

    [Test]
    public async Task BadgeFor_SaysWhereAndAsWhat_OrNotShown()
    {
        ShowSurface surface = ShowSurface.Of(Load());
        using (Assert.Multiple())
        {
            await Assert.That(surface.BadgeFor("kills")).IsEqualTo("shown: scoreboard as Kills, rounds as kills");
            await Assert.That(surface.BadgeFor("deaths")).IsEqualTo("shown: scoreboard as t.deaths").Because("an entry with no label shows under its reference");
            await Assert.That(surface.BadgeFor("ace")).IsEqualTo("shown: scoreboard as Aces, rounds as aces");
            await Assert.That(surface.BadgeFor("assists")).IsEqualTo("not shown");
            await Assert.That(ShowSurface.Empty.BadgeFor("kills")).IsEqualTo("not shown");
            await Assert.That(ShowSurface.Of(null).IsEmpty).IsTrue();
        }
    }

    [Test]
    public async Task TheShippedPlayerRound_SurfacesItsColumns()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        RulesetCorpus.Composed composed = rules.Load().Compose("player_round");
        ShowSurface surface = ShowSurface.Of(composed.Documents["player_round"]);
        using (Assert.Multiple())
        {
            await Assert.That(surface.BadgeFor("round_kills")).IsEqualTo("shown: player_round as kills");
            await Assert.That(surface.BadgeFor("round_kast_assists")).IsEqualTo("not shown");
            await Assert.That(surface.Scoreboard).IsEmpty();
        }
    }
}
