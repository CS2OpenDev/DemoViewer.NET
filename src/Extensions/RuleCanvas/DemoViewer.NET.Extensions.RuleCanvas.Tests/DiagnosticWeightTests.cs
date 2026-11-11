#region

using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Every diagnostic carries a weight. An error is something that cannot run; a warning is something the
///     engine tolerated or the canvas noticed, and a warning never blanks anything: an unfinished entry draws
///     as its face with the warning on it, the rest of the ruleset lowers, and the strip counts the two apart.
/// </summary>
public class DiagnosticWeightTests
{
    private static CanvasTabViewModel Open(RulesTree rules, string id)
    {
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = id;
        vm.OnActivated(null!);
        return vm;
    }

    [Test]
    public async Task TheShippedCorpus_HasOneKindOfError_TheContactTierWithoutGeometry_AndNoBlankCanvas()
    {
        string shipped = RuleSetLocator.ResolveShippedRulesDirectory();
        RulesetCorpus corpus = RulesetCorpus.Load([(shipped, "shipped")]);
        List<(string Id, RuleDiagnostic Diagnostic)> errors = [];
        List<string> unlowered = [];
        foreach (RulesetCorpus.Entry entry in corpus.Entries)
        {
            RulesetCorpus.Composed composed = corpus.Compose(entry.Id);
            errors.AddRange(composed.Report.Where(r => r.IsError).Select(r => (entry.Id, r)));
            if (!composed.Chain.IsLowered)
            {
                unlowered.Add(entry.Id);
            }
        }

        Console.WriteLine($"[weights] {corpus.Entries.Count} rulesets, {errors.Count} errors in " + string.Join(", ", errors.Select(e => e.Diagnostic.RulesetId).Distinct()));
        using (Assert.Multiple())
        {
            await Assert.That(unlowered).IsEmpty().Because(string.Join(", ", unlowered));
            await Assert.That(errors.Select(e => e.Id).Distinct()).Contains("aim_rating");
            await Assert.That(errors.Select(e => e.Diagnostic.RulesetId ?? "").Distinct()).IsEquivalentTo(["aim_rating", "player_match"])
                .Because("both declare the contact tier, which cannot run without geometry; nothing else in the corpus is an error: "
                         + string.Join("; ", errors.Select(e => e.Id + ": " + e.Diagnostic.Text)));
            await Assert.That(errors.All(e => e.Diagnostic.Message.Contains("geometry", StringComparison.Ordinal))).IsTrue()
                .Because(string.Join("; ", errors.Select(e => e.Diagnostic.Message)));
            await Assert.That(errors.All(e => e.Diagnostic.Line > 0)).IsTrue().Because("a skip is said at its entry");
        }
    }

    [Test]
    public async Task ABrokenReference_IsAnError_AndAStrayKey_IsAWarning_CountedApartOnTheStrip()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("mixed", """
            ruleset: mixed
            title: Mixed
            for: each_player

            stats:
              kills:
                count: kill
                per: round
                nonsense: true
              ratio:
                compute: "kills / no_such_stat"
                per: match
            """);
        RulesetCorpus.Composed composed = rules.Load().Compose("mixed");
        CanvasTabViewModel vm = Open(rules, "mixed");
        CanvasNodeViewModel kills = vm.Nodes.Single(n => n.Id == "kills");
        CanvasNodeViewModel ratio = vm.Nodes.Single(n => n.Id == "ratio");
        using (Assert.Multiple())
        {
            await Assert.That(composed.Report.Any(r => r.IsError && r.Message.Contains("no_such_stat", StringComparison.Ordinal))).IsTrue();
            await Assert.That(composed.Report.Any(r => r.IsWarning && r.Message.Contains("nonsense", StringComparison.Ordinal))).IsTrue()
                .Because("the loader dropped the key and ran the rest");
            await Assert.That(composed.Diagnostics.Count).IsEqualTo(composed.Report.Count).Because("the text list is the report, one line each");
            await Assert.That(kills.Diagnostics.Select(d => d.Weight)).IsEquivalentTo([DiagnosticWeight.Warning]);
            await Assert.That(kills.DiagnosticsToken).IsEqualTo("RuleDiagnosticWarning");
            await Assert.That(kills.HasErrors).IsFalse();
            await Assert.That(ratio.Diagnostics.Any(d => d.IsError)).IsTrue();
            await Assert.That(ratio.DiagnosticsToken).IsEqualTo("AccentError");
            await Assert.That(vm.ErrorCount).IsEqualTo(1);
            await Assert.That(vm.WarningCount).IsEqualTo(1);
            await Assert.That(vm.DiagnosticSummary).IsEqualTo("1 error, 1 warning");
            await Assert.That(vm.DiagnosticsToken).IsEqualTo("AccentError").Because("one error paints the strip red");
            await Assert.That(vm.DiagnosticEntries.Single(d => d.Node == kills).Token).IsEqualTo("RuleDiagnosticWarning");
            await Assert.That(vm.DiagnosticEntries.Single(d => d.Node == ratio).Token).IsEqualTo("AccentError");
            await Assert.That(vm.DiagnosticEntries.Count(d => d.Message.Contains("unwired", StringComparison.Ordinal))).IsEqualTo(0)
                .Because("nothing lowered, which the error explains; an unwired warning on top would blame the entries");
        }
    }

    [Test]
    [Arguments("no_view", "kills", "count:", "has no trigger: give count: a view")]
    [Arguments("no_on", "dmg", "sum: event.DmgHealth", "has no trigger: add on: with a view")]
    [Arguments("empty_when", "f", "flag:\n      when: \"\"", "has an empty when: type the test")]
    [Arguments("no_formula", "c", "compute: \"\"", "has no formula")]
    public async Task AnUnfinishedStat_DrawsWithAWarningSayingTheFix_AndTheRestStillLowers(string name, string id, string body, string expected)
    {
        using RulesTree rules = RulesTree.New();
        rules.User(name, $"""
            ruleset: {name}
            title: Probe
            for: each_player

            stats:
              whole:
                count: kill
                per: round
              {id}:
                {body}
                per: round
            """);
        RulesetCorpus.Composed composed = rules.Load().Compose(name);
        CanvasTabViewModel vm = Open(rules, name);
        CanvasNodeViewModel node = vm.Nodes.Single(n => n.Id == id);
        using (Assert.Multiple())
        {
            await Assert.That(composed.Chain.IsLowered).IsTrue().Because("the unfinished entry is kept out of what the resolver is handed: " + string.Join("; ", composed.Diagnostics));
            await Assert.That(composed.Report.Any(r => r.IsError)).IsFalse().Because(string.Join("; ", composed.Diagnostics));
            await Assert.That(composed.Evaluable.Single(d => d.Id == name).Stats.Select(s => s.Id)).IsEquivalentTo(["whole"]);
            await Assert.That(composed.Documents[name].Stats.Count).IsEqualTo(2).Because("the drawn document is the whole one");
            await Assert.That(node.IsAuthorable).IsTrue();
            await Assert.That(node.Diagnostics.Select(d => d.Weight)).IsEquivalentTo([DiagnosticWeight.Warning]);
            await Assert.That(node.DiagnosticsText).Contains(id + " " + expected);
            await Assert.That(node.DiagnosticsToken).IsEqualTo("RuleDiagnosticWarning");
            await Assert.That(vm.Connections.Any(c => c.Target.Owner?.Id == "whole")).IsTrue().Because("the whole stat still has its event wire");
            await Assert.That(vm.DiagnosticSummary).IsEqualTo("1 warning");
            await Assert.That(vm.DiagnosticsToken).IsEqualTo("RuleDiagnosticWarning");
            await Assert.That(vm.DiagnosticEntries.Single().Node).IsSameReferenceAs(node);
            await Assert.That(vm.Status).DoesNotContain("unwired");
        }
    }

    [Test]
    public async Task ABlankHighlight_AnEmptyTable_AndAnUnusedDefine_AreWarningsAtTheirNodes()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("loose", """
            ruleset: loose
            title: Loose
            for: each_player

            define:
              spare:
                on: death

            stats:
              kills:
                count: kill
                per: round

            highlights:
              blank:
                when: ""
                per: round

            show:
              tables:
                empty:
                  per: player_round
                  columns: []
            """);
        RulesetCorpus.Composed composed = rules.Load().Compose("loose");
        CanvasTabViewModel vm = Open(rules, "loose");
        CanvasNodeViewModel blank = vm.Nodes.Single(n => n.Id == "blank");
        CanvasNodeViewModel table = vm.Nodes.Single(n => n.IsShow);
        CanvasNodeViewModel define = vm.Nodes.Single(n => n.IsDefine);
        using (Assert.Multiple())
        {
            await Assert.That(composed.Chain.IsLowered).IsTrue().Because(string.Join("; ", composed.Diagnostics));
            await Assert.That(composed.Report.All(r => r.IsWarning)).IsTrue().Because(string.Join("; ", composed.Diagnostics));
            await Assert.That(composed.Evaluable.Single(d => d.Id == "loose").Highlights).IsEmpty().Because("a blank when: would fail the parse");
            await Assert.That(blank.IsHighlight).IsTrue();
            await Assert.That(blank.DiagnosticsText).Contains("blank has a blank when: type the test, or wire a stat into its when port");
            await Assert.That(blank.Lines.Any(l => l.Key == "when")).IsTrue().Because("the face is the highlight's, with its when: editor");
            await Assert.That(table.DiagnosticsText).Contains("table empty has no columns: wire a stat into it");
            await Assert.That(define.DiagnosticsText).Contains("nothing fires on define spare: set a stat's trigger to it");
            await Assert.That(vm.WarningCount).IsEqualTo(3);
            await Assert.That(vm.ErrorCount).IsEqualTo(0);
            await Assert.That(vm.DiagnosticEntries.All(d => d.Node is not null)).IsTrue().Because("each warning sits on its node, none at the ruleset");
            await Assert.That(vm.Nodes.Single(n => n.Id == "kills").HasDiagnostics).IsFalse();
        }
    }

    [Test]
    public async Task AnUnwiredEntry_IsAWarningOnItsNode_CountedWithTheDiagnostics()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("idle", """
            ruleset: idle
            title: Idle
            for: each_player

            params:
              min_kills: { type: int, default: 2 }

            stats:
              kills:
                count: kill
                per: round
            """);
        CanvasTabViewModel vm = Open(rules, "idle");
        CanvasNodeViewModel parameters = vm.Nodes.Single(n => n.IsParams);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Connections.Any(c => c.Source.Owner == parameters)).IsFalse();
            await Assert.That(parameters.Diagnostics.Select(d => d.Weight)).IsEquivalentTo([DiagnosticWeight.Warning]);
            await Assert.That(parameters.DiagnosticsText).Contains("params is unwired: no entry reads a params.<name> yet");
            await Assert.That(vm.DiagnosticSummary).IsEqualTo("1 warning");
            await Assert.That(vm.HasDiagnostics).IsTrue().Because("the strip badge counts the unwired warning like any other line");
            await Assert.That(vm.DiagnosticEntries.Single().Node).IsSameReferenceAs(parameters);
            await Assert.That(vm.Status).IsEqualTo($"{vm.Nodes.Count} nodes, {vm.Connections.Count} wires");
            await Assert.That(vm.Nodes.Where(n => n.IsScaffold).All(n => !n.HasDiagnostics)).IsTrue().Because("an engine node is never unwired by the author");
        }

        vm.SelectDiagnosticCommand.Execute(vm.DiagnosticEntries.Single());
        await Assert.That(vm.SelectedNodes).Contains(parameters);
    }
}
