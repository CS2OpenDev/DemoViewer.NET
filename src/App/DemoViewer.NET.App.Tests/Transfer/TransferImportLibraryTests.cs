#region

using System.Text;
using System.Text.Json;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Playback2D.Pipeline.Review;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Transfer;
using static DemoViewer.NET.AppTests.LibraryFingerprintTests;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     The point of importing rows before demo files: a copied demo joins the library through its imported row's
///     fingerprint, shows the imported parse, and is never parsed for it. The library harness counts parses.
/// </summary>
[NotInParallel]
public class TransferImportLibraryTests
{
    [Test]
    public async Task ACopiedDemo_AttachesToItsImportedRow_WithoutAParse()
    {
        using TempFolders temp = new();
        string bundleRoot = temp.New();
        string cacheRoot = temp.New();
        string destination = Path.Combine(temp.New(), "Imported demos");
        byte[] bytes = Bytes(71);
        using Library library = new(temp.New(), new DemoCacheStore(cacheRoot));
        (string sha, DemoContentFingerprint fingerprint) =
            DemoContentFingerprint.ComputeWithContentHash(new MemoryStream(bytes), library.Reader.WindowBytes);

        BundleBuilder bundle = new(bundleRoot);
        bundle.Demo(sha, "match one.dem", bytes.Length,
            [bundle.Record(BundleBuilder.ParsedRecord(sha, bytes.Length, "de_imported"))], fingerprint,
            bundle.Dem(sha, bytes));
        bundle.Write();
        TransferImporter importer = new(library.Store, library: library.Service);

        ImportPreview preview = await importer.PreviewAsync(bundleRoot, CancellationToken.None);
        TransferSummary summary = await importer.RunAsync(
            new ImportRequest(bundleRoot, ConflictPolicy.KeepMine, new HashSet<string> { sha }, destination, true),
            preview, null, CancellationToken.None);
        string copied = Path.Combine(destination, "match one.dem");

        await library.Service.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => library.Service.Entries.Count == 1
                && library.Service.Entries.All(e => e.State == DemoIndexState.Indexed)
                && library.Store.LocationOf(copied) is not null,
            "the copied demo attached");
        await Task.Delay(150);

        using (Assert.Multiple())
        {
            await Assert.That(summary.Transferred.Select(i => i.Sha256 ?? "")).IsEquivalentTo([sha]);
            await Assert.That(library.Service.Folders).Contains(destination);
            await Assert.That(library.Parses.GetValueOrDefault(copied)).IsEqualTo(0);
            await Assert.That(library.Store.LocationOf(copied)?.ContentId).IsEqualTo(sha);
            await Assert.That(library.Store.TryGetIndex(copied)?.ParseSchema ?? 0).IsGreaterThan(0);
            await Assert.That(library.Store.IsOrphaned(sha)).IsFalse();
            await Assert.That(library.Service.Entries.Single().MapName).IsEqualTo("de_imported");
            await Assert.That(library.Service.Tier2Backlog()).IsEmpty();
        }
    }

    [Test]
    public async Task ACancelDuringTheCopies_StillListsTheDemosThatFinished_AndLeavesNoPartialCopy()
    {
        using TempFolders temp = new();
        string bundleRoot = temp.New();
        string destination = Path.Combine(temp.New(), "Imported demos");
        byte[] small = Bytes(72);
        // Bigger than one copy buffer, so the cancel lands part way through it.
        byte[] large = new byte[(1 << 20) + 4096];
        new Random(73).NextBytes(large);
        using Library library = new(temp.New(), new DemoCacheStore(temp.New()));
        BundleBuilder bundle = new(bundleRoot);
        string smallSha = BundleBuilder.Sha(small);
        string largeSha = BundleBuilder.Sha(large);
        bundle.Demo(smallSha, "first.dem", small.Length, [], dem: bundle.Dem(smallSha, small));
        bundle.Demo(largeSha, "second.dem", large.Length, [], dem: bundle.Dem(largeSha, large));
        bundle.Write();
        TransferImporter importer = new(library.Store, library: library.Service);
        using CancellationTokenSource cts = new();
        CancelOnSecond progress = new(cts, small.Length);

        ImportPreview preview = await importer.PreviewAsync(bundleRoot, CancellationToken.None);
        TransferSummary summary = await importer.RunAsync(
            new ImportRequest(bundleRoot, ConflictPolicy.KeepMine, new HashSet<string> { smallSha, largeSha },
                destination, true), preview, progress, cts.Token);
        string copied = Path.Combine(destination, "first.dem");

        await WaitForAsync(() => library.Service.Entries.Any(e => e.FilePath == copied), "the finished copy listed");

        using (Assert.Multiple())
        {
            await Assert.That(summary.Cancelled).IsTrue();
            await Assert.That(library.Service.Folders).Contains(destination);
            await Assert.That(Directory.EnumerateFiles(destination).Select(f => Path.GetFileName(f)!))
                .IsEquivalentTo(["first.dem"]).Because("a copy cut short never takes its name or stays as a temp");
        }
    }

    // The importer hashed its own copy, so the library lists it as a confirmed location and a second import of
    // the same bundle finds the demo already here instead of copying it again as "name (2).dem".
    [Test]
    public async Task ImportingTheSameBundleTwice_DoesNotCopyTheDemoAgain()
    {
        using TempFolders temp = new();
        string bundleRoot = temp.New();
        string destination = Path.Combine(temp.New(), "Imported demos");
        // Bigger than the fingerprint's two windows, so a fingerprint match leaves part of the file unread.
        byte[] bytes = new byte[(1 << 20) + 4096];
        new Random(74).NextBytes(bytes);
        using Library library = new(temp.New(), new DemoCacheStore(temp.New()));
        (string sha, DemoContentFingerprint fingerprint) =
            DemoContentFingerprint.ComputeWithContentHash(new MemoryStream(bytes), library.Reader.WindowBytes);
        BundleBuilder bundle = new(bundleRoot);
        bundle.Demo(sha, "match one.dem", bytes.Length,
            [bundle.Record(BundleBuilder.ParsedRecord(sha, bytes.Length, "de_imported"))], fingerprint,
            bundle.Dem(sha, bytes));
        bundle.Write();
        TransferImporter importer = new(library.Store, library: library.Service);
        ImportRequest request = new(bundleRoot, ConflictPolicy.KeepMine, new HashSet<string> { sha }, destination,
            true);

        ImportPreview first = await importer.PreviewAsync(bundleRoot, CancellationToken.None);
        await importer.RunAsync(request, first, null, CancellationToken.None);
        string copied = Path.Combine(destination, "match one.dem");
        await library.Service.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => library.Store.LocationOf(copied) is not null, "the copied demo attached");

        ImportPreview second = await importer.PreviewAsync(bundleRoot, CancellationToken.None);
        await importer.RunAsync(request with { DemDestinationIsNewFolder = false }, second, null,
            CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(library.Store.LocationOf(copied)?.Location.Confirmed).IsTrue();
            await Assert.That(second.Entries.Single().DemAlreadyInLibrary).IsTrue();
            await Assert.That(Directory.EnumerateFiles(destination).Select(f => Path.GetFileName(f)!))
                .IsEquivalentTo(["match one.dem"]);
        }
    }

    // The clip was imported before the copy was listed: once the rescan has attached the copy, the clip takes
    // its path through the same lookup the importer and the library's change notice use.
    [Test]
    public async Task AnImportedReviewClip_TakesTheCopiedDemosPath_AfterTheRescan()
    {
        using TempFolders temp = new();
        string bundleRoot = temp.New();
        string destination = Path.Combine(temp.New(), "Imported demos");
        byte[] bytes = new byte[(1 << 20) + 4096];
        new Random(75).NextBytes(bytes);
        DemoCacheStore store = new(temp.New());
        using Library library = new(temp.New(), store);
        (string sha, DemoContentFingerprint fingerprint) =
            DemoContentFingerprint.ComputeWithContentHash(new MemoryStream(bytes), library.Reader.WindowBytes);
        BundleBuilder bundle = new(bundleRoot);
        bundle.Demo(sha, "match one.dem", bytes.Length,
            [bundle.Record(BundleBuilder.ParsedRecord(sha, bytes.Length, "de_imported"))], fingerprint,
            bundle.Dem(sha, bytes));
        bundle.Shared(TransferLayout.ReviewQueueFileName, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new ReviewQueueFile
            {
                Entries = [ReviewEntry.Clip("/source/match.dem", 10, 20, "a retake", ReviewSources.Manual, 64, sha)]
            }, ReviewQueueFile.JsonOptions)));
        bundle.Write();
        ReviewQueue review = new(null, pathOfContent: s => store.TryGetByContentId(s)?.Path);
        TransferImporter importer = new(store, library: library.Service, review: review);

        ImportPreview preview = await importer.PreviewAsync(bundleRoot, CancellationToken.None);
        await importer.RunAsync(
            new ImportRequest(bundleRoot, ConflictPolicy.KeepMine, new HashSet<string> { sha }, destination, true),
            preview, null, CancellationToken.None);
        string copied = Path.Combine(destination, "match one.dem");
        await library.Service.CopiesResolved.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => store.LocationOf(copied) is not null, "the copied demo attached");
        review.ResolvePaths();

        await Assert.That(review.Clips.Single().DemoPath).IsEqualTo(copied);
    }

    // Cancels once the second demo's copy is under way, after the first finished.
    private sealed class CancelOnSecond(CancellationTokenSource cts, long firstBytes) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value)
        {
            if (value is { Current: "second.dem" } && value.BytesDone > firstBytes)
            {
                cts.Cancel();
            }
        }
    }
}
