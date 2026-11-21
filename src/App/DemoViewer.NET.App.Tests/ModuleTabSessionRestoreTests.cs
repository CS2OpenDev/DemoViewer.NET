#region

using System.Text.Json;
using Avalonia.Controls;
using DemoViewer.NET.Models;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Module-tab session state through the shell's real ordering, not a descriptor driven by hand. The shell
///     builds its first tab in the constructor and the restored active tab in RestoreSession, both before any
///     demo loads, and a tab the user never opens is never built at all: each must still get, or keep, its blob.
/// </summary>
[NotInParallel]
public class ModuleTabSessionRestoreTests
{
    private const string FirstTab = "test.restore.first";
    private const string LazyTab = "test.restore.lazy";
    private const string UnopenedTab = "test.restore.unopened";

    private static Dictionary<string, JsonElement> SavedBlobs() => new()
    {
        [FirstTab] = JsonSerializer.SerializeToElement("first-blob"),
        [LazyTab] = JsonSerializer.SerializeToElement("lazy-blob"),
        [UnopenedTab] = JsonSerializer.SerializeToElement("unopened-blob")
    };

    private static async Task WithShell(Func<MainViewModel, SettingsService, RestoreModule, Task> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvmodtabs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                SettingsService svc = new(dir);
                svc.SaveSession(new SessionPayload(null, null, null, false, false, LazyTab, SavedBlobs()));

                RestoreModule module = new();
                ModuleRegistry registry = new();
                registry.Register(module);
                MainViewModel vm = new(null, registry, TestLibraries.Empty(), settingsService: svc);
                try
                {
                    vm.RestoreSession();
                    await body(vm, svc, module);
                }
                finally
                {
                    vm.Dispose();
                }
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

    [Test]
    public async Task TabsBuiltBeforeAnyDemoLoads_ReceiveTheirSavedState() =>
        await WithShell(async (vm, _, module) =>
        {
            using (Assert.Multiple())
            {
                await Assert.That(vm.SelectedTab?.TabId).IsEqualTo(LazyTab);
                await Assert.That(module.Restored.GetValueOrDefault(FirstTab)).IsEqualTo("first-blob")
                    .Because("the first tab is built in the constructor, before RestoreSession");
                await Assert.That(module.Restored.GetValueOrDefault(LazyTab)).IsEqualTo("lazy-blob")
                    .Because("the restored active tab is built by RestoreSession itself");
                await Assert.That(module.Built).DoesNotContain(UnopenedTab);
            }
        });

    [Test]
    public async Task ASave_KeepsTheBlobOfATabNeverOpenedThisSession() =>
        await WithShell(async (vm, svc, _) =>
        {
            vm.SaveSession();
            Dictionary<string, JsonElement>? saved = svc.LoadSession()?.ModuleTabs;

            using (Assert.Multiple())
            {
                await Assert.That(saved).IsNotNull();
                await Assert.That(saved!.TryGetValue(UnopenedTab, out JsonElement blob)).IsTrue();
                await Assert.That(blob.ValueKind == JsonValueKind.String ? blob.GetString() : null)
                    .IsEqualTo("unopened-blob");
                await Assert.That(saved[FirstTab].GetString()).IsEqualTo("first-blob");
            }
        });

    internal sealed class RestoreModule : IWorkspaceModule
    {
        public Dictionary<string, string?> Restored { get; } = [];
        public List<string> Built { get; } = [];

        public string Id => "test.restore";
        public string DisplayName => "Restore";
        public Version ContractVersion => new(1, 0);

        public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host) =>
        [
            Tab(FirstTab, -100),
            Tab(LazyTab, 900),
            Tab(UnopenedTab, 901)
        ];

        private WorkspaceTabDescriptor Tab(string id, int order) => new()
        {
            TabId = id,
            Header = id,
            Order = order,
            ViewModelFactory = () =>
            {
                Built.Add(id);
                return new RecordingTab(id, this);
            },
            ViewFactory = () => new ContentControl()
        };
    }

    private sealed class RecordingTab(string id, RestoreModule module) : IWorkspaceTabViewModel
    {
        private string? _state;

        public void OnActivated(IModuleContext context)
        {
        }

        public void OnDeactivated()
        {
        }

        public object? SnapshotState() => _state;

        public void RestoreState(object? state)
        {
            _state = state is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
            module.Restored[id] = _state;
        }
    }
}
