#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Turning Live Sync off in Settings removes the chip, and with it the only Disable, Re-sync and Reconnect
///     controls. A session running at that moment has to stop with it, not keep CS2 following the app with no
///     way left to end it.
/// </summary>
[NotInParallel]
public class LiveSyncGateOffTests
{
    [Test]
    public async Task TurningLiveSyncOff_StopsARunningSession_AndLeavesAnIdleEngineAlone() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            SwitchGate gate = new();
            MainViewModel vm = new(library: TestLibraries.Empty(), gate: gate);
            Cs2InstallRecoveryViewModelTests.FakeLiveSync liveSync = new(new Cs2InstallProbe(false, false, false));
            vm.AttachLiveSync(liveSync);
            try
            {
                liveSync.Raise(new LiveSyncState(LiveSyncStateKind.ConnectedIdle));
                gate.LiveSync = false;
                gate.RaiseChanged();
                await Assert.That(liveSync.DisableCalls).IsEqualTo(1);

                liveSync.Raise(LiveSyncState.Disconnected);
                gate.LiveSync = true;
                gate.RaiseChanged();
                gate.LiveSync = false;
                gate.RaiseChanged();
                await Assert.That(liveSync.DisableCalls).IsEqualTo(1).Because("nothing was running to stop");
            }
            finally
            {
                vm.Dispose();
            }
        });

    private sealed class SwitchGate : IFeatureGate
    {
        public bool LiveSync { get; set; } = true;
        public UserCategory Category => UserCategory.Developer;
        public int HiddenCount => 0;

        public bool IsEnabled(string featureId) => featureId != "chrome.livesync" || LiveSync;

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
