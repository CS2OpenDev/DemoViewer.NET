#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The throw log, the grenade store and the lineup store: every field survives the log, the store keeps
///     the rows under the walker version and refuses another demo's, the lineups are copied once from where an
///     older build kept them, and a demo walked this session gives its lineup a flight.
/// </summary>
[NotInParallel]
public class GrenadeStoreTests
{
    private const string Map = "de_mirage";

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-grenade-store-{Guid.NewGuid():N}");

    private static GrenadeRow Full(string id) => new()
    {
        Id = id,
        Kind = GrenadeKind.Molotov,
        ThrowerSlot = 4,
        ThrowerSteamId64 = "76561198000000004",
        ThrowerName = "sid",
        ThrowerTeam = 2,
        ThrowerSource = ThrowerSource.WeaponFire,
        RoundNumber = 7,
        ReleaseTick = 12345,
        ReleaseSource = ReleaseSource.GrenadeThrown,
        ReleasePosition = new WorldPoint(1293.41f, -352.27f, -145.2f),
        ReleaseEyePitch = -42.44123f,
        ReleaseEyeYaw = -173.9712f,
        ReleaseOnGround = false,
        ReleaseCrouched = true,
        ThrowStrength = 0.5f,
        ThrowStrengthClass = ThrowStrengthClass.Half,
        Movement = MovementClass.Walking,
        SpeedAtRelease = 18.3f,
        SpawnTick = 12352,
        SpawnPosition = new WorldPoint(1, 2, 3),
        SpawnVelocity = new WorldPoint(-621.41f, -13.22f, 263.24f),
        ThrowerPositionAtSpawn = new WorldPoint(4, 5, 6),
        ThrowerOnGroundAtSpawn = true,
        JumpThrow = true,
        JumpThrowSource = JumpThrowSource.Inputs,
        JumpPressTick = 12340,
        BounceCount = 6,
        AirTimeTicks = 167,
        DetonationTick = 12520,
        DetonationPosition = new WorldPoint(-1217.7f, -621.8f, -166f),
        DetonationSource = DetonationSource.Entity,
        EndTick = 13000,
        EndKind = GrenadeEndKind.Detonated,
        InfernoEntityIndex = 321,
        LandingPlace = "SnipersNest"
    };

    [Test]
    public async Task TheLog_KeepsEveryField_NullsIncluded_AndIsSmallerThanTheJson()
    {
        GrenadeDocument document = new()
        {
            Demo = new GrenadeDemoHeader { Sha256 = "abc", StableKey = "k", FileName = "a.dem", SizeBytes = 9 },
            Grenades =
            [
                Full("g1-2"), new GrenadeRow { Id = "g3-4" },
                .. Enumerable.Range(0, 200).Select(i => Full($"g{i}-1"))
            ]
        };

        byte[] log = GrenadeThrowLog.Encode(document);
        GrenadeDocument back = GrenadeThrowLog.TryDecode(log)!;
        byte[] json = Gzip(System.Text.Encoding.UTF8.GetBytes(GrenadeSidecar.Serialize(document)));
        using (Assert.Multiple())
        {
            await Assert.That(GrenadeThrowLog.SameRows(document.Grenades, back.Grenades)).IsTrue();
            await Assert.That(back.Grenades[0].ReleaseEyePitch).IsEqualTo(-42.44123f)
                .Because("floats keep full precision");
            await Assert.That(back.Grenades[1].ReleasePosition).IsNull();
            await Assert.That(back.Grenades[1].ReleaseOnGround).IsNull();
            await Assert.That(back.Demo.Sha256).IsEqualTo("abc");
            await Assert.That(log.Length).IsLessThan(json.Length);
            await Assert.That(GrenadeThrowLog.TryDecode([1, 2, 3])).IsNull();
        }
    }

    /// <summary>A log with a real magic, version and header, then <paramref name="tail" /> where the tables go.</summary>
    private static byte[] LogWith(string headerJson, params byte[] tail)
    {
        using MemoryStream output = new();
        using (System.IO.Compression.GZipStream gzip = new(output, System.IO.Compression.CompressionLevel.Fastest,
                   true))
        using (BinaryWriter w = new(gzip, System.Text.Encoding.UTF8, true))
        {
            w.Write("DVGL"u8);
            w.Write((byte)1);
            w.Write(headerJson);
            w.Write(tail);
        }

        return output.ToArray();
    }

    [Test]
    public async Task ALogWithMadeUpCountsOrANullHeaderBlock_IsNotALog_AndDoesNotThrow()
    {
        string header = GrenadeSidecar.Serialize(new GrenadeDocument());
        using (Assert.Multiple())
        {
            await Assert.That(GrenadeThrowLog.TryDecode(LogWith(header, 0, 0))).IsNotNull()
                .Because("the helper writes a log the decoder reads when its tables are honest");
            await Assert.That(GrenadeThrowLog.TryDecode(LogWith(header, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F))).IsNull()
                .Because("a string count of -1 is not a log");
            await Assert.That(GrenadeThrowLog.TryDecode(LogWith(header, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F))).IsNull()
                .Because("an over-long 7-bit integer is not a log");
            await Assert.That(GrenadeThrowLog.TryDecode(LogWith(header, 0xFF, 0xFF, 0xFF, 0xFF, 0x07))).IsNull()
                .Because("a string count of two thousand million with no strings behind it is not a log");
            await Assert.That(GrenadeThrowLog.TryDecode(LogWith(header, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F))).IsNull()
                .Because("a row count of -1 is not a log");
            await Assert.That(GrenadeThrowLog.TryDecode(LogWith("{\"schemaVersion\":1,\"demo\":null}", 0, 0)))
                .IsNull().Because("the store reads the demo block's hash");
        }
    }

    // The bundle's outer cap bounds the stored bytes, which are themselves gzip: a few kilobytes of all-zero rows
    // inflate to as many rows as the stream holds, each a heap object several times its 42 encoded bytes.
    [Test]
    public async Task ALogOfMoreRowsThanAnyDemoThrows_IsNotALog()
    {
        const int Rows = 150_000;
        const int RowBytes = 42;
        string header = GrenadeSidecar.Serialize(new GrenadeDocument());
        byte[] tail = new byte[3 + 3 + Rows * RowBytes];
        tail[0] = 1; // one string,
        tail[1] = 1; // one character long,
        tail[2] = (byte)'x';
        int count = Rows; // then the row count as a 7-bit integer
        int at = 3;
        while (count >= 0x80)
        {
            tail[at++] = (byte)(count | 0x80);
            count >>= 7;
        }

        tail[at++] = (byte)count;
        Array.Resize(ref tail, at + Rows * RowBytes);

        await Assert.That(GrenadeThrowLog.TryDecode(LogWith(header, tail))).IsNull();
    }

    private static GrenadeRow Smoke(string id, float x, int slot, string steam) => new()
    {
        Id = id,
        Kind = GrenadeKind.Smoke,
        ThrowerSlot = slot,
        ThrowerSteamId64 = steam,
        ThrowerTeam = 2,
        ReleaseTick = 1000,
        ReleasePosition = new WorldPoint(x, 0, -160),
        DetonationPosition = new WorldPoint(-1200, -630, -166),
        EndKind = GrenadeEndKind.Detonated,
        Trajectory = [new TrajectoryPoint(10, x, 0, -100, 0), new TrajectoryPoint(20, -1200, -630, -166, 1)]
    };

    [Test]
    public async Task TheStore_RoundTripsTheRows_UnderTheWalkerVersion_AndRefusesAnotherDemosRows()
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(RoundIndexTestData.ParsedRecord("/d/a.dem", Map, "abc"));
        GrenadeStore store = cache.Grenades();
        GrenadeDocument document = new()
        {
            Demo = new GrenadeDemoHeader { Sha256 = "abc", StableKey = "k" },
            Grenades = [Full("g1-1"), Full("g2-1")]
        };

        store.Write("/d/a.dem", document);

        using (Assert.Multiple())
        {
            await Assert.That(store.IsCurrent("/d/a.dem")).IsTrue();
            await Assert.That(store.Stamp("/d/a.dem")!.Count).IsEqualTo(2);
            await Assert.That(store.TryReadRows("/d/a.dem", "abc")!.Grenades.Count).IsEqualTo(2);
            await Assert.That(store.TryReadRows("/d/a.dem", "def")).IsNull()
                .Because("rows naming another demo's hash are ignored");
        }

        store.MarkFailed("/d/a.dem");
        await Assert.That(store.Needs("/d/a.dem")).IsFalse().Because("retrying a failure is the user's call");
        store.ClearFailed("/d/a.dem");
        await Assert.That(store.Needs("/d/a.dem")).IsTrue();
    }

    [Test]
    public async Task TheLineups_AreCopiedOnce_FromWhereAnOlderBuildKeptThem()
    {
        string root = TempRoot();
        try
        {
            string legacy = Path.Combine(root, "cache", GrenadeLineupStore.FileName);
            GrenadeLineupStore old = new(Path.Combine(root, "cache"));
            LineupAnchor anchor = new(Guid.NewGuid(), GrenadeKind.Smoke, new WorldPoint(1, 2, 3),
                new WorldPoint(4, 5, 6), "seed");
            old.For(Map).Anchors.Add(anchor);
            old.Save();

            ExtensionStores stores = new("dev.example.lineups", Path.Combine(root, "config"),
                Path.Combine(root, "cache"), []);
            IExtensionFolder folder = stores.Own(StoreRoot.Cache)!;
            GrenadeLineupStore copied = GrenadeLineupStore.In(folder, () => File.ReadAllBytes(legacy));
            using (Assert.Multiple())
            {
                await Assert.That(copied.For(Map).Anchors.Single()).IsEqualTo(anchor);
                await Assert.That(folder.Read(GrenadeLineupStore.FileName)).IsNotNull()
                    .Because("the copy lands in the extension's own cache folder");
            }

            // The copy is the store now: a change there is what the next start reads, not the old file.
            copied.For(Map).Anchors.Clear();
            copied.Save();
            GrenadeLineupStore reopened = GrenadeLineupStore.In(folder, () => File.ReadAllBytes(legacy));
            await Assert.That(reopened.For(Map).Anchors).IsEmpty();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TheLineupsAnOlderBuildWroteInTheCacheFolder_WinOverTheOnesBesideIt()
    {
        string root = TempRoot();
        try
        {
            // The two layouts v0.9.0 left behind: the oldest copy at the cache root, which the pack still
            // offers as a declared store, and the one the extension's own cache folder holds now. Re-rooting
            // that folder must not leave the newer file stranded, since its anchor ids are what the user's
            // strat steps and rendered clips are named by.
            string oldest = Path.Combine(root, "cache");
            string own = Path.Combine(oldest, ExtensionFolders.DataDirectoryName, "dev.example.lineups");
            Directory.CreateDirectory(own);
            GrenadeLineupStore oldestStore = new(oldest);
            oldestStore.For(Map).Anchors.Add(new LineupAnchor(Guid.NewGuid(), GrenadeKind.Smoke,
                new WorldPoint(1, 2, 3),
                new WorldPoint(4, 5, 6), "oldest"));
            oldestStore.Save();
            LineupAnchor current = new(Guid.NewGuid(), GrenadeKind.Smoke, new WorldPoint(7, 8, 9),
                new WorldPoint(10, 11, 12), "current");
            GrenadeLineupStore currentStore = new(own);
            currentStore.For(Map).Anchors.Add(current);
            currentStore.Save();

            ExtensionStores stores = new("dev.example.lineups", Path.Combine(root, "config"),
                oldest, []);
            GrenadeLineupStore opened = GrenadeLineupStore.In(stores.Own(StoreRoot.Cache),
                () => File.ReadAllBytes(Path.Combine(oldest, GrenadeLineupStore.FileName)));

            await Assert.That(opened.For(Map).Anchors.Single()).IsEqualTo(current)
                .Because("the move runs before the first read, so the legacy copy is never consulted");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ALineupFileANewerBuildWrote_IsNeverOverwritten()
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, GrenadeLineupStore.FileName);
            byte[] newer = Gzip("""{"schemaVersion":2,"maps":{}}"""u8.ToArray());
            await File.WriteAllBytesAsync(file, newer);

            GrenadeLineupStore store = new(root);
            store.For(Map).Anchors.Add(new LineupAnchor(Guid.NewGuid(), GrenadeKind.Smoke, new WorldPoint(1, 2, 3),
                new WorldPoint(4, 5, 6), "seed"));
            store.Save();

            await Assert.That(await File.ReadAllBytesAsync(file)).IsEquivalentTo(newer)
                .Because("a downgrade must not replace the newer build's lineups with an empty document");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ALineupFileLockedOnRead_IsNotReplaced_AndTheNextLoadSeesIt()
    {
        string root = TempRoot();
        try
        {
            LineupAnchor anchor = new(Guid.NewGuid(), GrenadeKind.Smoke, new WorldPoint(1, 2, 3),
                new WorldPoint(4, 5, 6), "seed");
            GrenadeLineupStore first = new(root);
            first.For(Map).Anchors.Add(anchor);
            first.Save();

            // Another process holds the file for as long as this load keeps trying.
            LockedOnceFolder folder = new(PackStores.At(root)) { LockedReads = int.MaxValue };
            GrenadeLineupStore store = GrenadeLineupStore.In(folder, null);
            store.For(Map).Anchors.Add(new LineupAnchor(Guid.NewGuid(), GrenadeKind.Flash,
                new WorldPoint(7, 8, 9), new WorldPoint(10, 11, 12), "other"));
            store.Save();
            folder.LockedReads = 0;
            store.Unload();

            await Assert.That(store.For(Map).Anchors.Select(a => a.Id)).Contains(anchor.Id)
                .Because("a read that failed is not an empty file");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ALineupFileLockedForOneRead_IsReadOnTheNextTry_AndTheSessionsEditsAreSaved()
    {
        string root = TempRoot();
        try
        {
            LineupAnchor anchor = new(Guid.NewGuid(), GrenadeKind.Smoke, new WorldPoint(1, 2, 3),
                new WorldPoint(4, 5, 6), "seed");
            GrenadeLineupStore first = new(root);
            first.For(Map).Anchors.Add(anchor);
            first.Save();

            // An antivirus scan or a sync client holds the file for the first read only.
            LockedOnceFolder folder = new(PackStores.At(root));
            GrenadeLineupStore store = GrenadeLineupStore.In(folder, null);
            LineupAnchor added = new(Guid.NewGuid(), GrenadeKind.Flash, new WorldPoint(7, 8, 9),
                new WorldPoint(10, 11, 12), "other");
            store.For(Map).Anchors.Add(added);
            store.Save();

            await Assert.That(new GrenadeLineupStore(root).For(Map).Anchors.Select(a => a.Id))
                .IsEquivalentTo([anchor.Id, added.Id])
                .Because("a file held for a moment is read on the next try, not given up on for the session");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ALineupFileHeldPastTheFirstRead_IsReadOnceItIsFree_AndAnImportIsSavedBesideIt()
    {
        string root = TempRoot();
        try
        {
            LineupAnchor anchor = new(Guid.NewGuid(), GrenadeKind.Smoke, new WorldPoint(1, 2, 3),
                new WorldPoint(4, 5, 6), "seed");
            GrenadeLineupStore first = new(root);
            first.For(Map).Anchors.Add(anchor);
            first.Save();

            // Held for every try of the first load, free afterwards.
            LockedOnceFolder folder = new(PackStores.At(root)) { LockedReads = 3 };
            GrenadeLineupStore store = GrenadeLineupStore.In(folder, null);
            DemoCacheStore cache = new(null);
            using GrenadeIndex index = new(cache.Library(), lineups: store, scheduleSave: a =>
            {
                a();
                return Task.CompletedTask;
            });
            int before = store.For(Map).Anchors.Count;

            LineupAnchor theirs = new(Guid.NewGuid(), GrenadeKind.Flash, new WorldPoint(700, 800, 9),
                new WorldPoint(1000, 1100, 12), "other");
            GrenadeLineupDocument imported = new();
            imported.Maps[Map] = new MapLineups { Anchors = [theirs] };
            LineupImport result = index.ImportLineups(imported, ImportConflictPolicy.KeepMine);
            index.FlushLineups();

            using (Assert.Multiple())
            {
                await Assert.That(before).IsEqualTo(0).Because("the first load gave up on the held file");
                await Assert.That(folder.LockedReads).IsEqualTo(0);
                await Assert.That(result.Added).IsEqualTo(1);
                await Assert.That(new GrenadeLineupStore(root).For(Map).Anchors.Select(a => a.Id))
                    .IsEquivalentTo([anchor.Id, theirs.Id])
                    .Because("an import counted as added is on disk, beside the lineups that were there");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AnImportIntoALineupFileThatDoesNotParse_IsLeftOut_AndTheFileIsKept()
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, GrenadeLineupStore.FileName);
            byte[] theirs = "{\"schemaVersion\":99,\"maps\":{}}"u8.ToArray();
            await File.WriteAllBytesAsync(file, theirs);

            DemoCacheStore cache = new(null);
            using GrenadeIndex index = new(cache.Library(), lineups: new GrenadeLineupStore(root), scheduleSave: a =>
            {
                a();
                return Task.CompletedTask;
            });
            GrenadeLineupDocument imported = new();
            imported.Maps[Map] = new MapLineups
            {
                Anchors =
                [
                    new LineupAnchor(Guid.NewGuid(), GrenadeKind.Flash, new WorldPoint(7, 8, 9),
                        new WorldPoint(10, 11, 12), "other")
                ]
            };
            LineupImport result = index.ImportLineups(imported, ImportConflictPolicy.KeepMine);
            index.FlushLineups();

            using (Assert.Multiple())
            {
                await Assert.That(result.Added).IsEqualTo(0)
                    .Because("a lineup that will never be saved is not reported as added");
                await Assert.That(result.LineupMap).IsEmpty();
                await Assert.That(await File.ReadAllBytesAsync(file)).IsEquivalentTo(theirs);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    [Arguments("{\"schemaVersion\":1,\"maps\":{\"de_mirage\":{},\"DE_MIRAGE\":{}}}")]
    [Arguments("{\"schemaVersion\":1,\"maps\":{\"de_mirage\":{\"paths\":null}}}")]
    [Arguments("{\"schemaVersion\":1,\"maps\":{\"de_mirage\":{\"anchors\":null}}}")]
    [Arguments("{\"schemaVersion\":1,\"maps\":{\"de_mirage\":{\"aliases\":null}}}")]
    [Arguments("{\"schemaVersion\":1,\"maps\":null}")]
    [Arguments("{\"schemaVersion\":1,\"maps\":{\"de_mirage\":{\"paths\":{\"k\":null}}}}")]
    [Arguments("{\"schemaVersion\":1,\"maps\":{\"de_mirage\":{\"paths\":{\"k\":{\"throw\":null,\"points\":[]}}}}}")]
    [Arguments("{\"schemaVersion\":1,\"maps\":{\"de_mirage\":{\"paths\":{\"k\":{\"throw\":\"t\",\"points\":null}}}}}")]
    public async Task ALineupFileWithCollidingMapsOrANullTable_IsNotALineupDocument_AndIsNotReplaced(string json)
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, GrenadeLineupStore.FileName);
            byte[] theirs = System.Text.Encoding.UTF8.GetBytes(json);
            await File.WriteAllBytesAsync(file, theirs);

            GrenadeLineupDocument? staged = GrenadeLineupStore.ReadFrom(PackStores.At(root));
            GrenadeLineupStore store = new(root);
            store.For(Map).Anchors.Add(new LineupAnchor(Guid.NewGuid(), GrenadeKind.Flash, new WorldPoint(7, 8, 9),
                new WorldPoint(10, 11, 12), "other"));
            store.Save();

            using (Assert.Multiple())
            {
                await Assert.That(staged).IsNull().Because("a bundle's copy that does not read is left out");
                await Assert.That(await File.ReadAllBytesAsync(file)).IsEquivalentTo(theirs)
                    .Because("a file that does not read is refused, not written over");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ADemoWalkedThisSession_GivesItsLineupAFlight()
    {
        DemoCacheStore cache = new(null);
        int n = 0;
        GrenadeIndexEvaluator evaluator = new(cache.Library(), cache.Grenades(), () => true, () => null,
            walk: _ => new GrenadeWalk([Smoke("g1-1", 1300 + 5 * n++, 3, "76561198000000003")], 1,
                GrenadeWalker.InputDecoderName, 4));
        using GrenadeIndex index = new(cache.Library(), evaluator: evaluator);
        index.Load();
        foreach (string path in new[] { "/d/a.dem", "/d/b.dem" })
        {
            DemoCacheRecord record = RoundIndexTestData.ParsedRecord(path, Map, "sha" + path);
            record.Players =
                [new CachedPlayerInfo { Slot = 3, Name = "window guy", SteamId64 = "76561198000000003", Team = 2 }];
            cache.Upsert(record);
            evaluator.Evaluate(path, RoundIndexTestData.Demo(lastTick: 5000));
        }

        GrenadeLineup lineup = index.Query(new GrenadeQuery(Map))[0].Lineups[0];
        using (Assert.Multiple())
        {
            await Assert.That(lineup.Throws.Count).IsEqualTo(2);
            await Assert.That(index.PathFor(lineup, null)!.Count).IsEqualTo(2);
            await Assert.That(evaluator.TakeFlights("/d/a.dem")).IsNull().Because("the index took them");
        }
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using MemoryStream buffer = new();
        using (System.IO.Compression.GZipStream
               gzip = new(buffer, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            gzip.Write(bytes);
        }

        return buffer.ToArray();
    }

    // A folder whose first read fails the way a file held open by another process does.
    internal sealed class LockedOnceFolder(IExtensionFolder inner) : IExtensionFolder
    {
        // How many reads still fail.
        public int LockedReads { get; set; } = 1;

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

        public byte[]? Read(string relativePath)
        {
            if (LockedReads > 0)
            {
                LockedReads--;
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            return inner.Read(relativePath);
        }

        public Stream? OpenRead(string relativePath) => inner.OpenRead(relativePath);

        public void WriteAtomic(string relativePath, ReadOnlySpan<byte> content) =>
            inner.WriteAtomic(relativePath, content);

        public void WriteAtomic(string relativePath, Action<Stream> write) => inner.WriteAtomic(relativePath, write);

        public void Append(string relativePath, string line) => inner.Append(relativePath, line);

        public void Move(string fromRelativePath, string toRelativePath, bool overwrite) =>
            inner.Move(fromRelativePath, toRelativePath, overwrite);

        public void Delete(string relativePath) => inner.Delete(relativePath);
    }
}
