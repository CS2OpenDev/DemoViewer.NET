#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The visual rules that are pure: which template a node takes, how big a compact scaffold row is
///     estimated, what a scaffold row calls itself, and when a wire label hides.
/// </summary>
public class CanvasVisualTests
{
    [Test]
    public async Task AScaffoldNode_TakesTheCompactTemplate_AndAnAuthoredOneTheFace()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasNodeTemplateSelector selector = new()
        {
            Authored = new FuncDataTemplate<CanvasNodeViewModel>((_, _) => new Border { Name = "face" }),
            Scaffold = new FuncDataTemplate<CanvasNodeViewModel>((_, _) => new Border { Name = "row" })
        };
        CanvasNodeViewModel scaffold = vm.Nodes.First(n => n.IsScaffold);
        CanvasNodeViewModel authored = vm.Nodes.First(n => !n.IsScaffold);
        using (Assert.Multiple())
        {
            await Assert.That(selector.Match(scaffold)).IsTrue();
            await Assert.That(selector.Match("not a node")).IsFalse();
            await Assert.That(selector.Build(scaffold)?.Name).IsEqualTo("row");
            await Assert.That(selector.Build(authored)?.Name).IsEqualTo("face");
            selector.Scaffold = null;
            await Assert.That(selector.Build(scaffold)?.Name).IsEqualTo("face").Because("without a scaffold template every node takes the face");
        }
    }

    [Test]
    public async Task AScaffoldNode_IsEstimatedAsOneRow_BetweenTheWidthBounds()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        List<CanvasNodeViewModel> scaffolds = [.. vm.Nodes.Where(n => n.IsScaffold)];
        await Assert.That(scaffolds).IsNotEmpty();
        using (Assert.Multiple())
        {
            foreach (CanvasNodeViewModel node in scaffolds)
            {
                Size size = NodeSizeEstimator.Estimate(node);
                await Assert.That(size.Height).IsEqualTo(30).Because(node.Id);
                await Assert.That(size.Width).IsGreaterThanOrEqualTo(140).Because(node.Id);
                await Assert.That(size.Width).IsLessThanOrEqualTo(260).Because(node.Id);
            }

            CanvasNodeViewModel shortest = scaffolds.MinBy(n => n.Id.Length)!;
            CanvasNodeViewModel longest = scaffolds.MaxBy(n => ScaffoldName.Of(n.Id).Length)!;
            await Assert.That(NodeSizeEstimator.WidthOf(longest)).IsGreaterThan(NodeSizeEstimator.WidthOf(shortest))
                .Because("a longer name takes a wider row");
            await Assert.That(NodeSizeEstimator.HeightOf(vm.Nodes.First(n => !n.IsScaffold))).IsGreaterThan(30)
                .Because("an authored face keeps its full estimate");
        }
    }

    [Test]
    public async Task AScaffoldName_DropsTheEnrichPrefix_AndKeepsTheRest()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ScaffoldName.Of("enrich.kill.was_enemy_kill")).IsEqualTo("kill.was_enemy_kill");
            await Assert.That(ScaffoldName.Of("player_death")).IsEqualTo("player_death");
            await Assert.That(ScaffoldName.Of("RoundActive")).IsEqualTo("RoundActive");
            await Assert.That(ScaffoldName.Of(null)).IsEqualTo("");
            await Assert.That(ScaffoldName.Instance.Convert("enrich.hurt.capped_damage", typeof(string), null, null!))
                .IsEqualTo("hurt.capped_damage");
        }
    }

    [Test]
    public async Task AWireLabel_HidesBelowTheReadableZoom()
    {
        using (Assert.Multiple())
        {
            await Assert.That(WireLabelAtZoom.Of("player_death on", 1.0)).IsEqualTo("player_death on");
            await Assert.That(WireLabelAtZoom.Of("player_death on", 0.7)).IsEqualTo("player_death on");
            await Assert.That(WireLabelAtZoom.Of("player_death on", 0.69)).IsEqualTo("");
            await Assert.That(WireLabelAtZoom.Of(null, 1.0)).IsEqualTo("");
            await Assert.That(WireLabelAtZoom.Instance.Convert(["reads", 0.5], typeof(string), null, null!)).IsEqualTo("");
            await Assert.That(WireLabelAtZoom.Instance.Convert(["reads"], typeof(string), null, null!))
                .IsEqualTo("reads").Because("no zoom reads as 1");
        }
    }

    [Test]
    public async Task ADiagnosticsBadge_IsAnErrorUnlessTheTextSaysWarning()
    {
        using (Assert.Multiple())
        {
            await Assert.That(DiagnosticSeverityToken.KeyFor("unresolved reference no_such_stat")).IsEqualTo("AccentError");
            await Assert.That(DiagnosticSeverityToken.KeyFor("Warning: keep is ignored")).IsEqualTo("AccentCaution");
            await Assert.That(DiagnosticSeverityToken.KeyFor(null)).IsEqualTo("AccentError");
        }
    }
}
