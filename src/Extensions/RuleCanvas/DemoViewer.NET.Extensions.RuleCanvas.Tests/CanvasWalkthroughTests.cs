#region

using System.ComponentModel;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     One moderately complex rule authored from an empty rules folder through the calls the canvas
///     gestures make, step by step, with the file checked after each. The YAML tab is only read, at the end.
/// </summary>
public class CanvasWalkthroughTests
{
    private static string ExpectedPath => Path.Combine(Path.GetDirectoryName(RulesTree.RepoRulesDirectory)!, "src", "Extensions", "RuleCanvas",
        "DemoViewer.NET.Extensions.RuleCanvas.Tests", "Walkthrough", "clutch_lab.expected.yaml");

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id && n.IsEntry && !n.IsDefine);

    private static string Field(string text, string id, string key) => RulesetEdits.Field(text, id, key) ?? "";

    // The writer leaves a space after an empty block's colon; an editor strips it from the committed file.
    private static List<string> Lines(string text) => [.. text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => l.TrimEnd())];

    [Test]
    public async Task AModeratelyComplexRule_IsAuthoredFromNothing_WithTheCanvasAlone()
    {
        using RulesTree rules = RulesTree.New();
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        string Text() => File.ReadAllText(rules.UserPath(CanvasWalkthrough.RulesetId));

        // 1. The New flyout writes an empty ruleset and opens it.
        await Assert.That(CanvasWalkthrough.NewRuleset(vm)).IsTrue().Because(vm.EditStatus);
        string empty = Text();
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo(CanvasWalkthrough.RulesetId);
            await Assert.That(vm.IsEditable).IsTrue();
            await Assert.That(vm.IsEmptyRuleset).IsTrue();
        }

        List<string> written = [];
        vm.PropertyChanged += Record;

        // 2. kill dropped from the gallery, renamed in the Inspector, facets and a where row from the add picker.
        CanvasWalkthrough.KillCount(vm);
        string text = Text();
        using (Assert.Multiple())
        {
            await Assert.That(Field(text, "enemy_kills", "count")).IsEqualTo("kill");
            await Assert.That(Field(text, "enemy_kills", "match.enemy")).IsEqualTo("true");
            await Assert.That(Field(text, "enemy_kills", "match.headshot")).IsEqualTo("true");
            await Assert.That(Field(text, "enemy_kills", "where")).IsEqualTo("event.AttackerBlind == true");
            await Assert.That(Node(vm, "enemy_kills").Location).IsEqualTo(new Avalonia.Point(520, 140)).Because("the rename carried the drop point");
        }

        // 3. The define stands in front of enemy_kills with its trigger, then takes a name and a gate.
        CanvasWalkthrough.CleanKillDefine(vm);
        text = Text();
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.DefineField(text, "clean_kill", "on")).IsEqualTo("kill");
            await Assert.That(RulesetEdits.DefineField(text, "clean_kill", "while")).IsEqualTo("round.active");
            await Assert.That(Field(text, "enemy_kills", "count")).IsEqualTo("clean_kill");
            await Assert.That(vm.Connections.Any(c => c.Source.Owner?.Title == "clean_kill" && c.Target.Owner?.Id == "enemy_kills")).IsTrue();
        }

        // 4. The sum's value picked on its face; the trigger picker already names the damage view.
        CanvasWalkthrough.DamageSum(vm);
        text = Text();
        using (Assert.Multiple())
        {
            await Assert.That(Field(text, "damage_dealt", "sum")).IsEqualTo("event.DmgHealth");
            await Assert.That(Field(text, "damage_dealt", "on")).IsEqualTo("damage_dealt");
            await Assert.That(Node(vm, "damage_dealt").Trigger!.Selected?.Written).IsEqualTo("damage_dealt");
        }

        // 5. A wire released on empty canvas starts the highlight; the wire editor and the face finish it.
        CanvasWalkthrough.Highlight(vm);
        text = Text();
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.HighlightField(text, "enemy_kills_highlight", "when")).IsEqualTo("enemy_kills > 2");
            await Assert.That(RulesetEdits.HighlightField(text, "enemy_kills_highlight", "score")).IsEqualTo("70");
            await Assert.That(RulesetEdits.HighlightField(text, "enemy_kills_highlight", "kind")).IsEqualTo("funny");
            await Assert.That(RulesetEdits.HighlightField(text, "enemy_kills_highlight", "title")).IsEqualTo("{player.name} clean clutch kills (round {round.number})");
            await Assert.That(Node(vm, "enemy_kills_highlight").Location).IsEqualTo(new Avalonia.Point(960, 60)).Because("the pick lands where the wire was released");
        }

        // 6. The flag's when built as rows from two wires, the connective clicked and the row editor.
        CanvasWalkthrough.ImpactFlag(vm);
        using (Assert.Multiple())
        {
            await Assert.That(Field(Text(), "impact", "flag.when")).IsEqualTo("enemy_kills > 0 or damage_dealt > 0 or player.survived");
            await Assert.That(Node(vm, "impact").Lines.Single(l => l.Key == "when").ShowRows).IsTrue().Because("the when stayed flat rows");
        }

        // 7. The table and its three columns, two relabelled.
        CanvasWalkthrough.Table(vm);
        text = Text();
        using (Assert.Multiple())
        {
            await Assert.That(text).Contains("        - { stat: enemy_kills, label: Clean kills }\n        - { stat: damage_dealt, label: Damage }\n        - { stat: impact, label: impact }\n");
            await Assert.That(vm.Nodes.Single(n => n.IsShow).Columns.Select(c => c.Stat)).IsEquivalentTo(["enemy_kills", "damage_dealt", "impact"]);
        }

        // 8. The frame.
        CanvasWalkthrough.Frame(vm);
        CanvasGroupViewModel frame = vm.Groups.Single();
        RulesetNodeLayout saved = RulesetLayoutStore.Load(rules.UserPath(CanvasWalkthrough.RulesetId));
        using (Assert.Multiple())
        {
            await Assert.That(frame.Title).IsEqualTo("kills");
            await Assert.That(frame.Members).IsEquivalentTo([Node(vm, "enemy_kills").Key, RulesetDocumentNodeKey.Define(CanvasWalkthrough.RulesetId, "clean_kill")]);
            await Assert.That(frame.Size.Height).IsLessThan(400).Because("the define sits beside its stat, so the frame holds one row");
            await Assert.That(saved.Groups.Single().Title).IsEqualTo("kills").Because("the sidecar holds the frame");
            await Assert.That(saved.Groups.Single().Members).Contains(Node(vm, "enemy_kills").Key);
        }

        // 9. The file loads clean through the resolver, every entry is wired, and the text is the expected one.
        text = Text();
        RulesetCorpus.Composed composed = rules.Load().Compose(CanvasWalkthrough.RulesetId);
        using (Assert.Multiple())
        {
            await Assert.That(composed.Report.Where(r => r.Weight == DiagnosticWeight.Error).Select(r => r.Text)).IsEmpty();
            await Assert.That(composed.Chain.IsLowered).IsTrue();
            await Assert.That(vm.DiagnosticEntries.Where(d => d.IsError).Select(d => d.Message)).IsEmpty();
            foreach (CanvasNodeViewModel entry in vm.Nodes.Where(n => n.IsAuthorable && !n.IsScaffold && !n.IsParams))
            {
                await Assert.That(vm.Connections.Any(c => c.Source.Owner == entry || c.Target.Owner == entry)).IsTrue().Because(entry.Id + " is wired");
            }

            await Assert.That(string.Join('\n', Lines(text))).IsEqualTo(string.Join('\n', Lines(File.ReadAllText(ExpectedPath))));
        }

        // Undo walks back through every write to the empty file; the frame goes first.
        vm.PropertyChanged -= Record;
        List<string> walkedBack = [Text()];
        for (int guard = 0; vm.CanUndo && guard < 200; guard++)
        {
            vm.UndoCommand.Execute(null);
            if (walkedBack[^1] != Text())
            {
                walkedBack.Add(Text());
            }
        }

        List<string> expectedBack = [.. Enumerable.Reverse(written), empty];
        using (Assert.Multiple())
        {
            await Assert.That(vm.CanUndo).IsFalse();
            await Assert.That(Text()).IsEqualTo(empty);
            await Assert.That(vm.Groups).IsEmpty();
            await Assert.That(walkedBack.Count).IsEqualTo(expectedBack.Count);
            await Assert.That(walkedBack.SequenceEqual(expectedBack)).IsTrue().Because("each undo returns the file to the text before one write");
        }

        void Record(object? sender, PropertyChangedEventArgs e)
        {
            // A redraw clears the text before it sets the new one.
            if (e.PropertyName == nameof(CanvasTabViewModel.DocumentText) && vm.DocumentText.Length > 0
                && (written.Count == 0 || written[^1] != vm.DocumentText))
            {
                written.Add(vm.DocumentText);
            }
        }
    }
}
