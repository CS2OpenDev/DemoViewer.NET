#region

using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="Playback2DTabViewModel.FollowSteamId" />: an account resolves to its slot in the roster as it is when
///     asked, goes through the same funnel a card pick does, and waits for a roster that is not in yet. The fake
///     context gives slot N the SteamID N + 1. Bodies run on the dispatcher for the reason
///     <see cref="Playback2DFollowFunnelTests" /> gives.
/// </summary>
[NotInParallel]
public class Playback2DFollowSteamIdTests
{
    private const string DemoA = "/demos/a.dem";

    [Test]
    public async Task AKnownAccount_FollowsItsSlot_ThroughTheFunnel() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DActivation.Activated(DemoA);

            bool followed = vm.FollowSteamId(2);

            using (Assert.Multiple())
            {
                await Assert.That(followed).IsTrue();
                await Assert.That(vm.FollowedSlot).IsEqualTo(1);
                await Assert.That(vm.SelectedPlayer?.Slot).IsEqualTo(1);
                await Assert.That(vm.FollowStatus).Contains("Bravo");
                await Assert.That(ctx.SpectateTargets).IsEquivalentTo([1]);
            }
        });

    [Test]
    public async Task ARecycledSlot_ResolvesToWhoeverHoldsItNow() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DActivation.Activated(DemoA);

            // Bravo (2) leaves and another account takes slot 1; the roster count does not change, so no re-seed.
            ctx.Roster[1] = new PlayerRosterEntry { Slot = 1, Name = "Bravo", SteamId = 99 };

            using (Assert.Multiple())
            {
                await Assert.That(vm.FollowSteamId(2)).IsFalse().Because("that account no longer holds a slot");
                await Assert.That(vm.FollowedSlot).IsEqualTo(-1);
                await Assert.That(vm.FollowSteamId(99)).IsTrue();
                await Assert.That(vm.FollowedSlot).IsEqualTo(1);
            }
        });

    [Test]
    public async Task AnUnknownAccountOrZero_FollowsNobody() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DActivation.Activated(DemoA);

            using (Assert.Multiple())
            {
                await Assert.That(vm.FollowSteamId(12345)).IsFalse();
                await Assert.That(vm.FollowSteamId(0)).IsFalse();
                await Assert.That(vm.FollowedSlot).IsEqualTo(-1);
                await Assert.That(ctx.SpectateTargets).IsEmpty();
            }
        });

    [Test]
    public async Task TheFollowGateOff_FollowsNobody_AndQueuesNothing() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DActivation.Activated(DemoA);
            ctx.Gate!.SetEnabled("playback2d.follow", false);

            bool followed = vm.FollowSteamId(2);
            ctx.Gate.SetEnabled("playback2d.follow", true);
            ctx.Push(1, 2);

            using (Assert.Multiple())
            {
                await Assert.That(followed).IsFalse();
                await Assert.That(vm.FollowedSlot).IsEqualTo(-1).Because("a refused follow is not held for later");
                await Assert.That(ctx.SpectateTargets).IsEmpty();
            }
        });

    [Test]
    public async Task APendingRoster_QueuesTheFollow_UntilThatDemosRosterArrives() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DFakeContext ctx = new() { Gate = new FakeModuleFeatureGate(), DemoPath = DemoA };
            Playback2DTabViewModel vm = new();
            vm.OnActivated(ctx);

            bool queued = vm.FollowSteamId(3, DemoA);
            int before = vm.FollowedSlot;

            // The host hands the roster over after the load; the next push re-seeds and applies the follow.
            ctx.AddPlayer(0, "Alpha", 2);
            ctx.AddPlayer(1, "Bravo", 2);
            ctx.AddPlayer(2, "Charlie", 3);
            ctx.Push(1, 2);

            using (Assert.Multiple())
            {
                await Assert.That(queued).IsTrue();
                await Assert.That(before).IsEqualTo(-1);
                await Assert.That(vm.FollowedSlot).IsEqualTo(2);
                await Assert.That(vm.FollowStatus).Contains("Charlie");
            }
        });

    [Test]
    public async Task AFollowAskedBeforeActivation_AppliesOnTheActivationForItsDemo() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DTabViewModel vm = new();
            bool queued = vm.FollowSteamId(1, DemoA);

            Playback2DFakeContext ctx = new() { Gate = new FakeModuleFeatureGate(), DemoPath = DemoA };
            ctx.AddPlayer(0, "Alpha", 2);
            ctx.AddPlayer(1, "Bravo", 2);
            vm.OnActivated(ctx);

            using (Assert.Multiple())
            {
                await Assert.That(queued).IsTrue();
                await Assert.That(vm.FollowedSlot).IsEqualTo(0);
                await Assert.That(ctx.SpectateTargets).IsEquivalentTo([0]);
            }
        });

    [Test]
    public async Task AQueuedFollow_IsDropped_WhenAnotherDemosRosterArrives() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            Playback2DTabViewModel vm = new();
            bool queued = vm.FollowSteamId(1, "/demos/b.dem");

            Playback2DFakeContext ctx = new() { Gate = new FakeModuleFeatureGate(), DemoPath = DemoA };
            ctx.AddPlayer(0, "Alpha", 2);
            vm.OnActivated(ctx);

            // Even when that demo is opened afterwards, the follow was about an open that never landed.
            ctx.ClearPlayers();
            ctx.DemoPath = "/demos/b.dem";
            ctx.AddPlayer(0, "Alpha", 2);
            ctx.RaiseDemoReset();

            using (Assert.Multiple())
            {
                await Assert.That(queued).IsTrue();
                await Assert.That(vm.FollowedSlot).IsEqualTo(-1);
                await Assert.That(ctx.SpectateTargets).IsEmpty();
            }
        });
}
