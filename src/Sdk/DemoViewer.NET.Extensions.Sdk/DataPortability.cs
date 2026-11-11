namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>What an import does with an item both machines hold in different versions.</summary>
[PublicAPI]
public enum ImportConflictPolicy
{
    /// <summary>The copy on this machine stays.</summary>
    KeepMine = 0,

    /// <summary>The imported copy replaces it.</summary>
    TakeImported = 1,

    /// <summary>Both are kept, merged where the data allows it and side by side where it does not.</summary>
    KeepBoth = 2
}

/// <summary>A file in the extension's own folder (<see cref="IExtensionStores.Own" />) that a transfer carries.</summary>
/// <param name="Root">Which of the extension's two folders.</param>
/// <param name="Path">The file, relative to that folder.</param>
[PublicAPI]
public sealed record PortableOwnPath(StoreRoot Root, string Path);

/// <summary>
///     The extension's side of Library Transfer: files of its own folders that travel, and the merge of an import
///     into the stores it declared with <see cref="StoreDescriptor.ExtensionMerges" />. Export is a copy the host
///     makes without calling the extension.
/// </summary>
[PublicAPI]
public interface IExtensionDataPortability
{
    /// <summary>The extension's master switch. While it is off on this machine, an import skips its data.</summary>
    string FeatureId { get; }

    /// <summary>Files of the extension's own folders that a transfer carries. None by default.</summary>
    IReadOnlyList<PortableOwnPath> OwnPaths => [];

    /// <summary>
    ///     Merges the imported copy into the extension's data. Called once per import, off the UI thread, after the
    ///     host's own file merges. It must keep everything the user made on this machine, and a second import of
    ///     the same bundle must change nothing.
    /// </summary>
    /// <param name="import">The bundle's copy and the user's choices.</param>
    /// <param name="ct">Canceled when the user cancels the import.</param>
    Task<ExtensionImportResult> MergeAsync(IExtensionImport import, CancellationToken ct);
}

/// <summary>One import, as <see cref="IExtensionDataPortability.MergeAsync" /> sees it.</summary>
[PublicAPI]
public interface IExtensionImport
{
    /// <summary>What to do where both machines hold the same item in different versions.</summary>
    ImportConflictPolicy Policy { get; }

    /// <summary>The content ids of the demos being imported; per-demo data of any other demo is not in the bundle.</summary>
    IReadOnlySet<string> DemoSha256s { get; }

    /// <summary>
    ///     The bundle's copy of a declared store, laid out as under the store's root, or null when the bundle has
    ///     none. It is read-only: a write throws.
    /// </summary>
    /// <param name="storeId">The <see cref="StoreDescriptor.Id" /> the extension declared.</param>
    IExtensionFolder? Staged(string storeId);

    /// <summary>
    ///     The bundle's copy of the extension's own folder under <paramref name="root" />, holding the
    ///     <see cref="IExtensionDataPortability.OwnPaths" /> it carried, or null when it carried none. Read-only.
    /// </summary>
    /// <param name="root">Which of the extension's two folders.</param>
    IExtensionFolder? StagedOwn(StoreRoot root);

    /// <summary>Adds a line to the import's report, such as an item kept because it was open.</summary>
    void Report(string line);
}

/// <summary>What a merge did, counted in the extension's own items.</summary>
/// <param name="Added">Items new to this machine.</param>
/// <param name="Merged">Items both held, combined or fast-forwarded.</param>
/// <param name="Skipped">Items already here unchanged, or left alone, such as one open in an editor.</param>
/// <param name="Conflicts">Items that differed and were settled by the policy.</param>
[PublicAPI]
public sealed record ExtensionImportResult(int Added, int Merged, int Skipped, int Conflicts);
