namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>Which root a <see cref="StoreDescriptor" />'s paths are under.</summary>
[PublicAPI]
public enum StoreRoot
{
    /// <summary>The user's config folder.</summary>
    Config,

    /// <summary>The cache folder.</summary>
    Cache
}

/// <summary>Files an extension owns.</summary>
/// <param name="Id">Unique within the extension.</param>
/// <param name="Label">What the delete confirmation names.</param>
/// <param name="Root">The root the paths are relative to.</param>
/// <param name="Paths">
///     Files or folders under the root. The store keeps its own copy, so changing the list afterwards changes
///     nothing. A null list counts as no paths and a null entry as an empty path; the host refuses both.
/// </param>
/// <param name="IsUserWork">True for what the user made and cannot rebuild; the confirmation names these.</param>
[PublicAPI]
public sealed record StoreDescriptor(
    string Id,
    string Label,
    StoreRoot Root,
    IReadOnlyList<string> Paths,
    bool IsUserWork)
{
    /// <summary>Files or folders under the root, as copied when the store was made.</summary>
    public IReadOnlyList<string> Paths
    {
        get;
        init => field = Snapshot(value);
    } = Snapshot(Paths);

    // The host checks these paths once and then opens and deletes what they name, so it must not see them
    // change. The argument comes from extension code, which may hand over null whatever the annotation says;
    // turning that into something the host refuses keeps the fault on this one store.
    private static string[] Snapshot(IReadOnlyList<string?>? paths) =>
        paths is null ? [] : [.. paths.Select(p => p ?? "")];
}

/// <summary>What a store holds on disk now.</summary>
/// <param name="Descriptor">The store.</param>
/// <param name="FileCount">Files found.</param>
/// <param name="Bytes">Their total size.</param>
[PublicAPI]
public sealed record StoreInventoryItem(StoreDescriptor Descriptor, int FileCount, long Bytes);

/// <summary>Everything an extension holds on disk.</summary>
/// <param name="Items">One row per store.</param>
public sealed record ExtensionDataInventory(IReadOnlyList<StoreInventoryItem> Items)
{
    /// <summary>Nothing on disk.</summary>
    public static readonly ExtensionDataInventory Empty = new([]);

    /// <summary>Total bytes.</summary>
    public long TotalBytes => Items.Sum(i => i.Bytes);

    /// <summary>The stores holding the user's own work.</summary>
    public IEnumerable<StoreInventoryItem> UserWorkItems => Items.Where(i => i.Descriptor.IsUserWork);
}

/// <summary>What a delete removed.</summary>
/// <param name="Ran">False when it did not run, such as when the user canceled.</param>
/// <param name="Removed">What was deleted.</param>
/// <param name="RecordsUpdated">Shared records (such as cache entries) the delete rewrote.</param>
/// <param name="Skipped">Files it could not delete.</param>
/// <param name="FirstSkippedPath">The first of those, for the message.</param>
[PublicAPI]
public sealed record ExtensionDataRemovalResult(
    bool Ran,
    ExtensionDataInventory Removed,
    int RecordsUpdated,
    int Skipped = 0,
    string? FirstSkippedPath = null)
{
    /// <summary>Did not run.</summary>
    public static readonly ExtensionDataRemovalResult NotRun = new(false, ExtensionDataInventory.Empty, 0);
}

/// <summary>
///     The extension's own "Delete extension data". Without one, the host deletes the extension's folders, its
///     per-demo data and the declared stores.
/// </summary>
[PublicAPI]
public interface IExtensionDataRemoval
{
    /// <summary>The extension's master switch.</summary>
    string FeatureId { get; }

    /// <summary>What a delete would remove.</summary>
    Task<ExtensionDataInventory> InventoryAsync();

    /// <summary>Deletes it.</summary>
    Task<ExtensionDataRemovalResult> DeleteAsync();
}

/// <summary>The count behind Settings' "N demos will be re-indexed" notice.</summary>
[PublicAPI]
public interface IReindexEstimate
{
    /// <summary>The extension's master switch.</summary>
    string FeatureId { get; }

    /// <summary>How many demos switching it on would re-index.</summary>
    Task<int> CountAsync();
}
