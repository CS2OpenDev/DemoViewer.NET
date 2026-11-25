#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     "Delete extension data" without a removal of the extension's own: the extension's folders (its per-demo
///     data included) and its declared stores go, the per-demo index forgets them, and the extension hears
///     about it afterwards. Another extension's folders are untouched.
/// </summary>
[NotInParallel]
public class HostDataRemovalTests
{
    [Test]
    public async Task TheHostsDelete_TakesTheExtensionsFoldersAndStores_AndNothingOfAnotherExtension()
    {
        string id = "dev.example.remove." + Guid.NewGuid().ToString("N")[..8];
        string otherId = id + ".other";
        string configRoot = AppPaths.ConfigRoot!;
        string cacheRoot = AppPaths.DemoCacheDir!;
        Pack pack = new(id,
            [new StoreDescriptor("legacy", "Legacy", StoreRoot.Config, [id + "-legacy.json"], IsUserWork: true)]);
        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        services.AddSingleton(new DemoCacheStore(null));
        await using ServiceProvider sp = services.BuildServiceProvider();
        ExtensionContext context = new(pack, sp);
        sp.GetRequiredService<DemoCacheStore>().Update("/demos/a.dem", 1, 1, r => r.Sha256 = "aaaa");

        context.Stores.Own(StoreRoot.Config)!.WriteAtomic("notes.json", "{}"u8);
        context.Stores.Own(StoreRoot.Cache)!.WriteAtomic("index.bin", [1]);
        context.Data.Write("/demos/a.dem", new DemoDataWrite("rounds", 1, "fp", new byte[] { 1, 2 }));
        string declared = Path.Combine(configRoot, id + "-legacy.json");
        await File.WriteAllTextAsync(declared, "{}");
        string otherFolder = Path.Combine(cacheRoot, ExtensionFolders.DataDirectoryName, otherId);
        Directory.CreateDirectory(otherFolder);
        await File.WriteAllTextAsync(Path.Combine(otherFolder, "keep.json"), "{}");

        PackContributions contributions = new(pack, () => context);
        int deleted = 0;
        contributions.DataDeleted(() => deleted++);
        HostDataRemoval removal = new(contributions, sp);

        try
        {
            ExtensionDataInventory before = await removal.InventoryAsync();
            await Assert.That(before.Items.Sum(i => i.FileCount)).IsEqualTo(5)
                .Because("the config file, the cache file, the data file, the data index and the declared store");

            ExtensionDataRemovalResult result = await removal.DeleteAsync();

            using (Assert.Multiple())
            {
                await Assert.That(result.Ran).IsTrue();
                await Assert.That(Directory.Exists(Path.Combine(configRoot, ExtensionFolders.DataDirectoryName, id)))
                    .IsFalse();
                await Assert.That(Directory.Exists(Path.Combine(cacheRoot, ExtensionFolders.DataDirectoryName, id)))
                    .IsFalse();
                await Assert.That(File.Exists(declared)).IsFalse();
                await Assert.That(context.Data.Stamp("/demos/a.dem", "rounds")).IsNull()
                    .Because("the index was forgotten with the files");
                await Assert.That(File.Exists(Path.Combine(otherFolder, "keep.json"))).IsTrue();
                await Assert.That(deleted).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(otherFolder, true);
        }
    }

    [Test]
    public async Task TheHostsDelete_ListsTheFactsOfTheExtensionsRulesets()
    {
        string id = "dev.example.remove." + Guid.NewGuid().ToString("N")[..8];
        Pack pack = new(id);
        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        services.AddSingleton(new DemoCacheStore(null));
        await using ServiceProvider sp = services.BuildServiceProvider();
        PackContributions contributions = new(pack, () => new ExtensionContext(pack, sp));
        contributions.Ruleset(new RulesetContribution("kills", () => new MemoryStream()));

        StoreDescriptor? facts = new HostDataRemoval(contributions, sp).Stores
            .FirstOrDefault(s => s.Id == "facts:" + RulesetContribution.QualifiedId(id, "kills"));

        await Assert.That(facts).IsNotNull();
        await Assert.That(facts!.Paths)
            .IsEquivalentTo([$"demos/*.facts.{RulesetContribution.QualifiedId(id, "kills")}.json.gz"],
                CollectionOrdering.Matching);
    }

    /// <summary>
    ///     The confirmation names user work, and the extension's cache folder holds the extension's own
    ///     saved state, so it has to be named. It also has to stay the whole folder: ExtensionStores strands
    ///     an older build's entry at the root when a newer copy is already in files/, and a descriptor
    ///     naming only the two subfolders would leave that behind on a delete.
    /// </summary>
    [Test]
    public async Task TheExtensionsCacheFolder_IsNamedAsUserWork_AndCoversTheWholeFolder()
    {
        string id = "dev.example.cache." + Guid.NewGuid().ToString("N")[..8];
        Pack pack = new(id);
        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        services.AddSingleton(new DemoCacheStore(null));
        await using ServiceProvider sp = services.BuildServiceProvider();
        PackContributions contributions = new(pack, () => new ExtensionContext(pack, sp));

        StoreDescriptor cache = new HostDataRemoval(contributions, sp).Stores.Single(s => s.Id == "extension-cache");

        using (Assert.Multiple())
        {
            await Assert.That(cache.IsUserWork).IsTrue()
                .Because("a rebuild cannot recover what the extension saved there");
            await Assert.That(cache.Root).IsEqualTo(StoreRoot.Cache);
            await Assert.That(cache.Paths)
                .IsEquivalentTo([ExtensionFolders.DataDirectoryName + "/" + id], CollectionOrdering.Matching)
                .Because("a stranded old-layout entry sits at the folder root, outside both subfolders");
        }
    }

    [Test]
    public async Task ARulesetAPassReads_HasNoFactsToDelete_AndReachesThatPass()
    {
        string id = "dev.example.reader." + Guid.NewGuid().ToString("N")[..8];
        Pack pack = new(id);
        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        services.AddSingleton(new DemoCacheStore(null));
        await using ServiceProvider sp = services.BuildServiceProvider();
        PackContributions contributions = new(pack, () => new ExtensionContext(pack, sp));
        contributions.Pass(id + ".reader", () => new ExtensionPassHostTests.FakePass { PassName = id + ".reader" });
        contributions.Ruleset(
            new RulesetContribution("kills", () => new MemoryStream())
            {
                ReadBy = id + ".reader", ReadsUserCommands = true
            });

        IReadOnlyList<StoreDescriptor> stores = new HostDataRemoval(contributions, sp).Stores;
        ExtensionPassHost host = (ExtensionPassHost)contributions.Passes.Single().Factory();

        using (Assert.Multiple())
        {
            await Assert.That(stores.Any(s => s.Id.StartsWith("facts:", StringComparison.Ordinal))).IsFalse();
            await Assert.That(contributions.Rulesets.Single().ReadBy).IsEqualTo(id + ".reader");
            await Assert.That(contributions.Rulesets.Single().ReadsUserCommands).IsTrue();
            await Assert.That(host.TableRulesets).IsEquivalentTo([RulesetContribution.QualifiedId(id, "kills")],
                CollectionOrdering.Matching);
            await Assert.That(host.Needs(new VisitedDemo("/d/match.dem")).UserCommands).IsTrue()
                .Because("the ruleset's declaration reaches the pass's needs");
        }
    }

    [Test]
    public async Task ARulesetTheManifestDoesNotList_IsDroppedAndReported()
    {
        string id = "dev.example.unlisted." + Guid.NewGuid().ToString("N")[..8];
        Pack pack = new(id);
        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        ExtensionGuard guard = ExtensionGuard.Standalone(pack);
        PackContributions contributions = new(pack, () => new ExtensionContext(pack, sp), guard: guard);

        contributions.Ruleset(new RulesetContribution("deaths", () => new MemoryStream()));
        contributions.Ruleset(new RulesetContribution("kills", () => new MemoryStream()));

        using (Assert.Multiple())
        {
            await Assert.That(contributions.Rulesets.Select(r => r.RulesetId))
                .IsEquivalentTo([RulesetContribution.QualifiedId(id, "kills")], CollectionOrdering.Matching);
            await Assert.That(guard.Faults.StateOf(pack.FeatureId).Count).IsEqualTo(1);
        }
    }

    /// <summary>
    ///     A ruleset is filed under its extension's id with every '.' and '-' made '_', so "x.y" and "x-y" file
    ///     "kills" under one id, and that id alone names the facts files and their stamps. The extension
    ///     declared first keeps it. The other's is dropped, so its delete cannot take the first one's facts.
    /// </summary>
    [Test]
    public async Task ARulesetAnEarlierExtensionAlreadyFiles_IsDroppedAndReported_AndItsFactsAreNotListed()
    {
        string tail = Guid.NewGuid().ToString("N")[..8];
        Pack first = new("dev.example.x" + tail);
        Pack second = new("dev.example-x" + tail);
        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        services.AddSingleton(new DemoCacheStore(null));
        await using ServiceProvider sp = services.BuildServiceProvider();
        ExtensionGuard guard = ExtensionGuard.Standalone(second);
        PackContributions kept = new(first, () => new ExtensionContext(first, sp)) { LoadedPacks = [first, second] };
        PackContributions dropped = new(second, () => new ExtensionContext(second, sp), guard: guard)
        {
            LoadedPacks = [first, second]
        };

        kept.Ruleset(new RulesetContribution("kills", () => new MemoryStream()));
        dropped.Ruleset(new RulesetContribution("kills", () => new MemoryStream()));

        string filed = RulesetContribution.QualifiedId(first.Id, "kills");
        using (Assert.Multiple())
        {
            await Assert.That(RulesetContribution.QualifiedId(second.Id, "kills")).IsEqualTo(filed)
                .Because("the two ids file the ruleset under one id, which is the collision");
            await Assert.That(kept.Rulesets.Select(r => r.RulesetId))
                .IsEquivalentTo([filed], CollectionOrdering.Matching);
            await Assert.That(dropped.Rulesets).IsEmpty();
            await Assert.That(guard.Faults.StateOf(second.FeatureId).Count).IsEqualTo(1);
            await Assert.That(new HostDataRemoval(dropped, sp).Stores
                    .Any(s => s.Id.StartsWith("facts:", StringComparison.Ordinal))).IsFalse()
                .Because("the facts under that id are the first extension's");
        }
    }

    /// <summary>
    ///     A pass id is global and the registry gives it to one extension. The extension it refused never ran a
    ///     pass under that id, so the stamps carrying it are another's, and its delete leaves them.
    /// </summary>
    [Test]
    public async Task TheHostsDelete_LeavesTheStampsOfAPassIdAnotherExtensionHolds()
    {
        string id = "dev.example.refused." + Guid.NewGuid().ToString("N")[..8];
        Pack pack = new(id);
        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        services.AddSingleton(new DemoCacheStore(null));
        await using ServiceProvider sp = services.BuildServiceProvider();
        DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
        cache.Update("/demos/a.dem", 1, 1, r =>
        {
            r.Sha256 = "aaaa";
            r.SetStamp(new PackStamp("lineups", 1, "fp"));
        });
        PackContributions contributions = new(pack, () => new ExtensionContext(pack, sp));
        contributions.Pass("lineups", () => new ExtensionPassHostTests.FakePass { PassName = "lineups" });
        HostDataRemoval removal = new(contributions, sp);

        PassRegistry registry = new();
        registry.AddCore("lineups", () => null!);
        contributions.PassIdsOfOthers = [.. contributions.Passes.Select(p => p.Id).Where(registry.Has)];
        ExtensionDataRemovalResult refused = await removal.DeleteAsync();
        string[] afterRefused = [.. cache.Contents.Single().PackStamps.Select(s => s.Id)];

        contributions.PassIdsOfOthers = [];
        ExtensionDataRemovalResult accepted = await removal.DeleteAsync();
        string[] afterAccepted = [.. cache.Contents.Single().PackStamps.Select(s => s.Id)];

        using (Assert.Multiple())
        {
            await Assert.That(refused.Ran).IsTrue();
            await Assert.That(afterRefused).IsEquivalentTo(["lineups"], CollectionOrdering.Matching)
                .Because("the stamp is the extension's whose pass the registry took");
            await Assert.That(accepted.Ran).IsTrue();
            await Assert.That(afterAccepted).IsEmpty().Because("an extension's own passes' stamps still go");
        }
    }

    /// <summary>
    ///     An extension whose Register failed or whose Contribute threw this session contributed nothing, so
    ///     its passes and rulesets, and with them the stamps and facts its delete strips, are not known. The
    ///     delete is refused with the reason, not run in part and reported as done.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AnExtensionThatDidNotStartThisSession_RefusesTheDelete_AndTheRowSaysWhy(bool registerFailed)
    {
        string id = "dev.example.unstarted." + Guid.NewGuid().ToString("N")[..8];
        Pack pack = new(id, throwInContribute: !registerFailed);
        ExtensionFaults faults = ExtensionFaults.For([pack]);
        if (registerFailed)
        {
            faults.FailStartup(faults.Scopes.Single(), "register", new InvalidOperationException("no"));
        }

        ServiceCollection services = new();
        services.AddSingleton<ExtensionShellHub>();
        services.AddSingleton(new DemoCacheStore(null));
        services.AddSingleton(faults);
        ExtensionContextAccess.Register(services, pack);
        await using ServiceProvider sp = services.BuildServiceProvider();
        DemoCacheStore cache = sp.GetRequiredService<DemoCacheStore>();
        cache.Update("/demos/a.dem", 1, 1, r =>
        {
            r.Sha256 = "aaaa";
            r.SetStamp(new PackStamp("lineups", 1, "fp"));
        });
        ExtensionContextAccess.ForHost(sp, id).Stores.Own(StoreRoot.Config)!.WriteAtomic("notes.json", "{}"u8);
        string folder = Path.Combine(AppPaths.ConfigRoot!, ExtensionFolders.DataDirectoryName, id);

        try
        {
            IExtensionDataRemoval removal = new PackContributionSet([pack], sp).DataRemovals.Single();
            ExtensionDataActionViewModel row = new(removal, "Unstarted");

            await row.ArmCommand.ExecuteAsync(null);
            string? armed = row.StatusText;
            bool confirming = row.IsConfirming;
            await row.ConfirmCommand.ExecuteAsync(null);
            string? confirmed = row.StatusText;
            ExtensionDataRemovalResult result = await removal.DeleteAsync();

            using (Assert.Multiple())
            {
                await Assert.That(result.Ran).IsFalse();
                await Assert.That(confirming).IsFalse().Because("there is nothing to confirm");
                await Assert.That(armed).Contains("did not start this session");
                await Assert.That(confirmed).Contains("did not start this session");
                await Assert.That(Directory.Exists(folder)).IsTrue().Because("nothing was deleted");
                await Assert.That(cache.Contents.Single().PackStamps.Select(s => s.Id))
                    .IsEquivalentTo(["lineups"], CollectionOrdering.Matching);
            }
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    private sealed class Pack(string id, IEnumerable<StoreDescriptor>? stores = null, bool throwInContribute = false)
        : IExtension, IManifestSource
    {
        public ExtensionManifest Manifest { get; } = ExtensionManifest.Parse($$"""
                                                                               {
                                                                                 "id": "{{id}}", "name": "Remove", "version": "1.0.0", "assembly": "Remove.dll", "entryType": "Remove.Pack",
                                                                                 "requiresHost": "^1.0", "requiresCs2DemoKit": "0.14.0", "rulesets": ["kills"]
                                                                               }
                                                                               """);

        public string Id => id;
        public string FeatureId => "pack." + id;
        public IEnumerable<ExtensionFeature> Features => [];
        public IEnumerable<StoreDescriptor> Stores => stores ?? [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
            if (throwInContribute)
            {
                contributions.Pass("lineups", () => new ExtensionPassHostTests.FakePass { PassName = "lineups" });
                throw new InvalidOperationException("contribute failed");
            }
        }
    }
}
