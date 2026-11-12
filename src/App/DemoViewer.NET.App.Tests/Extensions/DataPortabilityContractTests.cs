#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Enums;
using SdkPlayback = DemoViewer.NET.Extensions.Sdk.Playback;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The SDK 2.3 transfer contract is additive: a store declared before it stays on the machine, a double written
///     against 2.2 still compiles and ignores the new contributions, and the host keeps what a pack hands over.
/// </summary>
public class DataPortabilityContractTests
{
    [Test]
    public async Task AStoreDeclaredBefore23_StaysOnTheMachine_AndDeconstructsAsBefore()
    {
        StoreDescriptor store = new("notes", "Notes", StoreRoot.Config, ["notes.json"], true);
        (string id, string label, StoreRoot root, IReadOnlyList<string> paths, bool isUserWork) = store;

        using (Assert.Multiple())
        {
            await Assert.That(store.Portability).IsEqualTo(StorePortability.None);
            await Assert.That(store.DemoKeyedPaths).IsEmpty();
            await Assert.That(store.PortableExcludes).IsEmpty();
            await Assert.That(store.ExtensionMerges).IsFalse();
            await Assert.That((id, label, root, isUserWork)).IsEqualTo(("notes", "Notes", StoreRoot.Config, true));
            await Assert.That(paths).IsEquivalentTo(["notes.json"], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task TheTransferLists_AreCopiedWhenSet_LikePaths()
    {
        List<string?> templates = ["tags/demos/{sha256}.dvtag.json", null];
        List<string?> excludes = ["index.json"];
        StoreDescriptor store = new("tags", "Tags", StoreRoot.Config, ["tags"], true)
        {
            Portability = StorePortability.PerDemo,
            DemoKeyedPaths = templates!,
            PortableExcludes = excludes!,
            ExtensionMerges = true
        };
        templates.Clear();
        excludes.Add(".trash");

        using (Assert.Multiple())
        {
            await Assert.That(store.DemoKeyedPaths)
                .IsEquivalentTo(["tags/demos/{sha256}.dvtag.json", ""], CollectionOrdering.Matching)
                .Because("a null entry becomes an empty path, which the host refuses");
            await Assert.That(store.PortableExcludes).IsEquivalentTo(["index.json"], CollectionOrdering.Matching);
            await Assert.That(store.ExtensionMerges).IsTrue();
        }
    }

    [Test]
    public async Task ADoubleWrittenAgainst22_IgnoresTheTransferContributions()
    {
        Contributions22 contributions = new();
        IExtensionContributions asSdk = contributions;
        bool ran = false;

        asSdk.DataPortability(new MinimalPortability());
        asSdk.DataImported(() => ran = true);

        await Assert.That(ran).IsFalse();
    }

    [Test]
    public async Task APortabilityWithoutOwnPaths_CarriesNone()
    {
        IExtensionDataPortability portability = new MinimalPortability();

        await Assert.That(portability.OwnPaths).IsEmpty();
    }

    [Test]
    public async Task TheHost_KeepsThePortability_AndRaisesDataImportedUnderTheGuard()
    {
        Pack pack = new();
        PackContributions contributions = new(pack, () => throw new InvalidOperationException("no context needed"));
        MinimalPortability portability = new();
        int imported = 0;

        contributions.DataPortability(portability);
        contributions.DataImported(() => throw new InvalidOperationException("reload failed"));
        contributions.DataImported(() => imported++);
        contributions.RaiseDataImported();

        using (Assert.Multiple())
        {
            await Assert.That(contributions.DataPortabilityContribution).IsSameReferenceAs(portability);
            await Assert.That(imported).IsEqualTo(1).Because("a throwing callback does not stop the next one");
            await Assert.That(contributions.Guard.Faults.StateOf(pack.FeatureId).Count).IsEqualTo(1);
        }
    }

    private sealed class MinimalPortability : IExtensionDataPortability
    {
        public string FeatureId => "pack.transfer";

        public Task<ExtensionImportResult> MergeAsync(IExtensionImport import, CancellationToken ct) =>
            Task.FromResult(new ExtensionImportResult(0, 0, 0, 0));
    }

    // Every member IExtensionContributions had in 2.2, and nothing after.
    private sealed class Contributions22 : IExtensionContributions
    {
        public IExtensionContext Context => throw new NotSupportedException();

        public void Tabs(IWorkspaceModule workspaceModule)
        {
        }

        public void Pass(string id, Func<IExtensionPass> factory, params string[] after)
        {
        }

        public void RecordPass(string id, Func<IExtensionRecordPass> factory, params RecordInput[] dependsOn)
        {
        }

        public void Collector(CollectorContribution collector)
        {
        }

        public void Ruleset(RulesetContribution ruleset)
        {
        }

        public void SettingsPage(SettingsPageContribution page)
        {
        }

        public void SettingsSchema(SettingsSchema schema)
        {
        }

        public void Playback(SdkPlayback.IPlaybackContribution contribution)
        {
        }

        public void Library(ILibraryContribution contribution)
        {
        }

        public void DataRemoval(IExtensionDataRemoval removal)
        {
        }

        public void DataDeleted(Action afterDelete)
        {
        }

        public void ReindexEstimate(IReindexEstimate estimate)
        {
        }

        public void DemoAction(DemoAction action)
        {
        }

        public void HubTab(HubTabContribution hub)
        {
        }

        public void StatusChip(StatusChipContribution chip)
        {
        }
    }

    private sealed class Pack : IExtension
    {
        public string Id => "dev.example.transfer";
        public string FeatureId => "pack.transfer";
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}
