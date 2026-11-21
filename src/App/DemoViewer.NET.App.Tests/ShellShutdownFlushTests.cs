#region

using Avalonia.Controls;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Exit never disposes the shell, so the active tab's OnDeactivated, where the 2D tab flushes its debounced
///     annotation autosave, has to be reached by the shutdown flush itself, once, however often exit asks.
/// </summary>
[NotInParallel]
public class ShellShutdownFlushTests
{
    [Test]
    public async Task TheShutdownFlush_DeactivatesTheActiveTabOnce() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            CountingModule module = new();
            ModuleRegistry registry = new();
            registry.Register(module);
            MainViewModel vm = new(null, registry, TestLibraries.Empty());
            try
            {
                await Assert.That(vm.TrySelectTab(CountingModule.TabId)).IsTrue();

                vm.FlushForShutdown();
                vm.FlushForShutdown();

                await Assert.That(module.Deactivations).IsEqualTo(1);
            }
            finally
            {
                vm.Dispose();
            }
        });

    private sealed class CountingModule : IWorkspaceModule, IWorkspaceTabViewModel
    {
        public const string TabId = "test.shutdown.flush";

        public int Deactivations { get; private set; }

        public string Id => "test.shutdown";
        public string DisplayName => "Shutdown";
        public Version ContractVersion => new(1, 0);

        public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host) =>
        [
            new WorkspaceTabDescriptor
            {
                TabId = TabId, Header = TabId, Order = 900, ViewModelFactory = () => this,
                ViewFactory = () => new ContentControl()
            }
        ];

        public void OnActivated(IModuleContext context)
        {
        }

        public void OnDeactivated() => Deactivations++;

        public object? SnapshotState() => null;

        public void RestoreState(object? state)
        {
        }
    }
}
