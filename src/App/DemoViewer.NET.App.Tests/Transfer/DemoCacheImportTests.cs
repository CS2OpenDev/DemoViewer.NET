#region

using System.Text;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.MatchMeta;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     The cache side of an import: a row that lands as an imported orphan survives the sweep for the imported
///     grace, not the orphan one, and only until a path first lists it; and a merge into a row this machine has
///     keeps the current side per tier and per stamp, never takes a lower schema, and takes a sibling only with
///     its stamp.
/// </summary>
public class DemoCacheImportTests
{
    private const string Sha = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string OtherSha = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string EventFacts = ".out.facts.player_event.json.gz";
    private const string RoundFacts = ".out.facts.player_round.json.gz";

    private static readonly TimeSpan _orphanGrace = TimeSpan.FromDays(14);
    private static readonly TimeSpan _importedGrace = TimeSpan.FromDays(90);

    private static byte[] RecordBytes(DemoCacheRecord record) => SidecarJson.SerializeGzip(record, null);

    private static KeyValuePair<string, byte[]> Sibling(string suffix, string text) =>
        new(suffix, Encoding.UTF8.GetBytes(text));

    private static DemoContentFingerprint Fingerprint(long size) =>
        new(size, 256, new string('a', 64), new string('b', 64));

    [Test]
    public async Task AnImportedRow_OutlivesTheOrphanGrace_UntilTheImportedGraceEnds()
    {
        using TempFolders temp = new();
        string cache = temp.New();
        DateTime now = DateTime.UtcNow;
        DemoCacheStore store = new(cache);
        DemoCacheRecord record = BundleBuilder.ParsedRecord(Sha, 4096);
        record.SetStamp(new PackStamp(StampedFacts.StampId("player_event"), 1, "fp"));
        DemoImportResult result = store.ImportContent(Sha, Fingerprint(4096), RecordBytes(record),
            [Sibling(EventFacts, "facts")], nowUtc: now);

        // A plain orphan beside it: a demo whose folder went away at the same moment.
        store.Upsert(new DemoCacheRecord { Path = Path.Combine(cache, "x.dem"), Size = 9, Sha256 = OtherSha });
        store.Detach(Path.Combine(cache, "x.dem"));
        store.SaveIndex();
        string recordFile = Path.Combine(cache, "demos", Sha + DemoCacheStore.RecordSuffix);
        string siblingFile = Path.Combine(cache, "demos", Sha + EventFacts);

        using (Assert.Multiple())
        {
            await Assert.That(result.Outcome).IsEqualTo(DemoImportOutcome.Added);
            await Assert.That(store.ExpireOrphans(now, _orphanGrace, _importedGrace)).IsEqualTo(0);
            await Assert.That(File.Exists(recordFile) && File.Exists(siblingFile)).IsTrue()
                .Because("the sweep's unnamed-file pass leaves files a row names");
        }

        using (Assert.Multiple())
        {
            await Assert.That(store.ExpireOrphans(now + TimeSpan.FromDays(15), _orphanGrace, _importedGrace))
                .IsEqualTo(1).Because("only the plain orphan is past its grace");
            await Assert.That(store.HoldsContent(OtherSha)).IsFalse();
            await Assert.That(store.IsOrphaned(Sha)).IsTrue();
            await Assert.That(File.Exists(recordFile) && File.Exists(siblingFile)).IsTrue();
        }

        // The saved index keeps an imported row with no path, flag and all.
        store.SaveIndex();
        DemoCacheStore reloaded = new(cache);
        using (Assert.Multiple())
        {
            await Assert.That(reloaded.TryGetOrphan(Sha)?.ImportedUtcTicks).IsEqualTo(now.Ticks);
            await Assert.That(reloaded.TryGetOrphan(Sha)?.ParseSchema ?? 0).IsGreaterThan(0);
            await Assert.That(reloaded.TryGetOrphan(Sha)?.ContentFingerprint).IsEqualTo(Fingerprint(4096));
        }

        using (Assert.Multiple())
        {
            await Assert.That(reloaded.ExpireOrphans(now + TimeSpan.FromDays(91), _orphanGrace, _importedGrace))
                .IsEqualTo(1);
            await Assert.That(reloaded.HoldsContent(Sha)).IsFalse();
            await Assert.That(File.Exists(recordFile) || File.Exists(siblingFile)).IsFalse();
        }
    }

    [Test]
    public async Task AnImportedRow_ThatAPathListed_AgesLikeAnyOrphanOnceItLeaves()
    {
        DateTime now = DateTime.UtcNow;
        DemoCacheStore store = new(null);
        store.ImportContent(Sha, Fingerprint(4096), RecordBytes(BundleBuilder.ParsedRecord(Sha, 4096)), [],
            nowUtc: now);
        const string Path = "/library/match.dem";

        await Assert.That(store.AttachUnconfirmed(Sha, Path, 4096, 1)).IsTrue();
        await Assert.That(store.TryGetIndex(Path)?.ImportedUtcTicks).IsNull();
        store.Detach(Path);

        using (Assert.Multiple())
        {
            await Assert.That(store.TryGetOrphan(Sha)?.ImportedUtcTicks).IsNull();
            await Assert.That(store.ExpireOrphans(DateTime.UtcNow + TimeSpan.FromDays(15), _orphanGrace,
                _importedGrace)).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AnImportedRecord_LosesTheSourcePaths()
    {
        DemoCacheStore store = new(null);
        store.ImportContent(Sha, Fingerprint(4096), RecordBytes(BundleBuilder.ParsedRecord(Sha, 4096)), []);
        const string Path = "/library/match.dem";
        store.AttachUnconfirmed(Sha, Path, 4096, 1);

        DemoCacheRecord? record = store.TryLoadRecord(Path);
        using (Assert.Multiple())
        {
            await Assert.That(record?.Path).IsEqualTo(Path);
            await Assert.That(record?.Locations.Select(l => l.Path)).IsEquivalentTo([Path]);
            await Assert.That(store.TryGetOrphan(Sha)).IsNull();
        }
    }

    [Test]
    public async Task AMerge_NeverDowngrades_AndTakesASiblingOnlyWithItsStamp()
    {
        DemoCacheStore store = new(null);
        const string Path = "/library/match.dem";
        DemoCacheRecord local = BundleBuilder.ParsedRecord(Sha, 4096, "de_local");
        local.Path = Path;
        local.Locations = [];
        local.Header.Schema = 5;
        local.Analysis = new TierStamp { Schema = 3, ComputedAtTicks = 1 };
        local.AnalysisState = DemoAnalysisState.Indexed;
        local.ConfigFingerprint = "cfg-local";
        local.SetStamp(new PackStamp(StampedFacts.StampId("player_event"), 1, "fp-local"));
        store.Upsert(local);
        store.WriteSiblingBytes(Path, EventFacts, Encoding.UTF8.GetBytes("local"));

        DemoCacheRecord imported = BundleBuilder.ParsedRecord(Sha, 4096, "de_imported");
        imported.Header.Schema = 4;
        imported.Parse.Schema = local.Parse.Schema - 1;
        imported.Analysis = new TierStamp { Schema = 3, ComputedAtTicks = 2 };
        imported.AnalysisState = DemoAnalysisState.Failed;
        imported.SetStamp(new PackStamp(StampedFacts.StampId("player_event"), 1, "fp-imported")
        {
            State = DemoAnalysisState.Failed
        });
        imported.SetStamp(new PackStamp(StampedFacts.StampId("player_round"), 1, "fp-imported"));
        imported.SetStamp(new PackStamp(MatchMetaEvaluator.StampId, 99, "v"));
        imported.MatchMeta = new DemoMatchMeta { GameMode = "competitive" };

        DemoImportResult result = store.ImportContent(Sha, null, RecordBytes(imported),
            [Sibling(EventFacts, "imported"), Sibling(RoundFacts, "imported"), Sibling(".loose.json", "x")]);
        DemoCacheRecord merged = store.TryLoadRecord(Path)!;

        using (Assert.Multiple())
        {
            await Assert.That(result.Outcome).IsEqualTo(DemoImportOutcome.Merged);
            await Assert.That(result.TakenStamps).IsEquivalentTo(
                [StampedFacts.StampId("player_round"), MatchMetaEvaluator.StampId]);
            await Assert.That(merged.Map).IsEqualTo("de_local").Because("a lower parse schema never replaces");
            await Assert.That(merged.Header.Schema).IsEqualTo(5);
            await Assert.That(merged.AnalysisState).IsEqualTo(DemoAnalysisState.Indexed);
            await Assert.That(merged.ConfigFingerprint).IsEqualTo("cfg-local");
            await Assert.That(merged.Stamp(StampedFacts.StampId("player_event"))?.Fingerprint).IsEqualTo("fp-local");
            await Assert.That(store.TryReadSibling(Path, EventFacts)).IsEqualTo("local");
            await Assert.That(store.TryReadSibling(Path, RoundFacts)).IsEqualTo("imported");
            await Assert.That(store.TryReadSibling(Path, ".loose.json")).IsNull()
                .Because("no stamp vouches for it");
            await Assert.That(merged.MatchMeta?.GameMode).IsEqualTo("competitive");
            await Assert.That(store.TryGetIndex(Path)?.Path).IsEqualTo(Path).Because("the row keeps its path");
        }

        DemoImportResult again = store.ImportContent(Sha, null, RecordBytes(imported), []);
        await Assert.That(again.Outcome).IsEqualTo(DemoImportOutcome.Unchanged);
    }

    [Test]
    public async Task AMerge_TakesTheImportedStamp_WhenOnlyItIsCurrentHere()
    {
        DemoCacheStore store = new(null);
        const string Path = "/library/match.dem";
        DemoCacheRecord local = BundleBuilder.ParsedRecord(Sha, 4096);
        local.Path = Path;
        local.Locations = [];
        local.SetStamp(new PackStamp(StampedFacts.StampId("player_event"), 1, "old"));
        store.Upsert(local);
        store.WriteSiblingBytes(Path, EventFacts, Encoding.UTF8.GetBytes("old"));
        store.WriteSiblingBytes(Path, ".gone.facts.player_event.json.gz", Encoding.UTF8.GetBytes("old"));

        DemoCacheRecord imported = BundleBuilder.ParsedRecord(Sha, 4096);
        imported.SetStamp(new PackStamp(StampedFacts.StampId("player_event"), 1, "current"));
        DemoImportCurrency currency = new(StampCurrent: (stamp, _) => stamp.Fingerprint == "current");

        DemoImportResult result = store.ImportContent(Sha, null, RecordBytes(imported),
            [Sibling(EventFacts, "current")], currency);

        using (Assert.Multiple())
        {
            await Assert.That(result.TakenStamps).IsEquivalentTo([StampedFacts.StampId("player_event")]);
            await Assert.That(store.TryReadSibling(Path, EventFacts)).IsEqualTo("current");
            await Assert.That(store.TryReadSibling(Path, ".gone.facts.player_event.json.gz")).IsNull()
                .Because("the taken stamp's outputs replace that ruleset's whole set");
        }
    }

    [Test]
    public async Task AnImportedRow_IsNeverSweptBeforeAnOrdinaryOrphan()
    {
        DateTime now = DateTime.UtcNow;
        DemoCacheStore store = new(null);
        store.ImportContent(Sha, Fingerprint(4096), RecordBytes(BundleBuilder.ParsedRecord(Sha, 4096)), [],
            nowUtc: now);

        // The orphan grace raised past the imported one: the imported row gets the longer of the two.
        await Assert.That(store.ExpireOrphans(now + TimeSpan.FromDays(100), TimeSpan.FromDays(180), _importedGrace))
            .IsEqualTo(0);
        await Assert.That(store.ExpireOrphans(now + TimeSpan.FromDays(181), TimeSpan.FromDays(180), _importedGrace))
            .IsEqualTo(1);
    }

    [Test]
    public async Task AListedRowWhoseRecordDoesNotRead_KeepsItsPath_WhenTheImportReplacesIt()
    {
        using TempFolders temp = new();
        string cache = temp.New();
        string demo = Path.Combine(temp.New(), "match.dem");
        DemoCacheStore store = new(cache);
        store.Upsert(new DemoCacheRecord
        {
            Path = demo, Size = 4096, ModifiedTicks = 1234, Sha256 = Sha,
            Locations = [new DemoLocation(demo, true, 4096, 1234, 1)]
        });
        store.SaveIndex();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(cache, "demos")))
        {
            File.WriteAllBytes(file, [1, 2, 3]);
        }

        DemoCacheStore reloaded = new(cache);
        DemoImportResult result = reloaded.ImportContent(Sha, Fingerprint(4096),
            RecordBytes(BundleBuilder.ParsedRecord(Sha, 4096)), []);
        DemoCacheRecord? record = new DemoCacheStore(cache).TryLoadRecord(demo);

        using (Assert.Multiple())
        {
            await Assert.That(result.Outcome).IsEqualTo(DemoImportOutcome.Added);
            await Assert.That(record?.Path).IsEqualTo(demo)
                .Because("an index rebuilt from records must still list the demo");
            await Assert.That(record?.ModifiedTicks).IsEqualTo(1234);
            await Assert.That(record?.Locations.Select(l => l.Path) ?? []).IsEquivalentTo([demo]);
            await Assert.That(record?.Parse.Schema ?? 0).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task ARecordOfOtherContent_IsRefused()
    {
        DemoCacheStore store = new(null);
        await Assert.That(() => store.ImportContent(Sha, null, RecordBytes(BundleBuilder.ParsedRecord(OtherSha, 1)),
            [])).Throws<InvalidDataException>();
        await Assert.That(store.HoldsContent(Sha)).IsFalse();
    }

    [Test]
    [Arguments(".facts.player_event.json.gz", "facts:player_event")]
    [Arguments(".out.facts.player_event.json.gz", "facts:player_event")]
    [Arguments(".out.facts.json.gz", null)]
    [Arguments(".grenades.json", null)]
    public async Task ASibling_BelongsToTheFactsStampItsSuffixNames(string suffix, string? stamp) =>
        await Assert.That(DemoCacheStore.StampOfSibling(suffix)).IsEqualTo(stamp);
}
