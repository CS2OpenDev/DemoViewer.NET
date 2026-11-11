#region

using DemoViewer.NET.Modules.Library;
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
}
