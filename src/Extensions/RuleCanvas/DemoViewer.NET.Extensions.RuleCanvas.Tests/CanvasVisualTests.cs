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

    /// <summary>
    ///     A define face is estimated as an authored node with a count's rows less the actor toggle and the
    ///     label, is placed as one (between the event and the stat that fires on it), and is tinted by its own
    ///     kind token on the minimap.
    /// </summary>
    [Test]
    public async Task ADefineNode_IsSizedLikeACountLessTheActorToggle_PlacedBeforeItsStat_AndTintedByItsToken()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("sizes", """
            ruleset: sizes
            title: Sizes
            for: each_player

            define:
              named_kill:
                on: kill
                match: { enemy: true }
                where: "event.Headshot == true"

            stats:
              count_kill:
                count: kill
                match: { enemy: true }
                where: "event.Headshot == true"
                per: round
              fired:
                count: named_kill
                per: round
            """);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "sizes";
        vm.OnActivated(null!);
        CanvasNodeViewModel define = vm.Nodes.Single(n => n.IsDefine);
        CanvasNodeViewModel count = vm.Nodes.Single(n => n.Id == "count_kill");
        CanvasNodeViewModel fired = vm.Nodes.Single(n => n.Id == "fired");
        CanvasNodeViewModel eventNode = vm.Nodes.Single(n => n.IsScaffold && n.Id == "player_death");
        using (Assert.Multiple())
        {
            await Assert.That(vm.Diagnostics).IsEmpty();
            await Assert.That(define.IsAuthorable).IsTrue().Because("the fit and the placement treat it as an authored node");
            await Assert.That(count.ShowActorToggle).IsTrue();
            await Assert.That(define.ShowActorToggle).IsFalse();
            await Assert.That(NodeSizeEstimator.HeightOf(count) - NodeSizeEstimator.HeightOf(define)).IsEqualTo(22)
                .Because("the same rows: trigger, facets, add-facet, gate row, where line; only the actor toggle differs");
            await Assert.That(NodeSizeEstimator.WidthOf(define)).IsEqualTo(NodeSizeEstimator.WidthOf(count)).Because("the names are one length");
            await Assert.That(NodeSizeEstimator.HeightOf(define)).IsGreaterThan(30).Because("a define is a face, not a scaffold row");
            await Assert.That(define.EstimatedSize).IsEqualTo(NodeSizeEstimator.Estimate(define));
            await Assert.That(eventNode.Location.X).IsLessThan(define.Location.X).Because("the event fires the define");
            await Assert.That(define.Location.X).IsLessThan(fired.Location.X).Because("the define fires the stat");
            await Assert.That(RuleKindToken.Instance.Convert(define.Kind, typeof(string), null, null!)).IsEqualTo("RuleKindDefine")
                .Because("the minimap tints a define in its own token");
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
            await Assert.That(WireLabelAtZoom.Of("player_death on", ReadableFit.MinZoom)).IsEqualTo("player_death on")
                .Because("the opening zoom never hides the labels");
            await Assert.That(WireLabelAtZoom.Of("player_death on", 0.55)).IsEqualTo("player_death on");
            await Assert.That(WireLabelAtZoom.Of("player_death on", 0.54)).IsEqualTo("");
            await Assert.That(WireLabelAtZoom.Of(null, 1.0)).IsEqualTo("");
            await Assert.That(WireLabelAtZoom.Instance.Convert(["reads", 0.5], typeof(string), null, null!)).IsEqualTo("");
            await Assert.That(WireLabelAtZoom.Instance.Convert(["reads"], typeof(string), null, null!))
                .IsEqualTo("reads").Because("no zoom reads as 1");
        }
    }

    // Sugiyama centres a layer on one x; two sources of different widths then start at different lefts.
    [Test]
    public async Task APlacedLayer_SharesOneLeftEdge()
    {
        RulesetDocumentNodeKey a = new("r", "a"), b = new("r", "b"), c = new("r", "c"), d = new("r", "d");
        IReadOnlyList<CanvasPlacement.Item> items = [new(a, 200, 100), new(b, 320, 140), new(c, 260, 120), new(d, 180, 100)];
        IReadOnlyList<RulesetDocumentEdge> edges = [new(a, c, "a"), new(b, c, "b"), new(c, d, "c")];
        IReadOnlyDictionary<RulesetDocumentNodeKey, Point> placed = CanvasPlacement.Place(items, edges);
        using (Assert.Multiple())
        {
            await Assert.That(placed[a].X).IsEqualTo(placed[b].X).Because("a and b are one layer and start at the same left");
            await Assert.That(placed[c].X).IsGreaterThanOrEqualTo(placed[b].X + 320).Because("the next layer stays clear of the widest source");
            await Assert.That(placed[d].X).IsGreaterThanOrEqualTo(placed[c].X + 260);
            foreach (CanvasPlacement.Item one in items)
            {
                foreach (CanvasPlacement.Item other in items.Where(o => o != one))
                {
                    Rect first = new(placed[one.Key], new Size(one.Width, one.Height));
                    Rect second = new(placed[other.Key], new Size(other.Width, other.Height));
                    await Assert.That(first.Intersects(second)).IsFalse().Because(one.Key.Id + " over " + other.Key.Id);
                }
            }
        }
    }

    [Test]
    public async Task ADiagnosticsBadge_IsPaintedByWeight_AndTheStripCountsByWeight()
    {
        using (Assert.Multiple())
        {
            await Assert.That(RuleDiagnostic.TokenFor(DiagnosticWeight.Error)).IsEqualTo("AccentError");
            await Assert.That(RuleDiagnostic.TokenFor(DiagnosticWeight.Warning)).IsEqualTo("RuleDiagnosticWarning");
            await Assert.That(RuleDiagnostic.WorstToken([DiagnosticWeight.Warning, DiagnosticWeight.Error])).IsEqualTo("AccentError")
                .Because("one error outweighs any number of warnings");
            await Assert.That(RuleDiagnostic.WorstToken([DiagnosticWeight.Warning])).IsEqualTo("RuleDiagnosticWarning");
            await Assert.That(RuleDiagnostic.Summary([DiagnosticWeight.Error, DiagnosticWeight.Error, DiagnosticWeight.Warning])).IsEqualTo("2 errors, 1 warning");
            await Assert.That(RuleDiagnostic.Summary([DiagnosticWeight.Error])).IsEqualTo("1 error");
            await Assert.That(RuleDiagnostic.Summary([DiagnosticWeight.Warning, DiagnosticWeight.Warning])).IsEqualTo("2 warnings");
            await Assert.That(RuleDiagnostic.Summary([])).IsEmpty();
            await Assert.That(new CanvasDiagnostic(null, "x", DiagnosticWeight.Warning).Token).IsEqualTo("RuleDiagnosticWarning");
            await Assert.That(new CanvasDiagnostic(null, "x").Token).IsEqualTo("AccentError");
        }
    }
}
