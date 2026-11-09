#region

using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The bounded lists behind a track: the binary search answers the last tick at or before, the cap
///     thins a long list by every k-th while keeping both ends and saying so, the series answers the value
///     at a tick, the tick list answers next, previous and the count so far, and the reducer's transition
///     count reads a flag's on and off from its points.
/// </summary>
public class LiveLookupTests
{
    [Test]
    public async Task TheSearch_FindsTheLastTickAtOrBefore()
    {
        int[] ticks = [100, 500, 900];
        using (Assert.Multiple())
        {
            await Assert.That(LiveLookup.IndexAtOrBefore(ticks, 50)).IsEqualTo(-1);
            await Assert.That(LiveLookup.IndexAtOrBefore(ticks, 100)).IsEqualTo(0);
            await Assert.That(LiveLookup.IndexAtOrBefore(ticks, 499)).IsEqualTo(0);
            await Assert.That(LiveLookup.IndexAtOrBefore(ticks, 500)).IsEqualTo(1);
            await Assert.That(LiveLookup.IndexAtOrBefore(ticks, 5000)).IsEqualTo(2);
            await Assert.That(LiveLookup.IndexAtOrBefore([], 5)).IsEqualTo(-1);
        }
    }

    [Test]
    public async Task Decimation_KeepsEverythingUnderTheCap_AndBothEndsAbove()
    {
        IReadOnlyList<int> all = LiveLookup.Decimate(10, 512);
        IReadOnlyList<int> thinned = LiveLookup.Decimate(10_000, 512);
        IReadOnlyList<int> edge = LiveLookup.Decimate(513, 512);
        using (Assert.Multiple())
        {
            await Assert.That(all).IsEquivalentTo(Enumerable.Range(0, 10));
            await Assert.That(thinned.Count).IsLessThanOrEqualTo(512);
            await Assert.That(thinned.Count).IsGreaterThan(400).Because("the cap is filled, not halved");
            await Assert.That(thinned[0]).IsEqualTo(0);
            await Assert.That(thinned[^1]).IsEqualTo(9_999);
            await Assert.That(thinned.Zip(thinned.Skip(1)).All(p => p.Second > p.First)).IsTrue().Because("indices stay sorted and distinct");
            await Assert.That(edge.Count).IsLessThanOrEqualTo(512);
            await Assert.That(edge[^1]).IsEqualTo(512);
            await Assert.That(LiveLookup.Decimate(0, 512)).IsEmpty();
        }
    }

    [Test]
    public async Task ASeries_AnswersTheValueAtATick_AndSaysWhenItWasThinned()
    {
        ValueSeries short_ = ValueSeries.From([new ValuePoint(100, true, 1, "1"), new ValuePoint(500, true, 2, "2")]);
        ValueSeries long_ = ValueSeries.From([.. Enumerable.Range(0, 5000).Select(i => new ValuePoint(i * 10, true, i, null))]);
        using (Assert.Multiple())
        {
            await Assert.That(short_.IsSampled).IsFalse();
            await Assert.That(short_.Count).IsEqualTo(2);
            await Assert.That(short_.At(50)).IsNull();
            await Assert.That(short_.At(100)!.Value.Numeric).IsEqualTo(1);
            await Assert.That(short_.At(600)!.Value.Display).IsEqualTo("2");
            await Assert.That(long_.IsSampled).IsTrue();
            await Assert.That(long_.Count).IsLessThanOrEqualTo(ValueSeries.Cap);
            await Assert.That(long_.Ticks[0]).IsEqualTo(0);
            await Assert.That(long_.Ticks[^1]).IsEqualTo(49_990);
            await Assert.That(ValueSeries.Sampled([new ValuePoint(1, true, 1, null)]).IsSampled).IsTrue().Because("thinned before it got here");
            await Assert.That(ValueSeries.From([]).Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ATickList_AnswersNextPreviousFirstAndTheCountSoFar()
    {
        TickList ticks = TickList.From([100, 500, 900]);
        TickList many = TickList.From([.. Enumerable.Range(0, 10_000)]);
        using (Assert.Multiple())
        {
            await Assert.That(ticks.First).IsEqualTo(100);
            await Assert.That(ticks.After(50)).IsEqualTo(100);
            await Assert.That(ticks.After(100)).IsEqualTo(500);
            await Assert.That(ticks.After(900)).IsNull();
            await Assert.That(ticks.Before(100)).IsNull();
            await Assert.That(ticks.Before(101)).IsEqualTo(100);
            await Assert.That(ticks.Before(900)).IsEqualTo(500);
            await Assert.That(ticks.Before(5000)).IsEqualTo(900);
            await Assert.That(ticks.CountThrough(50)).IsEqualTo(0);
            await Assert.That(ticks.CountThrough(500)).IsEqualTo(2);
            await Assert.That(ticks.CountThrough(5000)).IsEqualTo(3);
            await Assert.That(ticks.IsSampled).IsFalse();
            await Assert.That(many.IsSampled).IsTrue();
            await Assert.That(many.Count).IsLessThanOrEqualTo(TickList.Cap);
            await Assert.That(many.Ticks[^1]).IsEqualTo(9_999);
            await Assert.That(TickList.Empty.First).IsNull();
        }
    }

    [Test]
    public async Task Transitions_CountOnAndOffFromInactive()
    {
        ValueSeries flips = ValueSeries.From([
            new ValuePoint(1, true, 1, null), new ValuePoint(2, true, 1, null), new ValuePoint(3, false, 0, null),
            new ValuePoint(4, true, 1, null), new ValuePoint(5, false, 0, null)
        ]);
        ValueSeries steady = ValueSeries.From([new ValuePoint(1, false, 0, null), new ValuePoint(2, false, 0, null)]);
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesReducer.Transitions(flips)).IsEqualTo((2, 2));
            await Assert.That(LiveValuesReducer.Transitions(steady)).IsEqualTo((0, 0));
            await Assert.That(LiveValuesReducer.Transitions(ValueSeries.Empty)).IsEqualTo((0, 0));
        }
    }

    [Test]
    public async Task ASeriesFromIndices_TakesTheTickOfEachMessage_AndDedupesARepeatedIndex()
    {
        List<CS2DemoKit.Analysis.MessageRef> messages = [.. Enumerable.Range(0, 10).Select(i => new CS2DemoKit.Analysis.MessageRef(i, i * 100, 0, null!))];
        ValueSeries series = LiveValuesReducer.SeriesOf([2, 2, 5, 9], messages, null, -1);
        using (Assert.Multiple())
        {
            await Assert.That(series.Ticks).IsEquivalentTo([200, 500, 900]);
            await Assert.That(series.Points.All(p => p.Numeric is null && !p.Active)).IsTrue().Because("no snapshot, ticks alone");
            await Assert.That(LiveValuesReducer.SeriesOf([], messages, null, -1).Count).IsEqualTo(0);
            await Assert.That(LiveValuesReducer.SeriesOf([3], [], null, -1).Count).IsEqualTo(0);
        }
    }
}
