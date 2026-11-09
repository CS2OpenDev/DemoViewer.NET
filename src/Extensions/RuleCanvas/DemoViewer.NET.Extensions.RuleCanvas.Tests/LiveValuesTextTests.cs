#region

using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The badge, tip and wire wording over a track: short, invariant, on/off for a bool, the stale line
///     only when stale, each wire kind in its own words, the value at the playhead first, and the seek
///     items with their reasons.
/// </summary>
public class LiveValuesTextTests
{
    private static readonly LiveInstance _alice = new(1, 0, "Alice");

    private static ValueSeries Counting(params int[] ticks) =>
        ValueSeries.From([.. ticks.Select((t, i) => new ValuePoint(t, true, i + 1, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))]);

    [Test]
    public async Task TheBadge_IsTheFinalValue_CutToTwelveCharacters()
    {
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.Badge(new NodeTrack(3, 10, 90, "23", 23f, true), false)).IsEqualTo("23");
            await Assert.That(LiveValuesText.Badge(new NodeTrack(3, 10, 90, null, 1.5f, true), false)).IsEqualTo("1.5");
            await Assert.That(LiveValuesText.Badge(new NodeTrack(0, null, null, null, null, false), false)).IsEqualTo("-");
            await Assert.That(LiveValuesText.Badge(new NodeTrack(3, 10, 90, "1", 1f, true), true)).IsEqualTo("on");
            await Assert.That(LiveValuesText.Badge(new NodeTrack(0, null, null, "0", 0f, false), true)).IsEqualTo("off");
            string cut = LiveValuesText.Badge(new NodeTrack(1, 1, 1, "a value far longer than the badge", null, true), false);
            await Assert.That(cut.Length).IsEqualTo(LiveValuesText.BadgeLength);
            await Assert.That(cut).EndsWith("…");
        }
    }

    [Test]
    public async Task TheBadgeAtATick_IsTheLastFireAtOrBeforeIt_AndADashBeforeTheFirst()
    {
        NodeTrack track = new(3, 100, 900, "3", 3f, true) { Series = Counting(100, 500, 900) };
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.Badge(track, false, 50)).IsEqualTo(LiveValuesText.NotYet);
            await Assert.That(LiveValuesText.Badge(track, false, 100)).IsEqualTo("1");
            await Assert.That(LiveValuesText.Badge(track, false, 499)).IsEqualTo("1");
            await Assert.That(LiveValuesText.Badge(track, false, 500)).IsEqualTo("2");
            await Assert.That(LiveValuesText.Badge(track, false, 5000)).IsEqualTo("3");
            await Assert.That(LiveValuesText.Badge(new NodeTrack(3, 10, 90, "7", 7f, true), false, 5)).IsEqualTo("7")
                .Because("a track with no series has only its final value to show");
            NodeTrack flag = new(2, 100, 500, "1", 1f, true)
            {
                Series = ValueSeries.From([new ValuePoint(100, true, 1, null), new ValuePoint(500, false, 0, null)])
            };
            await Assert.That(LiveValuesText.Badge(flag, true, 200)).IsEqualTo("on");
            await Assert.That(LiveValuesText.Badge(flag, true, 600)).IsEqualTo("off");
        }
    }

    [Test]
    public async Task TheTip_NamesTheInstanceTheFiresTheTicksAndTheFinal_AndTheStaleLineOnlyWhenStale()
    {
        NodeTrack fired = new(7, 120, 9800, "7", 7f, true);
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.Tip(fired, _alice, false, false)).IsEqualTo("Alice: fired 7 times, first tick 120, last tick 9800, final 7");
            await Assert.That(LiveValuesText.Tip(fired, _alice, false, true)).IsEqualTo("Alice: fired 7 times, first tick 120, last tick 9800, final 7\n" + LiveValuesText.StaleLine);
            await Assert.That(LiveValuesText.Tip(new NodeTrack(0, null, null, null, 0f, false), new LiveInstance(0, null, null), false, false))
                .IsEqualTo("game: fired 0 times, final 0");
        }
    }

    [Test]
    public async Task TheTipAtATick_LeadsWithTheValueThere_OrThatNothingFiredYet()
    {
        NodeTrack track = new(3, 100, 900, "3", 3f, true) { Series = Counting(100, 500, 900) };
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.Tip(track, _alice, false, false, 600))
                .IsEqualTo("at tick 600: 2\nAlice: fired 3 times, first tick 100, last tick 900, final 3");
            await Assert.That(LiveValuesText.Tip(track, _alice, false, false, 10))
                .IsEqualTo("not yet fired at tick 10\nAlice: fired 3 times, first tick 100, last tick 900, final 3");
            await Assert.That(LiveValuesText.Tip(track, _alice, false, true, 600)).EndsWith(LiveValuesText.StaleLine);
            await Assert.That(LiveValuesText.Tip(track, _alice, false, false, null)).DoesNotContain("at tick");
        }
    }

    [Test]
    public async Task ADefinesTip_SaysViaHowManyStats_AndItsBadgeIsItsFireCount()
    {
        NodeTrack define = new(12, 100, 900, null, 12f, true) { Series = Counting(100, 500, 900), ViaStats = 2 };
        NodeTrack one = new(4, 100, 900, null, 4f, true) { ViaStats = 1 };
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.Tip(define, _alice, false, false)).IsEqualTo("Alice: fired 12 times, first tick 100, last tick 900 via 2 stats, final 12 fires");
            await Assert.That(LiveValuesText.Tip(one, _alice, false, false)).Contains("via 1 stat,");
            await Assert.That(LiveValuesText.Badge(define, false)).IsEqualTo("12 fires");
            await Assert.That(LiveValuesText.Badge(define, false, 600)).IsEqualTo("2 fires");
        }
    }

    [Test]
    public async Task AFlagsTip_CountsItsOnAndOffAtFires()
    {
        NodeTrack flag = new(4, 100, 900, "1", 1f, true)
        {
            Series = ValueSeries.From([new ValuePoint(100, true, 1, null), new ValuePoint(300, true, 1, null), new ValuePoint(500, false, 0, null), new ValuePoint(900, true, 1, null)]),
            OnCount = 2,
            OffCount = 1
        };
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.Tip(flag, _alice, true, false))
                .IsEqualTo("Alice: fired 4 times, first tick 100, last tick 900, final on, on 2 times, off 1 times");
            await Assert.That(LiveValuesText.Tip(new NodeTrack(0, null, null, "0", 0f, false), _alice, true, false))
                .IsEqualTo("Alice: fired 0 times, final off").Because("no series, no counts to speak of");
        }
    }

    [Test]
    public async Task EachWireKind_HasItsOwnLine()
    {
        WireTrack fired = new(7, 120, 9800, TickList.From([120, 400, 9800]));
        WireTrack never = new(0, null, null, TickList.Empty);
        WireTrack gate = new(12, 120, 9800, TickList.From([120, 9800])) { TriggerCount = 143 };
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.WireTip(fired, LiveWireKind.Trigger, false)).IsEqualTo("fired 7 times, first tick 120, last tick 9800");
            await Assert.That(LiveValuesText.WireTip(never, LiveWireKind.Trigger, false)).IsEqualTo("never fired");
            await Assert.That(LiveValuesText.WireTip(never, LiveWireKind.Trigger, true)).IsEqualTo("never fired\n" + LiveValuesText.StaleLine);
            await Assert.That(LiveValuesText.WireTip(gate, LiveWireKind.Gate, false)).IsEqualTo("open on 12 of the trigger's 143 fires");
            await Assert.That(LiveValuesText.WireTip(fired, LiveWireKind.Gate, false)).IsEqualTo("open on 7 fires").Because("no message count for the trigger");
            await Assert.That(LiveValuesText.WireTip(fired, LiveWireKind.Read, false)).IsEqualTo("read 7 times");
            await Assert.That(LiveValuesText.WireTip(fired, LiveWireKind.Logic, false)).IsEqualTo("fired 7 times, first tick 120, last tick 9800");
            await Assert.That(LiveValuesText.WireTip(fired, LiveWireKind.Trigger, false, 400)).IsEqualTo("2 fires by tick 400\nfired 7 times, first tick 120, last tick 9800");
            await Assert.That(LiveValuesText.WireTip(fired, LiveWireKind.Trigger, false, 10)).StartsWith("0 fires by tick 10");
        }
    }

    [Test]
    public async Task AWireWithNoEdgeOfItsOwn_SpeaksFromANode()
    {
        NodeTrack source = new(5, 100, 900, "23", 23f, true);
        NodeTrack target = new(3, 100, 900, "3", 3f, true);
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.ReadFallback(source, false, false)).IsEqualTo("value at last fire: 23");
            await Assert.That(LiveValuesText.ReadFallback(source, true, true)).IsEqualTo("value at last fire: on\n" + LiveValuesText.StaleLine);
            await Assert.That(LiveValuesText.TargetFallback(target, false)).IsEqualTo("target fired 3 times, first tick 100, last tick 900");
            await Assert.That(LiveValuesText.TargetFallback(new NodeTrack(0, null, null, null, null, false), true)).IsEqualTo("target fired 0 times\n" + LiveValuesText.StaleLine);
        }
    }

    [Test]
    public async Task ThePositionWord_IsTheTickInsideTheRun_AndFinalOutsideIt()
    {
        LiveValuesRun run = new("/d.dem", "p", 64, 100, 9000, [], new Dictionary<(Model.RulesetDocumentNodeKey, int), NodeTrack>(), [],
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0);
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.Position(4000, run)).IsEqualTo("at tick 4000");
            await Assert.That(LiveValuesText.Position(100, run)).IsEqualTo("at tick 100");
            await Assert.That(LiveValuesText.Position(9001, run)).IsEqualTo("final");
            await Assert.That(LiveValuesText.Position(null, run)).IsEqualTo("final");
        }
    }

    [Test]
    public async Task ASeekItem_CarriesItsReasonAndSampled()
    {
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.SeekItem("Next fire", null, false)).IsEqualTo("Next fire");
            await Assert.That(LiveValuesText.SeekItem("Next fire", "no run yet", false)).IsEqualTo("Next fire (no run yet)");
            await Assert.That(LiveValuesText.SeekItem("Next fire", null, true)).IsEqualTo("Next fire (sampled)");
            await Assert.That(LiveValuesText.SeekItem("First fire", "never fired", true)).IsEqualTo("First fire (never fired) (sampled)");
        }
    }

    [Test]
    public async Task TheNodeBadgeMembers_StartEmpty_AndHasLiveFollowsTheBadge()
    {
        CanvasNodeViewModel node = new() { Id = "x", Kind = "count" };
        List<string> changed = [];
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        await Assert.That(node.HasLive).IsFalse();
        node.LiveBadge = "3";
        using (Assert.Multiple())
        {
            await Assert.That(node.HasLive).IsTrue();
            await Assert.That(changed).Contains(nameof(CanvasNodeViewModel.HasLive));
        }

        CanvasConnectionViewModel wire = new(new CanvasConnectorViewModel { Title = "a" }, new CanvasConnectorViewModel { Title = "b" });
        await Assert.That(wire.HasLiveTip).IsFalse();
        wire.LiveTip = "fired once";
        await Assert.That(wire.HasLiveTip).IsTrue();
    }

    [Test]
    public async Task AWiresLiveKind_FollowsItsClasses()
    {
        CanvasConnectorViewModel a = new() { Title = "a" };
        CanvasConnectorViewModel b = new() { Title = "b" };
        using (Assert.Multiple())
        {
            await Assert.That(new CanvasConnectionViewModel(a, b).LiveKind).IsEqualTo(LiveWireKind.Trigger);
            await Assert.That(new CanvasConnectionViewModel(a, b, isRead: true).LiveKind).IsEqualTo(LiveWireKind.Read);
            await Assert.That(new CanvasConnectionViewModel(a, b, isGate: true).LiveKind).IsEqualTo(LiveWireKind.Gate);
            await Assert.That(new CanvasConnectionViewModel(a, b, isAnd: true).LiveKind).IsEqualTo(LiveWireKind.Logic);
            await Assert.That(new CanvasConnectionViewModel(a, b, isOr: true).LiveKind).IsEqualTo(LiveWireKind.Logic);
        }
    }
}
