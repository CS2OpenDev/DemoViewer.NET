#region

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CS2DemoKit.Parser;
using DemoViewer.NET.AppTests.Extensions;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Transfer;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     Export: a populated config root and library go into a bundle folder that import can trust, and the
///     source is not touched. Demos not hashed yet carry no data, demos the queue is working on are flagged and
///     still exported, a FAT32 drive refuses a 4 GiB file, a cancelled run leaves no manifest, and an export into
///     the same bundle again rewrites only what changed.
/// </summary>
public class TransferExporterTests
{
    private const string Sibling = ".player_round.facts.player_round.json.gz";
    private const string ExtensionId = "net.demoviewer.pack.stratbook";
    private static readonly string[] _rootEntries = ["config", "library", "out"];

    [Test]
    public async Task AnExport_ChangesNothingAtTheSource_AndWritesOnlyTheBundle()
    {
        using Fixture fx = Fixture.Populated();
        // The store as the app has it at startup; loading it is not the export's doing.
        DemoCacheStore store = new(fx.CacheRoot);
        Dictionary<string, string> configBefore = Fingerprint(fx.ConfigRoot);
        Dictionary<string, string> libraryBefore = Fingerprint(fx.LibraryRoot);

        TransferExporter exporter = fx.Exporter(store);
        ExportPlan plan = await exporter.PlanAsync(ExportRequest.All(fx.OutRoot, true), CancellationToken.None);
        TransferSummary summary = await exporter.RunAsync(plan, null, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(summary.Cancelled).IsFalse();
            await Assert.That(summary.Failed).IsEmpty();
            await Assert.That(Fingerprint(fx.ConfigRoot)).IsEquivalentTo(configBefore)
                .Because("an export reads the config root and the cache, and writes nothing there");
            await Assert.That(Fingerprint(fx.LibraryRoot)).IsEquivalentTo(libraryBefore)
                .Because("an export writes nothing beside the demos");
            await Assert.That(Directory.EnumerateFileSystemEntries(fx.Root).Select(e => Path.GetFileName(e)!).Order())
                .IsEquivalentTo(_rootEntries)
                .Because("the bundle folder is the only new thing");
            await Assert.That(File.Exists(Path.Combine(plan.BundleFolder, TransferLayout.ManifestFileName)))
                .IsTrue();
            await Assert.That(Directory.EnumerateFiles(plan.BundleFolder, "*.tmp", SearchOption.AllDirectories))
                .IsEmpty();
        }
    }

    [Test]
    public async Task TheBundle_HoldsEachDemosDataUnderItsHash_AndTheManifestDescribesIt()
    {
        using Fixture fx = Fixture.Populated();
        DemoCacheStore store = new(fx.CacheRoot);
        TransferExporter exporter = fx.Exporter(store);
        ExportPlan plan = await exporter.PlanAsync(ExportRequest.All(fx.OutRoot, true), CancellationToken.None);
        TransferSummary summary = await exporter.RunAsync(plan, null, CancellationToken.None);
        string bundle = plan.BundleFolder;
        TransferManifest manifest = TransferManifest.Read(bundle);

        TransferDemo a = manifest.Demos.Single(d => d.Sha256 == fx.ShaA);
        TransferDemo b = manifest.Demos.Single(d => d.Sha256 == fx.ShaB);
        TransferDemo plain = manifest.Demos.Single(d => d.Sha256 == fx.ShaC);
        string[] aPaths = [.. a.Files.Select(f => f.Path)];
        using (Assert.Multiple())
        {
            await Assert.That(Path.GetFileName(bundle)).StartsWith(TransferLayout.BundleNamePrefix);
            await Assert.That(TransferManifest.CheckAppVersion(manifest)).IsNull();
            await Assert.That(manifest.Options).IsEqualTo(new TransferManifestOptions(true, true, true));
            await Assert.That(manifest.Demos.Count).IsEqualTo(3);
            await Assert.That(aPaths).Contains($"data/{fx.ShaA}/cache/demo.json.gz");
            await Assert.That(aPaths).Contains($"data/{fx.ShaA}/cache/demo{Sibling}");
            await Assert.That(aPaths).Contains($"data/{fx.ShaA}/ext/{ExtensionId}/grenades/demo.json.gz");
            await Assert.That(aPaths).Contains($"data/{fx.ShaA}/ext/{ExtensionId}/grenades/demo.positions.json.gz");
            await Assert.That(aPaths).Contains($"data/{fx.ShaA}/annotations.dvann.json")
                .Because("A's annotations sit beside the demo");
            await Assert.That(b.Files.Select(f => f.Path)).Contains($"data/{fx.ShaB}/annotations.dvann.json")
                .Because("B's annotations sit in the config root");
            await Assert.That(a.OriginalFileName).IsEqualTo("a.dem");
            await Assert.That(a.SizeBytes).IsEqualTo(new FileInfo(fx.DemA).Length);
            await Assert.That(a.Map).IsEqualTo("de_nuke");
            await Assert.That(a.Fingerprint).IsNotNull();
            await Assert.That(a.Dem).IsEqualTo(new TransferDemFile(TransferLayout.DemFile(fx.ShaA), a.SizeBytes));
            await Assert.That(plain.Files).IsEmpty().Because("a demo not hashed yet carries no data");
            await Assert.That(plain.Dem).IsNotNull().Because("its .dem still travels as a plain demo");
            await Assert.That(manifest.Extensions.Single()).IsEqualTo(new TransferExtensionVersion(ExtensionId, "9.9.9"));
            await Assert.That(manifest.RulesetIdentities).IsEquivalentTo(["facts:core:identity-core"])
                .Because("the manifest records this machine's rules in the shape import compares against");
            await Assert.That(summary.Transferred.Count).IsEqualTo(3);
        }

        // Every file the manifest lists is in the bundle with the size and hash it records.
        foreach (TransferFile file in manifest.Demos.SelectMany(d => d.Files).Concat(manifest.Shared))
        {
            byte[] bytes = await File.ReadAllBytesAsync(TransferLayout.FullPath(bundle, file.Path));
            await Assert.That(bytes.LongLength).IsEqualTo(file.Size);
            await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(file.Sha256);
        }

        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(
                await File.ReadAllBytesAsync(TransferLayout.FullPath(bundle, TransferLayout.DemFile(fx.ShaA))))))
            .IsEqualTo(fx.ShaA);

        // The shared files hold only the exported demos' entries, and a path-only review entry gets its hash.
        JsonObject breakpoints = JsonNode.Parse(await File.ReadAllTextAsync(TransferLayout.FullPath(bundle,
            TransferLayout.SharedFile(TransferLayout.GraphBreakpointsFileName))))!.AsObject();
        JsonArray review = JsonNode.Parse(await File.ReadAllTextAsync(TransferLayout.FullPath(bundle,
            TransferLayout.SharedFile(TransferLayout.ReviewQueueFileName))))!["entries"]!.AsArray();
        using (Assert.Multiple())
        {
            await Assert.That(breakpoints.Select(p => p.Key)).IsEquivalentTo(new[] { fx.ShaA }.AsEnumerable());
            await Assert.That(review.Select(e => e!["sha256"]!.GetValue<string>()).Order())
                .IsEquivalentTo(new[] { fx.ShaA, fx.ShaB }.Order());
            await Assert.That(manifest.Shared.Select(f => f.Path).Order()).IsEquivalentTo(new[]
            {
                TransferLayout.SharedFile(TransferLayout.GraphBreakpointsFileName),
                TransferLayout.SharedFile(TransferLayout.ReviewQueueFileName)
            }.Order());
        }
    }

    [Test]
    public async Task WithoutUserData_NoAnnotationsOrSharedFiles_AndWithoutDems_NoDemFolder()
    {
        using Fixture fx = Fixture.Populated();
        TransferExporter exporter = fx.Exporter(new DemoCacheStore(fx.CacheRoot));
        ExportPlan plan = await exporter.PlanAsync(ExportRequest.Chosen([fx.ShaA, fx.DemC], fx.OutRoot, false, false),
            CancellationToken.None);
        TransferSummary summary = await exporter.RunAsync(plan, null, CancellationToken.None);
        TransferManifest manifest = TransferManifest.Read(plan.BundleFolder);

        using (Assert.Multiple())
        {
            await Assert.That(manifest.Demos.Single().Sha256).IsEqualTo(fx.ShaA);
            await Assert.That(manifest.Demos.Single().Files.Select(f => f.Path))
                .DoesNotContain($"data/{fx.ShaA}/annotations.dvann.json");
            await Assert.That(manifest.Shared).IsEmpty();
            await Assert.That(manifest.Demos.Single().Dem).IsNull();
            await Assert.That(Directory.Exists(Path.Combine(plan.BundleFolder, TransferLayout.DemDirectory))).IsFalse();
            await Assert.That(summary.Skipped.Single().DisplayName).IsEqualTo("c.dem");
        }
    }

    [Test]
    public async Task AnUnhashedDemo_IsReportedNotHashed_AndIsNeverHashedIntoTheCache()
    {
        using Fixture fx = Fixture.Populated();
        DemoCacheStore store = new(fx.CacheRoot);
        TransferExporter exporter = fx.Exporter(store);
        ExportPlan plan = await exporter.PlanAsync(ExportRequest.All(fx.OutRoot, false), CancellationToken.None);
        ExportPlanEntry c = plan.Entries.Single(e => e.DemoPath == fx.DemC);

        using (Assert.Multiple())
        {
            await Assert.That(c.Status).IsEqualTo(ExportEntryStatus.NotHashed);
            await Assert.That(c.Sha256).IsNull();
            await Assert.That(c.ExportsData).IsFalse();
            await Assert.That(plan.Warnings.Select(w => w.Kind)).Contains(TransferWarningKind.NotHashed);
        }

        await exporter.RunAsync(plan, null, CancellationToken.None);
        await Assert.That(store.TryGetIndex(fx.DemC)!.Sha256).IsNull()
            .Because("hashing it on the way would write back to the cache");
    }

    [Test]
    public async Task ADemoTheQueueHolds_IsFlaggedInFlight_AndStillExported()
    {
        using Fixture fx = Fixture.Populated();
        SnapshotQueue queue = new([
            new DemoQueueItemSnapshot(Guid.NewGuid(), fx.DemB, "b.dem", ["library"], DemoJobPriority.Background,
                DemoQueueItemState.Running, null)
        ]);
        TransferExporter exporter = new(new DemoCacheStore(fx.CacheRoot), queue, fx.Sources, Fixture.Target());
        ExportPlan plan = await exporter.PlanAsync(ExportRequest.All(fx.OutRoot, false), CancellationToken.None);
        ExportPlanEntry b = plan.Entries.Single(e => e.Sha256 == fx.ShaB);

        using (Assert.Multiple())
        {
            await Assert.That(b.Status).IsEqualTo(ExportEntryStatus.InFlight);
            await Assert.That(b.ExportsData).IsTrue();
            await Assert.That(plan.Entries.Single(e => e.Sha256 == fx.ShaA).Status)
                .IsEqualTo(ExportEntryStatus.Included);
            await Assert.That(plan.Warnings.Single(w => w.Kind == TransferWarningKind.DemosInFlight).Blocking)
                .IsFalse();
            await Assert.That(plan.CanRun).IsTrue();
        }
    }

    [Test]
    public async Task ADemoQueuedAfterThePlan_IsStillReportedAsBeingProcessed()
    {
        using Fixture fx = Fixture.Populated();
        UpdateFixtures.RecordingQueue queue = new();
        TransferExporter exporter = new(new DemoCacheStore(fx.CacheRoot), queue, fx.Sources, Fixture.Target());
        ExportPlan plan = await exporter.PlanAsync(ExportRequest.All(fx.OutRoot, false), CancellationToken.None);
        ExportEntryStatus planned = plan.Entries.Single(e => e.Sha256 == fx.ShaB).Status;
        lock (queue.Snapshots)
        {
            queue.Snapshots.Add(new DemoQueueItemSnapshot(Guid.NewGuid(), fx.DemB, "b.dem", ["library"],
                DemoJobPriority.Background, DemoQueueItemState.Running, null));
        }

        TransferSummary summary = await exporter.RunAsync(plan, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        using (Assert.Multiple())
        {
            await Assert.That(planned).IsEqualTo(ExportEntryStatus.Included);
            await Assert.That(summary.Items.Single(i => i.Sha256 == fx.ShaB).Reason).Contains("being processed");
            await Assert.That(summary.Items.Single(i => i.Sha256 == fx.ShaA).Reason).IsNull();
        }
    }

    [Test]
    public async Task TheRun_IsAQueuedDataTransfer()
    {
        using Fixture fx = Fixture.Populated();
        UpdateFixtures.RecordingQueue queue = new();
        TransferExporter exporter = new(new DemoCacheStore(fx.CacheRoot), queue, fx.Sources, Fixture.Target());
        ExportPlan plan = await exporter.PlanAsync(ExportRequest.All(fx.OutRoot, false), CancellationToken.None);
        TransferSummary summary = await exporter.RunAsync(plan, null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        using (Assert.Multiple())
        {
            await Assert.That(queue.Requests.Single().Kind).IsEqualTo(QueueJobKind.DataTransfer);
            await Assert.That(queue.Requests.Single().Serial).IsNull();
            await Assert.That(summary.Transferred.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ExportingIntoTheSameBundleAgain_RewritesOnlyWhatChanged()
    {
        using Fixture fx = Fixture.Populated();
        TransferExporter exporter = fx.Exporter(new DemoCacheStore(fx.CacheRoot));
        ExportPlan first = await exporter.PlanAsync(ExportRequest.All(fx.OutRoot, true), CancellationToken.None);
        await exporter.RunAsync(first, null, CancellationToken.None);
        string bundle = first.BundleFolder;

        DateTime old = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, old);
        }

        string damaged = TransferLayout.FullPath(bundle, $"data/{fx.ShaA}/cache/demo{Sibling}");
        byte[] bytes = await File.ReadAllBytesAsync(damaged);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(damaged, bytes);
        File.SetLastWriteTimeUtc(damaged, old);
        string removed = TransferLayout.FullPath(bundle, TransferLayout.DemFile(fx.ShaB));
        File.Delete(removed);

        ExportPlan again = await exporter.PlanAsync(ExportRequest.All(bundle, true), CancellationToken.None);
        TransferSummary summary = await exporter.RunAsync(again, null, CancellationToken.None);
        string[] rewritten =
        [
            .. Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories)
                .Where(f => File.GetLastWriteTimeUtc(f) != old)
                .Select(f => Path.GetRelativePath(bundle, f).Replace('\\', '/')).Order(StringComparer.Ordinal)
        ];

        using (Assert.Multiple())
        {
            await Assert.That(again.BundleFolder).IsEqualTo(bundle).Because("picking a bundle resumes into it");
            await Assert.That(summary.Failed).IsEmpty();
            // The plain demo is hashed only by copying it, so it is the one file always written again.
            await Assert.That(rewritten).IsEquivalentTo(new[]
            {
                $"data/{fx.ShaA}/cache/demo{Sibling}", TransferLayout.DemFile(fx.ShaB),
                TransferLayout.DemFile(fx.ShaC), TransferLayout.ManifestFileName
            }.Order(StringComparer.Ordinal));
            await Assert.That(await File.ReadAllBytesAsync(damaged)).IsNotEquivalentTo(bytes);
            await Assert.That(TransferManifest.Read(bundle).Demos.Count).IsEqualTo(3);
        }
    }

    [Test]
    public async Task OnFat32_ADemOf4GiB_BlocksTheExport()
    {
        using Fixture fx = Fixture.Populated(bigDemSize: 5L << 30);
        TransferExporter exporter = new(new DemoCacheStore(fx.CacheRoot), null, fx.Sources,
            new TransferTarget(_ => new TransferDriveInfo("FAT32", long.MaxValue / 2)), null,
            path => path == fx.DemBig ? 5L << 30 : LengthOf(path));
        ExportPlan withDems = await exporter.PlanAsync(ExportRequest.All(fx.OutRoot, true), CancellationToken.None);
        ExportPlan dataOnly = await exporter.PlanAsync(ExportRequest.All(fx.OutRoot, false), CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(withDems.CanRun).IsFalse();
            await Assert.That(withDems.Warnings.Single(w => w.Kind == TransferWarningKind.Fat32FileTooLarge).Blocking)
                .IsTrue();
            await Assert.That(dataOnly.CanRun).IsTrue().Because("the data files are small");
        }

        TransferSummary refused = await exporter.RunAsync(withDems, null, CancellationToken.None);
        using (Assert.Multiple())
        {
            await Assert.That(refused.Transferred).IsEmpty();
            await Assert.That(refused.Warnings.Select(w => w.Kind)).Contains(TransferWarningKind.Fat32FileTooLarge);
            await Assert.That(Directory.Exists(withDems.BundleFolder)).IsFalse();
        }
    }

    [Test]
    public async Task ACancelledExport_LeavesNoManifest_AndNoTempFiles()
    {
        using Fixture fx = Fixture.Populated();
        TransferExporter exporter = fx.Exporter(new DemoCacheStore(fx.CacheRoot));
        ExportPlan plan = await exporter.PlanAsync(ExportRequest.All(fx.OutRoot, true), CancellationToken.None);
        using CancellationTokenSource cts = new();
        // Cancelled once the second demo starts: the first is in the bundle, the run is not complete.
        CancelOnSecondDemo progress = new(cts);

        TransferSummary summary = await exporter.RunAsync(plan, progress, cts.Token);

        using (Assert.Multiple())
        {
            await Assert.That(summary.Cancelled).IsTrue();
            await Assert.That(summary.Transferred.Count).IsEqualTo(1);
            await Assert.That(File.Exists(Path.Combine(plan.BundleFolder, TransferLayout.ManifestFileName)))
                .IsFalse();
            await Assert.That(Directory.EnumerateFiles(plan.BundleFolder, "*.tmp", SearchOption.AllDirectories))
                .IsEmpty();
            await Assert.That(() => TransferManifest.Read(plan.BundleFolder)).Throws<TransferManifestException>();
        }
    }

    [Test]
    public async Task ATargetInsideTheConfigRoot_IsRefused()
    {
        using Fixture fx = Fixture.Populated();
        TransferExporter exporter = fx.Exporter(new DemoCacheStore(fx.CacheRoot));
        ExportPlan intoConfig = await exporter.PlanAsync(ExportRequest.All(fx.ConfigRoot, false),
            CancellationToken.None);
        ExportPlan intoLibrary = await exporter.PlanAsync(ExportRequest.All(fx.LibraryRoot, false),
            CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(intoConfig.Warnings.Select(w => w.Kind)).Contains(TransferWarningKind.TargetProtected);
            await Assert.That(intoLibrary.Warnings.Select(w => w.Kind)).Contains(TransferWarningKind.TargetProtected);
            await Assert.That(intoConfig.CanRun).IsFalse();
        }
    }

    [Test]
    public async Task SnapshotFiles_ListsSiblingsFirstAndTheRecordLast_AndNothingForAnUnhashedDemo()
    {
        using Fixture fx = Fixture.Populated();
        DemoCacheStore store = new(fx.CacheRoot);
        IReadOnlyList<DemoCacheFile> files = store.SnapshotFiles(fx.ShaA);

        using (Assert.Multiple())
        {
            await Assert.That(files.Select(f => f.Rest)).IsEquivalentTo(
                new[] { Sibling, DemoCacheStore.RecordSuffix }.AsEnumerable());
            await Assert.That(files[^1].IsRecord).IsTrue();
            await Assert.That(store.SnapshotFiles(new string('0', 64))).IsEmpty();
            await Assert.That(new DemoCacheStore(null).SnapshotFiles(fx.ShaA)).IsEmpty();
        }
    }

    // path → size|mtime|sha256 of every file under root.
    private static Dictionary<string, string> Fingerprint(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f),
            f => $"{new FileInfo(f).Length}|{File.GetLastWriteTimeUtc(f).Ticks}|"
                 + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));

    private static long? LengthOf(string path) => File.Exists(path) ? new FileInfo(path).Length : null;

    /// <summary>
    ///     A temp root holding a config root (cache, extension data, annotations, breakpoints, review queue) and a
    ///     library folder: A and B hashed and analysed, C listed but not hashed yet.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(string root)
        {
            Root = root;
            Directory.CreateDirectory(ConfigRoot);
            Directory.CreateDirectory(LibraryRoot);
            Directory.CreateDirectory(OutRoot);
        }

        public string Root { get; }
        public string ConfigRoot => Path.Combine(Root, "config");
        public string CacheRoot => Path.Combine(ConfigRoot, "cache");
        public string LibraryRoot => Path.Combine(Root, "library");
        public string OutRoot => Path.Combine(Root, "out");
        public string DemA => Path.Combine(LibraryRoot, "a.dem");
        public string DemB => Path.Combine(LibraryRoot, "b.dem");
        public string DemC => Path.Combine(LibraryRoot, "c.dem");
        public string DemBig => Path.Combine(LibraryRoot, "big.dem");
        public string ShaA { get; private set; } = "";
        public string ShaB { get; private set; } = "";
        public string ShaC { get; private set; } = "";

        public TransferSources Sources => new(ConfigRoot, () => [LibraryRoot],
            () => new TransferEnvironment(["facts:core:identity-core"], []));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        public static TransferTarget Target() => new(_ => new TransferDriveInfo("NTFS", long.MaxValue / 2));

        public TransferExporter Exporter(DemoCacheStore store) => new(store, null, Sources, Target());

        public static Fixture Populated(long? bigDemSize = null)
        {
            Fixture fx = new(Path.Combine(Path.GetTempPath(), $"dv-transfer-{Guid.NewGuid():N}"));
            fx.ShaA = WriteDem(fx.DemA, 1, 300_000);
            fx.ShaB = WriteDem(fx.DemB, 2, 40_000);
            fx.ShaC = WriteDem(fx.DemC, 3, 20_000);

            DemoCacheStore store = new(fx.CacheRoot);
            store.Upsert(Analysed(fx.DemA, fx.ShaA, "de_nuke", "fp-core"));
            store.Upsert(Analysed(fx.DemB, fx.ShaB, "de_inferno", null));
            store.Upsert(new DemoCacheRecord
            {
                Path = fx.DemC, Size = new FileInfo(fx.DemC).Length,
                ModifiedTicks = File.GetLastWriteTime(fx.DemC).Ticks, Map = "de_mirage"
            });
            if (bigDemSize is { } big)
            {
                // No 5 GiB file is made: the exporter's length seam answers for it.
                string sha = Convert.ToHexStringLower(SHA256.HashData("big"u8));
                File.WriteAllBytes(fx.DemBig, [1]);
                DemoCacheRecord record = Analysed(fx.DemBig, sha, "de_dust2", null);
                record.Size = big;
                store.Upsert(record);
            }

            store.WriteSibling(fx.DemA, Sibling, "facts of a");
            store.WriteSibling(fx.DemB, Sibling, "facts of b");
            store.SaveIndex();

            ExtensionDemoDataStore data = new(ExtensionId,
                Path.Combine(fx.CacheRoot, ExtensionFolders.DataDirectoryName, ExtensionId), store, a => a(),
                "9.9.9");
            data.Write(fx.DemA, new DemoDataWrite("grenades", 1, "g1", "grenades of a"u8.ToArray())
            {
                Parts = new Dictionary<string, ReadOnlyMemory<byte>> { ["positions"] = "positions"u8.ToArray() }
            });

            File.WriteAllText(fx.DemA + ".dvann.json", """{"schemaVersion":1,"demo":"a"}""");
            Directory.CreateDirectory(Path.Combine(fx.ConfigRoot, "annotations"));
            File.WriteAllText(Path.Combine(fx.ConfigRoot, "annotations", fx.ShaB + ".dvann.json"),
                """{"schemaVersion":1,"demo":"b"}""");
            string other = new('e', 64);
            File.WriteAllText(Path.Combine(fx.ConfigRoot, "GraphBreakpoints.v2.json"),
                $$"""{"{{fx.ShaA}}":[{"TargetKind":"Node","NodeKey":"g:kill","Enabled":true}],"{{other}}":[]}""");
            File.WriteAllText(Path.Combine(fx.ConfigRoot, "review-queue.json"), $$"""
                {
                  "schemaVersion": 1,
                  "clock": {},
                  "entries": [
                    { "id": "{{Guid.NewGuid()}}", "kind": "clip", "demoPath": {{JsonValue.Create(fx.DemA).ToJsonString()}}, "fromTick": 1, "toTick": 2 },
                    { "id": "{{Guid.NewGuid()}}", "kind": "clip", "demoPath": "/elsewhere/b.dem", "sha256": "{{fx.ShaB}}", "fromTick": 1, "toTick": 2 },
                    { "id": "{{Guid.NewGuid()}}", "kind": "clip", "demoPath": "/elsewhere/x.dem", "sha256": "{{other}}", "fromTick": 1, "toTick": 2 },
                    { "id": "{{Guid.NewGuid()}}", "kind": "section", "title": "Round 1" }
                  ]
                }
                """);
            // The source's files settle into the past, so a rewrite of any of them would show.
            DateTime past = DateTime.UtcNow.AddMinutes(-10);
            foreach (string file in Directory.EnumerateFiles(fx.Root, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTimeUtc(file, past);
            }

            return fx;
        }

        private static string WriteDem(string path, int seed, int length)
        {
            byte[] bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        private static DemoCacheRecord Analysed(string path, string sha, string map, string? factsFingerprint)
        {
            DemoCacheRecord record = new()
            {
                Path = path,
                Size = new FileInfo(path).Length,
                ModifiedTicks = File.GetLastWriteTime(path).Ticks,
                Sha256 = sha,
                Map = map,
                Scoreboard = [new CachedStatRow { Slot = 1, Team = 3, Kills = 21 }],
                AnalysisState = DemoAnalysisState.Indexed
            };
            DemoCacheStore.StampParse(record);
            DemoCacheStore.StampAnalysis(record);
            if (factsFingerprint is not null)
            {
                record.PackStamps.Add(new PackStamp("facts:core", 1, factsFingerprint));
            }

            return record;
        }
    }

    private sealed class CancelOnSecondDemo(CancellationTokenSource cts) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            if (value is { DemosDone: 1, Current: not null })
            {
                cts.Cancel();
            }
        }
    }

    /// <summary>A queue that only answers <see cref="Snapshot" />, with the items it was given.</summary>
    private sealed class SnapshotQueue(IReadOnlyList<DemoQueueItemSnapshot> items) : IDemoProcessingQueue
    {
        public ReadOnlyObservableCollection<DemoQueueItem> Items { get; } = new([]);
        public bool BackgroundEnabled => true;
        public bool IsPaused => false;
        public int QueuedCount => 0;
        public int RunningCount => items.Count;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public event Action? CapacityAvailable
        {
            add { }
            remove { }
        }

        public Task<ParsedDemo> RequestForegroundAsync(string? path, ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IDemoQueueHandle SubmitVisit(DemoVisitRequest request) => throw new NotSupportedException();

        public IDemoQueueHandle SubmitJob(QueueJobRequest request) => throw new NotSupportedException();

        public IReadOnlyList<DemoQueueItemSnapshot> Snapshot() => items;

        public void RemoveByUser(Guid itemId)
        {
        }

        public void CancelOwned(string ownerTag)
        {
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }
    }
}
