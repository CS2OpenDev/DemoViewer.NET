#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="IExtensionShell.OpenPlayerOverview(ulong, PlayerOverviewScope?)" /> through a real shell: the scope
///     reaches the tab, a null scope opens it unscoped, and a shell built before the overload never opens the tab
///     unscoped when a scope was asked for.
/// </summary>
[NotInParallel]
public class ExtensionPlayerOverviewScopeTests
{
    private const ulong Grim = 76561190000000003;
    private const ulong Mate = 76561190000000012;

    [Test]
    public async Task TheShellView_PassesTheScopeToThePlayerOverview()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using IdentityService identity = new(new SettingsService(null));
            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty(),
                players: new PlayerDirectory(new DemoCacheStore(null)), identity: identity);
            try
            {
                IExtensionShell shell = Attach(vm);
                PlayerOverviewScope scope = new("vs Team X", ["abc123", "/demos/a.dem"]);

                bool opened = shell.OpenPlayerOverview(Grim, scope);
                using (Assert.Multiple())
                {
                    await Assert.That(opened).IsTrue();
                    await Assert.That(vm.PlayerOverviewTab!.SubjectSteamId64).IsEqualTo(Grim);
                    await Assert.That(vm.PlayerOverviewTab.Scope).IsSameReferenceAs(scope);
                    await Assert.That(vm.SelectedTab?.TabId).IsEqualTo(HostIds.PlayerOverviewTab);
                }

                await Assert.That(shell.OpenPlayerOverview(Mate, null)).IsTrue();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.PlayerOverviewTab.SubjectSteamId64).IsEqualTo(Mate);
                    await Assert.That(vm.PlayerOverviewTab.Scope).IsNull();
                }

                await Assert.That(shell.OpenPlayerOverview(0, scope)).IsFalse();
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Test]
    public async Task AnOlderShell_OpensUnscopedOnly_WhenNoScopeIsAsked()
    {
        OlderShell shell = new();
        IExtensionShell view = shell;

        using (Assert.Multiple())
        {
            await Assert.That(view.OpenPlayerOverview(Grim, null)).IsTrue();
            await Assert.That(view.OpenPlayerOverview(Grim, new PlayerOverviewScope("vs Team X", ["abc"]))).IsFalse();
            await Assert.That(shell.Opened).IsEqualTo(1).Because("a scoped open never falls back to an unscoped one");
        }
    }

    private static IExtensionShell Attach(MainViewModel vm)
    {
        FaultRig rig = new();
        ExtensionShellHub hub = new();
        hub.Attach(vm);
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(hub)
            .AddSingleton(rig.Faults)
            .BuildServiceProvider();
        return new ExtensionContext(new StubExtension(rig.Scope.FeatureId), services).Shell;
    }

    // A shell built against an SDK with only the unscoped open.
    private sealed class OlderShell : IExtensionShell
    {
        public int Opened { get; private set; }

        public string? CurrentDemoPath => null;

        public event Action? CurrentDemoChanged
        {
            add { }
            remove { }
        }

        public CS2DemoKit.Parser.ParsedDemo? CurrentDemo => null;

        public string? CurrentDemoHash => null;

        public int CurrentTick => 0;

        public bool IsPlaying => false;

        public event Action? PlayheadChanged
        {
            add { }
            remove { }
        }

        public Task<bool> OpenDemoAsync(string path) => Task.FromResult(false);

        public void SeekToTick(int tick)
        {
        }

        public bool SelectTab(string tabId) => false;

        public bool OpenPlayerOverview(ulong steamId64)
        {
            Opened++;
            return true;
        }

        public void RevealInFileManager(string path)
        {
        }
    }
}
