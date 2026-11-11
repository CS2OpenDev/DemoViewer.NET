#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The wire being dragged: as the editor writes its endpoints, the preview text is the sentence the
///     drop would write or the refusal, and nothing reaches the file until the drop.
/// </summary>
public class CanvasPendingConnectionTests
{
    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id);

    [Test]
    public async Task AGateOverAStat_SaysItSetsWhile_AndWritesNothing()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = File.ReadAllText(rules.UserPath("player_round"));
            CanvasConnectorViewModel output = vm.Connections.First(c => c.Source.Owner?.Id == "MatchLive").Source;

            vm.Pending.Source = output;
            vm.Pending.PreviewTarget = Node(vm, "round_kills");

            using (Assert.Multiple())
            {
                await Assert.That(vm.Pending.PreviewText).IsEqualTo("sets while: match.live");
                await Assert.That(vm.Pending.HasPreviewText).IsTrue();
                await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before).Because("a preview applies nothing");
                await Assert.That(vm.CanUndo).IsFalse();
            }
        }
    }

    [Test]
    public async Task AnEnrichmentOverACount_SaysItBindsTheFacet()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.Pending.Source = Node(vm, "enrich.kill.was_enemy_kill");
            vm.Pending.PreviewTarget = Node(vm, "round_deaths").Inputs.Single(i => i.Kind == PortKind.Read);
            await Assert.That(vm.Pending.PreviewText).IsEqualTo("binds facet enemy");
            vm.Pending.PreviewTarget = Node(vm, "round_deaths").Inputs.Single(i => i.Kind == PortKind.Trigger);
            await Assert.That(vm.Pending.PreviewText).Contains("takes an event").Because("the on port refuses a value before the body rules run");
            vm.Pending.PreviewTarget = Node(vm, "round_deaths");
            await Assert.That(vm.Pending.PreviewText).IsEqualTo("binds facet enemy").Because("a drop on the body still reads the source");
        }
    }

    [Test]
    public async Task AStatOverAFlag_SaysItJoinsTheWhen()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.Pending.Source = Node(vm, "round_damage");
            vm.Pending.PreviewTarget = Node(vm, "round_kast");
            await Assert.That(vm.Pending.PreviewText).IsEqualTo("joins when: with or round_damage > 0");
        }
    }

    [Test]
    public async Task AnEventThatFiresSeveralViews_ShowsTheRefusal()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string views = string.Join(", ", CanvasEditSemantics.ViewsFiredBy("player_death"));
            vm.Pending.Source = Node(vm, "player_death");
            vm.Pending.PreviewTarget = Node(vm, "round_kills");
            using (Assert.Multiple())
            {
                await Assert.That(views).Contains("kill");
                await Assert.That(vm.Pending.PreviewText).StartsWith($"player_death fires {views};");
                await Assert.That(vm.Pending.PreviewText).Contains("gallery");
            }
        }
    }

    [Test]
    public async Task OverNothing_OverTheCanvas_OrOverItself_TheHintShows_AndWithoutASourceNothing()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            vm.Pending.Source = Node(vm, "MatchLive");
            vm.Pending.PreviewTarget = null;
            string overNothing = vm.Pending.PreviewText;
            vm.Pending.PreviewTarget = vm;
            string overCanvas = vm.Pending.PreviewText;
            vm.Pending.PreviewTarget = Node(vm, "MatchLive");
            string overItself = vm.Pending.PreviewText;
            vm.Pending.Source = null;
            using (Assert.Multiple())
            {
                await Assert.That(overNothing).IsEqualTo(CanvasPendingConnectionViewModel.NoTargetText);
                await Assert.That(overCanvas).IsEqualTo(CanvasPendingConnectionViewModel.NoTargetText).Because("the editor's own data context is not a target");
                await Assert.That(overItself).IsEqualTo(CanvasPendingConnectionViewModel.NoTargetText);
                await Assert.That(vm.Pending.PreviewText).IsEmpty();
                await Assert.That(vm.Pending.HasPreviewText).IsFalse();
            }
        }
    }

    [Test]
    public async Task ThePreview_AgreesWithConnect_ForEveryEngineSourceAndAuthoredTarget()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            List<CanvasNodeViewModel> sources = [.. vm.Nodes.Where(n => n.IsScaffold)];
            List<CanvasNodeViewModel> targets = [.. vm.Nodes.Where(n => n.IsAuthorable && !n.IsScaffold)];
            using (Assert.Multiple())
            {
                foreach (CanvasNodeViewModel source in sources)
                {
                    foreach (CanvasNodeViewModel target in targets)
                    {
                        EditRequest request = CanvasEditSemantics.Connect(source, target);
                        string preview = CanvasEditSemantics.ConnectPreview(source, target);
                        await Assert.That(preview.Length).IsGreaterThan(0).Because($"{source.Id} to {target.Id}");
                        if (request.IsRefused)
                        {
                            await Assert.That(preview).IsEqualTo(request.Refusal ?? "").Because($"{source.Id} to {target.Id} is refused");
                        }
                        else
                        {
                            await Assert.That(preview.StartsWith("sets ", StringComparison.Ordinal) || preview.StartsWith("binds ", StringComparison.Ordinal))
                                .IsTrue().Because($"{source.Id} to {target.Id} writes: {preview}");
                        }
                    }
                }
            }
        }
    }
}
