#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The two <c>show:</c> entry keys the faces did not carry: a column's <c>as:</c> display format and a
///     scoreboard entry's <c>boards:</c> override. Both are read into the reference, drawn as chips on the
///     column row, declared as covered by the show face, and edited in the Inspector's COLUMNS section
///     through <see cref="RulesetEdits" />, which round-trips them over the shipped corpus.
/// </summary>
public class ShowEntryKeysTests
{
    private const string Boarded = """
        ruleset: boarded
        for: each_player
        stats:
          kills:
            count: kill
            per: round
          alive_ticks:
            capture: player.team
            on: round_freeze_end
            per: match
        show:
          scoreboard:
            - { stat: kills, label: Kills, boards: [round, match] }
            - { stat: alive_ticks, label: Alive, as: seconds }
          tables:
            rounds:
              per: player_round
              columns:
                - { stat: alive_ticks, label: Alive, as: time }
                - { stat: kills }
        """;

    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.New();
        rules.User("boarded", Boarded);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "boarded");
        return (rules, vm);
    }

    [Test]
    public async Task TheReference_CarriesBoardsAndAs_AndTheShowFaceCoversThem()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("boarded", Boarded);
        RulesetCorpus.Composed composed = rules.Load().Compose("boarded");
        List<ShowFace> faces = [.. NodeFaceBuilder.Build(composed).OfType<ShowFace>()];
        ShowFace board = faces.Single(f => f.IsScoreboard);
        ShowFace table = faces.Single(f => !f.IsScoreboard);
        using (Assert.Multiple())
        {
            await Assert.That(board.Columns[0].Boards).IsEquivalentTo(["round", "match"]);
            await Assert.That(board.Columns[0].As).IsNull();
            await Assert.That(board.Columns[1].Boards).IsNull().Because("no override: the board follows the stat's per:");
            await Assert.That(board.Columns[1].As).IsEqualTo("seconds");
            await Assert.That(table.Columns[0].As).IsEqualTo("time");
            await Assert.That(table.Columns[1].As).IsNull();
            await Assert.That(board.CoveredKeys).Contains("boards");
            await Assert.That(board.CoveredKeys).Contains("as");
            await Assert.That(ShowReference.AsText(CS2DemoKit.Analysis.RulesetsV2.Model.ColumnValueFormat.None)).IsNull();
        }
    }

    [Test]
    public async Task TheColumnRow_WearsAsAndBoardsAsChips()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel board = vm.Nodes.Single(n => n.IsShow && n.Key.IsScoreboard);
            CanvasNodeViewModel table = vm.Nodes.Single(n => n.IsShow && !n.Key.IsScoreboard);
            using (Assert.Multiple())
            {
                await Assert.That(board.Columns[0].BoardsChip).IsEqualTo("boards round, match");
                await Assert.That(board.Columns[0].HasBoards).IsTrue();
                await Assert.That(board.Columns[0].HasAs).IsFalse();
                await Assert.That(board.Columns[1].AsChip).IsEqualTo("as seconds");
                await Assert.That(board.Columns[1].HasBoards).IsFalse();
                await Assert.That(table.Columns[0].AsChip).IsEqualTo("as time");
                await Assert.That(table.Columns[1].AsChip).IsEmpty();
                await Assert.That(table.Columns[1].HasAs).IsFalse();
            }
        }
    }

    [Test]
    public async Task TheInspector_EditsAsAndBoards_InTheColumnsSection()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            InspectorViewModel inspector = vm.Inspector;
            vm.SelectedNodes.Add(vm.Nodes.Single(n => n.IsShow && n.Key.IsScoreboard));
            ShowEntryViewModel kills = inspector.Entries.Single(e => e.Stat == "kills");
            ShowEntryViewModel alive = inspector.Entries.Single(e => e.Stat == "alive_ticks");
            using (Assert.Multiple())
            {
                await Assert.That(kills.Boards.Text).IsEqualTo("round, match");
                await Assert.That(kills.Boards.IsVisible).IsTrue();
                await Assert.That(kills.As.Text).IsEmpty();
                await Assert.That(alive.As.Text).IsEqualTo("seconds");
                await Assert.That(alive.Boards.Text).IsEmpty();
                await Assert.That(alive.As.Options).IsEquivalentTo(["", "ticks", "seconds", "time"]);
                await Assert.That(kills.Boards.Options).IsEquivalentTo(["", "round", "match", "round, match"]);
            }

            alive.As.Text = "time";
            string text = File.ReadAllText(rules.UserPath("boarded"));
            await Assert.That(text).Contains("- { stat: alive_ticks, label: Alive, as: time }\n");

            inspector.Entries.Single(e => e.Stat == "kills").Boards.Text = "match";
            text = File.ReadAllText(rules.UserPath("boarded"));
            await Assert.That(text).Contains("- { stat: kills, label: Kills, boards: [match] }\n");

            inspector.Entries.Single(e => e.Stat == "kills").Boards.Text = "";
            text = File.ReadAllText(rules.UserPath("boarded"));
            await Assert.That(text).Contains("- { stat: kills, label: Kills }\n");

            inspector.Entries.Single(e => e.Stat == "kills").As.Text = "ticks";
            text = File.ReadAllText(rules.UserPath("boarded"));
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("- { stat: kills, label: Kills, as: ticks }\n");
                await Assert.That(vm.Nodes.Single(n => n.IsShow && n.Key.IsScoreboard).Columns[0].AsChip).IsEqualTo("as ticks");
                await Assert.That(vm.EditStatus).IsEmpty();
            }

            vm.SelectedNodes.Clear();
            vm.SelectedNodes.Add(vm.Nodes.Single(n => n.IsShow && !n.Key.IsScoreboard));
            ShowEntryViewModel column = inspector.Entries.Single(e => e.Stat == "alive_ticks");
            await Assert.That(column.Boards.IsVisible).IsFalse().Because("a table column has no boards");
            column.As.Text = "";
            await Assert.That(File.ReadAllText(rules.UserPath("boarded"))).Contains("- { stat: alive_ticks, label: Alive }\n");
        }
    }

    [Test]
    public async Task SetShowEntryBoards_WritesTheFlowList_ReplacesIt_AndRemovesIt()
    {
        string once = RulesetEdits.SetShowEntryBoards(Boarded, 1, ["round"]);
        string twice = RulesetEdits.SetShowEntryBoards(once, 1, ["match", "round"]);
        string gone = RulesetEdits.SetShowEntryBoards(twice, 1, null);
        string swapped = RulesetEdits.SetShowEntryBoards(Boarded, 0, ["match"]);
        using (Assert.Multiple())
        {
            await Assert.That(once).Contains("- { stat: alive_ticks, label: Alive, as: seconds, boards: [round] }");
            await Assert.That(twice).Contains("- { stat: alive_ticks, label: Alive, as: seconds, boards: [match, round] }");
            await Assert.That(gone).IsEqualTo(Boarded);
            await Assert.That(swapped).Contains("- { stat: kills, label: Kills, boards: [match] }");
            await Assert.That(RulesetEdits.SetShowEntryBoards(Boarded, 1, [])).IsEqualTo(Boarded);
            await Assert.That(() => RulesetEdits.SetShowEntryBoards(Boarded, 0, ["game"])).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEdits.SetShowEntryBoards(Boarded, 7, ["round"])).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEdits.SetShowEntryField(Boarded, null, 0, "boards", "round")).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEdits.SetShowEntryField(Boarded, null, 0, "as", "hours")).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task AsAndBoards_RoundTrip_OverEveryShippedScoreboardAndTable()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        int boards = 0, columns = 0;
        foreach (RulesetCorpus.Entry entry in corpus.Entries)
        {
            string text = File.ReadAllText(entry.Path);
            ShowSurface surface = ShowSurface.Of(corpus.Compose(entry.Id).Open);
            if (surface.Scoreboard.Count > 0)
            {
                boards++;
                string formatted = RulesetEdits.SetShowEntryField(text, null, 0, "as", "seconds");
                string boarded = RulesetEdits.SetShowEntryBoards(formatted, 0, ["round", "match"]);
                using (Assert.Multiple())
                {
                    await Assert.That(boarded).Contains("as: seconds, boards: [round, match]").Because(entry.Id);
                    await Assert.That(RulesetCorpus.WhyNotLoadable(boarded, entry.Path, entry.Id)).IsNull().Because(entry.Id);
                    await Assert.That(RulesetEdits.SetShowEntryField(RulesetEdits.SetShowEntryBoards(boarded, 0, null), null, 0, "as", null))
                        .IsEqualTo(text).Because(entry.Id + " round-trips");
                }
            }

            foreach (ShowTable table in surface.Tables.Where(t => t.Columns.Count > 0))
            {
                columns++;
                string formatted = RulesetEdits.SetShowEntryField(text, table.Name, 0, "as", "time");
                using (Assert.Multiple())
                {
                    await Assert.That(formatted).Contains("as: time").Because($"{entry.Id}.{table.Name}");
                    await Assert.That(RulesetCorpus.WhyNotLoadable(formatted, entry.Path, entry.Id)).IsNull().Because($"{entry.Id}.{table.Name}");
                    await Assert.That(RulesetEdits.SetShowEntryField(formatted, table.Name, 0, "as", null)).IsEqualTo(text).Because($"{entry.Id}.{table.Name}");
                }
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(boards).IsGreaterThan(10);
            await Assert.That(columns).IsGreaterThan(3);
        }
    }
}
