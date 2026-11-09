#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The opening fit keeps the surfaces in view: the authored box the fit frames takes in the SHOW and
///     PARAMS nodes, and when that box is too wide for the floor zoom it is framed from its right edge, so
///     the event side pans away and no surface is left under a pane edge.
/// </summary>
public class CanvasReadableFitTests
{
    // 1600x900 with the gallery (240) and the right dock (440) open, less the strip.
    private static readonly Size BothPanesOpen = new(1600 - 240 - 440, 900 - 60);

    [Test]
    public async Task OnPlayerRound_WithBothPanesOpen_TheShowNodeIsInsideTheViewport()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        (IReadOnlyList<CanvasNodeViewModel> nodes, _) = CanvasGraphBuilder.Build(corpus.Compose("player_round"));
        CanvasNodeViewModel show = nodes.Single(n => n.IsShow);

        ReadableFit fit = ReadableFit.Of(nodes.Select(n => (n.Location, n.EstimatedSize, n.IsAuthorable)), BothPanesOpen);
        Rect viewport = new(fit.Location, new Size(BothPanesOpen.Width / fit.Zoom, BothPanesOpen.Height / fit.Zoom));
        Rect showBox = new(show.Location, show.EstimatedSize);
        using (Assert.Multiple())
        {
            await Assert.That(show.IsAuthorable).IsTrue().Because("the show node counts as authored, so the fit frames it");
            await Assert.That(fit.Zoom).IsGreaterThanOrEqualTo(ReadableFit.MinZoom);
            await Assert.That(viewport.Contains(showBox.TopLeft) && viewport.Contains(showBox.BottomRight)).IsTrue()
                .Because($"the show node {showBox} is inside the viewport {viewport}");
        }
    }

    [Test]
    public async Task AnAuthoredBoxTooWideForTheFloor_IsFramedFromItsRightEdge()
    {
        Size viewport = new(1000, 600);
        // Events far left, stats and a surface spanning 2000 wide: 1200 px at the floor, so the left 200 pan away.
        List<(Point, Size, bool)> nodes =
        [
            (new Point(-3000, 0), new Size(200, 100), false),
            (new Point(0, 0), new Size(360, 300), true),
            (new Point(1600, 0), new Size(320, 300), true)
        ];

        ReadableFit fit = ReadableFit.Of(nodes, viewport);
        double right = 1920 + ReadableFit.Pad;
        using (Assert.Multiple())
        {
            await Assert.That(fit.FramesSourceToStat).IsTrue();
            await Assert.That(fit.Zoom).IsEqualTo(ReadableFit.MinZoom);
            await Assert.That(fit.Location.X).IsEqualTo(right - viewport.Width / ReadableFit.MinZoom).Within(0.001)
                .Because("the viewport's right edge lands on the authored box's right edge");
            await Assert.That(fit.Location.Y).IsEqualTo(-ReadableFit.Pad);
        }

        // Narrow enough to fit at the floor: framed from the left as before.
        List<(Point, Size, bool)> narrow =
        [
            (new Point(-3000, 0), new Size(200, 100), false),
            (new Point(0, 0), new Size(360, 300), true),
            (new Point(800, 0), new Size(320, 300), true)
        ];
        await Assert.That(ReadableFit.Of(narrow, viewport).Location.X).IsEqualTo(-ReadableFit.Pad);
    }
}
