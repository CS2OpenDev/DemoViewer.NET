#region

using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>The badge and tip wording over a track: short, invariant, on/off for a bool, and the stale line only when stale.</summary>
public class LiveValuesTextTests
{
    private static readonly LiveInstance _alice = new(1, 0, "Alice");

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
    public async Task TheTip_NamesTheInstanceTheFiresTheTicksAndTheFinal_AndTheStaleLineOnlyWhenStale()
    {
        NodeTrack fired = new(7, 120, 9800, "7", 7f, true);
        using (Assert.Multiple())
        {
            await Assert.That(LiveValuesText.Tip(fired, _alice, false, false)).IsEqualTo("Alice: fired 7 times, first tick 120, last tick 9800, final 7");
            await Assert.That(LiveValuesText.Tip(fired, _alice, false, true)).IsEqualTo("Alice: fired 7 times, first tick 120, last tick 9800, final 7\n" + LiveValuesText.StaleLine);
            await Assert.That(LiveValuesText.Tip(new NodeTrack(0, null, null, null, 0f, false), new LiveInstance(0, null, null), false, false))
                .IsEqualTo("game: fired 0 times, final 0");
            await Assert.That(LiveValuesText.WireTip(fired, false)).IsEqualTo("fired 7 times, last tick 9800");
            await Assert.That(LiveValuesText.WireTip(new NodeTrack(0, null, null, null, null, false), true)).IsEqualTo("fired 0 times\n" + LiveValuesText.StaleLine);
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
}
