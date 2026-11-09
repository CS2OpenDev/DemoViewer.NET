#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     An entry whose body the loader rejects never reaches the document, sometimes without a word: the
///     text's keys are scanned against what loaded and each missing one is an error in the list, with the
///     loader's reason when it gave one, that jumps the YAML tab to its line.
/// </summary>
public class DroppedEntryTests
{
    private const string Broken = "stats:\n  listed:\n    - count: kill\n  scalar: 5\n  kills_{weapon}:\n    count: kill\n    for_each: { weapon: [ak47, m4a1] }\n";

    [Test]
    public async Task AnEntryTheLoaderDrops_IsAnErrorInTheList_ThatJumpsToItsLine()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        string path = rules.UserPath("player_round");
        string text = File.ReadAllText(path).Replace("stats:\n", Broken, StringComparison.Ordinal);
        File.WriteAllText(path, text);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "player_round";
        vm.OnActivated(null!);
        int listedLine = Array.IndexOf(text.Split('\n'), "  listed:") + 1;
        int scalarLine = Array.IndexOf(text.Split('\n'), "  scalar: 5") + 1;
        List<CanvasDiagnostic> errors = [.. vm.DiagnosticEntries.Where(d => d.IsError)];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Nodes.Any(n => n.Id == "listed" || n.Id == "scalar")).IsFalse().Because("neither reaches the document");
            await Assert.That(errors.Select(e => (e.Message, e.Line))).IsEquivalentTo(
            [
                ("listed did not load: stat 'listed' must be a map with a kind key", listedLine),
                ("scalar did not load: stat 'scalar' must be a map with a kind key", scalarLine)
            ]).Because("a for_each template is held under the ids it expands to");
            await Assert.That(errors.Select(e => e.Where)).IsEquivalentTo([$"line {listedLine}", $"line {scalarLine}"]);
            await Assert.That(vm.DiagnosticEntries.Count(d => d.Message.Contains("must be a map with a kind key", StringComparison.Ordinal))).IsEqualTo(2)
                .Because("the loader's own warnings are carried by the errors, not repeated");
            await Assert.That(vm.ErrorCount).IsEqualTo(2);
        }

        int jumped = 0;
        vm.JumpRequested += line => jumped = line;
        vm.SelectDiagnosticCommand.Execute(errors.Single(e => e.Message.StartsWith("scalar", StringComparison.Ordinal)));
        using (Assert.Multiple())
        {
            await Assert.That(jumped).IsEqualTo(scalarLine);
            await Assert.That(vm.SelectedLine).IsEqualTo(scalarLine);
            await Assert.That(vm.ShowSource).IsTrue();
            await Assert.That(vm.DockTab).IsEqualTo(1);
        }
    }

    [Test]
    public async Task DroppedEntries_ReadsEverySection_AndNothingWhenAllLoaded()
    {
        const string text = "ruleset: probe\ntitle: P\nfor: each_player\nstats:\n  good:\n    count: kill\n  kills_{weapon}:\n    count: kill\n    for_each: { weapon: [ak47, m4a1] }\n"
                            + "highlights:\n  h:\n    when: good\n    title: t\n  h2: 5\ndefine:\n  d1:\n    on: kill\n";
        CS2DemoKit.Analysis.Yaml.RulesetDocumentLoader.Outcome outcome = CS2DemoKit.Analysis.Yaml.RulesetDocumentLoader.Load(text, "/x/probe.rules.yaml");
        IReadOnlyList<DroppedEntry> dropped = DroppedEntries.Of(text, outcome.Doc!, []);
        using (Assert.Multiple())
        {
            await Assert.That(dropped.Select(d => (d.Id, d.Section, d.Line))).IsEquivalentTo([("h2", EntrySection.Highlight, 14)]);
            await Assert.That(dropped[0].Diagnostic.Message).IsEqualTo("h2 did not load: its body was rejected").Because("no report was given");
            await Assert.That(DroppedEntries.Of("not: [yaml", outcome.Doc!, [])).IsEmpty();
        }
    }
}
