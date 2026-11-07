#region

using Avalonia.Controls;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The condition rows on a node face: every corpus <c>where:</c> and <c>when:</c> builds its rows
///     without an exception and writes nothing when no row changed; a row edit on a real document rewrites
///     only that row's bytes; add, remove, the bool toggle, the bare form and the connective switch each
///     write through the field writer; a nested condition and a formula stay text; and the completion fills
///     in the token at the caret.
/// </summary>
public class CanvasConditionTests
{
    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasExpressionViewModel Line(CanvasTabViewModel vm, string id, string key) =>
        vm.Nodes.Single(n => n.Id == id).Lines.Single(l => l.Key == key);

    private static string Text(RulesTree rules) => File.ReadAllText(rules.UserPath("player_round"));

    private static CanvasExpressionViewModel Standalone(string key, string text, List<(string Key, string? Value)> writes)
    {
        CanvasExpressionViewModel line = new(key, key, text, (k, v) => writes.Add((k, v)));
        line.IsEditable = true;
        return line;
    }

    // ── the corpus ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every line the factory makes for every shipped ruleset, with its context set, builds without an
    ///     exception, and committing each row from its own parts writes nothing: the rows and the text agree.
    /// </summary>
    [Test]
    public async Task EveryCorpusLine_BuildsItsRows_AndAnUntouchedRowWritesNothing()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        List<string> writes = [];
        int lines = 0, rowLines = 0, rows = 0, textLines = 0;
        CanvasNodeFactory factory = new(writeField: (node, key, value) => writes.Add($"{node.Id}.{key} = {value}"));
        foreach (RulesetCorpus.Entry entry in corpus.Entries)
        {
            IReadOnlyList<NodeFace> faces = NodeFaceBuilder.Build(corpus.Compose(entry.Id));
            string[] siblings = [.. faces.Where(f => f.IsAuthorable).Select(f => f.Id)];
            foreach (NodeFace face in faces.Where(f => f.IsAuthorable))
            {
                CanvasNodeViewModel node = factory.Create(face);
                node.IsEditable = true;
                foreach (CanvasExpressionViewModel line in node.Lines)
                {
                    lines++;
                    line.Context = new CanvasExpressionContext((face as StatFace)?.View, siblings);
                    if (!line.IsCondition)
                    {
                        continue;
                    }

                    if (line.ShowRows)
                    {
                        rowLines++;
                        await Assert.That(line.Rows.Count).IsEqualTo(line.Condition.Rows.Count).Because($"{entry.Id}/{face.Id} {line.Key}");
                        foreach (CanvasConditionRowViewModel row in line.Rows)
                        {
                            rows++;
                            await Assert.That(row.TryCommit()).IsFalse().Because($"{entry.Id}/{face.Id} {line.Key} row {row.Index} '{row.Path}'");
                        }
                    }
                    else
                    {
                        textLines++;
                        await Assert.That(line.ShowTextNote).IsTrue().Because($"{entry.Id}/{face.Id} {line.Key}");
                        await Assert.That(line.Rows.Count).IsEqualTo(0);
                    }
                }
            }
        }

        Console.WriteLine($"[rows] lines={lines} asRows={rowLines} rows={rows} asText={textLines} writes={writes.Count}");
        using (Assert.Multiple())
        {
            await Assert.That(rows).IsGreaterThan(100);
            await Assert.That(textLines).IsGreaterThan(0).Because("the corpus has nested conditions, drawn as text");
            await Assert.That(writes).IsEmpty().Because(string.Join("; ", writes));
        }
    }

    // ── row edits on a document ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheFlagsWhen_IsRows_AndEditingOneRow_RewritesOnlyThatRow()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasExpressionViewModel when = Line(vm, "round_kast", "when");
            using (Assert.Multiple())
            {
                await Assert.That(when.IsCondition).IsTrue();
                await Assert.That(when.ShowRows).IsTrue();
                await Assert.That(when.ShowTextEditor).IsFalse();
                await Assert.That(when.CanToggleText).IsTrue();
                await Assert.That(when.Rows.Count).IsEqualTo(4);
                await Assert.That(when.Connective).IsEqualTo(ConditionConnective.Or);
                await Assert.That(when.Rows[0].Path).IsEqualTo("round_kills");
                await Assert.That(when.Rows[0].Comparator).IsEqualTo(">");
                await Assert.That(when.Rows[0].Literal).IsEqualTo("0");
                await Assert.That(when.Rows[0].IsFirst).IsTrue();
                await Assert.That(when.Rows[1].IsFirst).IsFalse();
                await Assert.That(when.Rows[1].ConnectiveLabel).IsEqualTo("or");
                await Assert.That(when.Rows[2].IsBare).IsTrue();
                await Assert.That(when.Rows[2].Comparator).IsEqualTo(CanvasConditionRowViewModel.Bare);
            }

            when.Rows[0].Comparator = ">=";
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kast", "flag.when"))
                .IsEqualTo("round_kills >= 0 or round_kast_assists > 0 or player.survived or player.traded")
                .Because("a complete row writes as soon as its comparator changes");

            when = Line(vm, "round_kast", "when");
            when.Rows[1].Literal = "2";
            await Assert.That(when.Rows[1].TryCommit()).IsTrue();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kast", "flag.when"))
                .IsEqualTo("round_kills >= 0 or round_kast_assists > 2 or player.survived or player.traded");

            when = Line(vm, "round_kast", "when");
            when.Rows[3].Path = "player.alive";
            when.Rows[3].Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kast", "flag.when"))
                .IsEqualTo("round_kills >= 0 or round_kast_assists > 2 or player.survived or player.alive");
        }
    }

    [Test]
    public async Task AddingRemovingAndSwitchingTheJoin_WriteThroughTheRows()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasExpressionViewModel when = Line(vm, "round_kast", "when");
            when.AddRow();
            await Assert.That(when.Rows.Count).IsEqualTo(5);
            CanvasConditionRowViewModel added = when.Rows[4];
            using (Assert.Multiple())
            {
                await Assert.That(added.IsPending).IsTrue();
                await Assert.That(added.Index).IsEqualTo(4);
                await Assert.That(added.TryCommit()).IsFalse().Because("a row without a path is not written");
            }

            added.Path = "round_deaths";
            added.Comparator = "==";
            await Assert.That(added.TryCommit()).IsFalse().Because("a comparison without a literal is not written");
            added.Literal = "0";
            await Assert.That(added.TryCommit()).IsTrue();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kast", "flag.when"))
                .IsEqualTo("round_kills > 0 or round_kast_assists > 0 or player.survived or player.traded or round_deaths == 0");

            when = Line(vm, "round_kast", "when");
            when.AddRow();
            when.RemoveRow(when.Rows[5]);
            await Assert.That(when.Rows.Count).IsEqualTo(5).Because("removing a pending row touches nothing");
            when.RemoveRow(when.Rows[2]);
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kast", "flag.when"))
                .IsEqualTo("round_kills > 0 or round_kast_assists > 0 or player.traded or round_deaths == 0");

            when = Line(vm, "round_kast", "when");
            when.ToggleConnective();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kast", "flag.when"))
                .IsEqualTo("round_kills > 0 and round_kast_assists > 0 and player.traded and round_deaths == 0");
            await Assert.That(Line(vm, "round_kast", "when").Rows[1].ConnectiveLabel).IsEqualTo("and");
        }
    }

    [Test]
    public async Task AnEmptyWhere_StartsWithNoRows_AndTheBoolToggleAndBareFormWrite()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasExpressionViewModel where = Line(vm, "round_kills", "where");
            using (Assert.Multiple())
            {
                await Assert.That(where.ShowRows).IsTrue().Because("an empty condition is flat, with the add button only");
                await Assert.That(where.Rows.Count).IsEqualTo(0);
            }

            where.AddRow();
            where.Rows[0].Path = "event.Headshot";
            where.Rows[0].Comparator = "==";
            where.Rows[0].Literal = "true";
            where.Rows[0].Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "where")).IsEqualTo("event.Headshot == true");

            where = Line(vm, "round_kills", "where");
            using (Assert.Multiple())
            {
                await Assert.That(where.Rows[0].IsBoolLiteral).IsTrue();
                await Assert.That(where.Rows[0].ShowLiteralBox).IsFalse();
                await Assert.That(where.Rows[0].BoolLiteral).IsTrue();
            }

            where.Rows[0].BoolLiteral = false;
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "where")).IsEqualTo("event.Headshot == false");

            where = Line(vm, "round_kills", "where");
            where.Rows[0].Comparator = CanvasConditionRowViewModel.Bare;
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "where")).IsEqualTo("event.Headshot");

            where = Line(vm, "round_kills", "where");
            where.Rows[0].Comparator = "in";
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "where")).IsEqualTo("event.Headshot")
                .Because("leaving the bare form waits for a literal");
            where.Rows[0].Literal = "[true]";
            where.Rows[0].Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "round_kills", "where")).IsEqualTo("event.Headshot in [true]");
        }
    }

    [Test]
    public async Task RemovingTheOnlyRow_RemovesAWhere_AndIsRefusedForAFlagsWhen()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasExpressionViewModel where = Line(vm, "round_assists", "where");
            await Assert.That(where.Rows.Count).IsEqualTo(1);
            where.RemoveRow(where.Rows[0]);
            await Assert.That(RulesetEdits.Field(Text(rules), "round_assists", "where")).IsNull();

            CanvasExpressionViewModel when = Line(vm, "round_kast", "when");
            string before = Text(rules);
            while (when.Rows.Count > 1)
            {
                when.RemoveRow(when.Rows[^1]);
                when = Line(vm, "round_kast", "when");
            }

            await Assert.That(when.Rows.Count).IsEqualTo(1);
            when.RemoveRow(when.Rows[0]);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(Text(rules), "round_kast", "flag.when")).IsEqualTo("round_kills > 0");
                await Assert.That(vm.EditStatus).Contains("cannot be empty");
                await Assert.That(Text(rules)).IsNotEqualTo(before).Because("the earlier removals were written");
            }
        }
    }

    [Test]
    public async Task AShippedRuleset_CarriesTheRowsSwitchedOff()
    {
        using RulesTree rules = RulesTree.WithShipped("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        string before = File.ReadAllText(rules.ShippedPath("player_round"));
        CanvasExpressionViewModel when = Line(vm, "round_kast", "when");
        when.Rows[0].Comparator = "<";
        when.Rows[0].Commit();
        when.AddRow();
        when.ToggleConnective();
        using (Assert.Multiple())
        {
            await Assert.That(when.IsEditable).IsFalse();
            await Assert.That(when.Rows.Count).IsEqualTo(4).Because("nothing is added on a read-only node");
            await Assert.That(File.ReadAllText(rules.ShippedPath("player_round"))).IsEqualTo(before);
        }
    }

    // ── text, context and completion ─────────────────────────────────────────────────────────────

    [Test]
    public async Task ANestedCondition_IsTextOnly_AndCommitWritesTheWholeText()
    {
        List<(string Key, string? Value)> writes = [];
        CanvasExpressionViewModel where = Standalone("where", "a > 1 or (b and c)", writes);
        using (Assert.Multiple())
        {
            await Assert.That(where.IsFlat).IsFalse();
            await Assert.That(where.ShowRows).IsFalse();
            await Assert.That(where.ShowTextEditor).IsTrue();
            await Assert.That(where.ShowTextNote).IsTrue();
            await Assert.That(where.TextNote).IsEqualTo("edited as text");
            await Assert.That(where.CanToggleText).IsFalse();
            await Assert.That(where.Rows.Count).IsEqualTo(0);
        }

        where.AddRow();
        where.ToggleConnective();
        await Assert.That(writes).IsEmpty();

        where.Text = "a > 1 or (b and d)";
        where.Commit();
        await Assert.That(writes).IsEquivalentTo(new (string, string?)[] { ("where", "a > 1 or (b and d)") });

        CanvasExpressionViewModel broken = Standalone("when", "event.Weapon == ", writes);
        using (Assert.Multiple())
        {
            await Assert.That(broken.ShowRows).IsFalse();
            await Assert.That(broken.TextNote).StartsWith("edited as text: ");
        }
    }

    [Test]
    public async Task SwitchingAFlatConditionToText_HidesTheRows_AndTheTextEditorWritesWhole()
    {
        List<(string Key, string? Value)> writes = [];
        CanvasExpressionViewModel where = Standalone("where", "a == 1 && b == 2", writes);
        await Assert.That(where.ToggleLabel).IsEqualTo("edit as text");
        where.ToggleEditAsText();
        using (Assert.Multiple())
        {
            await Assert.That(where.ShowRows).IsFalse();
            await Assert.That(where.ShowTextEditor).IsTrue();
            await Assert.That(where.ShowTextNote).IsFalse();
            await Assert.That(where.CanToggleText).IsTrue();
            await Assert.That(where.ToggleLabel).IsEqualTo("edit as rows");
        }

        where.Text = "a == 3";
        using (Assert.Multiple())
        {
            await Assert.That(where.Rows.Count).IsEqualTo(1).Because("the rows follow the text");
            await Assert.That(where.Rows[0].Literal).IsEqualTo("3");
        }

        where.Commit();
        await Assert.That(writes).IsEquivalentTo(new (string, string?)[] { ("where", "a == 3") });

        where.Text = "  ";
        where.Commit();
        await Assert.That(writes[^1]).IsEqualTo(("where", null));
    }

    [Test]
    public async Task AFormulaAndAHighlightsTitle_AreText_AndOnlyTheFormulaCompletes()
    {
        List<(string Key, string? Value)> writes = [];
        CanvasExpressionViewModel compute = Standalone("compute", "round_kills * 2", writes);
        CanvasExpressionViewModel title = Standalone("title", "{player.name} double", writes);
        using (Assert.Multiple())
        {
            await Assert.That(compute.IsCondition).IsFalse();
            await Assert.That(compute.HasCompletion).IsTrue();
            await Assert.That(compute.ShowRows).IsFalse();
            await Assert.That(compute.ShowTextNote).IsFalse();
            await Assert.That(compute.CanToggleText).IsFalse();
            await Assert.That(title.HasCompletion).IsFalse();
            await Assert.That(title.ShowTextEditor).IsTrue();
        }

        compute.Text = "round_kills * 3";
        compute.Commit();
        await Assert.That(writes).IsEquivalentTo(new (string, string?)[] { ("compute", "round_kills * 3") });
    }

    [Test]
    public async Task TheContext_FeedsTheViewsPathsAndTheSiblings_ToEveryRow()
    {
        List<(string Key, string? Value)> writes = [];
        CanvasExpressionViewModel where = Standalone("where", "event.Weapon == \"awp\" && player.alive", writes);
        await Assert.That(where.Paths).IsEmpty().Because("no context yet");

        where.Context = new CanvasExpressionContext("kill", ["round_kills", "round_deaths"]);
        using (Assert.Multiple())
        {
            await Assert.That(where.Paths).Contains("event.Weapon");
            await Assert.That(where.Paths).Contains("enrich.kill.was_enemy_kill");
            await Assert.That(where.Paths).Contains("round_deaths");
            await Assert.That(where.Rows[1].Paths).IsEqualTo(where.Paths);
        }

        where.Context = new CanvasExpressionContext(null, ["flick_shots"]);
        using (Assert.Multiple())
        {
            await Assert.That(where.Paths.Any(p => p.StartsWith("event.", StringComparison.Ordinal))).IsFalse();
            await Assert.That(where.Paths).Contains("flick_shots");
        }

        where.Context = null;
        await Assert.That(where.Paths).IsEmpty();
    }

    [Test]
    public async Task TheCompletion_FillsInTheTokenAtTheCaret()
    {
        AutoCompleteFilterPredicate<string?> filter = ConditionRowsView.TokenFilter;
        AutoCompleteSelector<string?> selector = ConditionRowsView.TokenSelector;
        using (Assert.Multiple())
        {
            await Assert.That(filter("event.We", "event.Weapon")).IsTrue();
            await Assert.That(filter("EVENT.we", "event.Weapon")).IsTrue();
            await Assert.That(filter("event.We", "enrich.kill.was_enemy_kill")).IsFalse();
            await Assert.That(filter("round_kills * ev", "event.Weapon")).IsTrue().Because("only the trailing token is matched");
            await Assert.That(filter("round_kills * ", "event.Weapon")).IsFalse().Because("an operator is not a prefix of anything");
            await Assert.That(filter("", "event.Weapon")).IsTrue().Because("an empty editor offers every path");
            await Assert.That(filter(null, "event.Weapon")).IsTrue();
            await Assert.That(filter("x", null)).IsFalse();
            await Assert.That(selector("round_kills * ev", "event.Weapon")).IsEqualTo("round_kills * event.Weapon");
            await Assert.That(selector("ev", "event.Weapon")).IsEqualTo("event.Weapon");
            await Assert.That(selector("", "event.Weapon")).IsEqualTo("event.Weapon");
            await Assert.That(selector("a > 1 && ", "player.alive")).IsEqualTo("a > 1 && player.alive");
        }
    }

    /// <summary>
    ///     The control builds from its XAML over every shape a line takes, which is as far as a test without
    ///     a platform can take the render; the hub's UiCapture variants draw it for real.
    /// </summary>
    [Test]
    public async Task TheView_BuildsFromItsXaml_ForEveryShape()
    {
        string[][] shapes =
        [
            ["where", ""], ["where", "event.Headshot == true"], ["when", "round_kills > 0 or player.survived"],
            ["when", "a or (b and c)"], ["where", "event.Weapon == "], ["compute", "round_kills * 2"], ["title", "{player.name}"]
        ];
        foreach (string[] shape in shapes)
        {
            CanvasExpressionViewModel line = Standalone(shape[0], shape[1], []);
            line.Context = new CanvasExpressionContext("kill", ["round_kills"]);
            ConditionRowsView view = new() { DataContext = line };
            await Assert.That(view.Content).IsNotNull().Because($"{shape[0]}: '{shape[1]}'");
            line.ToggleEditAsText();
            await Assert.That(view.DataContext).IsEqualTo(line);
        }
    }

    // ── the model's connective switch ────────────────────────────────────────────────────────────

    [Test]
    public async Task WithConnective_FlipsEverySeparator_InTheAuthorsSpelling()
    {
        ConditionExpression words = ConditionExpression.Parse("round_kills > 0 or player.survived or player.traded");
        ConditionExpression symbols = ConditionExpression.Parse("a==1&&b  ==  2");
        ConditionExpression one = ConditionExpression.Parse("player.alive");
        ConditionExpression nested = ConditionExpression.Parse("a or (b and c)");
        using (Assert.Multiple())
        {
            await Assert.That(words.WithConnective(ConditionConnective.And))
                .IsEqualTo("round_kills > 0 and player.survived and player.traded");
            await Assert.That(words.WithConnective(ConditionConnective.Or)).IsEqualTo(words.Text).Because("already or");
            await Assert.That(symbols.WithConnective(ConditionConnective.Or)).IsEqualTo("a==1||b  ==  2");
            await Assert.That(one.WithConnective(ConditionConnective.Or)).IsEqualTo("player.alive");
            await Assert.That(ConditionExpression.Parse("").WithConnective(ConditionConnective.Or)).IsEqualTo("");
        }

        await Assert.That(() => nested.WithConnective(ConditionConnective.And)).Throws<InvalidOperationException>();
    }
}
