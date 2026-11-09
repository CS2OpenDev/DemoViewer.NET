#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The fixture block: written directly above the entry at its indentation, replaced rather than stacked
///     by a second freeze, left alone by the comment-preserving writer's other edits, and invisible to the
///     loader over every shipped ruleset.
/// </summary>
public class RulesetFixtureCommentTests
{
    private static readonly string[] _lines = ["demo one, Alice", "final 3, fired 3 times, first tick 100, last tick 900", "taken 2026-11-09"];

    private const string Doc = """
        ruleset: t
        title: T
        for: each_player

        define:
          enemy_kill:
            on: kill
            match: { enemy: true }

        stats:
          kills:
            count: kill
            per: round
          # a note the author left
          deaths:
            count: death
            per: round

        highlights:
          ace:
            when: "kills >= 5"
            title: Ace
        """;

    [Test]
    public async Task Set_WritesTheBlockAboveTheEntry_AtItsIndentation()
    {
        string after = RulesetFixtureComment.Set(Doc, EntrySection.Stat, "kills", _lines);
        using (Assert.Multiple())
        {
            await Assert.That(after).Contains("stats:\n  # fixture: demo one, Alice\n  # fixture: final 3, fired 3 times, first tick 100, last tick 900\n  # fixture: taken 2026-11-09\n  kills:\n");
            await Assert.That(RulesetFixtureComment.Read(after, EntrySection.Stat, "kills")).IsEquivalentTo(_lines);
            await Assert.That(RulesetFixtureComment.Read(after, EntrySection.Stat, "deaths")).IsEmpty();
            await Assert.That(RulesetFixtureComment.Read(Doc, EntrySection.Stat, "kills")).IsEmpty();
            await Assert.That(RulesetFixtureComment.Read(Doc, EntrySection.Stat, "nope")).IsEmpty().Because("no entry, no block, no throw");
        }
    }

    [Test]
    public async Task ASecondFreeze_ReplacesTheBlock_AndAnAuthorsCommentAboveStays()
    {
        string once = RulesetFixtureComment.Set(Doc, EntrySection.Stat, "deaths", _lines);
        string twice = RulesetFixtureComment.Set(once, EntrySection.Stat, "deaths", ["demo two, Bob", "final 1, fired 1 times, first tick 500, last tick 500", "taken 2026-11-10"]);
        using (Assert.Multiple())
        {
            await Assert.That(once).Contains("  # a note the author left\n  # fixture: demo one, Alice\n");
            await Assert.That(twice.Split("# fixture:").Length - 1).IsEqualTo(3).Because("replaced, not stacked");
            await Assert.That(twice).Contains("  # a note the author left\n  # fixture: demo two, Bob\n");
            await Assert.That(twice).DoesNotContain("Alice");
            await Assert.That(RulesetFixtureComment.Read(twice, EntrySection.Stat, "deaths")[0]).IsEqualTo("demo two, Bob");
        }
    }

    [Test]
    public async Task ADefineAndAHighlight_TakeABlockToo_AndAnUnknownEntryIsRefused()
    {
        string define = RulesetFixtureComment.Set(Doc, EntrySection.Define, "enemy_kill", ["demo one, game"]);
        string highlight = RulesetFixtureComment.Set(Doc, EntrySection.Highlight, "ace", ["demo one, Alice"]);
        using (Assert.Multiple())
        {
            await Assert.That(define).Contains("define:\n  # fixture: demo one, game\n  enemy_kill:\n");
            await Assert.That(highlight).Contains("highlights:\n  # fixture: demo one, Alice\n  ace:\n");
            await Assert.That(() => RulesetFixtureComment.Set(Doc, EntrySection.Stat, "nope", _lines)).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task TheBlock_SurvivesOtherEditsThroughTheWriter()
    {
        string frozen = RulesetFixtureComment.Set(Doc, EntrySection.Stat, "kills", _lines);
        string labelled = RulesetEdits.SetField(frozen, "kills", "label", "Kills");
        string facet = RulesetEdits.SetFacet(labelled, "kills", "enemy", "true");
        string removed = RulesetEdits.RemoveStat(facet, "deaths");
        string added = RulesetEdits.AddStat(removed, "assists:\n  count: assist\n  per: round");
        using (Assert.Multiple())
        {
            await Assert.That(RulesetFixtureComment.Read(labelled, EntrySection.Stat, "kills")).IsEquivalentTo(_lines);
            await Assert.That(RulesetFixtureComment.Read(facet, EntrySection.Stat, "kills")).IsEquivalentTo(_lines);
            await Assert.That(RulesetFixtureComment.Read(removed, EntrySection.Stat, "kills")).IsEquivalentTo(_lines);
            await Assert.That(RulesetFixtureComment.Read(added, EntrySection.Stat, "kills")).IsEquivalentTo(_lines);
            await Assert.That(added).Contains("label: Kills");
        }
    }

    // Over every shipped ruleset with a stat: the frozen text still loads as the same ruleset and composes to
    // the same nodes, wires and diagnostics, so a fixture never changes what runs.
    [Test]
    public async Task OverTheShippedCorpus_AFixtureNeverChangesWhatLoads()
    {
        using RulesTree before = RulesTree.New();
        using RulesTree after = RulesTree.New();
        List<string> checkedIds = [];
        foreach (string path in Directory.GetFiles(RulesTree.RepoRulesDirectory, "*.rules.yaml").Order(StringComparer.Ordinal))
        {
            string text = File.ReadAllText(path);
            string id = Path.GetFileName(path)[..^".rules.yaml".Length];
            before.User(id, text);
            IReadOnlyList<string> stats = RulesetEdits.StatIds(text);
            if (stats.Count == 0)
            {
                after.User(id, text);
                continue;
            }

            string frozen = RulesetFixtureComment.Set(text, EntrySection.Stat, stats[0], _lines);
            await Assert.That(RulesetCorpus.WhyNotLoadable(frozen, path, id)).IsNull().Because(id);
            after.User(id, frozen);
            checkedIds.Add(id);
        }

        await Assert.That(checkedIds.Count).IsGreaterThan(10);
        RulesetCorpus was = before.Load();
        RulesetCorpus now = after.Load();
        foreach (string id in checkedIds)
        {
            RulesetCorpus.Composed a = was.Compose(id);
            RulesetCorpus.Composed b = now.Compose(id);
            using (Assert.Multiple())
            {
                await Assert.That(b.DrawnKeys.ToHashSet()).IsEquivalentTo(a.DrawnKeys.ToHashSet()).Because(id);
                await Assert.That(b.Graph.Edges.Count).IsEqualTo(a.Graph.Edges.Count).Because(id);
                await Assert.That(b.Chain.Nodes.Count).IsEqualTo(a.Chain.Nodes.Count).Because(id);
                await Assert.That(b.Diagnostics).IsEquivalentTo(a.Diagnostics).Because(id);
            }
        }
    }

    [Test]
    public async Task TheFixtureLines_NameTheDemoTheInstanceTheValueTheFiresAndTheDate()
    {
        NodeTrack kills = new(3, 100, 900, "3", 3f, true);
        NodeTrack flag = new(0, null, null, "0", 0f, false);
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.FixtureLines(kills, new LiveInstance(1, 0, "Alice"), "one", false, new DateOnly(2026, 11, 9)))
                .IsEquivalentTo(_lines);
            await Assert.That(LiveValuesText.FixtureLines(flag, new LiveInstance(0, null, null), "003816248937665266002_0544286934", true, new DateOnly(2026, 1, 2)))
                .IsEquivalentTo(["demo 003816248937665266002_0544286934, game", "final off, never fired", "taken 2026-01-02"]);
        }
    }
}
