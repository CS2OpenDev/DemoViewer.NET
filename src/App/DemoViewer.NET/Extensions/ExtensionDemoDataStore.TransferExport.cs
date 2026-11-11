#region

using System.Text.Json;
using DemoViewer.NET.ViewModels.Diagnostics;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>One file of an extension's per-demo data, as <see cref="ExtensionDemoDataStore.FilesFor(string)" /> lists it.</summary>
/// <param name="Facet">The facet folder it sits in.</param>
/// <param name="Rest">The name after the content hash: <c>.json.gz</c> for the main file, <c>.&lt;part&gt;.json.gz</c> for a part.</param>
/// <param name="FullPath">Where it is now.</param>
/// <param name="Size">Its length when it was listed.</param>
internal sealed record DemoDataFile(string Facet, string Rest, string FullPath, long Size)
{
    /// <summary>The facet's main file, whose header names the writer.</summary>
    public bool IsMain => string.Equals(Rest, ".json.gz", StringComparison.Ordinal);
}

// Read-only access for a library export. These read the folder, not the index: loading the index sweeps and
// saves it, and a lookup by path moves files, and an export may do neither.
internal sealed partial class ExtensionDemoDataStore
{
    /// <summary>The files this store holds under the content hash <paramref name="sha256" />, in every facet.</summary>
    /// <param name="sha256">The demo's content hash.</param>
    public IReadOnlyList<DemoDataFile> FilesFor(string sha256) => FilesFor(Root, sha256);

    /// <summary>
    ///     The files the store rooted at <paramref name="root" /> (an extension's <see cref="DirectoryName" />
    ///     folder) holds under <paramref name="sha256" />. Files still keyed by a path hash are not the
    ///     content's until the store moves them, so they are left out.
    /// </summary>
    /// <param name="root">The store's folder.</param>
    /// <param name="sha256">The demo's content hash.</param>
    public static IReadOnlyList<DemoDataFile> FilesFor(string root, string sha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(sha256);
        List<DemoDataFile> files = [];
        if (!ExtensionFolders.IsSafeName(sha256) || !Directory.Exists(root))
        {
            return files;
        }

        try
        {
            foreach (string folder in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                string facet = Path.GetFileName(folder);
                if (!ExtensionFolders.IsSafeName(facet))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(folder, sha256 + "*" + Suffix)
                             .Order(StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(file);
                    if (name != sha256 + Suffix && !name.StartsWith(sha256 + ".", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        files.Add(new DemoDataFile(facet, name[sha256.Length..], file, new FileInfo(file).Length));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Deleted between the listing and the stat.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.ExtensionStoreUnreadable(Log, Path.GetFileName(Path.GetDirectoryName(root)) ?? root, root,
                ex.Message);
        }

        return files;
    }

    /// <summary>The extension version a data file's header records, or null when it cannot be read.</summary>
    /// <param name="file">A facet's main file.</param>
    public static string? WriterVersionOf(string file)
    {
        try
        {
            return ReadHeader(file).ExtensionVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or JsonException)
        {
            return null;
        }
    }
}
