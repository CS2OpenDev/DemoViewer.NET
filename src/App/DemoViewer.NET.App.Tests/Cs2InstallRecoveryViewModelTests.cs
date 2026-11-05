#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels.LiveSync;
using DemoViewer.NET.ViewModels.Playback;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The app-level leftover-install banner: probed at startup for every profile, restored unasked only
///     when the engine proves it safe, held without a dismiss until restored otherwise, stood down while
///     Live Sync or a reel owns CS2 and probed again when either lets go. Fakes only: nothing here reaches
///     a CS2 install.
/// </summary>
public class Cs2InstallRecoveryViewModelTests
{
    private static readonly Cs2InstallProbe _clean = new(false, false, false);
    private static readonly Cs2InstallProbe _leftovers = new(true, false, false);
    private static readonly Cs2InstallProbe _safeLeftovers = new(true, false, true);
    private static readonly Cs2InstallProbe _leftoversCs2Open = new(true, true, false);

    [Test]
    public async Task CleanInstall_ShowsNothing()
    {
        FakeLiveSync svc = new(_clean);
        Cs2InstallRecoveryViewModel vm = new(svc, () => false);

        await vm.CheckAsync();

        await Assert.That(vm.IsVisible).IsFalse();
        await Assert.That(svc.RestoreCalls).IsEqualTo(0);
    }

    [Test]
    public async Task UnprovenLeftovers_HoldTheBanner_WithNoWayToDismissIt()
    {
        FakeLiveSync svc = new(_leftovers);
        Cs2InstallRecoveryViewModel vm = new(svc, () => false);

        await vm.CheckAsync();

        await Assert.That(vm.NeedsRestore).IsTrue();
        await Assert.That(vm.IsVisible).IsTrue();
        await Assert.That(vm.Message).IsEqualTo(Cs2InstallRecoveryViewModel.LeftoverText);
        await Assert.That(vm.CanDismiss).IsFalse();
        await Assert.That(svc.RestoreCalls).IsEqualTo(0).Because("not provably safe, so never restored unasked");

        vm.DismissCommand.Execute(null);
        await Assert.That(vm.IsVisible).IsTrue().Because("leftovers hold the banner until they are restored");
    }

    [Test]
    public async Task ProvablySafeLeftovers_AreRestoredUnasked_AndReported()
    {
        FakeLiveSync svc = new(_safeLeftovers);
        Cs2InstallRecoveryViewModel vm = new(svc, () => false);

        await vm.CheckAsync();

        await Assert.That(svc.RestoreCalls).IsEqualTo(1);
        await Assert.That(vm.NeedsRestore).IsFalse();
        await Assert.That(vm.Message).IsEqualTo(Cs2InstallRecoveryViewModel.AutoRestoredText);
        await Assert.That(vm.CanDismiss).IsTrue();

        vm.DismissCommand.Execute(null);
        await Assert.That(vm.IsVisible).IsFalse();
    }

    [Test]
    public async Task FailedAutoRestore_FallsBackToTheBanner()
    {
        FakeLiveSync svc = new(_safeLeftovers) { RestoreFailure = new IOException("in use") };
        Cs2InstallRecoveryViewModel vm = new(svc, () => false);

        await vm.CheckAsync();

        await Assert.That(vm.NeedsRestore).IsTrue();
        await Assert.That(vm.FailureText).Contains("a file in the CS2 folder is in use");
        await Assert.That(vm.CanDismiss).IsFalse();
    }

    [Test]
    public async Task RestoreCommand_Succeeds_AndSaysSo()
    {
        FakeLiveSync svc = new(_leftovers);
        Cs2InstallRecoveryViewModel vm = new(svc, () => false);
        await vm.CheckAsync();

        await vm.RestoreCommand.ExecuteAsync(null);

        await Assert.That(svc.RestoreCalls).IsEqualTo(1);
        await Assert.That(vm.NeedsRestore).IsFalse();
        await Assert.That(vm.Message).IsEqualTo(Cs2InstallRecoveryViewModel.RestoredText);
        await Assert.That(vm.FailureText).IsNull();
        await Assert.That(vm.IsRestoring).IsFalse();
    }

    [Test]
    public async Task RestoreCommand_Fails_KeepsTheBanner_WithTheReasonAndSteamFallback()
    {
        FakeLiveSync svc = new(_leftovers) { RestoreFailure = new FileNotFoundException("no backup") };
        Cs2InstallRecoveryViewModel vm = new(svc, () => false);
        await vm.CheckAsync();

        await vm.RestoreCommand.ExecuteAsync(null);

        await Assert.That(vm.NeedsRestore).IsTrue();
        await Assert.That(vm.FailureText).Contains("the backup DemoViewer made of gameinfo.gi is missing");
        await Assert.That(vm.FailureText).Contains("Verify integrity of game files");
        await Assert.That(vm.FailureText!.Contains('—')).IsFalse().Because("no em-dashes in user-facing copy");
    }

    [Test]
    public async Task Cs2Running_AsksToCloseIt_AndRestoreWaitsForThat()
    {
        FakeLiveSync svc = new(_leftoversCs2Open);
        Cs2InstallRecoveryViewModel vm = new(svc, () => false);
        await vm.CheckAsync();

        await Assert.That(vm.Message).IsEqualTo(Cs2InstallRecoveryViewModel.Cs2RunningText);

        await vm.RestoreCommand.ExecuteAsync(null);
        await Assert.That(svc.RestoreCalls).IsEqualTo(0);
        await Assert.That(vm.FailureText).IsEqualTo(Cs2InstallRecoveryViewModel.StillRunningText);

        svc.Probe = _leftovers;
        await vm.RestoreCommand.ExecuteAsync(null);
        await Assert.That(svc.RestoreCalls).IsEqualTo(1);
        await Assert.That(vm.NeedsRestore).IsFalse();
    }

    // A Live Sync session owns the install while it runs, and its own stop restores it: the banner stands
    // down for the session and probes again once sync is back to Off, which is when a failed teardown shows.
    [Test]
    public async Task LiveSyncSession_StandsTheBannerDown_ThenReprobesWhenItEnds()
    {
        FakeLiveSync svc = new(_leftovers);
        Cs2InstallRecoveryViewModel vm = new(svc, () => svc.State.Kind != LiveSyncStateKind.Disconnected);
        await vm.CheckAsync();
        await Assert.That(vm.NeedsRestore).IsTrue();

        svc.Raise(new LiveSyncState(LiveSyncStateKind.LaunchingCs2));
        await Assert.That(vm.IsVisible).IsFalse();
        int probesDuringSession = svc.ProbeCalls;
        await vm.CheckAsync();
        await Assert.That(svc.ProbeCalls).IsEqualTo(probesDuringSession).Because("no probe while CS2 is owned");

        svc.Probe = _clean;
        svc.Raise(LiveSyncState.Disconnected);
        await Assert.That(svc.ProbeCalls).IsEqualTo(probesDuringSession + 1);
        await Assert.That(vm.IsVisible).IsFalse();

        svc.Raise(new LiveSyncState(LiveSyncStateKind.LaunchingCs2));
        svc.Probe = _leftovers;
        svc.Raise(LiveSyncState.Disconnected);
        await Assert.That(vm.NeedsRestore).IsTrue().Because("a session that ended dirty surfaces at once");
    }

    // The same for a reel, which is what most profiles run: the shell re-evaluates on each reel status.
    [Test]
    public async Task ReelJob_LeftoversSurfaceWhenTheReelFinishes()
    {
        FakeLiveSync svc = new(_clean);
        bool reelRunning = false;
        Cs2InstallRecoveryViewModel vm = new(svc, () => reelRunning);
        await vm.CheckAsync();

        reelRunning = true;
        vm.Reevaluate();
        svc.Probe = _leftovers;
        vm.Reevaluate();
        await Assert.That(vm.IsVisible).IsFalse();

        reelRunning = false;
        vm.Reevaluate();
        await Assert.That(vm.NeedsRestore).IsTrue();
    }

    // The chip flyout's older offer mirrors the banner rather than probing on its own, so restoring from
    // either place clears both.
    [Test]
    public async Task ChipOffer_MirrorsTheBanner()
    {
        FakeLiveSync svc = new(_leftovers);
        Cs2InstallRecoveryViewModel recovery = new(svc, () => false);
        await recovery.CheckAsync();
        using LiveSyncStatusViewModel chip = new(svc, null, new PlaybackController(), () => { },
            installRecovery: recovery);

        await Assert.That(chip.ShowLeftoverRestoreOffer).IsTrue();
        await Assert.That(svc.ProbeCalls).IsEqualTo(1).Because("the chip does not probe on its own");

        await chip.RestoreInstallCommand.ExecuteAsync(null);

        await Assert.That(svc.RestoreCalls).IsEqualTo(1);
        await Assert.That(chip.ShowLeftoverRestoreOffer).IsFalse();
        await Assert.That(recovery.NeedsRestore).IsFalse();
        await Assert.That(recovery.Message).IsEqualTo(Cs2InstallRecoveryViewModel.RestoredText);
    }

    internal sealed class FakeLiveSync(Cs2InstallProbe probe) : ILiveSyncService
    {
        public Cs2InstallProbe Probe { get; set; } = probe;
        public Exception? RestoreFailure { get; init; }
        public int ProbeCalls { get; private set; }
        public int RestoreCalls { get; private set; }

        public LiveSyncState State { get; private set; } = LiveSyncState.Disconnected;
        public long? LastCs2DemoTick => null;
        public LiveSyncVersionInfo? Versions => null;
        public LiveSyncCapabilities? Capabilities => null;

        public event EventHandler<LiveSyncStateChangedEventArgs>? StateChanged;

        public Task EnableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResyncAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> VerifyMomentAsync(int frameClockTick, int preRollTicks = 192, int postRollTicks = 64,
            string? spectateName = null, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> HasLeftoverInstallModificationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Probe.HasLeftovers);

        public Task<Cs2InstallProbe> ProbeInstallAsync(CancellationToken cancellationToken = default)
        {
            ProbeCalls++;
            return Task.FromResult(Probe);
        }

        public Task RestoreInstallAsync(CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            if (RestoreFailure is { } failure)
            {
                return Task.FromException(failure);
            }

            Probe = _clean;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Raise(LiveSyncState next)
        {
            LiveSyncState prev = State;
            State = next;
            StateChanged?.Invoke(this, new LiveSyncStateChangedEventArgs(prev, next));
        }
    }
}

/// <summary>
///     The banner reaches every profile: the shell probes on attach whatever the category, while the Live Sync
///     chip, the only surface the offer had before, stays developer-only. Real <see cref="FeatureGate" /> over
///     a temp settings dir, so the category matrix is the shipped one.
/// </summary>
[NotInParallel]
public class Cs2InstallRecoveryAcrossProfilesTests
{
    // A Faulted session does not own CS2, and a pre-launch guard that refused the launch lands there: the
    // shell's busy check has to let the banner probe and show, or the guard's "use the banner" goes nowhere.
    [Test]
    public async Task FaultedLiveSync_DoesNotHideTheBanner()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());
            Cs2InstallRecoveryViewModelTests.FakeLiveSync liveSync = new(new Cs2InstallProbe(false, false, false));
            vm.AttachLiveSync(liveSync);

            liveSync.Raise(new LiveSyncState(LiveSyncStateKind.HostStarting));
            liveSync.Probe = new Cs2InstallProbe(true, false, false);
            liveSync.Raise(new LiveSyncState(LiveSyncStateKind.Faulted, "the guard refused the launch"));

            await Assert.That(vm.Cs2InstallRecovery!.NeedsRestore).IsTrue();
            await Assert.That(vm.Cs2InstallRecovery.IsVisible).IsTrue();
        });
    }

    [Test]
    [Arguments(UserCategory.Consumer, false)]
    [Arguments(UserCategory.PowerUser, false)]
    [Arguments(UserCategory.Developer, true)]
    public async Task LeftoversSurface_InTheShellBanner_ForEveryProfile(UserCategory category, bool chipShown)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvinstallrecovery_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            SettingsService svc = new(dir);
            svc.Write(s => s.UserCategory = category);
            ServiceCollection services = new();
            services.Configure<AppSettings>(svc.Configuration);
            await using ServiceProvider sp = services.BuildServiceProvider();
            using FeatureGate gate = new(sp.GetRequiredService<IOptionsMonitor<AppSettings>>(), false);

            await HeadlessSession.RunOnUi(async () =>
            {
                MainViewModel vm = new(library: TestLibraries.Empty(), gate: gate);
                Cs2InstallRecoveryViewModelTests.FakeLiveSync liveSync = new(new Cs2InstallProbe(true, false, false));
                vm.AttachLiveSync(liveSync);

                await Assert.That(vm.Cs2InstallRecovery).IsNotNull();
                await Assert.That(vm.Cs2InstallRecovery!.NeedsRestore).IsTrue()
                    .Because($"{category} must see the leftover banner");
                await Assert.That(vm.Cs2InstallRecovery.IsVisible).IsTrue();
                await Assert.That(vm.IsLiveSyncEnabled).IsEqualTo(chipShown)
                    .Because("the chip gate is unchanged: the banner does not depend on it");
            });
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
