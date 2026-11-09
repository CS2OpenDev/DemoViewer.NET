#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The <c>show:</c> block as nodes: one per table and one for the scoreboard, wired from the entries they
///     surface by a read wire per column. A stat wired in becomes a column, a column's wire removed takes the
///     column out and leaves the table, the gallery places an empty table, and the Inspector edits the columns.
/// </summary>
public class ShowNodeTests
{
    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Table(CanvasTabViewModel vm, string name) => vm.Nodes.Single(n => n.IsShow && n.Title == name);

    [Test]
    public async Task PlayerRound_DrawsItsTable_WiredFromEveryColumn_RightOfTheStats()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel table = Table(vm, "player_round");
            List<CanvasConnectionViewModel> into = [.. vm.Connections.Where(w => ReferenceEquals(w.Target.Owner, table))];
            using (Assert.Multiple())
            {
                await Assert.That(table.Kind).IsEqualTo("show");
                await Assert.That(table.IsAuthorable).IsTrue();
                await Assert.That(table.IsScaffold).IsFalse();
                await Assert.That(table.IsEntry).IsFalse().Because("a table is not a stat, a highlight or a define");
                await Assert.That(table.Chips).Contains("per player_round");
                await Assert.That(table.Columns.Select(c => c.Label)).IsEquivalentTo(["side", "kills", "deaths", "assists", "damage", "survived", "kast"]);
                await Assert.That(table.Inputs.Count).IsEqualTo(7).Because("one read input per column");
                await Assert.That(table.Outputs).IsEmpty().Because("nothing reads a table");
                await Assert.That(into.Count).IsEqualTo(7);
                await Assert.That(into.All(w => w.IsRead)).IsTrue();
                await Assert.That(into.Select(w => w.Text)).IsEquivalentTo(["side", "kills", "deaths", "assists", "damage", "survived", "kast"])
                    .Because("the wire carries the column's label");
                await Assert.That(into.All(w => table.Columns.Any(c => ReferenceEquals(c.Port, w.Target)))).IsTrue()
                    .Because("each wire lands on its own column's port");
                await Assert.That(into.Single(w => w.Text == "kills").Source.Owner?.Id).IsEqualTo("round_kills");
                foreach (CanvasConnectionViewModel wire in into)
                {
                    await Assert.That(table.Location.X).IsGreaterThan(wire.Source.Owner!.Location.X).Because(wire.Text + " is left of the table");
                }

                await Assert.That(vm.Nodes.Count(n => n.IsShow)).IsEqualTo(1).Because("player_round has no scoreboard");
                await Assert.That(vm.Nodes.Any(n => n.IsParams)).IsFalse().Because("player_round has no params");
                await Assert.That(table.Surfacing).IsEmpty().Because("a table is not surfaced; it is the surface");
            }
        }
    }

    [Test]
    public async Task TheShippedCorpus_HasAShowNodePerTableAndScoreboard_WithAWirePerResolvedColumn()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        RulesetCorpus.Composed stats = corpus.Compose("player_stats");
        RulesetCorpus.Composed kast = corpus.Compose("kast");
        ChainNode board = stats.Chain.Nodes.Single(n => n.Kind == ChainNodeKind.Show);
        using (Assert.Multiple())
        {
            await Assert.That(board.Key).IsEqualTo(RulesetDocumentNodeKey.Scoreboard("player_stats"));
            await Assert.That(stats.Chain.Edges.Count(e => e.Target == board.Key)).IsEqualTo(stats.Open!.Show!.Scoreboard.Count)
                .Because("every scoreboard entry of player_stats names a stat or highlight of its own");
            await Assert.That(stats.Chain.Edges.Where(e => e.Target == board.Key).Select(e => e.Column).Distinct().Count())
                .IsEqualTo(stats.Open!.Show!.Scoreboard.Count).Because("each wire carries its column's index");
            await Assert.That(kast.Chain.Nodes.Where(n => n.Kind == ChainNodeKind.Show).Select(n => n.Name)).IsEquivalentTo(["kast_game_totals", "scoreboard"]);
            await Assert.That(kast.Chain.Edges.Any(e => e.Target == RulesetDocumentNodeKey.Show("kast", "kast_game_totals") && e.Source.Id == "kast" && e.Label == "KASTRounds"))
                .IsTrue().Because("a highlight surfaced by its count is wired from the highlight");
            foreach (RulesetCorpus.Entry entry in corpus.Entries)
            {
                RulesetCorpus.Composed composed = corpus.Compose(entry.Id);
                int tables = composed.Open!.Show?.Tables.Count ?? 0;
                int boards = composed.Open!.Show is { Scoreboard.Count: > 0 } ? 1 : 0;
                await Assert.That(composed.Chain.Nodes.Count(n => n.Kind == ChainNodeKind.Show)).IsEqualTo(tables + boards).Because(entry.Id);
            }
        }
    }

    [Test]
    public async Task WiringAStatIntoATable_AppendsAColumn_AndRemovingTheWire_TakesItOutWithTheCommentsIntact()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = File.ReadAllText(rules.UserPath("player_round"));
            CanvasNodeViewModel table = Table(vm, "player_round");
            CanvasNodeViewModel kastAssists = vm.Nodes.Single(n => n.Id == "round_kast_assists");
            await Assert.That(CanvasEditSemantics.ConnectPreview(kastAssists, table, PortKind.Read)).IsEqualTo("adds column round_kast_assists to player_round");

            vm.Connect(kastAssists, table, PortKind.Read);
            string after = File.ReadAllText(rules.UserPath("player_round"));
            table = Table(vm, "player_round");
            using (Assert.Multiple())
            {
                await Assert.That(after).Contains("        - { stat: round_kast,     label: kast }\n        - { stat: round_kast_assists, label: round_kast_assists }\n");
                await Assert.That(after.Replace("        - { stat: round_kast_assists, label: round_kast_assists }\n", "", StringComparison.Ordinal)).IsEqualTo(before)
                    .Because("the write is the one line");
                await Assert.That(table.Columns.Count).IsEqualTo(8);
                await Assert.That(vm.Connections.Any(w => ReferenceEquals(w.Target.Owner, table) && w.Source.Owner?.Id == "round_kast_assists")).IsTrue();
                await Assert.That(vm.EditStatus).IsEmpty();
            }

            vm.Connect(vm.Nodes.Single(n => n.Id == "round_kast_assists"), table, PortKind.Read);
            await Assert.That(vm.EditStatus).IsEqualTo("round_kast_assists is already a column of player_round.");

            CanvasConnectionViewModel wire = vm.Connections.Single(w => ReferenceEquals(w.Target.Owner, table) && w.Source.Owner?.Id == "round_kast_assists");
            vm.DisconnectCommand.Execute(wire);
            using (Assert.Multiple())
            {
                await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before).Because("the column goes and nothing else moves");
                await Assert.That(Table(vm, "player_round").Columns.Count).IsEqualTo(7);
            }
        }
    }

    [Test]
    public async Task RemovingTheLastColumnWire_KeepsTheTable_AsAnEmptyList()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("t", """
            ruleset: t
            for: each_player
            stats:
              kills:
                count: kill
                per: round
            show:
              tables:
                rounds:   # the one table
                  per: player_round
                  columns:
                    - { stat: kills, label: kills }
            """);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "t");
        CanvasNodeViewModel table = Table(vm, "rounds");
        vm.DisconnectCommand.Execute(vm.Connections.Single(w => ReferenceEquals(w.Target.Owner, table)));
        string text = File.ReadAllText(rules.UserPath("t"));
        using (Assert.Multiple())
        {
            await Assert.That(text).Contains("    rounds:   # the one table\n      per: player_round\n      columns: []\n");
            await Assert.That(RulesetCorpus.WhyNotLoadable(text, rules.UserPath("t"), "t")).IsNull();
            await Assert.That(Table(vm, "rounds").Columns).IsEmpty();
        }

        // And back: a wire into the empty table opens the block list again.
        vm.Connect(vm.Nodes.Single(n => n.Id == "kills"), Table(vm, "rounds"));
        await Assert.That(File.ReadAllText(rules.UserPath("t"))).Contains("      columns: \n        - { stat: kills, label: kills }\n");
    }

    [Test]
    public async Task TheGallerysTable_PlacedOnTheCanvas_IsAnEmptyTableOnTheRulesetsGrain_WhereItWasDropped()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            GalleryItem item = vm.Gallery.Single(g => g.Title == "Surfacing").Items.Single(i => i.Kind == GalleryItemKind.Table);
            vm.PlaceItem(item, new Point(640, 480));
            string text = File.ReadAllText(rules.UserPath("player_round"));
            CanvasNodeViewModel table = Table(vm, "new_table");
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("    new_table:\n      per: player_round\n      columns: []\n");
                await Assert.That(RulesetCorpus.WhyNotLoadable(text, rules.UserPath("player_round"), "player_round")).IsNull();
                await Assert.That(table.Location).IsEqualTo(new Point(640, 480));
                await Assert.That(table.Columns).IsEmpty();
                await Assert.That(table.Key).IsEqualTo(RulesetDocumentNodeKey.Show("player_round", "new_table"));
            }

            vm.Connect(vm.Nodes.Single(n => n.Id == "round_kills"), table);
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).Contains("    new_table:\n      per: player_round\n      columns: \n        - { stat: round_kills, label: round_kills }\n");

            // A team ruleset's table takes the team grain.
            rules.User("teams", "ruleset: teams\nfor: each_team\nstats:\n  wins:\n    count: round_won\n    per: match\n");
            vm.FilesChanged([rules.UserPath("teams")]);
            vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "teams");
            vm.PlaceItem(item, new Point(10, 10));
            await Assert.That(File.ReadAllText(rules.UserPath("teams"))).Contains("show:\n  tables:\n    new_table:\n      per: team_round\n      columns: []\n");
        }
    }

    [Test]
    public async Task SelectingATable_ShowsItsColumnsAndGrainInTheInspector_AndDeleteTakesTheTable()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            InspectorViewModel inspector = vm.Inspector;
            CanvasNodeViewModel table = Table(vm, "player_round");
            vm.SelectedNodes.Add(table);
            using (Assert.Multiple())
            {
                await Assert.That(inspector.Heading).IsEqualTo("TABLE player_round");
                await Assert.That(inspector.SurfacingHeading).IsEqualTo("COLUMNS");
                await Assert.That(inspector.Surfacing).IsEqualTo("7 columns");
                await Assert.That(inspector.Entries.Select(e => e.Label.Text)).IsEquivalentTo(["side", "kills", "deaths", "assists", "damage", "survived", "kast"]);
                await Assert.That(inspector.Entries.All(e => !e.Group.IsVisible)).IsTrue().Because("a column has no group");
                await Assert.That(inspector.TablePer.IsVisible).IsTrue();
                await Assert.That(inspector.TablePer.Text).IsEqualTo("player_round");
                await Assert.That(inspector.Per.IsVisible).IsFalse();
                await Assert.That(inspector.Id.IsVisible).IsFalse().Because("a table is not renamed here");
                await Assert.That(inspector.CanSurfaceNode).IsFalse();
                await Assert.That(inspector.DeleteLabel).IsEqualTo("Delete table");
                await Assert.That(inspector.CanDelete).IsTrue();
            }

            ShowEntryViewModel kills = inspector.Entries.Single(e => e.Label.Text == "kills");
            kills.Label.Text = "Kills";
            kills.Label.Commit();
            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("- { stat: round_kills,    label: Kills }");
                await Assert.That(Table(vm, "player_round").Columns.Single(c => c.Stat == "round_kills").Label).IsEqualTo("Kills");
                await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Single().IsShow).IsTrue().Because("the redraw keeps the table selected");
            }

            inspector.TablePer.Text = "player_match";
            await Assert.That(RulesetEdits.DocumentField(File.ReadAllText(rules.UserPath("player_round")), "for")).IsEqualTo("each_player");
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).Contains("      per: player_match\n");

            inspector.DeleteCommand.Execute(null);
            text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(text).DoesNotContain("show:").Because("the only table went, and the emptied block with it");
                await Assert.That(vm.Nodes.Any(n => n.IsShow)).IsFalse();
                await Assert.That(vm.Nodes.Single(n => n.Id == "round_kills").Surfacing).IsEqualTo("not shown");
            }
        }
    }

    [Test]
    public async Task AFailedBuild_StillDrawsTheTableFromTheDocument_AndIsNotLowered()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        string path = rules.UserPath("player_round");
        File.WriteAllText(path, RulesetEdits.AddStat(File.ReadAllText(path), "broken:\n  flag:\n    when: \"no_such_stat > 0\"\n  per: round\n"));
        RulesetCorpus.Composed composed = rules.Load().Compose("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(composed.Chain.IsLowered).IsFalse();
            await Assert.That(composed.Chain.Nodes.Select(n => n.Kind)).IsEquivalentTo([ChainNodeKind.Show]);
            await Assert.That(composed.Chain.Edges.Count).IsEqualTo(7).Because("the columns still wire from the document's entries");
            await Assert.That(vm.CanRunLive).IsFalse().Because("nothing lowered, so nothing can run");
            await Assert.That(Table(vm, "player_round").Columns.Count).IsEqualTo(7);
            await Assert.That(vm.Connections.Count(w => w.Target.Owner is { IsShow: true })).IsEqualTo(7);
        }
    }

    [Test]
    public async Task ATableTakesNothingButAStatOrHighlight_AndNothingStartsFromIt()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel table = Table(vm, "player_round");
            CanvasNodeViewModel death = vm.Nodes.Single(n => n.Ruleset == "~event" && n.Id == "player_death");
            CanvasNodeViewModel kills = vm.Nodes.Single(n => n.Id == "round_kills");
            GalleryItem gate = vm.Gallery.Single(g => g.Title == "Gates").Items[0];
            using (Assert.Multiple())
            {
                await Assert.That(CanvasEditSemantics.Connect(death, table).Refusal).Contains("shows stats and highlights");
                await Assert.That(CanvasEditSemantics.Connect(death, table, PortKind.Read).Refusal).Contains("shows stats and highlights");
                await Assert.That(CanvasEditSemantics.Connect(table, kills).Refusal).Contains("nothing reads a table");
                await Assert.That(CanvasEditSemantics.WireDropOptions(table)).IsEmpty();
                await Assert.That(CanvasEditSemantics.DropOnNode(gate, table).Refusal).Contains("define or a stat");
                await Assert.That(CanvasEditSemantics.Duplicate(table, "x").IsRefused).IsTrue();
                await Assert.That(CanvasConnectorViewModel.Accepts(PortKind.Read, kills.Outputs[0])).IsTrue();
                await Assert.That(CanvasConnectorViewModel.Accepts(PortKind.Read, death.Outputs[0])).IsFalse().Because("an event fires; a column is a value");
            }
        }
    }
}
