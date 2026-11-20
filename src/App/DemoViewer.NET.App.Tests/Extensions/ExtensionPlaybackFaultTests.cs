#region

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Playback2D.Timeline;
using TUnit.Assertions.Enums;
using Sdk = DemoViewer.NET.Extensions.Sdk.Playback;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     An SDK playback contribution whose every handler, track, lane, menu and factory throws, hosted on a real
///     2D tab: nothing reaches the tab, each fault is counted against the extension, and a per-frame
///     handler that throws on every frame counts once.
/// </summary>
[NotInParallel]
public class ExtensionPlaybackFaultTests
{
    private static readonly TimelineBandViewModel _roundBand =
        new(new TimelineBand("round", 0, 10, "7", "round 7", 0), 0, 1, Brushes.Gray);

    private static (Playback2DTabViewModel Vm, FaultRig Rig, SdkPlaybackContribution Hosted, ThrowingContribution Fake)
        Attach()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
        FaultRig rig = new();
        ThrowingContribution fake = new();
        SdkPlaybackContribution hosted = new(fake, rig.Guard);
        hosted.Attach(vm.Surface, ctx);
        return (vm, rig, hosted, fake);
    }

    [Test]
    public async Task APlayheadHandlerThatThrowsEveryFrame_LeavesTheFrameLoopRunning_AndCountsOnce()
    {
        (Playback2DTabViewModel vm, FaultRig rig, SdkPlaybackContribution hosted, _) = Attach();
        int laterHandler = 0;
        using IDisposable later = vm.Surface.OnPlayheadChanged(_ => laterHandler++);
        int before = rig.Faults.StateOf("pack.fake").Count;

        for (int tick = 0; tick < 100; tick++)
        {
            vm.Surface.NotifyPlayheadChanged(tick);
        }

        using (Assert.Multiple())
        {
            await Assert.That(laterHandler).IsEqualTo(100).Because("a host handler after the extension's still runs");
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(before + 1);
        }

        hosted.Detach();
        vm.Dispose();
    }

    [Test]
    public async Task KeysActionsAndTheDemoChange_AreContained()
    {
        (Playback2DTabViewModel vm, FaultRig rig, SdkPlaybackContribution hosted, _) = Attach();

        bool key = vm.Surface.TryHandleKey(Key.F9, KeyModifiers.None);
        bool action = vm.Surface.TryExecute("");
        vm.Surface.NotifyDemoChanged();

        using (Assert.Multiple())
        {
            await Assert.That(key).IsFalse();
            await Assert.That(action).IsFalse();
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsGreaterThanOrEqualTo(3);
        }

        hosted.Detach();
        vm.Dispose();
    }

    [Test]
    public async Task ABandMenuThatThrows_ShowsNoEntries_AndAThrowingEntryIsContained()
    {
        (Playback2DTabViewModel vm, FaultRig rig, SdkPlaybackContribution hosted, ThrowingContribution fake) = Attach();

        IReadOnlyList<Sdk.MenuEntry> menu = vm.Timeline.MenuFor(_roundBand);
        await Assert.That(menu.Select(m => m.Header))
            .IsEquivalentTo([ThrowingContribution.EntryHeader], CollectionOrdering.Matching)
            .Because("the throwing menu contributed nothing; the good one its entry");

        menu[0].Run();
        await Assert.That(rig.Faults.StateOf("pack.fake").LastSite).IsEqualTo("menu entry");

        hosted.Detach();
        await Assert.That(fake.Detached).IsTrue();
        vm.Dispose();
    }

    /// <summary>
    ///     What a menu callback returns is the extension's as much as the callback is: a null entry or an entry
    ///     with no Run is that extension's fault, counted against it, and the menu opens with the rest.
    /// </summary>
    [Test]
    public async Task ABandMenuWithANullEntryOrANullRun_IsContained_AndTheOtherMenusStillShow()
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(vm.Surface, rig.Guard);
        _ = surface.AddBandMenu(_ => [new Sdk.MenuEntry("no run", null!)]);
        _ = surface.AddBandMenu(_ => [null!]);
        _ = surface.AddBandMenu(_ => [new Sdk.MenuEntry("good", () => { })]);
        using Sdk.ILaneHandle lane = surface.AddLane(new QuietTrack(), new NullRunLane());
        TimelineBandViewModel laneBand = new(new TimelineBand(lane.TrackId, 0, 10, "x", "", 0), 0, 1, Brushes.Gray);

        IReadOnlyList<Sdk.MenuEntry> menu = vm.Timeline.MenuFor(_roundBand);
        int afterContributors = rig.Faults.StateOf("pack.fake").Count;
        IReadOnlyList<Sdk.MenuEntry> laneMenu = vm.Timeline.MenuFor(laneBand);

        using (Assert.Multiple())
        {
            await Assert.That(menu.Select(m => m.Header)).IsEquivalentTo(["good"], CollectionOrdering.Matching);
            await Assert.That(afterContributors).IsEqualTo(2);
            await Assert.That(laneMenu.Select(m => m.Header)).IsEquivalentTo(["good"], CollectionOrdering.Matching)
                .Because("the lane's own menu is dropped and the contributors' still shows");
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(5)
                .Because("the lane's menu and the two bad contributors again");
        }

        vm.Dispose();
    }

    /// <summary>
    ///     A contributor may drop its own registration from inside its callback. The timeline asks a copy of
    ///     the list, so that is not a list changed under the loop reading it.
    /// </summary>
    [Test]
    public async Task ABandMenuThatRemovesItselfWhenAsked_DoesNotBreakTheMenu()
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        FaultRig rig = new();
        using SdkPlaybackSurface surface = new(vm.Surface, rig.Guard);
        IDisposable? once = null;
        once = surface.AddBandMenu(_ =>
        {
            once!.Dispose();
            return [new Sdk.MenuEntry("once", () => { })];
        });
        _ = surface.AddBandMenu(_ => [new Sdk.MenuEntry("always", () => { })]);

        IReadOnlyList<Sdk.MenuEntry> first = vm.Timeline.MenuFor(_roundBand);
        IReadOnlyList<Sdk.MenuEntry> second = vm.Timeline.MenuFor(_roundBand);

        using (Assert.Multiple())
        {
            await Assert.That(first.Select(m => m.Header))
                .IsEquivalentTo(["once", "always"], CollectionOrdering.Matching);
            await Assert.That(second.Select(m => m.Header)).IsEquivalentTo(["always"], CollectionOrdering.Matching);
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsEqualTo(0);
        }

        vm.Dispose();
    }

    [Test]
    public async Task ALaneWhoseTrackThrows_StillBuildsTheTimeline()
    {
        (Playback2DTabViewModel vm, FaultRig rig, SdkPlaybackContribution hosted, _) = Attach();

        IReadOnlyList<string> lanes = [.. vm.Timeline.Lanes.Select(l => l.Track.Id)];
        _ = vm.Timeline.LaneBands.ToList();

        using (Assert.Multiple())
        {
            await Assert.That(lanes).Contains("ext.pack.fake.track")
                .Because("a throwing Id reads as the extension's fallback id");
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsGreaterThanOrEqualTo(1);
        }

        hosted.Detach();
        vm.Dispose();
    }

    [Test]
    public async Task APanelWhoseFactoriesThrow_ShowsThePlaceholder()
    {
        (Playback2DTabViewModel vm, _, SdkPlaybackContribution hosted, ThrowingContribution fake) = Attach();

        fake.Panel!.Open();
        object? content = vm.Surface.Panels.Single().View;

        using (Assert.Multiple())
        {
            await Assert.That(content).IsTypeOf<TextBlock>();
            await Assert.That(((TextBlock)content!).Text).Contains("Fake could not show this panel");
        }

        hosted.Detach();
        vm.Dispose();
    }

    // An open pane whose view model's Dispose and Closed handler throw is taken down at detach with
    // everything registered after it: the throw is counted, and the key handler does not outlive the switch-off.
    [Test]
    public async Task APaneWhoseTeardownThrows_DoesNotStrandTheRegistrationsAfterIt()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
        FaultRig rig = new();
        PaneThenKeyContribution fake = new();
        SdkPlaybackContribution hosted = new(fake, rig.Guard);
        hosted.Attach(vm.Surface, ctx);
        fake.Pane!.Open();

        Exception? thrown = null;
        try
        {
            hosted.Detach();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        bool handled = vm.Surface.TryHandleKey(Key.F9, KeyModifiers.None);

        using (Assert.Multiple())
        {
            await Assert.That(thrown).IsNull();
            await Assert.That(handled).IsFalse();
            await Assert.That(fake.Keys).IsEqualTo(0).Because("the key handler went with the detach");
            await Assert.That(rig.Faults.StateOf("pack.fake").Count).IsGreaterThanOrEqualTo(1);
        }

        vm.Dispose();
    }

    private sealed class PaneThenKeyContribution : Sdk.IPlaybackContribution
    {
        public Sdk.IPaneHandle? Pane { get; private set; }
        public int Keys { get; private set; }

        public void Attach(Sdk.IPlaybackSurface surface, IModuleContext context)
        {
            Pane = surface.AddPane(Sdk.PanePlacement.Side, 0, () => new ThrowingViewModel());
            Pane.Closed += () => throw new InvalidOperationException("closed");
            _ = surface.AddKeyHandler((_, _) =>
            {
                Keys++;
                return true;
            });
        }

        public void Detach()
        {
        }
    }

    private sealed class ThrowingViewModel : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("view model dispose");
    }

    private sealed class ThrowingContribution : Sdk.IPlaybackContribution
    {
        public const string EntryHeader = "Throws when run";

        public Sdk.IPanelHandle? Panel { get; private set; }
        public bool Detached { get; private set; }

        public void Attach(Sdk.IPlaybackSurface surface, IModuleContext context)
        {
            _ = surface.OnPlayheadChanged(_ => throw new InvalidOperationException("playhead"));
            _ = surface.OnDemoChanged(() => throw new InvalidOperationException("demo"));
            _ = surface.AddKeyHandler((_, _) => throw new InvalidOperationException("key"));
            _ = surface.AddActionHandler(_ => throw new InvalidOperationException("action"));
            _ = surface.AddBandMenu(_ => throw new InvalidOperationException("menu"));
            _ = surface.AddBandMenu(_ =>
                [new Sdk.MenuEntry(EntryHeader, () => throw new InvalidOperationException("entry"))]);
            _ = surface.AddLane(new ThrowingTrack(), new ThrowingLane());
            Panel = surface.AddPanel(0, () => throw new InvalidOperationException("panel vm"),
                () => throw new InvalidOperationException("panel view"));
        }

        public void Detach()
        {
            Detached = true;
            throw new InvalidOperationException("detach");
        }
    }

    private sealed class ThrowingTrack : ITimelineTrack
    {
        public string Id => throw new InvalidOperationException("id");
        public string DisplayName => "Throwing";

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public bool IsAvailable(ITimelineData data) => true;

        public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) =>
            throw new InvalidOperationException("markers");

        public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) =>
            throw new InvalidOperationException("bands");
    }

    private sealed class QuietTrack : ITimelineTrack
    {
        public string Id => "quiet";
        public string DisplayName => "Quiet";

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public bool IsAvailable(ITimelineData data) => true;

        public IReadOnlyList<TimelineMarker> BuildMarkers(ITimelineData data) => [];

        public IReadOnlyList<TimelineBand> BuildBands(ITimelineData data) => [];
    }

    private sealed class NullRunLane : Sdk.ILaneBehaviour
    {
        public IEnumerable<Sdk.MenuEntry> MenuFor(Sdk.PlaybackBand band) => [new("lane entry", null!)];
    }

    private sealed class ThrowingLane : Sdk.ILaneBehaviour
    {
        public IEnumerable<Sdk.MenuEntry> MenuFor(Sdk.PlaybackBand band) =>
            throw new InvalidOperationException("lane menu");
    }
}
