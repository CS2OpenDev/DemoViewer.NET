#region

using DemoViewer.NET.Extensions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The host's side of what an extension declares for Library Transfer: a demo-keyed template or an own path
///     that would let an import write past the extension's files is refused, and the enumeration the exporter and
///     importer read lists only what travels.
/// </summary>
public class HostDataTransferTests
{
    private const string ExtensionId = "dev.example.transfer";

    [Test]
    // Accepted: one placeholder in the file name, under a declared path, in either separator.
    [Arguments(StorePortability.PerDemo, "dev.example.transfer/demos/{sha256}.json", true)]
    [Arguments(StorePortability.PerDemo, "dev.example.transfer/{sha256}", true)]
    [Arguments(StorePortability.PerDemo, "dev.example.transfer\\demos\\{sha256}.json", true)]
    [Arguments(StorePortability.PerDemo, "DEV.EXAMPLE.TRANSFER/{sha256}.json", true)]
    // The placeholder: missing, twice, or in a folder name.
    [Arguments(StorePortability.PerDemo, "dev.example.transfer/demos/a.json", false)]
    [Arguments(StorePortability.PerDemo, "dev.example.transfer/{sha256}.{sha256}.json", false)]
    [Arguments(StorePortability.PerDemo, "dev.example.transfer/{sha256}/a.json", false)]
    [Arguments(StorePortability.PerDemo, "dev.example.transfer/{SHA256}.json", false)]
    // Outside the store: another folder, the store's name as a prefix only, a climb, a rooted path, a wildcard.
    [Arguments(StorePortability.PerDemo, "other/{sha256}.json", false)]
    [Arguments(StorePortability.PerDemo, "dev.example.transferx/{sha256}.json", false)]
    [Arguments(StorePortability.PerDemo, "{sha256}.json", false)]
    [Arguments(StorePortability.PerDemo, "dev.example.transfer/../settings/{sha256}.json", false)]
    [Arguments(StorePortability.PerDemo, "/dev.example.transfer/{sha256}.json", false)]
    [Arguments(StorePortability.PerDemo, "C:/dev.example.transfer/{sha256}.json", false)]
    [Arguments(StorePortability.PerDemo, "dev.example.transfer/*/{sha256}.json", false)]
    [Arguments(StorePortability.PerDemo, "", false)]
    // A template on a store that does not travel per demo means nothing the exporter would honour.
    [Arguments(StorePortability.LibraryWide, "dev.example.transfer/{sha256}.json", false)]
    [Arguments(StorePortability.None, "dev.example.transfer/{sha256}.json", false)]
    public async Task ADemoKeyedTemplate_MustNameOneDemosFileInsideTheStore(StorePortability portability,
        string template, bool accepted)
    {
        StoreDescriptor store = new("s", "S", StoreRoot.Config, [ExtensionId], IsUserWork: true)
        {
            Portability = portability, DemoKeyedPaths = [template]
        };

        string? refusal = PackContributions.StoreRefusal(ExtensionId, store);

        await Assert.That(refusal is null).IsEqualTo(accepted).Because(refusal ?? "accepted");
    }

    [Test]
    public async Task APortabilityDeclaration_IsChecked_BesideThePaths()
    {
        StoreDescriptor Declared(StorePortability portability, string[]? excludes = null) =>
            new("s", "S", StoreRoot.Config, [ExtensionId], IsUserWork: true)
            {
                Portability = portability, PortableExcludes = excludes ?? []
            };

        using (Assert.Multiple())
        {
            await Assert.That(PackContributions.StoreRefusal(ExtensionId, Declared(StorePortability.None))).IsNull();
            await Assert.That(PackContributions.StoreRefusal(ExtensionId, Declared(StorePortability.LibraryWide)))
                .IsNull();
            await Assert.That(PackContributions.StoreRefusal(ExtensionId,
                Declared(StorePortability.LibraryWide, ["index.json", ".trash"]))).IsNull();
            await Assert.That(PackContributions.StoreRefusal(ExtensionId, Declared(StorePortability.PerDemo)))
                .IsNotNull().Because("a per-demo store with no template would carry nothing");
            await Assert.That(PackContributions.StoreRefusal(ExtensionId, Declared((StorePortability)7)))
                .IsNotNull();
            await Assert.That(PackContributions.StoreRefusal(ExtensionId,
                Declared(StorePortability.LibraryWide, ["sub/index.json"]))).IsNotNull();
            await Assert.That(PackContributions.StoreRefusal(ExtensionId,
                Declared(StorePortability.LibraryWide, [" "]))).IsNotNull();
        }
    }

    [Test]
    [Arguments(StoreRoot.Config, "state.json", true)]
    [Arguments(StoreRoot.Cache, "sub/lineups.json.gz", true)]
    [Arguments(StoreRoot.Cache, "sub\\lineups.json.gz", true)]
    [Arguments(StoreRoot.Config, "../settings.json", false)]
    [Arguments(StoreRoot.Config, "sub/../../x.json", false)]
    [Arguments(StoreRoot.Config, "/etc/passwd", false)]
    [Arguments(StoreRoot.Config, "C:/x.json", false)]
    [Arguments(StoreRoot.Config, "", false)]
    [Arguments(StoreRoot.Config, "./x.json", false)]
    [Arguments((StoreRoot)9, "x.json", false)]
    public async Task AnOwnPath_MustNameAFileInsideTheOwnFolder(StoreRoot root, string path, bool accepted)
    {
        string? refusal = PackContributions.OwnPathRefusal(new PortableOwnPath(root, path));

        await Assert.That(refusal is null).IsEqualTo(accepted).Because(refusal ?? "accepted");
    }

    [Test]
    public async Task TheEnumeration_ListsTheStoresThatTravel_AndTheAcceptedOwnPaths()
    {
        StoreDescriptor local = new("local", "Local", StoreRoot.Cache, [ExtensionId + "-index.json"], false);
        StoreDescriptor library = new("library", "Library", StoreRoot.Config, [ExtensionId + ".json"], true)
        {
            Portability = StorePortability.LibraryWide, ExtensionMerges = true
        };
        StoreDescriptor perDemo = new("per-demo", "Per demo", StoreRoot.Config, [ExtensionId], true)
        {
            Portability = StorePortability.PerDemo, DemoKeyedPaths = [ExtensionId + "/{sha256}.json"]
        };
        StoreDescriptor refused = new("refused", "Refused", StoreRoot.Config, [ExtensionId + "-x"], true)
        {
            Portability = StorePortability.PerDemo, DemoKeyedPaths = ["elsewhere/{sha256}.json"]
        };
        Portability portability = new(ExtensionId, [
            new PortableOwnPath(StoreRoot.Config, "state.json"),
            new PortableOwnPath(StoreRoot.Cache, "../escape.json"),
            null!
        ]);
        Pack pack = new([local, library, perDemo, refused], portability);
        ExtensionGuard guard = ExtensionGuard.Standalone(pack);
        PackContributions contributions = new(pack, () => null!, guard: guard);
        pack.Contribute(contributions, new ServiceCollection().BuildServiceProvider());

        HostDataTransfer transfer = new(contributions);
        IReadOnlyList<PortableOwnPath> first = transfer.OwnPaths;
        IReadOnlyList<PortableOwnPath> second = transfer.OwnPaths;

        using (Assert.Multiple())
        {
            await Assert.That(transfer.Stores).IsEquivalentTo([library, perDemo], CollectionOrdering.Matching);
            await Assert.That(first).IsEquivalentTo([new PortableOwnPath(StoreRoot.Config, "state.json")],
                CollectionOrdering.Matching);
            await Assert.That(second).IsSameReferenceAs(first);
            await Assert.That(portability.OwnPathsReads).IsEqualTo(1)
                .Because("the list is read once, so each refusal is reported once");
            await Assert.That(guard.Faults.StateOf(pack.FeatureId).Count).IsEqualTo(3)
                .Because("the refused store, the climbing own path and the null one are each reported");
            await Assert.That(transfer.FeatureId).IsEqualTo(pack.FeatureId);
            await Assert.That(transfer.Pack.DataPortabilityContribution).IsSameReferenceAs(portability);
        }
    }

    [Test]
    public async Task WithoutAPortabilityContribution_OnlyTheDeclaredStoresTravel()
    {
        StoreDescriptor library = new("library", "Library", StoreRoot.Config, [ExtensionId + ".json"], true)
        {
            Portability = StorePortability.LibraryWide
        };
        Pack pack = new([library], null);
        PackContributions contributions = new(pack, () => null!, guard: ExtensionGuard.Standalone(pack));

        HostDataTransfer transfer = new(contributions);

        using (Assert.Multiple())
        {
            await Assert.That(transfer.Stores).IsEquivalentTo([library], CollectionOrdering.Matching);
            await Assert.That(transfer.OwnPaths).IsEmpty();
            await Assert.That(transfer.FeatureId).IsEqualTo(pack.FeatureId);
        }
    }

    [Test]
    public async Task APortabilityThatNamesAnothersFeature_IsGatedOnTheMasterSwitch()
    {
        Pack pack = new([], new Portability("someone.else", []));
        ExtensionGuard guard = ExtensionGuard.Standalone(pack);
        PackContributions contributions = new(pack, () => null!, guard: guard);
        pack.Contribute(contributions, new ServiceCollection().BuildServiceProvider());

        string featureId = new HostDataTransfer(contributions).FeatureId;

        using (Assert.Multiple())
        {
            await Assert.That(featureId).IsEqualTo(pack.FeatureId);
            await Assert.That(guard.Faults.StateOf(pack.FeatureId).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task DataImported_RunsEachCallbackOnTheUiThread_AndAThrowDoesNotStopTheRest()
    {
        Pack pack = new([], null);
        ExtensionGuard guard = ExtensionGuard.Standalone(pack);
        List<string> ran = [];
        int marshalled = 0;
        PackContributions contributions = new(pack, () => null!, a =>
        {
            marshalled++;
            a();
        }, guard);
        contributions.DataImported(() => ran.Add("first"));
        contributions.DataImported(() => throw new InvalidOperationException("boom"));
        contributions.DataImported(() => ran.Add("third"));

        contributions.RaiseDataImported();

        using (Assert.Multiple())
        {
            await Assert.That(ran).IsEquivalentTo(["first", "third"], CollectionOrdering.Matching);
            await Assert.That(marshalled).IsEqualTo(3);
            await Assert.That(guard.Faults.StateOf(pack.FeatureId).Count).IsEqualTo(1);
        }
    }

    private sealed class Portability(string featureId, IReadOnlyList<PortableOwnPath> ownPaths)
        : IExtensionDataPortability
    {
        public int OwnPathsReads { get; private set; }

        public string FeatureId => featureId;

        public IReadOnlyList<PortableOwnPath> OwnPaths
        {
            get
            {
                OwnPathsReads++;
                return ownPaths;
            }
        }

        public Task<ExtensionImportResult> MergeAsync(IExtensionImport import, CancellationToken ct) =>
            Task.FromResult(new ExtensionImportResult(0, 0, 0, 0));
    }

    private sealed class Pack(IEnumerable<StoreDescriptor> stores, IExtensionDataPortability? portability)
        : IExtension
    {
        public string Id => ExtensionId;
        public string FeatureId => "pack." + ExtensionId;
        public IEnumerable<ExtensionFeature> Features => [];
        public IEnumerable<StoreDescriptor> Stores => stores;

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
            if (portability is not null)
            {
                contributions.DataPortability(portability);
            }
        }
    }
}
