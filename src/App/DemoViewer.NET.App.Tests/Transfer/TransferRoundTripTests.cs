#region

using System.Security.Cryptography;
using System.Text.Json;
using CS2DemoKit.Parser;
using DemoViewer.NET.Debugging;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Review;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.Transfer;
using DemoViewer.NET.TestSupport;
using static DemoViewer.NET.AppTests.LibraryFingerprintTests;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     Export on one machine and import on another, with the real exporter and importer over two config roots: the
///     demo copied into the second library afterwards joins its imported row by fingerprint and is never parsed,
///     its stamps and facts arrive intact, and its annotations, breakpoints and review clip come with it. The
///     source is not touched.
/// </summary>
[NotInParallel]
public partial class TransferRoundTripTests
{
    private const string Ruleset = "core";
    private const string FactsFingerprint = "fp-core";
    private const string FactsText = "facts of the demo";

    // Both machines run the same rules, so nothing arrives stale.
    private static readonly TransferEnvironment _environment = new(["core:identity-core"], []);

    private static readonly PersistedGraphBreakpoint _breakpoint =
        new(GraphBreakpointTarget.Node, "g:kill", null, null, null, null, null, true);

    [Test]
    public async Task ADemoExportedHere_OpensThereFromItsImportedRow_WithoutAParse()
    {
        using TempFolders temp = new();
        string libraryA = temp.New();
        string demo = Path.Combine(libraryA, "match one.dem");
        await File.WriteAllBytesAsync(demo, Bytes(91));

        await RoundTripAsync(temp, demo, libraryA, null);
    }

    // The same trip with a real demo and a record projected from its real parse. The demo is only read where it
    // lies; the importing side gets a copy.
    [Test]
    [Category("RealDemo")]
    public async Task ARealDemo_ExportedAndImported_IsNotParsedAgain()
    {
        string demo = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoParser.Parse((await File.ReadAllBytesAsync(demo)).AsMemory());
        (List<CachedPlayerInfo> players, List<CachedRound> rounds) = DemoLibraryService.ProjectTier2(parsed);

        using TempFolders temp = new();
        await RoundTripAsync(temp, demo, Path.GetDirectoryName(demo)!, record =>
        {
            record.Map = parsed.MapName;
            record.TickRate = parsed.TickRate;
            record.TickCount = parsed.TickCount;
            record.Players = players;
            record.Rounds = rounds;
            record.RoundCount = rounds.Count;
        });
    }

    private static async Task RoundTripAsync(TempFolders temp, string demo, string libraryA,
        Action<DemoCacheRecord>? fill, StratBookLeg? stratBook = null, string? configB = null)
    {
        // Machine A: an analysed demo with user data in the config root.
        string configA = temp.New();
        string outRoot = temp.New();
        using Library b = new(temp.New(), new DemoCacheStore(Path.Combine(temp.New(), "cache")));
        DemoCacheStore storeA = new(Path.Combine(configA, "cache"));
        string sha = Seed(storeA, configA, demo, b.Reader.WindowBytes, fill);
        stratBook?.Populate(configA, sha);
        Dictionary<string, string> configBefore = Snapshot(configA);
        (long Length, DateTime Written) demoBefore = (new FileInfo(demo).Length, File.GetLastWriteTimeUtc(demo));

        TransferExporter exporter = new(storeA, null,
            new TransferSources(configA, () => [libraryA], () => _environment, stratBook is null ? null : stratBook.Sources),
            new TransferTarget(_ => new TransferDriveInfo("NTFS", long.MaxValue / 2)));
        ExportPlan plan = await exporter.PlanAsync(ExportRequest.All(outRoot, false), CancellationToken.None);
        TransferSummary exported = await exporter.RunAsync(plan, null, CancellationToken.None);

        // Machine B: an empty library, then the bundle, then the demo file itself.
        configB ??= temp.New();
        string libraryB = temp.New();
        await b.Service.AddFoldersAsync([libraryB]);
        GraphBreakpointStore breakpointsB = new(Path.Combine(configB, "GraphBreakpoints.v2.json"));
        ReviewQueue reviewB = new(null);
        TransferImporter importer = new(b.Store, configB, breakpointsB, reviewB, b.Service,
            environment: () => _environment, extensions: stratBook?.Host(configB));
        ImportPreview preview = await importer.PreviewAsync(plan.BundleFolder, CancellationToken.None);
        TransferSummary imported = await importer.RunAsync(ImportRequest.DataOnly(plan.BundleFolder), preview, null,
            CancellationToken.None);

        string copied = Path.Combine(libraryB, Path.GetFileName(demo));
        File.Copy(demo, copied);
        await b.Service.RescanAsync();
        await WaitForAsync(() => b.Service.Entries.Count == 1
                && b.Service.Entries.All(e => e.State == DemoIndexState.Indexed)
                && b.Store.LocationOf(copied) is not null,
            "the copied demo attached");
        // Long enough for anything the attach enlisted to have started.
        await Task.Delay(150);

        DemoCacheRecord? record = b.Store.TryLoadRecord(copied);
        PackStamp? stamp = record?.Stamp(StampedFacts.StampId(Ruleset));
        using (Assert.Multiple())
        {
            await Assert.That(exported.Failed).IsEmpty();
            await Assert.That(Snapshot(configA)).IsEquivalentTo(configBefore)
                .Because("an export writes nothing at the source");
            await Assert.That((new FileInfo(demo).Length, File.GetLastWriteTimeUtc(demo))).IsEqualTo(demoBefore);
            await Assert.That(preview.Entries.Single().Status).IsEqualTo(ImportEntryStatus.New);
            await Assert.That(preview.AnyStale).IsFalse().Because("both machines run the same rules");
            await Assert.That(imported.Transferred.Where(i => i.Sha256 is not null).Select(i => i.Sha256!))
                .IsEquivalentTo([sha]);

            await Assert.That(b.Store.LocationOf(copied)?.ContentId).IsEqualTo(sha)
                .Because("the copy attaches to the imported row by its fingerprint");
            await Assert.That(b.Parses.GetValueOrDefault(copied)).IsEqualTo(0);
            await Assert.That(b.TotalParses).IsEqualTo(0);
            await Assert.That(b.Service.Tier2Backlog()).IsEmpty().Because("no parse is enlisted for it");
            await Assert.That(b.Store.TryGetIndex(copied)?.ParseSchema ?? 0).IsGreaterThan(0);
            await Assert.That(b.Store.IsOrphaned(sha)).IsFalse();
            await Assert.That(b.Store.TryGetByContentId(sha)?.ImportedUtcTicks).IsNull()
                .Because("the imported mark clears on the first attach");
            await Assert.That(stamp?.IsCurrent(StampedFacts.Schema, FactsFingerprint) ?? false).IsTrue()
                .Because("the facts stamp arrives as current as it left");
            await Assert.That(b.Store.TryReadSibling(copied, StampedFacts.RulesetSuffix(Ruleset)))
                .IsEqualTo(FactsText);
            await Assert.That(record?.Path).IsEqualTo(copied);

            string annotations = AnnotationStore.AppDataFile(configB, sha);
            await Assert.That(File.Exists(annotations)).IsTrue();
            await Assert.That(File.Exists(copied + ".dvann.json")).IsFalse()
                .Because("import never writes beside a demo");
            await Assert.That(BundleBuilder.ElementIds(annotations)).IsEquivalentTo(["stroke-1"]);
            await Assert.That(breakpointsB.LoadAll().GetValueOrDefault(sha)?.Select(p => p.NodeKey ?? "") ?? [])
                .IsEquivalentTo(["g:kill"]);
            await Assert.That(reviewB.Clips.Single().Sha256).IsEqualTo(sha);
            await Assert.That(reviewB.Clips.Single().DemoPath).IsEqualTo("");
        }

        if (stratBook is not null)
        {
            await stratBook.AssertArrivedAsync(configB, sha, imported);
        }
    }

    // An analysed row of the demo with a current facts stamp and its sibling, plus user data in the config root
    // (never beside the demo, which may be a corpus file).
    private static string Seed(DemoCacheStore store, string configRoot, string demo, int windowBytes,
        Action<DemoCacheRecord>? fill)
    {
        string sha;
        DemoContentFingerprint fingerprint;
        using (FileStream stream = File.OpenRead(demo))
        {
            (sha, fingerprint) = DemoContentFingerprint.ComputeWithContentHash(stream, windowBytes);
        }

        FileInfo info = new(demo);
        DemoCacheRecord record = new()
        {
            Path = demo,
            Size = info.Length,
            ModifiedTicks = info.LastWriteTime.Ticks,
            Map = "de_roundtrip",
            TickRate = 64,
            AnalysisState = DemoAnalysisState.Indexed
        };
        fill?.Invoke(record);
        record.SetContentHash(sha, fingerprint);
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        record.PackStamps.Add(new PackStamp(StampedFacts.StampId(Ruleset), StampedFacts.Schema, FactsFingerprint));
        store.Upsert(record);
        store.WriteSibling(demo, StampedFacts.RulesetSuffix(Ruleset), FactsText);
        store.SaveIndex();

        string annotations = AnnotationStore.AppDataFile(configRoot, sha);
        Directory.CreateDirectory(Path.GetDirectoryName(annotations)!);
        File.WriteAllBytes(annotations, BundleBuilder.AnnotationDocument(sha, "stroke-1"));
        File.WriteAllBytes(Path.Combine(configRoot, "GraphBreakpoints.v2.json"),
            GraphBreakpointStore.Serialize(new Dictionary<string, List<PersistedGraphBreakpoint>>
            {
                [sha] = [_breakpoint], [new string('e', 64)] = [_breakpoint]
            }));
        ReviewQueueFile review = new()
        {
            Entries =
            [
                ReviewEntry.Clip(demo, 10, 20, "a retake", ReviewSources.Manual, 64, sha),
                ReviewEntry.Clip(@"D:\elsewhere\other.dem", 10, 20, "another demo", ReviewSources.Manual, 64,
                    new string('e', 64))
            ]
        };
        File.WriteAllText(Path.Combine(configRoot, ReviewQueue.FileName),
            JsonSerializer.Serialize(review, ReviewQueueFile.JsonOptions));

        // Settled into the past, so a rewrite of any of them shows.
        DateTime past = DateTime.UtcNow.AddMinutes(-10);
        foreach (string file in Directory.EnumerateFiles(configRoot, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, past);
        }

        return sha;
    }

    // Every file under the root by relative path: size, write time and content hash.
    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f),
            f => string.Join('|', new FileInfo(f).Length, File.GetLastWriteTimeUtc(f).Ticks,
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))),
            StringComparer.Ordinal);
}
