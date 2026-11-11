namespace DemoViewer.NET.Services.Transfer;

/// <summary>What import does with user-authored data (annotations, breakpoints) that exists on both sides.</summary>
public enum ConflictPolicy
{
    /// <summary>Keep this machine's copy and skip the imported one. The default.</summary>
    KeepMine = 0,

    /// <summary>Replace this machine's copy with the imported one.</summary>
    TakeImported,

    /// <summary>
    ///     Keep both where the store can hold two (e.g. merged entries). Where it cannot, falls back to
    ///     <see cref="KeepMine" /> and says so in the summary.
    /// </summary>
    KeepBoth
}

/// <summary>How a bundle demo relates to this machine.</summary>
public enum ImportEntryStatus
{
    /// <summary>This machine has no data for it.</summary>
    New,

    /// <summary>This machine already has data for the same content; derived data merges, keeping the current side.</summary>
    AlreadyHere,

    /// <summary>Both sides hold user-authored data for it; the <see cref="ConflictPolicy" /> decides.</summary>
    UserDataConflict
}

/// <summary>One bundle demo in an <see cref="ImportPreview" />.</summary>
/// <param name="Demo">Its manifest entry.</param>
/// <param name="Status">How it relates to this machine.</param>
/// <param name="HasDem">The bundle carries its <c>.dem</c>.</param>
/// <param name="DemAlreadyInLibrary">The library already has a confirmed copy of the <c>.dem</c>, so it is never copied.</param>
/// <param name="Stale">Its data was computed under rulesets or extension versions that differ from this machine's.</param>
public sealed record ImportPreviewEntry(
    TransferDemo Demo,
    ImportEntryStatus Status,
    bool HasDem,
    bool DemAlreadyInLibrary,
    bool Stale)
{
    /// <summary>The demo's content hash.</summary>
    public string Sha256 => Demo.Sha256;
}

/// <summary>A validated bundle and what importing it would do. Previewing writes nothing.</summary>
/// <param name="BundleFolder">The bundle's folder.</param>
/// <param name="Manifest">Its validated manifest; the version already matched.</param>
/// <param name="Entries">One per bundle demo, in manifest order.</param>
/// <param name="DataBytes">The bytes of data files to copy.</param>
/// <param name="DemBytes">The bytes of every <c>.dem</c> the bundle carries that the library does not have.</param>
/// <param name="Warnings">Findings for the preview step: staleness, and anything that blocks.</param>
public sealed record ImportPreview(
    string BundleFolder,
    TransferManifest Manifest,
    IReadOnlyList<ImportPreviewEntry> Entries,
    long DataBytes,
    long DemBytes,
    IReadOnlyList<TransferWarning> Warnings)
{
    /// <summary>Some demos' data was computed under different rules or extensions and will be recomputed.</summary>
    public bool AnyStale => Entries.Any(e => e.Stale);

    /// <summary>The bundle carries <c>.dem</c> files the library does not have, so the demo-files step is shown.</summary>
    public bool OffersDemFiles => Entries.Any(e => e.HasDem && !e.DemAlreadyInLibrary);

    /// <summary>No warning blocks the import.</summary>
    public bool CanRun => Warnings.All(w => !w.Blocking);
}

/// <summary>What the user chose in the import wizard.</summary>
/// <param name="BundleFolder">The bundle's folder.</param>
/// <param name="Policy">What happens to user data present on both sides.</param>
/// <param name="DemFilesToCopy">The content hashes whose <c>.dem</c> is copied; empty copies none.</param>
/// <param name="DemDestination">
///     The folder the <c>.dem</c> files go to: an existing library root, or a new folder that import adds to
///     the library. Null when <paramref name="DemFilesToCopy" /> is empty.
/// </param>
/// <param name="DemDestinationIsNewFolder">True when <paramref name="DemDestination" /> is to be added as a library folder.</param>
public sealed record ImportRequest(
    string BundleFolder,
    ConflictPolicy Policy,
    IReadOnlySet<string> DemFilesToCopy,
    string? DemDestination,
    bool DemDestinationIsNewFolder)
{
    /// <summary>Data only, no <c>.dem</c> files.</summary>
    public static ImportRequest DataOnly(string bundleFolder, ConflictPolicy policy = ConflictPolicy.KeepMine) =>
        new(bundleFolder, policy, new HashSet<string>(StringComparer.Ordinal), null, false);
}

/// <summary>Imports a transfer bundle into this machine's cache and stores.</summary>
public interface ITransferImporter
{
    /// <summary>
    ///     Reads and validates the bundle's manifest, checks the version, and classifies every demo against this
    ///     machine. Writes nothing.
    /// </summary>
    /// <exception cref="TransferManifestException">The bundle is incomplete, damaged, unsafe or from another version.</exception>
    Task<ImportPreview> PreviewAsync(string bundleFolder, CancellationToken ct);

    /// <summary>
    ///     Imports per <paramref name="request" />: cache rows first (saved through the store, so the orphan sweep
    ///     keeps them), then extension data and user data, then <c>.dem</c> files and a library rescan.
    /// </summary>
    Task<TransferSummary> RunAsync(ImportRequest request, ImportPreview preview,
        IProgress<TransferProgress>? progress, CancellationToken ct);
}
