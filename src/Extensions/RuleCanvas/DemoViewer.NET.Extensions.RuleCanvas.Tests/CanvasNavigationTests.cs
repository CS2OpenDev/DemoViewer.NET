#region

using Avalonia.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Following a reference through the view model: a dependency output or a cross-ruleset wire to the entry
///     it reads in the other ruleset, a define to the stats on it and back, a diagnostic to its node, and
///     Alt+Left back to where the jump left from. Every jump selects and asks the view to frame.
/// </summary>
public class CanvasNavigationTests
{
    private static CanvasTabViewModel Open(RulesTree rules, string id)
    {
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = id;
        vm.OnActivated(null!);
        return vm;
    }

    [Test]
    public async Task GoToDefinition_OpensTheDependency_SelectsTheEntry_AndAltLeftComesBack()
    {
        using RulesTree rules = RulesetCorpusReplaceTests.CorpusWithUser("player_stats");
        CanvasTabViewModel vm = Open(rules, "player_stats");
        int frames = 0;
        vm.FrameRequested += () => frames++;
        CanvasNodeViewModel hltv = vm.Nodes.Single(n => n.Id == "HLTV");
        vm.SelectedNodes.Add(hltv);
        CanvasConnectionViewModel read = vm.Connections.Single(c => c.Source.Owner is { IsDependency: true } && c.Target.Owner?.Id == "HLTV");
        using (Assert.Multiple())
        {
            await Assert.That(read.CanGoToDefinition).IsTrue();
            await Assert.That(vm.CanGoBack).IsFalse();
        }

        await Assert.That(vm.GoToDefinition(read)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("kast").Because("the picker switches to the ruleset read");
            await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Select(n => n.Id)).IsEquivalentTo(["kast_pct"]);
            await Assert.That(vm.SelectedLine).IsGreaterThan(0).Because("the entry is the open document's now, so the pane jumps too");
            await Assert.That(frames).IsEqualTo(1);
            await Assert.That(vm.CanGoBack).IsTrue();
        }

        vm.NavigateBackCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("player_stats");
            await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Select(n => n.Id)).IsEquivalentTo(["HLTV"]).Because("back to the selection the jump left");
            await Assert.That(frames).IsEqualTo(2);
            await Assert.That(vm.CanGoBack).IsFalse();
        }

        // On the dependency node itself: Open switches, Go to definition on an output selects the entry.
        CanvasNodeViewModel dependency = vm.Nodes.Single(n => n.IsDependency);
        await Assert.That(vm.GoToDefinition(dependency, "kast_pct")).IsTrue();
        await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Single().Key).IsEqualTo(new RulesetDocumentNodeKey("kast", "kast_pct"));
        vm.NavigateBackCommand.Execute(null);
        await Assert.That(vm.GoToDefinition(vm.Nodes.Single(n => n.Id == "HLTV"))).IsFalse().Because("the open document's own node has nowhere else to go");
        await Assert.That(vm.OpenRuleset("kast")).IsTrue();
        await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("kast");
        await Assert.That(vm.OpenRuleset("no_such")).IsFalse();
    }

    [Test]
    public async Task TheHistory_IsTwoDeep()
    {
        using RulesTree rules = RulesetCorpusReplaceTests.CorpusWithUser("player_match");
        CanvasTabViewModel vm = Open(rules, "player_match");
        await Assert.That(vm.Nodes.Count(n => n.IsDependency)).IsEqualTo(2).Because("player_match uses player_stats and kast");

        await Assert.That(vm.NavigateTo("player_stats", null)).IsTrue();
        await Assert.That(vm.NavigateTo("kast", null)).IsTrue();
        await Assert.That(vm.NavigateTo("player_round", null)).IsTrue();
        vm.NavigateBackCommand.Execute(null);
        await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("kast");
        vm.NavigateBackCommand.Execute(null);
        await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("player_stats");
        using (Assert.Multiple())
        {
            await Assert.That(vm.CanGoBack).IsFalse().Because("the first jump fell off a two-deep history");
            await Assert.That(vm.NavigateTo("no_such", null)).IsFalse();
        }
    }

    [Test]
    public async Task ADefine_SelectsTheStatsFiringOnIt_AndAStat_SelectsItsDefine()
    {
        using RulesTree rules = RulesTree.WithUser("post_plant_double");
        CanvasTabViewModel vm = Open(rules, "post_plant_double");
        int frames = 0;
        vm.FrameRequested += () => frames++;
        CanvasNodeViewModel define = vm.Nodes.Single(n => n.IsDefine);
        CanvasNodeViewModel kills = vm.Nodes.Single(n => n.Id == "post_plant_kills");
        using (Assert.Multiple())
        {
            await Assert.That(kills.FiresOnDefine).IsTrue();
            await Assert.That(vm.Nodes.Single(n => n.Id == "plant_tick").FiresOnDefine).IsFalse();
        }

        vm.ShowStatsFiringOn(define);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Select(n => n.Id)).IsEquivalentTo(["post_plant_kills", "kill_ticks"]);
            await Assert.That(frames).IsEqualTo(1);
        }

        vm.GoToDefine(kills);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Select(n => n.Key)).IsEquivalentTo([define.Key]);
            await Assert.That(frames).IsEqualTo(2);
        }

        vm.GoToDefine(vm.Nodes.Single(n => n.Id == "plant_tick"));
        await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Select(n => n.Key)).IsEquivalentTo([define.Key]).Because("a stat on an event goes nowhere");
    }

    [Test]
    public async Task ADiagnosticLine_SelectsAndFramesItsNode()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("broken", """
            ruleset: broken
            title: Broken
            for: each_player

            stats:
              kills:
                count: kill
                per: round
              ratio:
                compute: "kills / no_such_stat"
                per: match

            """);
        CanvasTabViewModel vm = Open(rules, "broken");
        int frames = 0;
        vm.FrameRequested += () => frames++;
        CanvasDiagnostic inNode = vm.DiagnosticEntries.First(d => d.Node is not null);
        vm.SelectDiagnosticCommand.Execute(inNode);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedNodes).Contains(inNode.Node!);
            await Assert.That(frames).IsEqualTo(1);
        }

        vm.SelectDiagnosticCommand.Execute(vm.DiagnosticEntries.FirstOrDefault(d => d.Node is null));
        await Assert.That(frames).IsEqualTo(1).Because("a line about the ruleset as a whole selects nothing");
    }

    [Test]
    public async Task AltLeft_IsTheBackKey_AndPlainLeftIsNot()
    {
        using (Assert.Multiple())
        {
            await Assert.That(CanvasKeyMap.Resolve(Key.Left, KeyModifiers.Alt)).IsEqualTo(CanvasKeyAction.Back);
            await Assert.That(CanvasKeyMap.Resolve(Key.Left, KeyModifiers.None)).IsEqualTo(CanvasKeyAction.None).Because("the editor's own arrows nudge the selection");
            await Assert.That(CanvasKeyMap.Resolve(Key.Left, KeyModifiers.Control)).IsEqualTo(CanvasKeyAction.None);
        }
    }
}
