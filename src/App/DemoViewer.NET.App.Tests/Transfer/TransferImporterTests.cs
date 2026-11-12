#region

using System.Text;
using System.Text.Json;
using DemoViewer.NET.Debugging;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Review;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.Transfer;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     Import end to end against hand-built bundles: a bundle from another version or with an unsafe or damaged
///     file list is refused before anything is written; user data on both sides follows the conflict policy and
///     never lands beside a demo; extension facets survive the library's drop while their demo is an imported
///     orphan; and copied demos get portable, de-duplicated names and are never copied twice.
/// </summary>
public class TransferImporterTests
{
    private const string Sha = "3333333333333333333333333333333333333333333333333333333333333333";

    private static readonly PersistedGraphBreakpoint _mine =
        new(GraphBreakpointTarget.Node, "g:mine", null, null, null, null, null, true);

    private static readonly PersistedGraphBreakpoint _theirs =
        new(GraphBreakpointTarget.Node, "g:theirs", null, null, null, null, null, true);

    private static BundleBuilder OneDemo(string root, Action<BundleBuilder, List<TransferFile>>? more = null)
    {
        BundleBuilder bundle = new(root);
        List<TransferFile> files = [bundle.Record(BundleBuilder.ParsedRecord(Sha, 4096))];
        more?.Invoke(bundle, files);
        bundle.Demo(Sha, "match.dem", 4096, files);
        return bundle;
    }

    [Test]
    public async Task ABundleFromAnotherVersion_IsRefused()
    {
        using TempFolders temp = new();
        string root = temp.New();
        OneDemo(root).Write("0.0.0-elsewhere");
        DemoCacheStore cache = new(null);

        await Assert.That(() => (Task)new TransferImporter(cache).PreviewAsync(root, CancellationToken.None))
            .Throws<TransferManifestException>().WithMessageContaining("0.0.0-elsewhere");
    }

    [Test]
    [Arguments("data/3333333333333333333333333333333333333333333333333333333333333333/../../outside.json")]
    [Arguments("/etc/passwd")]
    [Arguments("C:/Windows/win.ini")]
    [Arguments("data/3333333333333333333333333333333333333333333333333333333333333333/cache/a\\..\\..\\b")]
    public async Task AnUnsafePath_RefusesTheWholeBundle(string path)
    {
        using TempFolders temp = new();
        string root = temp.New();
        BundleBuilder bundle = new(root);
        bundle.Demo(Sha, "match.dem", 4096, [new TransferFile(path, 1, new string('a', 64))]);
        bundle.Write();
        DemoCacheStore cache = new(null);

        await Assert.That(() => (Task)new TransferImporter(cache).PreviewAsync(root, CancellationToken.None))
            .Throws<TransferManifestException>();
        await Assert.That(cache.HoldsContent(Sha)).IsFalse();
    }

    [Test]
    public async Task ACaseDuplicate_RefusesTheWholeBundle()
    {
        using TempFolders temp = new();
        string root = temp.New();
        BundleBuilder bundle = new(root);
        TransferFile upper =
            bundle.File(TransferLayout.CacheFile(Sha, TransferLayout.DemoFileName(".A.facts.x.json.gz")), [1]);
        bundle.Demo(Sha, "match.dem", 4096, [upper, upper with { Path = upper.Path.Replace(".A.", ".a.") }]);
        bundle.Write();

        await Assert.That(() => (Task)new TransferImporter(new DemoCacheStore(null)).PreviewAsync(root, CancellationToken.None))
            .Throws<TransferManifestException>();
    }

    [Test]
    public async Task AFileOfAnotherSize_RefusesTheWholeBundle()
    {
        using TempFolders temp = new();
        string root = temp.New();
        TransferManifest manifest = OneDemo(root).Manifest();
        TransferFile record = manifest.Demos[0].Files[0];
        File.AppendAllText(TransferLayout.FullPath(root, record.Path), "x");
        manifest.Write(root);

        await Assert.That(() => (Task)new TransferImporter(new DemoCacheStore(null)).PreviewAsync(root, CancellationToken.None))
            .Throws<TransferManifestException>().WithMessageContaining("damaged");
    }

    [Test]
    public async Task AFileWhoseBytesDiffer_FailsItsDemo_AndWritesNothingOfIt()
    {
        using TempFolders temp = new();
        string root = temp.New();
        TransferManifest manifest = OneDemo(root).Write();
        string recordFile = TransferLayout.FullPath(root, manifest.Demos[0].Files[0].Path);
        byte[] bytes = File.ReadAllBytes(recordFile);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(recordFile, bytes);
        DemoCacheStore cache = new(null);
        TransferImporter importer = new(cache);

        ImportPreview preview = await importer.PreviewAsync(root, CancellationToken.None);
        TransferSummary summary = await importer.RunAsync(ImportRequest.DataOnly(root), preview, null,
            CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(summary.Failed.Select(i => i.Sha256 ?? "")).IsEquivalentTo([Sha]);
            await Assert.That(cache.HoldsContent(Sha)).IsFalse();
        }
    }

    [Test]
    public async Task ANewDemo_ImportsAsAnOrphanWithItsFingerprint()
    {
        using TempFolders temp = new();
        string root = temp.New();
        DemoContentFingerprint fingerprint = new(4096, 256, new string('c', 64), new string('d', 64));
        BundleBuilder bundle = new(root);
        bundle.Demo(Sha, "match.dem", 4096, [bundle.Record(BundleBuilder.ParsedRecord(Sha, 4096))], fingerprint);
        bundle.Write();
        DemoCacheStore cache = new(null);
        TransferImporter importer = new(cache);

        ImportPreview preview = await importer.PreviewAsync(root, CancellationToken.None);
        TransferSummary summary = await importer.RunAsync(ImportRequest.DataOnly(root), preview, null,
            CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(preview.Entries.Single().Status).IsEqualTo(ImportEntryStatus.New);
            await Assert.That(summary.Transferred.Select(i => i.Sha256 ?? "")).IsEquivalentTo([Sha]);
            await Assert.That(cache.TryGetOrphan(Sha)?.ContentFingerprint).IsEqualTo(fingerprint);
            await Assert.That(cache.TryGetOrphan(Sha)?.ImportedUtcTicks).IsNotNull();
            await Assert.That(cache.TryGetOrphan(Sha)?.Path).IsEqualTo("");
        }

        // The same bundle again finds everything here and current.
        ImportPreview again = await importer.PreviewAsync(root, CancellationToken.None);
        TransferSummary second = await importer.RunAsync(ImportRequest.DataOnly(root), again, null,
            CancellationToken.None);
        using (Assert.Multiple())
        {
            await Assert.That(again.Entries.Single().Status).IsEqualTo(ImportEntryStatus.AlreadyHere);
            await Assert.That(second.Skipped.Select(i => i.Sha256 ?? "")).IsEquivalentTo([Sha]);
        }
    }

    [Test]
    public async Task DifferentRules_MarkTheDataStaleOnArrival()
    {
        using TempFolders temp = new();
        string root = temp.New();
        OneDemo(root).Write(rulesets: ["player_match:elsewhere"]);
        TransferImporter importer = new(new DemoCacheStore(null),
            environment: () => new TransferEnvironment(["player_match:here"], []));

        ImportPreview preview = await importer.PreviewAsync(root, CancellationToken.None);
        TransferSummary summary = await importer.RunAsync(ImportRequest.DataOnly(root), preview, null,
            CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(preview.AnyStale).IsTrue();
            await Assert.That(preview.Warnings.Select(w => w.Kind)).Contains(TransferWarningKind.StaleOnArrival);
            await Assert.That(summary.Stale.Select(i => i.Sha256 ?? "")).IsEquivalentTo([Sha]);
        }
    }

    [Test]
    [Arguments(ConflictPolicy.KeepMine, new[] { "mine" }, new[] { "g:mine" })]
    [Arguments(ConflictPolicy.TakeImported, new[] { "theirs" }, new[] { "g:theirs" })]
    [Arguments(ConflictPolicy.KeepBoth, new[] { "mine", "theirs" }, new[] { "g:mine", "g:theirs" })]
    public async Task UserDataOnBothSides_FollowsThePolicy(ConflictPolicy policy, string[] annotations,
        string[] breakpoints)
    {
        using TempFolders temp = new();
        string root = temp.New();
        string config = temp.New();
        BundleBuilder bundle = OneDemo(root, (b, files) => files.Add(b.Annotations(Sha, "theirs")));
        bundle.Shared(TransferLayout.GraphBreakpointsFileName,
            GraphBreakpointStore.Serialize(new Dictionary<string, List<PersistedGraphBreakpoint>>
            {
                [Sha] = [_theirs]
            }));
        bundle.Write();

        string mineFile = AnnotationStore.AppDataFile(config, Sha);
        Directory.CreateDirectory(Path.GetDirectoryName(mineFile)!);
        File.WriteAllBytes(mineFile, BundleBuilder.AnnotationDocument(Sha, "mine"));
        string breakpointsFile = Path.Combine(config, "GraphBreakpoints.v2.json");
        File.WriteAllBytes(breakpointsFile, GraphBreakpointStore.Serialize(
            new Dictionary<string, List<PersistedGraphBreakpoint>> { [Sha] = [_mine] }));
        GraphBreakpointStore store = new(breakpointsFile);
        TransferImporter importer = new(new DemoCacheStore(null), config, store);

        ImportPreview preview = await importer.PreviewAsync(root, CancellationToken.None);
        await importer.RunAsync(ImportRequest.DataOnly(root, policy), preview, null, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(preview.Entries.Single().Status).IsEqualTo(ImportEntryStatus.UserDataConflict);
            await Assert.That(BundleBuilder.ElementIds(mineFile)).IsEquivalentTo(annotations, CollectionOrdering.Matching);
            await Assert.That(store.Load(Sha).Select(b => b.NodeKey ?? "")).IsEquivalentTo(breakpoints,
                CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Annotations_WithNoLocalCopy_GoToAppData_NotBesideTheDemo()
    {
        using TempFolders temp = new();
        string root = temp.New();
        string config = temp.New();
        string library = temp.New();
        OneDemo(root, (b, files) => files.Add(b.Annotations(Sha, "theirs"))).Write();
        DemoCacheStore cache = new(null);
        string demo = Path.Combine(library, "match.dem");
        cache.Upsert(new DemoCacheRecord { Path = demo, Size = 4096, Sha256 = Sha });
        TransferImporter importer = new(cache, config);

        ImportPreview preview = await importer.PreviewAsync(root, CancellationToken.None);
        await importer.RunAsync(ImportRequest.DataOnly(root), preview, null, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(File.Exists(demo + AnnotationStore.SidecarExtension)).IsFalse();
            await Assert.That(BundleBuilder.ElementIds(AnnotationStore.AppDataFile(config, Sha)))
                .IsEquivalentTo(["theirs"]);
        }
    }

    [Test]
    public async Task ImportedAnnotations_LoadFromAppData_WhileTheDemosFolderHasNone()
    {
        using TempFolders temp = new();
        string config = temp.New();
        string demo = Path.Combine(temp.New(), "match.dem");
        File.WriteAllBytes(demo, [1, 2, 3]);
        string imported = AnnotationStore.AppDataFile(config, Sha);
        Directory.CreateDirectory(Path.GetDirectoryName(imported)!);
        File.WriteAllBytes(imported, BundleBuilder.AnnotationDocument(Sha, "theirs"));
        AnnotationStore store = new(config, _ => Sha);

        AnnotationLoadResult loaded = await store.LoadAsync(demo, new ClockIdentity("dv-frame-clock", 64, 1, 0, 0));

        using (Assert.Multiple())
        {
            await Assert.That(loaded.Path).IsEqualTo(imported);
            await Assert.That(loaded.Location).IsEqualTo(AnnotationStoreLocation.AppData);
            await Assert.That(store.ResolvePath(demo)).IsEqualTo(Path.GetFullPath(demo) + AnnotationStore.SidecarExtension)
                .Because("the next save goes beside the demo, which wins from then on");
        }
    }

    [Test]
    public async Task AnnotationsBesideADemo_AreKept_WhateverThePolicy()
    {
        using TempFolders temp = new();
        string root = temp.New();
        string config = temp.New();
        string library = temp.New();
        OneDemo(root, (b, files) => files.Add(b.Annotations(Sha, "theirs"))).Write();
        DemoCacheStore cache = new(null);
        string demo = Path.Combine(library, "match.dem");
        cache.Upsert(new DemoCacheRecord { Path = demo, Size = 4096, Sha256 = Sha });
        File.WriteAllBytes(demo + AnnotationStore.SidecarExtension, BundleBuilder.AnnotationDocument(Sha, "mine"));
        TransferImporter importer = new(cache, config);

        ImportPreview preview = await importer.PreviewAsync(root, CancellationToken.None);
        TransferSummary summary = await importer.RunAsync(ImportRequest.DataOnly(root, ConflictPolicy.TakeImported),
            preview, null, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(BundleBuilder.ElementIds(demo + AnnotationStore.SidecarExtension))
                .IsEquivalentTo(["mine"]);
            await Assert.That(File.Exists(AnnotationStore.AppDataFile(config, Sha))).IsFalse();
            await Assert.That(summary.Items.Single().Reason).Contains("beside the demo were kept");
        }
    }

    [Test]
    public async Task AnExtensionFacet_IsImportedWithoutItsSourcePath_AndSurvivesTheLibraryDrop()
    {
        using TempFolders temp = new();
        string root = temp.New();
        string extData = temp.New();
        const string Extension = "dev.example.data";
        OneDemo(root, (b, files) =>
        {
            files.Add(b.File(TransferLayout.ExtensionFacetFile(Sha, Extension, "rounds", TransferLayout.DemoFileName(".json.gz")),
                BundleBuilder.FacetFile("rounds", null, 2, "fp", Sha, @"D:\source\match.dem", "payload")));
            files.Add(b.File(TransferLayout.ExtensionFacetFile(Sha, Extension, "rounds", TransferLayout.DemoFileName(".positions.json.gz")),
                BundleBuilder.FacetFile("rounds", "positions", 2, "fp", Sha, @"D:\source\match.dem", "part")));
        }).Write();
        DemoCacheStore cache = new(null);
        ExtensionDemoDataStore live = new(Extension, Path.Combine(extData, Extension), cache, a => a(), "1.0.0");
        TransferImporter importer = new(cache, extensionData: id => id == Extension ? live : null);

        ImportPreview preview = await importer.PreviewAsync(root, CancellationToken.None);
        await importer.RunAsync(ImportRequest.DataOnly(root), preview, null, CancellationToken.None);

        // The library's whole-store drop (a batch names no path) runs while the demo is still an imported orphan.
        using (cache.BeginBatch())
        {
            cache.Upsert(new DemoCacheRecord { Path = "/elsewhere/other.dem", Size = 1, Sha256 = new string('9', 64) });
            cache.Remove("/elsewhere/other.dem");
        }

        string facetFile = Path.Combine(extData, Extension, ExtensionDemoDataStore.DirectoryName, "rounds",
            Sha + ".json.gz");
        await Assert.That(File.Exists(facetFile)).IsTrue();
        await Assert.That(live.Stamps("rounds")).IsEmpty().Because("an orphan's data is no path's");

        // A folder lists the bytes and a full read confirms them: the facet reads through the new path.
        const string Demo = "/library/match.dem";
        cache.AttachUnconfirmed(Sha, Demo, 4096, 1);
        cache.ConfirmLocation(Demo, Sha, 4096);
        ExtensionDemoDataStore reopened = new(Extension, Path.Combine(extData, Extension), cache, a => a(), "1.0.0");
        using (Assert.Multiple())
        {
            await Assert.That(live.Stamp(Demo, "rounds")?.Schema).IsEqualTo(2);
            await Assert.That(Encoding.UTF8.GetString(live.Read(Demo, "rounds", 2, "fp")!)).IsEqualTo("payload");
            await Assert.That(Encoding.UTF8.GetString(live.Read(Demo, "rounds", 2, "fp", "positions")!))
                .IsEqualTo("part");
            await Assert.That(reopened.Stamp(Demo, "rounds")?.Fingerprint).IsEqualTo("fp")
                .Because("the index on disk carries the imported facet");
        }

        live.StopFollowingLibrary();
        reopened.StopFollowingLibrary();
    }

    [Test]
    public async Task ABadFacet_IsReportedOnItsOwn_AndTheDemosOtherDataStillImports()
    {
        using TempFolders temp = new();
        string root = temp.New();
        string config = temp.New();
        string extData = temp.New();
        const string Extension = "dev.example.data";
        OneDemo(root, (b, files) =>
        {
            // A part with no main payload: the store refuses this facet.
            files.Add(b.File(TransferLayout.ExtensionFacetFile(Sha, Extension, "broken", TransferLayout.DemoFileName(".positions.json.gz")),
                BundleBuilder.FacetFile("broken", "positions", 2, "fp", Sha, @"D:\source\match.dem", "part")));
            files.Add(b.File(TransferLayout.ExtensionFacetFile(Sha, Extension, "rounds", TransferLayout.DemoFileName(".json.gz")),
                BundleBuilder.FacetFile("rounds", null, 2, "fp", Sha, @"D:\source\match.dem", "payload")));
            files.Add(b.Annotations(Sha, "theirs"));
        }).Write();
        DemoCacheStore cache = new(null);
        ExtensionDemoDataStore live = new(Extension, Path.Combine(extData, Extension), cache, a => a(), "1.0.0");
        TransferImporter importer = new(cache, config, extensionData: id => id == Extension ? live : null);

        ImportPreview preview = await importer.PreviewAsync(root, CancellationToken.None);
        TransferSummary summary = await importer.RunAsync(ImportRequest.DataOnly(root), preview, null,
            CancellationToken.None);
        TransferSummaryItem item = summary.Items.Single();

        using (Assert.Multiple())
        {
            await Assert.That(item.Outcome).IsEqualTo(TransferOutcome.Transferred);
            await Assert.That(item.Reason).Contains("broken data was not imported");
            await Assert.That(cache.HoldsContent(Sha)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(extData, Extension, ExtensionDemoDataStore.DirectoryName,
                "rounds", Sha + ".json.gz"))).IsTrue();
            await Assert.That(BundleBuilder.ElementIds(AnnotationStore.AppDataFile(config, Sha)))
                .IsEquivalentTo(["theirs"]);
        }

        live.StopFollowingLibrary();
    }

    [Test]
    [Arguments("update.exe", "update.exe.dem")]
    [Arguments("notes", "notes.dem")]
    [Arguments("match.DEM", "match.DEM")]
    [Arguments("con", "_con.dem")]
    [Arguments("", "demo.dem")]
    public async Task ACopiedDemo_IsAlwaysNamedDotDem(string original, string expected) =>
        await Assert.That(TransferImporter.WithDemExtension(original)).IsEqualTo(expected);

    [Test]
    public async Task ACopiedDemoWithAForeignExtension_LandsAsADem()
    {
        using TempFolders temp = new();
        string root = temp.New();
        string destination = temp.New("Demos");
        byte[] bytes = new byte[300];
        new Random(4).NextBytes(bytes);
        BundleBuilder bundle = new(root);
        string sha = BundleBuilder.Sha(bytes);
        bundle.Demo(sha, "run.bat", bytes.Length, [], dem: bundle.Dem(sha, bytes));
        bundle.Write();
        TransferImporter importer = new(new DemoCacheStore(null));

        ImportPreview preview = await importer.PreviewAsync(root, CancellationToken.None);
        await importer.RunAsync(new ImportRequest(root, ConflictPolicy.KeepMine, new HashSet<string> { sha },
            destination, false), preview, null, CancellationToken.None);

        await Assert.That(Directory.EnumerateFiles(destination).Select(f => Path.GetFileName(f)!))
            .IsEquivalentTo(["run.bat.dem"]);
    }

    [Test]
    public async Task CopiedDemos_GetPortableUniqueNames_AndOneTheLibraryHasIsNotCopied()
    {
        using TempFolders temp = new();
        string root = temp.New();
        string destination = temp.New("Demos");
        File.WriteAllBytes(Path.Combine(destination, "match_1.dem"), [0]);
        byte[] first = new byte[300];
        byte[] second = new byte[301];
        byte[] third = new byte[302];
        new Random(1).NextBytes(first);
        new Random(2).NextBytes(second);
        new Random(3).NextBytes(third);
        BundleBuilder bundle = new(root);
        foreach ((byte[] bytes, string name) in new[] { (first, "match:1.dem"), (second, "MATCH:1.dem"), (third, "x.dem") })
        {
            string sha = BundleBuilder.Sha(bytes);
            bundle.Demo(sha, name, bytes.Length, [], dem: bundle.Dem(sha, bytes));
        }

        bundle.Write();
        DemoCacheStore cache = new(null);
        cache.Upsert(new DemoCacheRecord
        {
            Path = Path.Combine(temp.New(), "x.dem"), Size = third.Length, Sha256 = BundleBuilder.Sha(third)
        });
        TransferImporter importer = new(cache);

        ImportPreview preview = await importer.PreviewAsync(root, CancellationToken.None);
        HashSet<string> all = [.. preview.Entries.Select(e => e.Sha256)];
        TransferSummary summary = await importer.RunAsync(
            new ImportRequest(root, ConflictPolicy.KeepMine, all, destination, false), preview, null,
            CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(preview.Entries.Single(e => e.Sha256 == BundleBuilder.Sha(third)).DemAlreadyInLibrary)
                .IsTrue();
            await Assert.That(File.ReadAllBytes(Path.Combine(destination, "match_1 (2).dem"))).IsEquivalentTo(first);
            await Assert.That(File.ReadAllBytes(Path.Combine(destination, "MATCH_1 (3).dem"))).IsEquivalentTo(second);
            await Assert.That(File.Exists(Path.Combine(destination, "x.dem"))).IsFalse();
            await Assert.That(Directory.EnumerateFiles(destination, "*.tmp")).IsEmpty();
            await Assert.That(summary.Transferred.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task AReviewClip_ArrivesWithNoSourcePath_JoinedByItsHash()
    {
        using TempFolders temp = new();
        string root = temp.New();
        BundleBuilder bundle = OneDemo(root);
        ReviewQueueFile file = new()
        {
            Entries =
            [
                ReviewEntry.Section("Retakes"),
                ReviewEntry.Clip(@"D:\source\match.dem", 10, 20, "a retake", ReviewSources.Manual, 64, Sha),
                ReviewEntry.Clip(@"D:\source\nohash.dem", 10, 20, "no hash", ReviewSources.Manual, 64)
            ]
        };
        bundle.Shared(TransferLayout.ReviewQueueFileName,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file, ReviewQueueFile.JsonOptions)));
        bundle.Write();
        ReviewQueue review = new(null);
        TransferImporter importer = new(new DemoCacheStore(null), review: review);

        ImportPreview preview = await importer.PreviewAsync(root, CancellationToken.None);
        await importer.RunAsync(ImportRequest.DataOnly(root), preview, null, CancellationToken.None);

        ReviewEntry clip = review.Clips.Single();
        using (Assert.Multiple())
        {
            await Assert.That(clip.DemoPath).IsEqualTo("");
            await Assert.That(clip.Sha256).IsEqualTo(Sha);
            await Assert.That(review.SectionOf(clip.Id)).IsEqualTo("Retakes");
        }

        // Importing it again keeps the one clip.
        await importer.RunAsync(ImportRequest.DataOnly(root), await importer.PreviewAsync(root, CancellationToken.None),
            null, CancellationToken.None);
        await Assert.That(review.Clips.Count).IsEqualTo(1);
    }
}
