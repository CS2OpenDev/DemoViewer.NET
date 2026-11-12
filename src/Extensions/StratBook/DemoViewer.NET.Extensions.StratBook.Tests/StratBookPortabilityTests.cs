#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     What Library Transfer carries of the Strat Book. A file holding a local path or a demo's StableKey (a
///     hash of its path) means nothing on another machine, so such a store must stay behind, unless the
///     extension merges it and drops those keys on the way in. A store added without a row here fails, so a new
///     store is classified on purpose rather than by default.
/// </summary>
public class StratBookPortabilityTests
{
    // Every declared store, with what a transfer does with it. Old layouts and derived stores stay behind.
    private static readonly Dictionary<string, (StorePortability Portability, bool ExtensionMerges)> _expected =
        new(StringComparer.Ordinal)
        {
            [StratBookStores.Strats] = (StorePortability.LibraryWide, true),
            [StratBookStores.Tags] = (StorePortability.PerDemo, true),
            [StratBookStores.Palettes] = (StorePortability.LibraryWide, true),
            [StratBookStores.SuggestedTags] = (StorePortability.LibraryWide, false),
            [StratBookStores.LineupClips] = (StorePortability.LibraryWide, true),
            [StratBookStores.Teams] = (StorePortability.LibraryWide, true),
            [StratBookStores.WatchedSituations] = (StorePortability.LibraryWide, true),
            [StratBookStores.VetoHistory] = (StorePortability.LibraryWide, true),
            [StratBookStores.ScoutingReportNotes] = (StorePortability.LibraryWide, true),
            [StratBookStores.TeamRanks] = (StorePortability.LibraryWide, true),
            [StratBookStores.ScoutingReportNotesOldName] = (StorePortability.None, false),
            [StratBookStores.StratMiningState] = (StorePortability.None, false),
            ["strat-mining-cache"] = (StorePortability.None, false),
            ["team-index"] = (StorePortability.None, false),
            ["round-index"] = (StorePortability.None, false),
            ["suggestions"] = (StorePortability.None, false),
            [StratBookStores.GrenadeLineups] = (StorePortability.None, false),
            ["grenades-v3-attempts"] = (StorePortability.None, false),
            ["grenade-sidecars"] = (StorePortability.None, false)
        };

    // The stores whose files hold a local path or a StableKey: the mining detections and team index key on
    // demo paths, the old round index, proposals and grenade sidecars carry StableKey fields, the old lineup
    // file seeds anchors from them, and teams.json keeps an override by StableKey while a demo is unhashed.
    private static readonly string[] _pathOrStableKey =
    [
        "strat-mining-cache", "team-index", "round-index", "suggestions", StratBookStores.GrenadeLineups,
        "grenades-v3-attempts", "grenade-sidecars", StratBookStores.Teams
    ];

    // The one store above that travels: B1's team import drops every override keyed by a StableKey.
    private static readonly string[] _mergedDroppingLocalKeys = [StratBookStores.Teams];

    [Test]
    public async Task EveryStore_IsClassified_AsTheInventorySays()
    {
        string[] actual = [.. StratBookStores.All.Select(s => $"{s.Id}: {s.Portability}, merges {s.ExtensionMerges}")];
        string[] expected = [.. _expected.Select(e => $"{e.Key}: {e.Value.Portability}, merges {e.Value.ExtensionMerges}")];

        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Any);
    }

    [Test]
    public async Task TheRebuiltIndexes_TheTrash_AndTheLocalClipCap_StayBehind()
    {
        string[] Excludes(string id) => [.. StratBookStores.All.Single(s => s.Id == id).PortableExcludes];

        using (Assert.Multiple())
        {
            await Assert.That(Excludes(StratBookStores.Strats)).IsEquivalentTo(["index.json", ".trash"],
                CollectionOrdering.Any);
            await Assert.That(Excludes(StratBookStores.Tags)).IsEquivalentTo(["index.json"]);
            await Assert.That(Excludes(StratBookStores.LineupClips)).IsEquivalentTo(["evicted.txt"]);
        }
    }

    [Test]
    public async Task EveryStoreHoldingAPathOrStableKey_StaysBehind_UnlessTheExtensionMergesItsKeysAway()
    {
        using (Assert.Multiple())
        {
            foreach (StoreDescriptor store in StratBookStores.All.Where(s => _pathOrStableKey.Contains(s.Id)))
            {
                if (_mergedDroppingLocalKeys.Contains(store.Id))
                {
                    await Assert.That(store.ExtensionMerges).IsTrue()
                        .Because($"'{store.Id}' travels only because the extension's merge drops its local keys");
                    continue;
                }

                await Assert.That(store.Portability).IsEqualTo(StorePortability.None)
                    .Because($"'{store.Id}' holds a local path or StableKey");
            }

            // A byte copy merged file by file would carry whatever it holds, so only key-free stores get one.
            foreach (StoreDescriptor store in StratBookStores.All.Where(s =>
                         s.Portability != StorePortability.None && !s.ExtensionMerges))
            {
                await Assert.That(_pathOrStableKey).DoesNotContain(store.Id);
            }
        }
    }

    [Test]
    public async Task EveryStore_PassesTheHostsChecks()
    {
        using (Assert.Multiple())
        {
            foreach (StoreDescriptor store in StratBookStores.All)
            {
                await Assert.That(PackContributions.StoreRefusal(StratBookPack.PackId, store)).IsNull()
                    .Because($"'{store.Id}' would be dropped from the extension's stores");
            }

            foreach (PortableOwnPath own in StratBookStores.PortableOwnPaths)
            {
                await Assert.That(PackContributions.OwnPathRefusal(own)).IsNull();
            }
        }
    }

    [Test]
    public async Task TheTagTemplates_NameTheFilesTheTagStoreWrites()
    {
        string configRoot = Path.Combine(Path.GetTempPath(), $"dv-portability-{Guid.NewGuid():N}");
        string sha = new('c', 64);
        TagStore tags = new(PackStores.Folder(configRoot, StratBookStores.Tags));
        StoreDescriptor store = StratBookStores.All.Single(s => s.Id == StratBookStores.Tags);

        string[] expanded =
        [
            .. store.DemoKeyedPaths.Select(t =>
                Path.GetFullPath(Path.Combine(configRoot, t.Replace("{sha256}", sha, StringComparison.Ordinal))))
        ];

        await Assert.That(expanded).IsEquivalentTo(
            [Path.GetFullPath(tags.PathFor(sha)!), Path.GetFullPath(tags.VerdictsPathFor(sha)!)],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheOwnPathsThatTravel_AreTheUserFiles_NotTheDerivedOnes()
    {
        string[] own = [.. StratBookStores.PortableOwnPaths.Select(p => p.Root + ":" + p.Path)];

        await Assert.That(own).IsEquivalentTo(
            ["Config:strat-mining.json", "Cache:grenade-lineups.json.gz"], CollectionOrdering.Matching);
    }
}
