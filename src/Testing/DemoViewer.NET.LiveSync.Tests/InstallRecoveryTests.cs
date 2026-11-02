#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.LiveSync;
using Microsoft.Extensions.Options;
using TUnit.Core.Exceptions;
using TimeoutException = System.TimeoutException;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     Leftover-install detection and restore against a FAKE CS2 install in a temp directory (pointed at
///     through <c>Cs2RootInstallationDirectory</c>), run through CSVG's real path discovery, backup manager
///     and plugin uninstaller. The CS2-running check is injected so a CS2 open on the test machine cannot
///     change the outcome. No real CS2 install is read or written. On the port key with the host-starting
///     classes because the reel case would bring a host up if the guard ever let it through.
/// </summary>
[NotInParallel("csvg-port-50051")]
public class InstallRecoveryTests
{
    private const string Original =
        "\"GameInfo\"\n{\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n\t\t\tGame\tcsgo\n\t\t\tGame\tcore\n\t\t}\n\t}\n}\n";

    // Byte-for-byte what CSVG's ModifyGameInfoFile writes: the injected line ahead of the matched one.
    private const string Patched =
        "\"GameInfo\"\n{\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n\t\t\tGame\tcsgo/csvg\n\t\t\tGame\tcsgo\n\t\t\tGame\tcore\n\t\t}\n\t}\n}\n";

    [Test]
    public async Task IsExactlyThePatch_AcceptsOnlyTheOneInjectedLine()
    {
        await Assert.That(InstallRecovery.IsExactlyThePatch(Patched, Original)).IsTrue();
        await Assert.That(InstallRecovery.IsExactlyThePatch(
            Patched.Replace("\n", "\r\n", StringComparison.Ordinal),
            Original.Replace("\n", "\r\n", StringComparison.Ordinal))).IsTrue().Because("CRLF installs too");

        // A game update since the backup: restoring it would roll the update back.
        await Assert.That(InstallRecovery.IsExactlyThePatch(
            Patched.Replace("Game\tcore", "Game\tcore2", StringComparison.Ordinal), Original)).IsFalse();
        await Assert.That(InstallRecovery.IsExactlyThePatch(
            Patched.Replace("Game\tcsgo\n", "Game\tcsgo/csvg\n\t\t\tGame\tcsgo\n", StringComparison.Ordinal),
            Original)).IsFalse().Because("a doubled marker is not CSVG's single patch");
        await Assert.That(InstallRecovery.IsExactlyThePatch(Original, Original)).IsFalse();
    }

    [Test]
    public async Task Detect_CleanInstall_FindsNothing()
    {
        using FakeInstall install = new();

        InstallRecovery.LeftoverState? state = InstallRecovery.Detect(install.Settings, () => false);

        await Assert.That(state).IsNotNull();
        await Assert.That(state!.Any).IsFalse();
    }

    [Test]
    public async Task Detect_PatchedWithMatchingBackup_IsSafeToAutoRestore_UnlessCs2IsRunning()
    {
        using FakeInstall install = new();
        install.LeaveCrashedSession(Original);

        InstallRecovery.LeftoverState? closed = InstallRecovery.Detect(install.Settings, () => false);
        await Assert.That(closed!.GameInfoPatched).IsTrue();
        await Assert.That(closed.PluginFilesPresent).IsTrue();
        await Assert.That(closed.SafeToAutoRestore).IsTrue();

        InstallRecovery.LeftoverState? open = InstallRecovery.Detect(install.Settings, () => true);
        await Assert.That(open!.Cs2Running).IsTrue();
        await Assert.That(open.SafeToAutoRestore).IsFalse();
    }

    [Test]
    public async Task Detect_BackupFromBeforeAGameUpdate_IsNotSafeToAutoRestore()
    {
        using FakeInstall install = new();
        install.LeaveCrashedSession(Original.Replace("Game\tcore", "Game\tcore_old", StringComparison.Ordinal));

        InstallRecovery.LeftoverState? state = InstallRecovery.Detect(install.Settings, () => false);

        await Assert.That(state!.Any).IsTrue();
        await Assert.That(state.SafeToAutoRestore).IsFalse();
    }

    [Test]
    public async Task Detect_PluginFilesOnly_IsSafeToAutoRestore()
    {
        using FakeInstall install = new();
        Directory.CreateDirectory(install.PluginDir);

        InstallRecovery.LeftoverState? state = InstallRecovery.Detect(install.Settings, () => false);

        await Assert.That(state!.GameInfoPatched).IsFalse();
        await Assert.That(state.PluginFilesPresent).IsTrue();
        await Assert.That(state.SafeToAutoRestore).IsTrue();
    }

    [Test]
    public async Task Restore_UnpatchesGameInfo_AndRemovesThePlugin()
    {
        using FakeInstall install = new();
        install.LeaveCrashedSession(Original);
        List<string> log = [];

        InstallRecovery.Restore(install.Settings, log.Add, () => false);

        await Assert.That(await File.ReadAllTextAsync(install.GameInfo)).IsEqualTo(Original);
        await Assert.That(Directory.Exists(install.PluginDir)).IsFalse();
        await Assert.That(InstallRecovery.Detect(install.Settings, () => false)!.Any).IsFalse();
        await Assert.That(log).Contains("CS2 install restored.");
    }

    [Test]
    public async Task Restore_RefusesWhileCs2IsRunning_AndTouchesNothing()
    {
        using FakeInstall install = new();
        install.LeaveCrashedSession(Original);

        await Assert.That(() => InstallRecovery.Restore(install.Settings, null, () => true))
            .Throws<InvalidOperationException>().WithMessageContaining("CS2 is running");
        await Assert.That(await File.ReadAllTextAsync(install.GameInfo)).IsEqualTo(Patched);
        await Assert.That(Directory.Exists(install.PluginDir)).IsTrue();
    }

    [Test]
    public async Task PreLaunchGuard_RestoresLeftovers_SoTheLaunchBacksUpACleanFile()
    {
        using FakeInstall install = new();
        install.LeaveCrashedSession(Original);

        InstallRecovery.EnsureRestoredBeforeLaunch(install.Settings, null, () => false);

        await Assert.That(await File.ReadAllTextAsync(install.GameInfo)).IsEqualTo(Original);
        await Assert.That(Directory.Exists(install.PluginDir)).IsFalse();
    }

    [Test]
    public async Task PreLaunchGuard_RefusesTheLaunch_WhenTheLeftoversCannotBeUndone()
    {
        using FakeInstall install = new();
        install.LeaveCrashedSession(Original);

        await Assert.That(() => InstallRecovery.EnsureRestoredBeforeLaunch(install.Settings, null, () => true))
            .Throws<InvalidOperationException>().WithMessageContaining("so CS2 was not started");
    }

    // A CS2 update since the crash makes the backup stale: restoring it would roll the update back. The banner
    // refuses that unattended, so the guard must too, and send the user to the banner or Steam instead.
    [Test]
    public async Task PreLaunchGuard_LeavesAStaleBackupAlone_AndRefusesTheLaunch()
    {
        using FakeInstall install = new();
        install.LeaveCrashedSession(Original.Replace("Game\tcore", "Game\tcore_old", StringComparison.Ordinal));

        await Assert.That(() => InstallRecovery.EnsureRestoredBeforeLaunch(install.Settings, null, () => false))
            .Throws<InvalidOperationException>().WithMessageContaining("Restore my CS2 install");
        await Assert.That(await File.ReadAllTextAsync(install.GameInfo)).IsEqualTo(Patched);
        await Assert.That(Directory.Exists(install.PluginDir)).IsTrue();
    }

    // The banner's unattended restore and the pre-launch guard run on different threads. Held mid-restore,
    // the first must keep the second out until it is done; the second then finds a clean install.
    [Test]
    [Timeout(30_000)]
    public async Task Restores_AreSerialized(CancellationToken cancellationToken)
    {
        using FakeInstall install = new();
        install.LeaveCrashedSession(Original);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();

        Task banner = Task.Run(() => InstallRecovery.Restore(install.Settings, line =>
        {
            if (line.StartsWith("Restoring patched", StringComparison.Ordinal))
            {
                entered.Set();
                release.Wait(cancellationToken);
            }
        }, () => false), cancellationToken);
        entered.Wait(cancellationToken);

        List<string> guardLog = [];
        Task guard = Task.Run(() => InstallRecovery.EnsureRestoredBeforeLaunch(install.Settings, guardLog.Add,
            () => false), cancellationToken);
        await Task.Delay(500, cancellationToken);
        bool guardRanAlongside = guard.IsCompleted;

        release.Set();
        await banner;
        await guard;

        await Assert.That(guardRanAlongside).IsFalse().Because("the guard waits for the restore in progress");
        await Assert.That(guardLog).IsEmpty().Because("by then there is nothing left to restore");
        await Assert.That(await File.ReadAllTextAsync(install.GameInfo, cancellationToken)).IsEqualTo(Original);
        await Assert.That(Directory.Exists(install.PluginDir)).IsFalse();

        // And a restore that loses the race to the guard finds a clean install and still succeeds.
        InstallRecovery.Restore(install.Settings, null, () => false);
    }

    [Test]
    public async Task WaitForCs2Exit_ReturnsOnceCs2IsGone_OrGivesUpAtTheTimeout()
    {
        int polls = 0;
        await Assert.That(await InstallRecovery.WaitForCs2ExitAsync(TimeSpan.FromSeconds(10), CancellationToken.None,
            () => ++polls <= 2)).IsTrue();
        await Assert.That(polls).IsEqualTo(3);

        await Assert.That(await InstallRecovery.WaitForCs2ExitAsync(TimeSpan.FromMilliseconds(300),
            CancellationToken.None, () => true)).IsFalse();

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));
        await Assert.That(async () => await InstallRecovery.WaitForCs2ExitAsync(TimeSpan.FromSeconds(30), cts.Token,
            () => true)).Throws<OperationCanceledException>();
    }

    // The reel path runs the same guard before CSVG launches CS2. Leftovers with no backup to restore from
    // fail the job with the guard's reason, before any host or CS2 comes up, and the install is untouched.
    [Test]
    public async Task ReelJob_WithUnrecoverableLeftovers_FailsBeforeLaunchingCs2()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SkipTestException("real (non-dry-run) reels are Windows-only; the job fails earlier elsewhere");
        }

        using FakeInstall install = new();
        install.LeaveCrashedSession(null);

        ReelJobService reel = new(null, null, new FixedMonitor(new AppSettings
        {
            LiveSync = install.Settings
        }));
        string demo = Path.Combine(install.Root, "x.dem");
        reel.Start(new ReelRequest(
            [new ReelClip(demo, null, 1, "p", 0, 64, 64, "clip")],
            install.Root, "reel", "mp4", 60, false, false, null, null, false, false));

        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (reel.Status.IsRunning || reel.Status.Phase == ReelJobPhase.Idle)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the reel job never finished");
            }

            await Task.Delay(20);
        }

        await Assert.That(reel.Status.Phase).IsEqualTo(ReelJobPhase.Failed);
        await Assert.That(reel.Status.Error).Contains("so CS2 was not started");
        await Assert.That(await File.ReadAllTextAsync(install.GameInfo)).IsEqualTo(Patched);
    }

    private sealed class FixedMonitor(AppSettings value) : IOptionsMonitor<AppSettings>
    {
        public AppSettings CurrentValue => value;
        public AppSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<AppSettings, string?> listener) => null;
    }

    private sealed class FakeInstall : IDisposable
    {
        public FakeInstall()
        {
            Root = Path.Combine(Path.GetTempPath(), "dv-fake-cs2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "game", "csgo"));
            File.WriteAllText(GameInfo, Original);
            Settings = new LiveSyncSettings
            {
                Cs2RootInstallationDirectory = Root
            };
        }

        public string Root { get; }
        public string GameInfo => Path.Combine(Root, "game", "csgo", "gameinfo.gi");
        public string PluginDir => Path.Combine(Root, "game", "csgo", "csvg");
        public LiveSyncSettings Settings { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch
            {
                // a leftover temp directory is not a test failure
            }
        }

        // What a DV crash mid-session leaves: CSVG's adjacent backup (when given), the patched file and the
        // plugin directory.
        public void LeaveCrashedSession(string? backup)
        {
            if (backup is not null)
            {
                File.WriteAllText(GameInfo + ".backup", backup);
            }

            File.WriteAllText(GameInfo, Patched);
            Directory.CreateDirectory(Path.Combine(PluginDir, "bin", "win64"));
            File.WriteAllText(Path.Combine(PluginDir, "bin", "win64", "server.dll"), "plugin");
        }
    }
}
