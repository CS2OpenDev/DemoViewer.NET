#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The document-level edits: header keys inserted where a ruleset writes them, the <c>use:</c> list,
///     the <c>show:</c> block grown and shrunk entry by entry, and the smallest new ruleset the loader
///     accepts and the canvas can add to.
/// </summary>
public class RulesetEditsDocumentTests
{
    private const string Doc = """
        ruleset: t
        title: T
        for: each_player

        stats:
          kills:
            count: kill
            per: round
          deaths:
            count: death
            per: round

        show:
          scoreboard:
            - { stat: kills, label: Kills, group: round }
            - { stat: deaths, label: Deaths }
          tables:
            rounds:
              per: player_round
              columns:
                - { stat: kills, label: kills }
                - { stat: deaths, label: deaths }
        """;

    [Test]
    public async Task SetDocumentField_ReplacesInPlace_OrInsertsAfterTheNearestHeaderKey()
    {
        string titled = RulesetEdits.SetDocumentField(Doc, "title", "Totals: per player");
        string summarised = RulesetEdits.SetDocumentField(titled, "summary", "What it does.");
        using (Assert.Multiple())
        {
            await Assert.That(titled).Contains("ruleset: t\ntitle: \"Totals: per player\"\nfor: each_player").Because("a colon-space needs quotes");
            await Assert.That(summarised).Contains("title: \"Totals: per player\"\nsummary: What it does.\nfor: each_player")
                .Because("summary goes after title, not at the end of the file");
            await Assert.That(RulesetEdits.DocumentField(summarised, "summary")).IsEqualTo("What it does.");
            await Assert.That(RulesetEdits.SetDocumentField(Doc, "for", "match")).Contains("for: match");
            await Assert.That(RulesetEdits.RemoveDocumentField(summarised, "summary")).IsEqualTo(titled);
            await Assert.That(RulesetEdits.RemoveDocumentField(Doc, "summary")).IsEqualTo(Doc);
        }
    }

    [Test]
    public async Task SetDocumentField_OnABareRuleset_GoesAfterTheId()
    {
        const string bare = "ruleset: t\nstats:\n  kills:\n    count: kill\n";
        await Assert.That(RulesetEdits.SetDocumentField(bare, "for", "each_player")).IsEqualTo("ruleset: t\nfor: each_player\nstats:\n  kills:\n    count: kill\n");
    }

    [Test]
    public async Task AddUse_CreatesTheList_ThenAppends_AndRemoveUse_TakesTheLastOneWithTheKey()
    {
        string one = RulesetEdits.AddUse(Doc, "kast");
        string two = RulesetEdits.AddUse(one, "player_stats");
        using (Assert.Multiple())
        {
            await Assert.That(one).Contains("for: each_player\nuse: [kast]\n");
            await Assert.That(RulesetEdits.Uses(two)).IsEquivalentTo(["kast", "player_stats"]);
            await Assert.That(RulesetEdits.AddUse(two, "kast")).IsEqualTo(two).Because("a ruleset is used once");
            await Assert.That(RulesetEdits.Uses(RulesetEdits.RemoveUse(two, "kast"))).IsEquivalentTo(["player_stats"]);
            await Assert.That(RulesetEdits.Uses(RulesetEdits.RemoveUse(two, "player_stats"))).IsEquivalentTo(["kast"]);
            await Assert.That(RulesetEdits.RemoveUse(one, "kast")).IsEqualTo(Doc);
            await Assert.That(RulesetEdits.RemoveUse(Doc, "kast")).IsEqualTo(Doc);
            await Assert.That(() => RulesetEdits.AddUse(Doc, "not an id")).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task AddUse_AppendsToABlockList_Too()
    {
        const string block = "ruleset: t\nuse:\n  - kast\nstats:\n  kills:\n    count: kill\n";
        string after = RulesetEdits.AddUse(block, "player_stats");
        using (Assert.Multiple())
        {
            await Assert.That(after).Contains("use:\n  - kast\n  - player_stats\nstats:");
            await Assert.That(RulesetEdits.RemoveUse(after, "kast")).Contains("use:\n  - player_stats\nstats:");
        }
    }

    [Test]
    public async Task AddToScoreboard_AppendsAnEntry_OrGrowsTheBlock_OrTheSection()
    {
        string appended = RulesetEdits.AddToScoreboard(Doc, "assists", "Assists");
        string noShow = "ruleset: t\nstats:\n  kills:\n    count: kill\n";
        string created = RulesetEdits.AddToScoreboard(noShow, "kills", "Kills");
        string tablesOnly = "ruleset: t\nstats:\n  kills:\n    count: kill\nshow:\n  tables:\n    r:\n      per: player_round\n      columns:\n        - { stat: kills }\n";
        string grown = RulesetEdits.AddToScoreboard(tablesOnly, "kills", "4K");
        using (Assert.Multiple())
        {
            await Assert.That(appended).Contains("    - { stat: deaths, label: Deaths }\n    - { stat: assists, label: Assists }\n  tables:");
            await Assert.That(RulesetEdits.AddToScoreboard(Doc, "kills", "Again")).IsEqualTo(Doc).Because("an entry already there stays");
            await Assert.That(created).EndsWith("show:\n  scoreboard:\n    - { stat: kills, label: Kills }\n");
            await Assert.That(grown).Contains("show:\n  tables:\n    r:\n      per: player_round\n      columns:\n        - { stat: kills }\n  scoreboard:\n    - { stat: kills, label: \"4K\" }\n")
                .Because("a label starting with a digit is quoted, as the corpus writes 4K");
            await Assert.That(RulesetEdits.AddToScoreboard(Doc, "deaths_2", "12")).Contains("{ stat: deaths_2, label: \"12\" }").Because("a label YAML would read as a number is quoted");
        }
    }

    [Test]
    public async Task AddToTable_AppendsAColumn_AndRefusesAnUnknownTable()
    {
        string after = RulesetEdits.AddToTable(Doc, "rounds", "assists", "assists");
        using (Assert.Multiple())
        {
            await Assert.That(after).Contains("        - { stat: deaths, label: deaths }\n        - { stat: assists, label: assists }\n");
            await Assert.That(() => RulesetEdits.AddToTable(Doc, "nope", "assists", "assists")).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task SetShowEntryField_SetsOrRemovesALabelOrGroup_InsideTheBraces()
    {
        string relabelled = RulesetEdits.SetShowEntryField(Doc, null, 0, "label", "Frags");
        string ungrouped = RulesetEdits.SetShowEntryField(Doc, null, 0, "group", null);
        string grouped = RulesetEdits.SetShowEntryField(Doc, null, 1, "group", "round");
        string column = RulesetEdits.SetShowEntryField(Doc, "rounds", 1, "label", "D");
        using (Assert.Multiple())
        {
            await Assert.That(relabelled).Contains("- { stat: kills, label: Frags, group: round }");
            await Assert.That(ungrouped).Contains("- { stat: kills, label: Kills }");
            await Assert.That(grouped).Contains("- { stat: deaths, label: Deaths, group: round }");
            await Assert.That(column).Contains("- { stat: deaths, label: D }");
            await Assert.That(RulesetEdits.SetShowEntryField(Doc, null, 1, "group", null)).IsEqualTo(Doc);
            await Assert.That(() => RulesetEdits.SetShowEntryField(Doc, null, 5, "label", "x")).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task RemoveShowReference_TakesEveryEntryNamingTheId_AndWhatItEmptied()
    {
        string noKills = RulesetEdits.RemoveShowReference(Doc, "kills");
        string none = RulesetEdits.RemoveShowReference(noKills, "deaths");
        using (Assert.Multiple())
        {
            await Assert.That(noKills).EndsWith("  scoreboard:\n    - { stat: deaths, label: Deaths }\n  tables:\n    rounds:\n      per: player_round\n      columns:\n        - { stat: deaths, label: deaths }");
            await Assert.That(noKills).DoesNotContain("stat: kills");
            await Assert.That(none).DoesNotContain("show:").Because("an emptied list, table and section go too");
            await Assert.That(none).Contains("  deaths:\n    count: death\n    per: round\n");
            await Assert.That(RulesetEdits.RemoveShowReference(Doc, "assists")).IsEqualTo(Doc);
        }
    }

    /// <summary>
    ///     A flow list's last item is removed with the separator before it and any other with the one after,
    ///     so two adjacent hits ending at the last item must not claim the same separator twice.
    /// </summary>
    [Test]
    public async Task RemoveShowReference_TakesAdjacentFlowEntries_WhereverTheyLie()
    {
        const string flow = """
            ruleset: t
            title: T
            for: each_player

            stats:
              kills:
                count: kill
                per: round
              deaths:
                count: death
                per: round

            show:
              scoreboard: [{ stat: deaths, label: D }, { stat: kills, label: B }, { stat: kills, label: C }]
            """;
        string lastTwo = RulesetEdits.RemoveShowReference(flow, "kills");
        string firstTwo = RulesetEdits.RemoveShowReference(flow
            .Replace("deaths, label: D", "kills, label: A", StringComparison.Ordinal)
            .Replace("kills, label: C", "deaths, label: C", StringComparison.Ordinal), "kills");
        string outer = RulesetEdits.RemoveShowReference(flow
            .Replace("deaths, label: D", "kills, label: A", StringComparison.Ordinal)
            .Replace("kills, label: B", "deaths, label: B", StringComparison.Ordinal), "kills");
        string all = RulesetEdits.RemoveShowReference(lastTwo, "deaths");
        using (Assert.Multiple())
        {
            await Assert.That(lastTwo).Contains("scoreboard: [{ stat: deaths, label: D }]");
            await Assert.That(firstTwo).Contains("scoreboard: [{ stat: deaths, label: C }]");
            await Assert.That(outer).Contains("scoreboard: [{ stat: deaths, label: B }]");
            await Assert.That(all).DoesNotContain("show:").Because("an emptied flow list goes with its section");
        }
    }

    [Test]
    public async Task RemoveShowItem_RemovesOneEntry_ByItsPlace()
    {
        string after = RulesetEdits.RemoveShowItem(Doc, "rounds", 0);
        using (Assert.Multiple())
        {
            await Assert.That(after).EndsWith("      columns:\n        - { stat: deaths, label: deaths }");
            await Assert.That(after).Contains("- { stat: kills, label: Kills, group: round }").Because("the scoreboard entry is another place");
        }
    }

    [Test]
    public async Task NewRulesetText_Loads_AndTakesItsFirstStat()
    {
        string text = RulesetEdits.NewRulesetText("my_rules", "my rules");
        string withStat = RulesetEdits.AddStat(text, "kills:\n  count: kill\n  per: round\n");
        using (Assert.Multiple())
        {
            await Assert.That(text).IsEqualTo("ruleset: my_rules\ntitle: my rules\nfor: each_player\n\nstats: {}\n");
            await Assert.That(RulesetCorpus.WhyNotLoadable(text, "/x/my_rules.rules.yaml", "my_rules")).IsNull();
            await Assert.That(withStat).Contains("stats: \n  kills:\n    count: kill\n    per: round\n");
            await Assert.That(RulesetCorpus.WhyNotLoadable(withStat, "/x/my_rules.rules.yaml", "my_rules")).IsNull();
            await Assert.That(RulesetEdits.StatIds(withStat)).IsEquivalentTo(["kills"]);
            await Assert.That(() => RulesetEdits.NewRulesetText("1x", "t")).Throws<InvalidOperationException>();
        }
    }
}
