#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The viewer panel's content from a track: a stepped sparkline on the run's tick axis with the largest
///     value at the top, a marker where the playhead is, the fire and final lines, the sampled note, and a
///     breakdown of at most ten players with the selected one marked.
/// </summary>
public class CanvasLiveViewerViewModelTests
{
    private static readonly RulesetDocumentNodeKey _kills = new("r", "kills");
    private static readonly LiveInstance _game = new(0, null, null);
    private static readonly LiveInstance _alice = new(1, 0, "Alice");
    private static readonly LiveInstance _bob = new(2, 1, "Bob");

    private static LiveValuesRun Run(Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> nodes, params LiveInstance[] instances) =>
        new("/d.dem", "p", 64, 0, 1000, instances, nodes, [], TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0);

    [Test]
    public async Task TheSparkline_StepsThroughTheSeries_OnTheRunsTickAxis()
    {
        NodeTrack track = new(3, 100, 900, "3", 3f, true) { Series = LiveCanned.Counting(100, 500, 900) };
        LiveValuesRun run = Run(new Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> { [(_kills, 1)] = track }, _game, _alice);

        CanvasLiveViewerViewModel viewer = CanvasLiveViewerViewModel.Of(run, _kills, track, _alice, false, false, null);
        using (Assert.Multiple())
        {
            // x: 100/1000 of 160 = 16, 500 = 80, 900 = 144; y: 1 at the bottom inset (34), 2 mid (18), 3 at the top inset (2).
            await Assert.That(viewer.SparkPoints).IsEquivalentTo([new Point(16, 34), new Point(80, 34), new Point(80, 18), new Point(144, 18), new Point(144, 2), new Point(160, 2)]);
            await Assert.That(viewer.HasSeries).IsTrue();
            await Assert.That(viewer.HasPlayhead).IsFalse();
            await Assert.That(viewer.Fires).IsEqualTo("fired 3 times, first tick 100, last tick 900");
            await Assert.That(viewer.FinalText).IsEqualTo("final 3");
            await Assert.That(viewer.HasMessage).IsFalse();
        }

        viewer.SetPlayhead(500);
        using (Assert.Multiple())
        {
            await Assert.That(viewer.HasPlayhead).IsTrue();
            await Assert.That(viewer.PlayheadMargin).IsEqualTo(new Thickness(80, 0, 0, 0));
        }

        viewer.SetPlayhead(null);
        await Assert.That(viewer.HasPlayhead).IsFalse();
    }

    [Test]
    public async Task AFlatSeries_SitsMidBox_AndABoolStepsBetweenOnAndOff()
    {
        NodeTrack flat = new(2, 100, 500, "1", 1f, true) { Series = ValueSeries.From([new ValuePoint(100, true, 1, null), new ValuePoint(500, true, 1, null)]) };
        NodeTrack flag = new(2, 100, 500, "0", 0f, false) { Series = ValueSeries.From([new ValuePoint(100, true, null, null), new ValuePoint(500, false, null, null)]) };
        LiveValuesRun run = Run(new Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> { [(_kills, 1)] = flat }, _game, _alice);
        using (Assert.Multiple())
        {
            await Assert.That(CanvasLiveViewerViewModel.Of(run, _kills, flat, _alice, false, false, null).SparkPoints.Select(p => p.Y).Distinct()).IsEquivalentTo([18.0]);
            CanvasLiveViewerViewModel bools = CanvasLiveViewerViewModel.Of(run, _kills, flag, _alice, true, false, null);
            await Assert.That(bools.SparkPoints).IsEquivalentTo([new Point(16, 2), new Point(80, 2), new Point(80, 34), new Point(160, 34)]);
            await Assert.That(bools.FinalText).IsEqualTo("final off");
        }
    }

    [Test]
    public async Task ATrackWithNoSeries_DrawsNoSparkline_AndNoMarker()
    {
        NodeTrack track = new(3, 100, 900, "3", 3f, true);
        LiveValuesRun run = Run(new Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> { [(_kills, 0)] = track }, _game);
        CanvasLiveViewerViewModel viewer = CanvasLiveViewerViewModel.Of(run, _kills, track, _game, false, false, 500);
        using (Assert.Multiple())
        {
            await Assert.That(viewer.HasSeries).IsFalse();
            await Assert.That(viewer.HasPlayhead).IsFalse();
            await Assert.That(viewer.HasBreakdown).IsFalse().Because("a game stat has no players to break down");
            await Assert.That(viewer.FinalText).IsEqualTo("final 3");
        }
    }

    [Test]
    public async Task TheBreakdown_IsTheLargestTenPlayers_WithTheSelectedOneMarked()
    {
        Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> nodes = [];
        List<LiveInstance> instances = [_game];
        for (int i = 1; i <= 12; i++)
        {
            LiveInstance player = new(i, i - 1, "P" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            instances.Add(player);
            nodes[(_kills, i)] = new NodeTrack(i, 10, 20, null, i, true);
        }

        LiveValuesRun run = Run(nodes, [.. instances]);
        LiveInstance selected = instances[3];
        CanvasLiveViewerViewModel viewer = CanvasLiveViewerViewModel.Of(run, _kills, nodes[(_kills, 3)], selected, false, false, null);
        using (Assert.Multiple())
        {
            await Assert.That(viewer.Breakdown.Count).IsEqualTo(CanvasLiveViewerViewModel.BreakdownRows);
            await Assert.That(viewer.Breakdown[0].Name).IsEqualTo("P12");
            await Assert.That(viewer.Breakdown[0].Fraction).IsEqualTo(1);
            await Assert.That(viewer.Breakdown[0].BarWidth).IsEqualTo(CanvasLiveViewerViewModel.BarWidth);
            await Assert.That(viewer.Breakdown[^1].Name).IsEqualTo("P3").Because("twelve players, the two smallest dropped");
            await Assert.That(viewer.Breakdown.Single(b => b.IsSelected).Name).IsEqualTo("P3");
            await Assert.That(viewer.Breakdown[^1].Value).IsEqualTo("3");
        }
    }

    [Test]
    public async Task ASaidViewer_CarriesTheMessageAndNothingElse()
    {
        CanvasLiveViewerViewModel said = CanvasLiveViewerViewModel.Said(LiveValuesText.NoData, true);
        using (Assert.Multiple())
        {
            await Assert.That(said.Message).IsEqualTo("no data");
            await Assert.That(said.HasMessage).IsTrue();
            await Assert.That(said.IsStale).IsTrue();
            await Assert.That(said.HasSeries).IsFalse();
            await Assert.That(said.HasBreakdown).IsFalse();
            await Assert.That(said.Fires).IsEqualTo("");
        }

        said.SetPlayhead(5);
        await Assert.That(said.HasPlayhead).IsFalse();
    }

    [Test]
    public async Task TheViewerWordings_AreTheTracksLines()
    {
        NodeTrack define = new(12, 100, 900, null, 12f, true) { ViaStats = 2 };
        NodeTrack never = new(0, null, null, null, 0f, false);
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.ViewerFires(define)).IsEqualTo("fired 12 times, first tick 100, last tick 900 via 2 stats");
            await Assert.That(LiveValuesText.ViewerFires(never)).IsEqualTo("never fired");
            await Assert.That(LiveValuesText.ViewerFinal(define, false)).IsEqualTo("final 12 fires");
            await Assert.That(LiveValuesText.ViewerFinal(never, true)).IsEqualTo("final off");
            await Assert.That(LiveValuesText.BreakdownValue(new NodeTrack(1, 1, 1, "a value far longer than the badge", null, true), false))
                .IsEqualTo("a value far longer than the badge").Because("a breakdown row is not cut to the badge's budget");
        }
    }
}
