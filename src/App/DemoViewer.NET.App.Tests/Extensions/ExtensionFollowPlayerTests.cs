#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="IExtensionShell.FollowPlayer" /> through a real shell with the 2D tab registered: the call shows the
///     tab and reaches its view model, which queues the follow while the demo's roster is not in.
/// </summary>
[NotInParallel]
public class ExtensionFollowPlayerTests
{
    [Test]
    public async Task TheShellView_ShowsThe2DTab_AndHandsTheAccountToIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-follow-player-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string demo = Path.Combine(dir, "a.dem");
        await File.WriteAllBytesAsync(demo, [1]);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                using HeavyJobGate gate = new();
                using DemoProcessingQueue queue = new(gate, run => run(),
                    parseBytes: _ => throw new InvalidDataException("not a demo"),
                    compactHeap: () => Task.CompletedTask);
                ModuleRegistry registry = new();
                registry.Register(new Playback2DModule());
                MainViewModel vm = new(null, registry, TestLibraries.Empty(), heavyJobGate: gate,
                    processingQueue: queue);
                try
                {
                    IExtensionShell shell = Attach(vm);
                    bool beforeLoad = shell.FollowPlayer(42);
                    string? tabBeforeLoad = vm.SelectedTab?.TabId;

                    // The parse fails, which leaves the path on the shell and no roster: the follow is queued.
                    await vm.LoadDemoFromPathAsync(demo);
                    bool afterLoad = shell.FollowPlayer(42);

                    using (Assert.Multiple())
                    {
                        await Assert.That(beforeLoad).IsFalse().Because("no demo is loaded");
                        await Assert.That(tabBeforeLoad).IsNotEqualTo(HostIds.Playback2DTab);
                        await Assert.That(vm.LoadedDemoPath).IsEqualTo(demo);
                        await Assert.That(afterLoad).IsTrue();
                        await Assert.That(vm.SelectedTab?.TabId).IsEqualTo(HostIds.Playback2DTab);
                        await Assert.That(vm.SelectedTab?.TabViewModel).IsTypeOf<Playback2DTabViewModel>();
                        await Assert.That(shell.FollowPlayer(0)).IsFalse();
                    }
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task AShellViewWithNoShell_FollowsNobody()
    {
        FaultRig rig = new();
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(new ExtensionShellHub())
            .AddSingleton(rig.Faults)
            .BuildServiceProvider();
        IExtensionShell shell = new ExtensionContext(new StubExtension(rig.Scope.FeatureId), services).Shell;

        await Assert.That(shell.FollowPlayer(42)).IsFalse();
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
}
