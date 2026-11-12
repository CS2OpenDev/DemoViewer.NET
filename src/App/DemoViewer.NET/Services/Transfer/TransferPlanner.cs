#region

using System.Globalization;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>
///     Where an export reads from besides the cache store: the config root (annotations, breakpoints, the review
///     queue) and the library folders a bundle may not be written into.
/// </summary>
/// <param name="ConfigRoot">The app's config root, or null when there is none.</param>
/// <param name="LibraryRoots">The library's folders, read when a plan is made.</param>
/// <param name="Environment">
///     This machine's rules and extensions, recorded in the manifest in the shape import compares against; null
///     records none, so import has nothing to call stale.
/// </param>
public sealed record TransferSources(
    string? ConfigRoot,
    Func<IReadOnlyList<string>> LibraryRoots,
    Func<TransferEnvironment>? Environment = null)
{
    /// <summary>Breakpoints, as <see cref="AppPaths.GraphBreakpointsFile" /> names them under the config root.</summary>
    // Not read through AppPaths: its resolve migrates legacy files, and an export writes nothing at the source.
    internal const string GraphBreakpointsFileName = "GraphBreakpoints.v2.json";

    /// <summary>The folder AppData annotations sit in under the config root.</summary>
    internal const string AnnotationsDirectory = "annotations";
}

/// <summary>
///     Turns an <see cref="ExportRequest" /> into plan entries from a snapshot of the cache's rows. Reads the
///     cache through its read-only APIs and stats files; writes nothing and hashes nothing.
/// </summary>
internal sealed class TransferPlanner(
    DemoCacheStore cache,
    IDemoProcessingQueue? queue,
    TransferSources sources,
    Func<string, long?>? demLength = null)
{
    private readonly Func<string, long?> _demLength = demLength ?? FileLength;

    /// <summary>The cache root the store reads, or null for an in-memory store.</summary>
    public string? CacheRoot => cache.CacheRoot;

    /// <summary>Every extension's per-demo data folder, with the extension's id.</summary>
    public IReadOnlyList<(string ExtensionId, string Root)> ExtensionRoots()
    {
        if (CacheRoot is not { } cacheRoot)
        {
            return [];
        }

        string parent = Path.Combine(cacheRoot, ExtensionFolders.DataDirectoryName);
        try
        {
            return Directory.Exists(parent)
                ?
                [
                    .. Directory.EnumerateDirectories(parent).Order(StringComparer.Ordinal)
                        .Select(dir => (Path.GetFileName(dir), Path.Combine(dir, ExtensionDemoDataStore.DirectoryName)))
                        .Where(e => Directory.Exists(e.Item2))
                ]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The listed, hashed row for <paramref name="sha256" /> as it is now, or null.</summary>
    public DemoCacheIndexEntry? RowOf(string sha256) =>
        cache.Contents.FirstOrDefault(r => string.Equals(r.Sha256, sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>The paths of a demo's content, confirmed first.</summary>
    public IReadOnlyList<string> PathsOf(string sha256) => cache.PathsOfContent(sha256);

    /// <summary>
    ///     The first path of <paramref name="sha256" /> whose file has the row's size: the one to copy the
    ///     <c>.dem</c> from. Null when none does.
    /// </summary>
    public string? DemSource(string sha256, long size) =>
        PathsOf(sha256).FirstOrDefault(p => _demLength(p) == size);

    /// <summary>The annotations file for a demo: beside one of its paths, else under the config root. Null when there is none.</summary>
    public string? AnnotationsOf(string sha256, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            string beside = path + ".dvann.json";
            if (File.Exists(beside))
            {
                return beside;
            }
        }

        if (sources.ConfigRoot is not { } root)
        {
            return null;
        }

        string appData = Path.Combine(root, TransferSources.AnnotationsDirectory, sha256 + ".dvann.json");
        return File.Exists(appData) ? appData : null;
    }

    /// <summary>The folders a bundle may not be written into.</summary>
    public IReadOnlyList<string?> ProtectedRoots() =>
        [sources.ConfigRoot, CacheRoot, .. sources.LibraryRoots()];

    /// <summary>The shared files the export may filter into the bundle, with their source path.</summary>
    public IReadOnlyList<(string BundleName, string Source)> SharedSources() =>
        sources.ConfigRoot is { } root
            ?
            [
                (TransferLayout.GraphBreakpointsFileName, Path.Combine(root, TransferSources.GraphBreakpointsFileName)),
                (TransferLayout.ReviewQueueFileName, Path.Combine(root, Review.ReviewQueue.FileName))
            ]
            : [];

    /// <summary>The entries and warnings for <paramref name="request" />, in library order.</summary>
    public (List<ExportPlanEntry> Entries, List<TransferWarning> Warnings, long SharedBytes) Plan(
        ExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<DemoCacheIndexEntry> contents = cache.Contents;
        HashSet<string> inFlight = InFlightPaths();
        IReadOnlyList<(string ExtensionId, string Root)> extensions = ExtensionRoots();

        List<ExportPlanEntry> entries = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((DemoCacheIndexEntry? row, string key) in Select(request, contents))
        {
            ct.ThrowIfCancellationRequested();
            if (row is null)
            {
                if (seen.Add(key))
                {
                    entries.Add(new ExportPlanEntry(TransferLayout.IsSha256(key) ? key : null,
                        TransferLayout.IsSha256(key) ? null : key, DisplayName(key), null, ExportEntryStatus.Missing,
                        0, 0));
                }

                continue;
            }

            if (!seen.Add(row.Sha256 is { Length: > 0 } s ? s : row.Path))
            {
                continue;
            }

            entries.Add(row.Sha256 is { Length: > 0 }
                ? Hashed(row, request, inFlight, extensions)
                : new ExportPlanEntry(null, row.Path, DisplayName(row.Path), row.Map, ExportEntryStatus.NotHashed, 0,
                    request.IncludeDemFiles ? _demLength(row.Path) ?? 0 : 0));
        }

        long sharedBytes = request.IncludeUserData ? SharedSources().Sum(s => FileLength(s.Source) ?? 0) : 0;
        return (entries, Warnings(entries, request), sharedBytes);
    }

    private ExportPlanEntry Hashed(DemoCacheIndexEntry row, ExportRequest request, HashSet<string> inFlight,
        IReadOnlyList<(string ExtensionId, string Root)> extensions)
    {
        string sha = row.Sha256!.ToLowerInvariant();
        IReadOnlyList<DemoCacheFile> files = cache.SnapshotFiles(sha);
        if (!files.Any(f => f.IsRecord))
        {
            return new ExportPlanEntry(sha, row.Path, DisplayName(row.Path), row.Map, ExportEntryStatus.Missing, 0, 0);
        }

        IReadOnlyList<string> paths = PathsOf(sha);
        long data = files.Sum(f => f.Size)
                    + extensions.Sum(e => ExtensionDemoDataStore.FilesFor(e.Root, sha).Sum(f => f.Size));
        if (request.IncludeUserData && AnnotationsOf(sha, paths) is { } annotations)
        {
            data += FileLength(annotations) ?? 0;
        }

        long dem = request.IncludeDemFiles && DemSource(sha, row.Size) is not null ? row.Size : 0;
        bool busy = paths.Append(row.Path).Any(inFlight.Contains);
        return new ExportPlanEntry(sha, row.Path, DisplayName(row.Path), row.Map,
            busy ? ExportEntryStatus.InFlight : ExportEntryStatus.Included, data, dem);
    }

    // Every listed row, or the chosen ones: a hash names the content's row, a path the row listing it.
    private IEnumerable<(DemoCacheIndexEntry? Row, string Key)> Select(ExportRequest request,
        IReadOnlyList<DemoCacheIndexEntry> contents)
    {
        if (request.AllDemos)
        {
            foreach (DemoCacheIndexEntry row in contents.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
            {
                yield return (row, row.Path);
            }

            yield break;
        }

        Dictionary<string, DemoCacheIndexEntry> bySha = new(StringComparer.OrdinalIgnoreCase);
        foreach (DemoCacheIndexEntry row in contents)
        {
            if (row.Sha256 is { Length: > 0 } sha)
            {
                bySha.TryAdd(sha, row);
            }
        }

        foreach (string key in request.DemoKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (TransferLayout.IsSha256(key))
            {
                yield return (bySha.GetValueOrDefault(key), key);
                continue;
            }

            DemoCacheIndexEntry? listed = cache.TryGetIndex(key);
            yield return listed?.Sha256 is { Length: > 0 } sha ? (bySha.GetValueOrDefault(sha), key) : (listed, key);
        }
    }

    /// <summary>
    ///     True when the queue holds any of <paramref name="paths" /> now, waiting or running. The run asks again
    ///     as it reaches each demo: one queued after the plan was made is just as likely to change mid-copy.
    /// </summary>
    public bool IsInFlight(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        HashSet<string> inFlight = InFlightPaths();
        return inFlight.Count > 0 && paths.Any(inFlight.Contains);
    }

    // Demos the queue holds, waiting or running: their data may change while the export copies it.
    private HashSet<string> InFlightPaths()
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        if (queue is null)
        {
            return paths;
        }

        foreach (DemoQueueItemSnapshot item in queue.Snapshot())
        {
            if (item.State is DemoQueueItemState.Queued or DemoQueueItemState.Running
                && !string.IsNullOrEmpty(item.Path) && item.Kind != QueueJobKind.DataTransfer)
            {
                paths.Add(item.Path);
            }
        }

        return paths;
    }

    private static List<TransferWarning> Warnings(List<ExportPlanEntry> entries, ExportRequest request)
    {
        List<TransferWarning> warnings = [];
        int inFlight = entries.Count(e => e.Status == ExportEntryStatus.InFlight);
        if (inFlight > 0)
        {
            warnings.Add(new TransferWarning(TransferWarningKind.DemosInFlight,
                $"{Count(inFlight)} being processed right now, so {(inFlight == 1 ? "it" : "they")} may not be "
                + "included in full. Each is exported as it stands when the export reaches it."));
        }

        int notHashed = entries.Count(e => e.Status == ExportEntryStatus.NotHashed);
        if (notHashed > 0)
        {
            warnings.Add(new TransferWarning(TransferWarningKind.NotHashed,
                $"{Count(notHashed)} not analysed yet, so no data is exported for "
                + $"{(notHashed == 1 ? "it" : "them")}"
                + (request.IncludeDemFiles ? "; the .dem files are still copied." : ".")));
        }

        foreach (ExportPlanEntry missing in entries.Where(e => e.Status == ExportEntryStatus.Missing))
        {
            warnings.Add(new TransferWarning(TransferWarningKind.Missing,
                $"{missing.DisplayName} is no longer in the library or has no analysis, so it is left out.",
                Sha256: missing.Sha256));
        }

        if (request.IncludeDemFiles)
        {
            foreach (ExportPlanEntry entry in entries.Where(e => e is { ExportsData: true, DemBytes: 0 }))
            {
                warnings.Add(new TransferWarning(TransferWarningKind.Other,
                    $"The .dem file of {entry.DisplayName} is not where the library last saw it, so only its data "
                    + "is exported.", Sha256: entry.Sha256));
            }
        }

        return warnings;
    }

    private static string Count(int n) =>
        n == 1 ? "1 demo is" : n.ToString(CultureInfo.InvariantCulture) + " demos are";

    private static string DisplayName(string pathOrKey) =>
        TransferLayout.IsSha256(pathOrKey) ? pathOrKey[..12] : Path.GetFileName(pathOrKey);

    private static long? FileLength(string path)
    {
        try
        {
            FileInfo info = new(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return null;
        }
    }
}
