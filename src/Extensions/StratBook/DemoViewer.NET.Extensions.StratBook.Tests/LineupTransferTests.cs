#region

using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The lineup file and the clip folder ready to travel between machines: anchors seed only from hashed
///     demos, an older file's path keys move onto the hash (ids never change, and a second load changes
///     nothing), an import aliases a lineup at a local spot and appends the rest, clips follow the alias, and
///     the sweep keeps a clip whose lineup is stored even before its demos arrive.
/// </summary>
public class LineupTransferTests
{
    private const string Mirage = "de_mirage";
    private const string Technique = "stand-throw-left";

    private static readonly Func<Action, Task> _inline = work =>
    {
        work();
        return Task.CompletedTask;
    };

    private static readonly WorldPoint _spot = new(512, 288, -160);
    private static readonly WorldPoint _landing = new(-1400, -1400, -170);

    // A real-shaped content hash, so the stored keys look like the ones the app writes.
    private static string Sha(int n) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"demo {n}")));

    private static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));

    private static string DemoPath(int n) => $"/d/mirage-{n}.dem";

    private static GrenadeRow Row(string id, WorldPoint origin, WorldPoint landing, GrenadeKind kind = GrenadeKind.Smoke) =>
        new()
        {
            Id = id,
            Kind = kind,
            ThrowerTeam = 3,
            ReleaseTick = 1000,
            ReleasePosition = origin,
            DetonationPosition = landing,
            EndKind = GrenadeEndKind.Detonated
        };

    private static void Indexed(DemoCacheStore cache, string path, string? sha, params GrenadeRow[] rows)
    {
        cache.Upsert(RoundIndexTestData.ParsedRecord(path, Mirage, sha));
        cache.WriteGrenades(path, new GrenadeDocument
        {
            Demo = new GrenadeDemoHeader { Sha256 = sha, StableKey = DemoCacheStore.StableKey(path) },
            Grenades = [.. rows]
        });
    }

    private static GrenadeIndex Index(DemoCacheStore cache, GrenadeLineupStore store) =>
        new(cache.Library(), lineups: store, scheduleSave: _inline);

    private static LineupPath Flight(string throwKey, float x) => new(throwKey, [new TrajectoryPoint(1, x, 0, 0, 0)]);

    private static string TempDir(string name) => Directory.CreateTempSubdirectory($"dv-{name}-").FullName;

    // ── M2: seeds from hashed demos only, and the migration of an older file ──────────────────────

    [Test]
    public async Task ARepeatedThrow_FromUnhashedDemosOnly_MintsNothing_AndAHashedMemberSeedsTheAnchor()
    {
        DemoCacheStore unhashed = new(null);
        Indexed(unhashed, DemoPath(1), null, Row("a", _spot, _landing));
        Indexed(unhashed, DemoPath(2), null, Row("a", new WorldPoint(515, 290, -160), _landing));
        GrenadeLineupStore none = new(null);
        using (GrenadeIndex index = Index(unhashed, none))
        {
            index.Load();
            await Assert.That(index.Query(new GrenadeQuery(Mirage)).Single().Lineups.Single().Throws.Count)
                .IsEqualTo(2).Because("the pair still shows as one lineup, under its single id");
        }

        DemoCacheStore mixed = new(null);
        Indexed(mixed, DemoPath(1), null, Row("a", _spot, _landing));
        Indexed(mixed, DemoPath(2), Sha(2), Row("a", new WorldPoint(515, 290, -160), _landing));
        GrenadeLineupStore some = new(null);
        using (GrenadeIndex index = Index(mixed, some))
        {
            index.Load();
        }

        using (Assert.Multiple())
        {
            await Assert.That(none.For(Mirage).Anchors).IsEmpty()
                .Because("a seed from a path key would mint a different id on every machine");
            await Assert.That(some.For(Mirage).Anchors.Single().Seed).IsEqualTo(Sha(2) + "/a");
        }
    }

    [Test]
    public async Task AnOlderFilesPathKeys_MoveOntoTheHash_IdsStay_AndASecondLoadChangesNothing()
    {
        string root = TempDir("lineup-migrate");
        try
        {
            DemoCacheStore cache = new(null);
            Indexed(cache, DemoPath(1), Sha(1), Row("a", _spot, _landing));
            Indexed(cache, DemoPath(2), Sha(2), Row("a", new WorldPoint(515, 290, -160), _landing));
            string pathKey = DemoCacheStore.StableKey(DemoPath(1));
            const string Offline = "0123456789abcdef01234567";

            Guid onSpot = Id("on-spot"), offline = Id("offline"), hashed = Id("hashed"), grid = Id("grid");
            GrenadeLineupStore old = new(root);
            MapLineups map = old.For(Mirage);
            map.Anchors.Add(new LineupAnchor(onSpot, GrenadeKind.Smoke, _spot, _landing, pathKey + "/a"));
            map.Anchors.Add(new LineupAnchor(offline, GrenadeKind.Smoke, new WorldPoint(3000, 3000, 0), _landing,
                Offline + "/z"));
            map.Anchors.Add(new LineupAnchor(hashed, GrenadeKind.Flash, new WorldPoint(-500, -500, 0), _landing,
                Sha(1) + "/f"));
            map.Aliases[grid] = onSpot;
            map.Paths[$"{onSpot:N}/{Technique}"] = Flight(pathKey + "/a", 1);
            map.Paths[$"{offline:N}/{Technique}"] = Flight(Offline + "/z", 2);
            old.Save();

            GrenadeLineupStore store = new(root);
            using (GrenadeIndex index = Index(cache, store))
            {
                index.Load();
                index.FlushLineups();
            }

            MapLineups migrated = store.For(Mirage);
            byte[] first = await File.ReadAllBytesAsync(Path.Combine(root, GrenadeLineupStore.FileName));
            using (Assert.Multiple())
            {
                await Assert.That(migrated.Anchors.Select(a => a.Id))
                    .IsEquivalentTo([onSpot, offline, hashed], CollectionOrdering.Matching);
                await Assert.That(migrated.Anchors[0].Seed).IsEqualTo(Sha(1) + "/a");
                await Assert.That(migrated.Anchors[1].Seed).IsEqualTo("")
                    .Because("its demo is not here to say which hash it had");
                await Assert.That(migrated.Anchors[2].Seed).IsEqualTo(Sha(1) + "/f");
                await Assert.That(migrated.Aliases[grid]).IsEqualTo(onSpot);
                await Assert.That(migrated.Paths[$"{onSpot:N}/{Technique}"].Throw).IsEqualTo(Sha(1) + "/a");
                await Assert.That(migrated.Paths[$"{onSpot:N}/{Technique}"].Points.Single().X).IsEqualTo(1f);
                await Assert.That(migrated.Paths.ContainsKey($"{offline:N}/{Technique}")).IsFalse();
                await Assert.That(migrated.Anchors.Count).IsEqualTo(3).Because("the throws joined the stored anchor");
            }

            GrenadeLineupStore again = new(root);
            using (GrenadeIndex index = Index(cache, again))
            {
                index.Load();
                index.FlushLineups();
            }

            await Assert.That(await File.ReadAllBytesAsync(Path.Combine(root, GrenadeLineupStore.FileName)))
                .IsEquivalentTo(first, CollectionOrdering.Matching)
                .Because("the migration is idempotent");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── B2: lineups ───────────────────────────────────────────────────────────────────────────────

    private static readonly Guid _local = Id("local"), _localAlias = Id("local-alias");
    private static readonly Guid _near = Id("near"), _nearAlias = Id("near-alias");
    private static readonly Guid _flashThere = Id("flash-there"), _far = Id("far");

    // This machine: one smoke anchor with an old alias and a flight.
    private static GrenadeLineupStore LocalStore()
    {
        GrenadeLineupStore store = new(null);
        MapLineups map = store.For(Mirage);
        map.Anchors.Add(new LineupAnchor(_local, GrenadeKind.Smoke, _spot, _landing, Sha(1) + "/a"));
        map.Aliases[_localAlias] = _local;
        map.Paths[$"{_local:N}/{Technique}"] = Flight(Sha(1) + "/a", 1);
        return store;
    }

    // The other machine: the same anchor, an anchor under this machine's alias, a smoke 18 units off this
    // machine's (the same spot), a flash from the same spot (another lineup), and one far away.
    private static GrenadeLineupDocument Imported()
    {
        MapLineups map = new();
        map.Anchors.Add(new LineupAnchor(_local, GrenadeKind.Smoke, _spot, _landing, Sha(1) + "/a"));
        map.Anchors.Add(new LineupAnchor(_localAlias, GrenadeKind.Smoke, _spot, _landing, Sha(1) + "/a"));
        map.Anchors.Add(new LineupAnchor(_near, GrenadeKind.Smoke, new WorldPoint(530, 288, -160),
            new WorldPoint(-1300, -1400, -170), Sha(7) + "/n"));
        map.Anchors.Add(new LineupAnchor(_flashThere, GrenadeKind.Flash, _spot, _landing, Sha(7) + "/f"));
        map.Anchors.Add(new LineupAnchor(_far, GrenadeKind.Smoke, new WorldPoint(2000, 2000, 0), _landing,
            DemoKeys.StableKey("/elsewhere.dem") + "/x"));
        map.Aliases[_nearAlias] = _near;
        map.Paths[$"{_near:N}/{Technique}"] = Flight(Sha(7) + "/n", 7);
        map.Paths[$"{_near:N}/run-throw-left"] = Flight(Sha(7) + "/r", 8);
        map.Paths[$"{_far:N}/{Technique}"] = Flight(DemoKeys.StableKey("/elsewhere.dem") + "/x", 9);
        GrenadeLineupDocument document = new();
        document.Maps[Mirage] = map;
        return document;
    }

    [Test]
    public async Task AnImport_AliasesTheSameSpot_AppendsTheRest_KeepsEveryLocalId_AndRunsTwiceAsOnce()
    {
        string staged = TempDir("lineup-staged");
        try
        {
            GrenadeLineupStore bundle = new(staged);
            foreach ((string name, MapLineups lineups) in Imported().Maps)
            {
                MapLineups into = bundle.For(name);
                into.Anchors.AddRange(lineups.Anchors);
                foreach ((Guid old, Guid now) in lineups.Aliases)
                {
                    into.Aliases[old] = now;
                }

                into.Paths = new Dictionary<string, LineupPath>(lineups.Paths, StringComparer.Ordinal);
            }

            bundle.Save();
            GrenadeLineupStore store = LocalStore();
            using GrenadeIndex index = Index(new DemoCacheStore(null), store);
            index.Load();
            int changes = 0;
            index.Changed += () => changes++;

            LineupImport result = index.ImportLineups(PackStores.At(staged), ImportConflictPolicy.KeepMine);
            MapLineups map = store.For(Mirage);
            using (Assert.Multiple())
            {
                await Assert.That(result.Skipped).IsEqualTo(2);
                await Assert.That(result.Aliased).IsEqualTo(1);
                await Assert.That(result.Added).IsEqualTo(2);
                await Assert.That(result.LineupMap[_local]).IsEqualTo(_local);
                await Assert.That(result.LineupMap[_localAlias]).IsEqualTo(_local);
                await Assert.That(result.LineupMap[_near]).IsEqualTo(_local);
                await Assert.That(result.LineupMap[_nearAlias]).IsEqualTo(_local)
                    .Because("the imported anchor's own aliases follow it");
                await Assert.That(result.LineupMap[_flashThere]).IsEqualTo(_flashThere)
                    .Because("another kind from the same spot is another lineup");
                await Assert.That(result.LineupMap[_far]).IsEqualTo(_far);
                await Assert.That(map.Anchors.Select(a => a.Id))
                    .IsEquivalentTo([_local, _flashThere, _far], CollectionOrdering.Matching);
                await Assert.That(map.Anchors[0].Seed).IsEqualTo(Sha(1) + "/a");
                await Assert.That(map.Anchors.Single(a => a.Id == _far).Seed).IsEqualTo("")
                    .Because("a path key from the other machine names nothing here");
                await Assert.That(map.Aliases[_near]).IsEqualTo(_local);
                await Assert.That(map.Aliases[_nearAlias]).IsEqualTo(_local);
                await Assert.That(map.Aliases[_localAlias]).IsEqualTo(_local);
                await Assert.That(map.Paths[$"{_local:N}/{Technique}"].Throw).IsEqualTo(Sha(1) + "/a")
                    .Because("keep mine keeps this machine's flight");
                await Assert.That(map.Paths[$"{_local:N}/run-throw-left"].Throw).IsEqualTo(Sha(7) + "/r")
                    .Because("a flight this machine lacks comes across under the local id");
                await Assert.That(map.Paths.ContainsKey($"{_far:N}/{Technique}")).IsFalse();
                await Assert.That(changes).IsEqualTo(1);
            }

            string before = Describe(map);
            LineupImport second = index.ImportLineups(PackStores.At(staged), ImportConflictPolicy.KeepMine);
            using (Assert.Multiple())
            {
                await Assert.That(second.Added + second.Aliased + second.Paths).IsEqualTo(0);
                await Assert.That(second.Skipped).IsEqualTo(5);
                await Assert.That(second.LineupMap[_near]).IsEqualTo(_local);
                await Assert.That(Describe(map)).IsEqualTo(before);
                await Assert.That(changes).IsEqualTo(1).Because("nothing changed, so nothing is saved or raised");
            }

            LineupImport taken = index.ImportLineups(PackStores.At(staged), ImportConflictPolicy.TakeImported);
            using (Assert.Multiple())
            {
                await Assert.That(taken.Paths).IsEqualTo(1);
                await Assert.That(map.Paths[$"{_local:N}/{Technique}"].Throw).IsEqualTo(Sha(7) + "/n");
                await Assert.That(map.Anchors.Select(a => a.Id))
                    .IsEquivalentTo([_local, _flashThere, _far], CollectionOrdering.Matching)
                    .Because("no policy replaces an anchor");
                await Assert.That(index.ImportLineups(PackStores.At(staged), ImportConflictPolicy.TakeImported).Paths)
                    .IsEqualTo(0);
            }
        }
        finally
        {
            Directory.Delete(staged, true);
        }
    }

    [Test]
    public async Task AnImportedAlias_ResolvesTheLocalLineup_ForAStratStep()
    {
        DemoCacheStore cache = new(null);
        Indexed(cache, DemoPath(1), Sha(1), Row("a", _spot, _landing));
        Indexed(cache, DemoPath(2), Sha(2), Row("a", new WorldPoint(515, 290, -160), _landing));
        GrenadeLineupStore store = new(null);
        using GrenadeIndex index = Index(cache, store);
        index.Load();
        Guid local = store.For(Mirage).Anchors.Single().Id;
        GrenadeLineupDocument imported = new();
        imported.Maps[Mirage] = new MapLineups
        {
            Anchors = [new LineupAnchor(_near, GrenadeKind.Smoke, new WorldPoint(520, 290, -160), _landing, "")]
        };

        index.ImportLineups(imported, ImportConflictPolicy.KeepMine);

        GrenadeLineup lineup = index.Query(new GrenadeQuery(Mirage)).Single().Lineups.Single();
        using (Assert.Multiple())
        {
            await Assert.That(lineup.Id).IsEqualTo(local);
            await Assert.That(lineup.Answers(_near)).IsTrue();
            await Assert.That(index.DescribeLineup(Mirage, _near)?.Id).IsEqualTo(local);
        }
    }

    private static string Describe(MapLineups map) =>
        string.Join('\n', map.Anchors.Select(a => $"{a.Id} {a.Seed}")
            .Concat(map.Aliases.OrderBy(a => a.Key).Select(a => $"{a.Key}>{a.Value}"))
            .Concat(map.Paths.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.Throw}")));

    // ── B2: clips ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AStem_RemapsOnlyItsLineupId()
    {
        string stem = LineupClipPlanner.FileStem("de_dust2-old", GrenadeKind.Smoke, _near, Technique);
        Dictionary<Guid, Guid> map = new() { [_near] = _local };
        using (Assert.Multiple())
        {
            await Assert.That(LineupClipPlanner.LineupIdOf(stem)).IsEqualTo(_near);
            await Assert.That(LineupClipPlanner.RemapStem(stem, map))
                .IsEqualTo(LineupClipPlanner.FileStem("de_dust2-old", GrenadeKind.Smoke, _local, Technique));
            await Assert.That(LineupClipPlanner.RemapStem(stem, new Dictionary<Guid, Guid>())).IsEqualTo(stem);
            await Assert.That(LineupClipPlanner.LineupIdOf("de_mirage-smoke-0123456789ab")).IsNull();
        }
    }

    [Test]
    public async Task ImportedClips_FollowTheLineupMap_GifFirst_NeverOverALocalPair_AndRunTwiceAsOnce()
    {
        string staged = TempDir("clips-staged");
        string local = TempDir("clips-local");
        try
        {
            string clipsIn = Path.Combine(staged, LineupClipService.DirectoryName);
            Directory.CreateDirectory(clipsIn);

            void Pair(string directory, string stem, string gif, bool sidecar = true)
            {
                File.WriteAllText(Path.Combine(directory, stem + LineupClipPlanner.GifExtension), gif);
                if (sidecar)
                {
                    File.WriteAllText(Path.Combine(directory, stem + LineupClipPlanner.SetposExtension),
                        "setpos " + gif);
                }
            }

            string Stem(Guid id, string technique = Technique) =>
                LineupClipPlanner.FileStem(Mirage, GrenadeKind.Smoke, id, technique);

            Pair(clipsIn, Stem(_near), "near");
            Pair(clipsIn, Stem(_far), "far");
            Pair(clipsIn, Stem(_near, "run-throw-left"), "theirs");
            Pair(local, Stem(_local, "run-throw-left"), "mine");
            Pair(clipsIn, Stem(_far, "stand-jump-left"), "half", sidecar: false);
            File.WriteAllText(Path.Combine(clipsIn, LineupClipService.EvictedFileName), Stem(_far));

            RecordingFolder folder = new(PackStores.At(local));
            using LineupClipService service = new(() => [], folder, () => true, new NoRenderer());
            Dictionary<Guid, Guid> lineupMap = new() { [_near] = _local, [_far] = _far };

            (int added, int skipped) = service.ImportPairs(PackStores.At(staged), LineupClipService.DirectoryName,
                lineupMap);

            string Read(string stem, string extension) => File.ReadAllText(Path.Combine(local, stem + extension));
            using (Assert.Multiple())
            {
                await Assert.That(added).IsEqualTo(2);
                await Assert.That(skipped).IsEqualTo(2);
                await Assert.That(Read(Stem(_local), LineupClipPlanner.GifExtension)).IsEqualTo("near");
                await Assert.That(Read(Stem(_local), LineupClipPlanner.SetposExtension)).IsEqualTo("setpos near");
                await Assert.That(Read(Stem(_far), LineupClipPlanner.GifExtension)).IsEqualTo("far");
                await Assert.That(Read(Stem(_local, "run-throw-left"), LineupClipPlanner.GifExtension))
                    .IsEqualTo("mine").Because("a pair this machine has is never written over");
                await Assert.That(File.Exists(Path.Combine(local, Stem(_near) + LineupClipPlanner.GifExtension)))
                    .IsFalse();
                await Assert.That(File.Exists(Path.Combine(local, Stem(_far, "stand-jump-left")
                    + LineupClipPlanner.GifExtension))).IsFalse().Because("half a pair is not a clip");
                await Assert.That(File.Exists(Path.Combine(local, LineupClipService.EvictedFileName))).IsFalse()
                    .Because("the cap is this machine's");
                await Assert.That(folder.Writes).IsEquivalentTo(
                [
                    Stem(_far) + LineupClipPlanner.GifExtension, Stem(_far) + LineupClipPlanner.SetposExtension,
                    Stem(_local) + LineupClipPlanner.GifExtension, Stem(_local) + LineupClipPlanner.SetposExtension
                ], CollectionOrdering.Any);
                foreach (string stem in (string[])[Stem(_far), Stem(_local)])
                {
                    await Assert.That(folder.Writes.IndexOf(stem + LineupClipPlanner.GifExtension))
                        .IsLessThan(folder.Writes.IndexOf(stem + LineupClipPlanner.SetposExtension))
                        .Because("the GIF lands first, so a crash leaves a half pair the planner renders again");
                }
            }

            (int addedAgain, _) = service.ImportPairs(PackStores.At(staged), LineupClipService.DirectoryName,
                lineupMap);
            await Assert.That(addedAgain).IsEqualTo(0);
            await Assert.That(folder.Writes.Count).IsEqualTo(4);
        }
        finally
        {
            Directory.Delete(staged, true);
            Directory.Delete(local, true);
        }
    }

    [Test]
    public async Task TheSweep_KeepsAPairWhoseLineupIsStored_EvenWithNoThrowNamingIt()
    {
        string clips = TempDir("clips-sweep");
        try
        {
            string stored = LineupClipPlanner.FileStem(Mirage, GrenadeKind.Smoke, _far, Technique);
            string orphan = LineupClipPlanner.FileStem(Mirage, GrenadeKind.Smoke, _near, Technique);
            foreach (string stem in (string[])[stored, orphan])
            {
                await File.WriteAllTextAsync(Path.Combine(clips, stem + LineupClipPlanner.GifExtension), "gif");
                await File.WriteAllTextAsync(Path.Combine(clips, stem + LineupClipPlanner.SetposExtension), "setpos");
            }

            using LineupClipService service = new(() => [], PackStores.At(clips), () => true, new NoRenderer(),
                orphanGrace: TimeSpan.Zero, storedLineups: () => new HashSet<Guid> { _far });
            service.Plan();
            await service.SweepTask;

            using (Assert.Multiple())
            {
                await Assert.That(File.Exists(Path.Combine(clips, stored + LineupClipPlanner.GifExtension))).IsTrue()
                    .Because("an imported clip waits for its demos");
                await Assert.That(File.Exists(Path.Combine(clips, stored + LineupClipPlanner.SetposExtension)))
                    .IsTrue();
                await Assert.That(File.Exists(Path.Combine(clips, orphan + LineupClipPlanner.GifExtension))).IsFalse()
                    .Because("the sweep itself ran");
            }
        }
        finally
        {
            Directory.Delete(clips, true);
        }
    }

    private sealed class NoRenderer : ILineupClipRenderer
    {
        public Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, ParsedDemo? demo,
            IReadOnlyList<LineupClipJob> jobs, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LineupClipJob>>([]);
    }

    // The host's folder with every atomic write recorded in order.
    private sealed class RecordingFolder(IExtensionFolder inner) : IExtensionFolder
    {
        public List<string> Writes { get; } = [];

        public string Path => inner.Path;

        public void Create() => inner.Create();

        public bool FileExists(string relativePath) => inner.FileExists(relativePath);

        public bool DirectoryExists(string relativePath) => inner.DirectoryExists(relativePath);

        public void SetLastWriteTimeUtc(string relativePath, DateTime value) =>
            inner.SetLastWriteTimeUtc(relativePath, value);

        public DateTime? LastWriteTimeUtc(string relativePath) => inner.LastWriteTimeUtc(relativePath);

        public IEnumerable<string> EnumerateFiles(string relativePath, string searchPattern) =>
            inner.EnumerateFiles(relativePath, searchPattern);

        public IEnumerable<string> EnumerateDirectories(string relativePath) =>
            inner.EnumerateDirectories(relativePath);

        public byte[]? Read(string relativePath) => inner.Read(relativePath);

        public Stream? OpenRead(string relativePath) => inner.OpenRead(relativePath);

        public void WriteAtomic(string relativePath, ReadOnlySpan<byte> content)
        {
            Writes.Add(relativePath);
            inner.WriteAtomic(relativePath, content);
        }

        public void WriteAtomic(string relativePath, Action<Stream> write)
        {
            Writes.Add(relativePath);
            inner.WriteAtomic(relativePath, write);
        }

        public void Append(string relativePath, string line) => inner.Append(relativePath, line);

        public void Move(string fromRelativePath, string toRelativePath, bool overwrite) =>
            inner.Move(fromRelativePath, toRelativePath, overwrite);

        public void Delete(string relativePath) => inner.Delete(relativePath);
    }
}
