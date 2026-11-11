#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>
///     Where everything sits inside a transfer bundle folder. Every path the bundle holds is ASCII built from a
///     content hash, digits and the fixed names here, so it is valid on every file system the bundle may travel
///     on; original file names live only in the manifest. Relative paths always use <c>/</c>, as the manifest
///     stores them.
///     <code>
///     DemoViewer Transfer 2026-11-11 1530/
///       transfer.json                              written last; its presence marks a complete bundle
///       data/&lt;sha&gt;/cache/&lt;file&gt;                    core record and siblings
///       data/&lt;sha&gt;/ext/&lt;extId&gt;/&lt;facet&gt;/&lt;file&gt;      extension per-demo data
///       data/&lt;sha&gt;/ext/&lt;extId&gt;/stores/&lt;relpath&gt;   declared per-demo user stores
///       data/&lt;sha&gt;/annotations.dvann.json
///       shared/graph-breakpoints.json, shared/review-queue.json
///       dem/&lt;sha&gt;.dem                             optional
///     </code>
/// </summary>
public static class TransferLayout
{
    /// <summary>The manifest at the bundle root.</summary>
    public const string ManifestFileName = "transfer.json";

    /// <summary>The folder holding one subfolder per demo, named by its content hash.</summary>
    public const string DataDirectory = "data";

    /// <summary>Under a demo's folder: the core cache record and its siblings.</summary>
    public const string CacheDirectory = "cache";

    /// <summary>Under a demo's folder: one subfolder per extension id.</summary>
    public const string ExtensionDirectory = "ext";

    /// <summary>Under an extension's folder: its declared per-demo user stores, beside its facet folders.</summary>
    public const string StoresDirectory = "stores";

    /// <summary>A demo's annotations file.</summary>
    public const string AnnotationsFileName = "annotations.dvann.json";

    /// <summary>The folder for files that hold entries of many demos, filtered to the exported ones.</summary>
    public const string SharedDirectory = "shared";

    /// <summary>The graph breakpoints of the exported demos.</summary>
    public const string GraphBreakpointsFileName = "graph-breakpoints.json";

    /// <summary>The review-queue entries of the exported demos.</summary>
    public const string ReviewQueueFileName = "review-queue.json";

    /// <summary>The folder holding the optional <c>.dem</c> copies.</summary>
    public const string DemDirectory = "dem";

    /// <summary>The start of every bundle folder's name.</summary>
    public const string BundleNamePrefix = "DemoViewer Transfer ";

    /// <summary>The bundle folder name for a local time, e.g. <c>DemoViewer Transfer 2026-11-11 1530</c>.</summary>
    public static string BundleFolderName(DateTime localTime) =>
        BundleNamePrefix + localTime.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture);

    /// <summary>
    ///     A bundle folder under <paramref name="parent" /> that does not exist yet: <see cref="BundleFolderName" />,
    ///     then with <c> (2)</c>, <c> (3)</c>… when two exports start in the same minute. Pure: creates nothing.
    /// </summary>
    public static string NewBundleFolder(string parent, DateTime localTime)
    {
        ArgumentException.ThrowIfNullOrEmpty(parent);
        string name = BundleFolderName(localTime);
        string candidate = Path.Combine(parent, name);
        for (int n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
        {
            candidate = Path.Combine(parent, $"{name} ({n.ToString(CultureInfo.InvariantCulture)})");
        }

        return candidate;
    }

    /// <summary>True for a lowercase-hex SHA-256, the only form a demo folder may be named by.</summary>
    public static bool IsSha256(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     True for a name that may be one segment of a bundle path: 1 to 100 characters of ASCII letters,
    ///     digits, <c>.</c>, <c>_</c> and <c>-</c>, not <c>.</c> or <c>..</c>.
    /// </summary>
    public static bool IsPortableSegment(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 100 || value is "." or "..")
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>data/&lt;sha&gt;</c>.</summary>
    public static string DemoFolder(string sha256) => DataDirectory + "/" + Sha(sha256);

    /// <summary><c>data/&lt;sha&gt;/cache/&lt;fileName&gt;</c>, for the record and its siblings.</summary>
    public static string CacheFile(string sha256, string fileName) =>
        DemoFolder(sha256) + "/" + CacheDirectory + "/" + Segment(fileName);

    /// <summary><c>data/&lt;sha&gt;/ext/&lt;extensionId&gt;/&lt;facet&gt;/&lt;fileName&gt;</c>.</summary>
    public static string ExtensionFacetFile(string sha256, string extensionId, string facet, string fileName) =>
        ExtensionFolder(sha256, extensionId) + "/" + Segment(facet) + "/" + Segment(fileName);

    /// <summary>
    ///     <c>data/&lt;sha&gt;/ext/&lt;extensionId&gt;/stores/&lt;relativePath&gt;</c>. Every segment of
    ///     <paramref name="relativePath" /> (<c>/</c> or <c>\</c> separated) must be portable.
    /// </summary>
    public static string ExtensionStoreFile(string sha256, string extensionId, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        string[] segments = relativePath.Split('/', '\\');
        foreach (string segment in segments)
        {
            Segment(segment);
        }

        return ExtensionFolder(sha256, extensionId) + "/" + StoresDirectory + "/" + string.Join('/', segments);
    }

    /// <summary><c>data/&lt;sha&gt;/annotations.dvann.json</c>.</summary>
    public static string AnnotationsFile(string sha256) => DemoFolder(sha256) + "/" + AnnotationsFileName;

    /// <summary><c>shared/&lt;fileName&gt;</c>.</summary>
    public static string SharedFile(string fileName) => SharedDirectory + "/" + Segment(fileName);

    /// <summary><c>dem/&lt;sha&gt;.dem</c>.</summary>
    public static string DemFile(string sha256) => DemDirectory + "/" + Sha(sha256) + ".dem";

    /// <summary>
    ///     The full path of a bundle-relative path under <paramref name="bundleRoot" />, checked by
    ///     <see cref="PathSafety.FullPathInside" />.
    /// </summary>
    /// <exception cref="ArgumentException">The path is unsafe or resolves outside the bundle.</exception>
    public static string FullPath(string bundleRoot, string relativePath) =>
        PathSafety.FullPathInside(bundleRoot, relativePath)
        ?? throw new ArgumentException($"'{relativePath}' is not a safe path inside the bundle.",
            nameof(relativePath));

    private static string ExtensionFolder(string sha256, string extensionId) =>
        DemoFolder(sha256) + "/" + ExtensionDirectory + "/" + Segment(extensionId);

    private static string Sha(string sha256) =>
        IsSha256(sha256)
            ? sha256
            : throw new ArgumentException("A demo folder is named by a lowercase-hex SHA-256.", nameof(sha256));

    private static string Segment(string value) =>
        IsPortableSegment(value)
            ? value
            : throw new ArgumentException($"'{value}' is not a portable bundle path segment.", nameof(value));
}
