#region

using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>
///     What one enabled extension declared for Library Transfer, read from the host's
///     <see cref="HostDataTransfer" /> when an export is planned. The exporter only lists and copies what this
///     names; no extension code runs during the copy.
/// </summary>
/// <param name="ExtensionId">The extension's id, which names its folders in the bundle.</param>
/// <param name="Label">The extension's name, as its master switch shows it.</param>
/// <param name="Stores">Its accepted stores whose portability is not <see cref="StorePortability.None" />.</param>
/// <param name="OwnPaths">The accepted files of its own folders that travel.</param>
public sealed record TransferExtensionSource(
    string ExtensionId,
    string Label,
    IReadOnlyList<StoreDescriptor> Stores,
    IReadOnlyList<PortableOwnPath> OwnPaths)
{
    /// <summary>True when it carries anything beyond per-demo stores: a library-wide store or an own file.</summary>
    public bool HasLibraryData =>
        OwnPaths.Count > 0 || Stores.Any(s => s.Portability == StorePortability.LibraryWide);
}

/// <summary>One file of an extension's store or own folder, found on disk, with what the manifest says of it.</summary>
/// <param name="FullPath">Where it is on this machine.</param>
/// <param name="Size">Its length when it was listed.</param>
/// <param name="ExtensionId">The extension.</param>
/// <param name="StoreId">The store, or null for an own file.</param>
/// <param name="Root">The store's root, or the own folder's.</param>
/// <param name="Portability">Per demo or library-wide.</param>
/// <param name="RelativePath">Relative to the store's root or the own folder, <c>/</c>-separated.</param>
/// <param name="DemoSha256">The demo a per-demo file belongs to.</param>
internal sealed record ExtensionStoreSource(
    string FullPath,
    long Size,
    string ExtensionId,
    string? StoreId,
    StoreRoot Root,
    StorePortability Portability,
    string RelativePath,
    string? DemoSha256)
{
    /// <summary>Its bundle path.</summary>
    /// <exception cref="ArgumentException">A name in it cannot be made portable (an id, say).</exception>
    public string BundlePath() => (StoreId, DemoSha256) switch
    {
        ({ } store, { } sha) => TransferLayout.ExtensionStoreFile(sha, ExtensionId, store, RelativePath),
        ({ } store, null) => TransferLayout.LibraryStoreFile(ExtensionId, store, RelativePath),
        _ => TransferLayout.LibraryOwnFile(ExtensionId, Root, RelativePath)
    };

    /// <summary>The manifest entry for its copy.</summary>
    public TransferExtensionFile Entry(TransferFile copied) =>
        new(copied.Path, copied.Size, copied.Sha256, ExtensionId, StoreId, Root, Portability, RelativePath,
            DemoSha256);
}

/// <summary>
///     Lists the files of declared stores and own folders under the config and cache roots. Read only: it stats
///     and enumerates, never opens a file for writing and never runs extension code. Links and junctions are
///     neither followed nor copied, and a store's <see cref="StoreDescriptor.PortableExcludes" /> are skipped
///     wherever they appear under it.
/// </summary>
/// <param name="configRoot">The config root, or null when there is none.</param>
/// <param name="cacheRoot">The cache root, or null when there is none.</param>
internal sealed class ExtensionStoreFiles(string? configRoot, string? cacheRoot)
{
    /// <summary>The demo-keyed files of <paramref name="source" />'s per-demo stores for one demo.</summary>
    public List<ExtensionStoreSource> PerDemo(TransferExtensionSource source, string sha256)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<ExtensionStoreSource> files = [];
        foreach (StoreDescriptor store in source.Stores.Where(s => s.Portability == StorePortability.PerDemo))
        {
            if (RootOf(store.Root) is not { } root)
            {
                continue;
            }

            foreach (string template in store.DemoKeyedPaths)
            {
                string relative = template.Replace('\\', '/').Trim()
                    .Replace(PackContributions.Sha256Placeholder, sha256, StringComparison.Ordinal);
                if (!Excluded(relative, store.PortableExcludes)
                    && PathSafety.FullPathInside(root, relative) is { } full && PlainFile(full) is { } size)
                {
                    files.Add(new ExtensionStoreSource(full, size, source.ExtensionId, store.Id, store.Root,
                        StorePortability.PerDemo, relative, sha256));
                }
            }
        }

        return files;
    }

    /// <summary>Every file of <paramref name="source" />'s library-wide stores, then of its own paths.</summary>
    public List<ExtensionStoreSource> LibraryWide(TransferExtensionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<ExtensionStoreSource> files = [];
        foreach (StoreDescriptor store in source.Stores.Where(s => s.Portability == StorePortability.LibraryWide))
        {
            if (RootOf(store.Root) is not { } root)
            {
                continue;
            }

            HashSet<string> seen = new(PathSafety.CaseInsensitivePaths);
            foreach (string path in store.Paths)
            {
                foreach ((string full, long size) in Expand(root, path, store.PortableExcludes))
                {
                    string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                    if (seen.Add(relative) && !Excluded(relative, store.PortableExcludes))
                    {
                        files.Add(new ExtensionStoreSource(full, size, source.ExtensionId, store.Id, store.Root,
                            StorePortability.LibraryWide, relative, null));
                    }
                }
            }
        }

        HashSet<string> seenOwn = new(PathSafety.CaseInsensitivePaths);
        foreach (PortableOwnPath own in source.OwnPaths)
        {
            if (OwnFolder(source.ExtensionId, own.Root) is not { } folder)
            {
                continue;
            }

            foreach ((string full, long size) in Expand(folder, own.Path.Trim(), []))
            {
                string relative = Path.GetRelativePath(folder, full).Replace('\\', '/');
                if (seenOwn.Add(TransferLayout.RootName(own.Root) + "/" + relative))
                {
                    files.Add(new ExtensionStoreSource(full, size, source.ExtensionId, null, own.Root,
                        StorePortability.LibraryWide, relative, null));
                }
            }
        }

        return files;
    }

    private string? RootOf(StoreRoot root) => root switch
    {
        StoreRoot.Config => configRoot,
        StoreRoot.Cache => cacheRoot,
        _ => null
    };

    // The folders IExtensionStores.Own hands out, worked out here rather than opened through it: opening the
    // cache side moves an older build's files, and an export writes nothing at the source.
    private string? OwnFolder(string extensionId, StoreRoot root)
    {
        if (!ExtensionFolders.IsSafeName(extensionId) || RootOf(root) is not { } rootPath)
        {
            return null;
        }

        string own = Path.Combine(rootPath, ExtensionFolders.DataDirectoryName, extensionId);
        return root == StoreRoot.Cache ? Path.Combine(own, ExtensionFolders.OwnCacheDirectoryName) : own;
    }

    // The files a declared path names: the file itself, every file under a folder, or the matches of a
    // pattern in its last segment.
    private static List<(string FullPath, long Size)> Expand(string root, string path,
        IReadOnlyList<string> excludes)
    {
        List<(string, long)> files = [];
        string normalized = path.Replace('\\', '/').Trim().TrimEnd('/');
        int slash = normalized.LastIndexOf('/');
        string last = normalized[(slash + 1)..];
        try
        {
            if (last.Contains('*', StringComparison.Ordinal) || last.Contains('?', StringComparison.Ordinal))
            {
                string? folder = slash < 0
                    ? Path.GetFullPath(root)
                    : PathSafety.FullPathInside(root, normalized[..slash]);
                if (folder is not null && PlainFolder(folder))
                {
                    foreach (string file in Directory.EnumerateFiles(folder, last).Order(StringComparer.Ordinal))
                    {
                        if (PlainFile(file) is { } size)
                        {
                            files.Add((file, size));
                        }
                    }
                }

                return files;
            }

            if (PathSafety.FullPathInside(root, normalized) is not { } full)
            {
                return files;
            }

            if (PlainFile(full) is { } length)
            {
                files.Add((full, length));
            }
            else if (PlainFolder(full))
            {
                Walk(full, excludes, files);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that went away or cannot be read while it was listed carries what was found so far.
        }

        return files;
    }

    private static void Walk(string folder, IReadOnlyList<string> excludes, List<(string, long)> files)
    {
        foreach (string file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
        {
            if (!IsExcludedName(Path.GetFileName(file), excludes) && PlainFile(file) is { } size)
            {
                files.Add((file, size));
            }
        }

        foreach (string sub in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
        {
            if (!IsExcludedName(Path.GetFileName(sub), excludes) && PlainFolder(sub))
            {
                Walk(sub, excludes, files);
            }
        }
    }

    // A regular file's length; null for a missing file, a link, or a temp file an atomic write left behind.
    private static long? PlainFile(string path)
    {
        FileInfo info = new(path);
        string name = info.Name;
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0
                           && !(name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            ? info.Length
            : null;
    }

    private static bool PlainFolder(string path)
    {
        DirectoryInfo info = new(path);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static bool Excluded(string relative, IReadOnlyList<string> excludes) =>
        excludes.Count > 0 && relative.Split('/').Any(segment => IsExcludedName(segment, excludes));

    private static bool IsExcludedName(string name, IReadOnlyList<string> excludes) =>
        excludes.Any(e => string.Equals(e.Trim(), name, StringComparison.OrdinalIgnoreCase));
}
