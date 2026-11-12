#region

using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>
///     Writes library data to a transfer bundle folder. The source is only read: the cache through
///     <see cref="DemoCacheStore.SnapshotFiles(string)" /> and its row snapshots, everything else as plain file
///     reads. Nothing is written outside the bundle folder but the target check's probe beside it.
///     <para>
///         Every file goes to a temp name in the bundle and is renamed when it is whole, hashed as it streams;
///         one the bundle already holds with the same size and hash is left alone, so exporting into the same
///         bundle again copies only what changed. <c>transfer.json</c> is written last, so a cancelled or failed
///         run leaves no manifest.
///     </para>
/// </summary>
public sealed class TransferExporter : ITransferExporter
{
    private const int CopyBufferSize = 1 << 20;

    // The owner writes the breakpoint file in place, so a read can land mid-write; one retry after a pause.
    private static readonly TimeSpan _sharedRetryDelay = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    private readonly DemoCacheStore _cache;
    private readonly TransferPlanner _planner;
    private readonly TransferSources _sources;
    private readonly IDemoProcessingQueue? _queue;
    private readonly TransferTarget _target;
    private readonly TimeProvider _time;

    /// <param name="cache">The cache store, read only.</param>
    /// <param name="queue">The processing queue: the export runs as its item, and its demos count as in flight. Null runs inline.</param>
    /// <param name="sources">The config root and library folders.</param>
    /// <param name="target">The target checks; null for the real drives.</param>
    /// <param name="time">The clock the bundle is named and stamped by.</param>
    public TransferExporter(DemoCacheStore cache, IDemoProcessingQueue? queue, TransferSources sources,
        TransferTarget? target = null, TimeProvider? time = null)
        : this(cache, queue, sources, target, time, null)
    {
    }

    /// <summary>The public constructor, plus <paramref name="demLength" />.</summary>
    /// <param name="cache">The cache store, read only.</param>
    /// <param name="queue">The processing queue, or null.</param>
    /// <param name="sources">The config root and library folders.</param>
    /// <param name="target">The target checks; null for the real drives.</param>
    /// <param name="time">The clock.</param>
    /// <param name="demLength">Reads a <c>.dem</c>'s length; a test seam for files too large to make.</param>
    internal TransferExporter(DemoCacheStore cache, IDemoProcessingQueue? queue, TransferSources sources,
        TransferTarget? target, TimeProvider? time, Func<string, long?>? demLength)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(sources);
        _cache = cache;
        _queue = queue;
        _target = target ?? new TransferTarget();
        _time = time ?? TimeProvider.System;
        _sources = sources;
        _planner = new TransferPlanner(cache, queue, sources, demLength);
    }

    /// <inheritdoc />
    public Task<ExportPlan> PlanAsync(ExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.DestinationFolder);
        return Task.Run(() => Plan(request, ct), ct);
    }

    private ExportPlan Plan(ExportRequest request, CancellationToken ct)
    {
        IReadOnlyList<TransferExtensionSource> extensions = _planner.ExtensionSources();
        (List<ExportPlanEntry> entries, List<TransferWarning> warnings, long sharedBytes, long libraryBytes) =
            _planner.Plan(request, extensions, ct);
        string bundle = BundleFolderFor(request.DestinationFolder);
        ExportPlan plan = new(request, bundle, entries, sharedBytes, warnings)
        {
            LibraryBytes = libraryBytes, ExtensionSources = extensions
        };
        TransferTargetCheck check = _target.Check(request.DestinationFolder, plan.TotalBytes, plan.LargestFileBytes,
            _planner.ProtectedRoots());
        return plan with { Warnings = [.. check.Warnings, .. warnings] };
    }

    // An existing bundle picked as the destination is exported into again, which is how a cut-short export
    // resumes; any other folder gets a new bundle inside it.
    private string BundleFolderFor(string destination)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        return Path.GetFileName(full).StartsWith(TransferLayout.BundleNamePrefix, StringComparison.Ordinal)
               && Directory.Exists(full)
            ? full
            : TransferLayout.NewBundleFolder(full, _time.GetLocalNow().DateTime);
    }

    /// <inheritdoc />
    public Task<TransferSummary> RunAsync(ExportPlan plan, IProgress<TransferProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_queue is null)
        {
            return Task.Run(() => Run(plan, progress, ct), ct);
        }

        int count = plan.Entries.Count(e => e.ExportsData || e.DemBytes > 0);
        string title = $"Export {count.ToString(CultureInfo.InvariantCulture)} demo{(count == 1 ? "" : "s")}";
        return TransferQueue.RunAsync(_queue, title, plan.BundleFolder, (job, relay) =>
        {
            // An export only reads the library, so demo processing keeps going beside it (D4: best effort).
            job.ReleaseSlot();
            return Task.Run(() => Run(plan, relay, job.CancellationToken), job.CancellationToken);
        }, progress, false, ct);
    }

    private TransferSummary Run(ExportPlan plan, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ExportRequest request = plan.Request;
        string bundle = plan.BundleFolder;
        List<TransferWarning> warnings = [];

        // The target is checked again: a drive can go away or fill between the review step and the run.
        TransferTargetCheck check = _target.Check(request.DestinationFolder, plan.TotalBytes, plan.LargestFileBytes,
            _planner.ProtectedRoots());
        if (!check.CanProceed)
        {
            return new TransferSummary(
                [.. plan.Entries.Select(e => new TransferSummaryItem(e.Sha256, e.DisplayName, TransferOutcome.Failed,
                    "The export could not start."))],
                check.Warnings, bundle);
        }

        ExportRun run = new(this, bundle, check.IsFat32, progress, plan, ct);
        try
        {
            Directory.CreateDirectory(bundle);
            // What follows changes the bundle, so an older manifest no longer describes it.
            File.Delete(Path.Combine(bundle, TransferLayout.ManifestFileName));
            run.ExportDemos();
            ct.ThrowIfCancellationRequested();
            List<TransferFile> shared = request.IncludeUserData ? run.ExportShared() : [];
            ct.ThrowIfCancellationRequested();
            if (request.IncludeLibraryData)
            {
                run.ExportLibrary();
                ct.ThrowIfCancellationRequested();
            }

            TransferManifest manifest = new()
            {
                FormatId = TransferManifest.FormatIdValue,
                AppVersion = TransferManifest.CurrentAppVersion,
                CreatedUtc = _time.GetUtcNow().UtcDateTime,
                SourceOs = RuntimeInformation.OSDescription,
                Options = new TransferManifestOptions(request.IncludeDemFiles, request.IncludeUserData,
                    request.AllDemos, request.IncludeLibraryData),
                RulesetIdentities = [.. (_sources.Environment?.Invoke().RulesetIdentities ?? [])],
                Extensions = run.ExtensionVersions(),
                Demos = run.Demos,
                Shared = shared,
                ExtensionData = run.ExtensionData
            };
            manifest.Write(bundle);
            warnings.AddRange(run.Warnings);
            return new TransferSummary(run.Items, warnings, bundle);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            warnings.AddRange(run.Warnings);
            return new TransferSummary(run.Items, warnings, bundle, true);
        }
    }

    /// <summary>One export run's state: what has been written, said and reported so far.</summary>
    private sealed class ExportRun(
        TransferExporter owner,
        string bundle,
        bool fat32,
        IProgress<TransferProgress>? progress,
        ExportPlan plan,
        CancellationToken ct)
    {
        private readonly Dictionary<string, Dictionary<string, int>> _extensionVersions =
            new(StringComparer.Ordinal);

        private readonly IReadOnlyList<(string ExtensionId, string Root)> _extensions =
            owner._planner.ExtensionRoots();

        private readonly Dictionary<string, IReadOnlyDictionary<(string Sha256, string Facet), int>> _indexCounts =
            new(StringComparer.Ordinal);

        private readonly HashSet<string> _skippedExtensions = new(StringComparer.Ordinal);
        private readonly ExtensionStoreFiles _stores = owner._planner.Stores;
        private bool _warnedStoresFacet;
        private long _bytesDone;
        private int _demosDone;

        public List<TransferDemo> Demos { get; } = [];

        /// <summary>Every store and own file copied so far, per-demo and library-wide.</summary>
        public List<TransferExtensionFile> ExtensionData { get; } = [];
        public List<TransferSummaryItem> Items { get; } = [];
        public List<TransferWarning> Warnings { get; } = [];

        // Every exported demo's paths on this machine, for the review queue's path-only entries.
        private Dictionary<string, string> ShaByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

        private int DemosTotal => plan.Entries.Count;

        public void ExportDemos()
        {
            HashSet<string> exported = new(StringComparer.Ordinal);
            // Hashed demos first: a plain .dem copy hashes to its content, and a hashed entry of the same content
            // must win the bundle's one slot for it.
            foreach (ExportPlanEntry entry in plan.Entries.OrderBy(e => e.Status == ExportEntryStatus.NotHashed))
            {
                ct.ThrowIfCancellationRequested();
                Report(entry.DisplayName);
                try
                {
                    TransferSummaryItem item = entry.Status switch
                    {
                        ExportEntryStatus.Missing => new TransferSummaryItem(entry.Sha256, entry.DisplayName,
                            TransferOutcome.Skipped, "It is no longer in the library or has no analysis."),
                        ExportEntryStatus.NotHashed => PlainDemo(entry, exported),
                        _ => Demo(entry, exported)
                    };
                    Items.Add(item);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Items.Add(new TransferSummaryItem(entry.Sha256, entry.DisplayName, TransferOutcome.Failed,
                        ex.Message));
                }

                _demosDone++;
                Report(null);
            }
        }

        // A hashed demo: its cache files under the store's locks, then its extension data, annotations and .dem.
        private TransferSummaryItem Demo(ExportPlanEntry entry, HashSet<string> exported)
        {
            string sha = entry.Sha256!;
            if (owner._planner.RowOf(sha) is not { } row)
            {
                return new TransferSummaryItem(sha, entry.DisplayName, TransferOutcome.Skipped,
                    "It left the library before the export reached it.");
            }

            IReadOnlyList<string> paths = owner._planner.PathsOf(sha);
            bool busy = entry.Status == ExportEntryStatus.InFlight
                        || owner._planner.IsInFlight(paths.Append(row.Path));
            List<TransferFile> files = owner._cache.SnapshotFiles<List<TransferFile>?>(sha, snapshot =>
            {
                // Siblings come first and the record last (the order SnapshotFiles gives), so a demo written to
                // while it is copied can look stale in the bundle, never current but wrong.
                if (!snapshot.Any(f => f.IsRecord))
                {
                    return null;
                }

                List<TransferFile> copied = [];
                foreach (DemoCacheFile file in snapshot)
                {
                    if (BundleName(file.FullPath, () => TransferLayout.CacheFile(sha,
                            TransferLayout.DemoFileName(file.Rest))) is { } relative)
                    {
                        copied.Add(CopyData(file.FullPath, relative, FileShare.Read));
                    }
                }

                return copied;
            }) ?? [];
            if (files.Count == 0)
            {
                return new TransferSummaryItem(sha, entry.DisplayName, TransferOutcome.Skipped,
                    "Its analysis was removed before the export reached it.");
            }

            // Store entries join the manifest only with their demo, so a demo that fails later leaves none behind.
            List<TransferExtensionFile> storeEntries = [];
            Dictionary<string, int> facetCounts = new(StringComparer.Ordinal);
            files.AddRange(ExtensionFiles(sha, storeEntries, facetCounts));
            if (plan.Request.IncludeUserData && owner._planner.AnnotationsOf(sha, paths) is { } annotations)
            {
                // The annotation editor may hold it open for writing.
                files.Add(CopyData(annotations, TransferLayout.AnnotationsFile(sha), FileShare.ReadWrite));
            }

            TransferFingerprint? fingerprint = row.ContentFingerprint is { } fp && fp.Size == row.Size
                ? TransferFingerprint.From(fp)
                : null;
            TransferDemFile? dem = null;
            // Queued or started while it was copied counts too: the copy may hold a mix of before and after.
            busy = busy || owner._planner.IsInFlight(paths.Append(row.Path));
            string? reason = busy
                ? "It was being processed; it was exported as it stood."
                : null;
            if (plan.Request.IncludeDemFiles)
            {
                (dem, string? demReason, DemoContentFingerprint? copiedFingerprint) = Dem(sha, row.Size);
                reason = demReason ?? reason;
                fingerprint ??= copiedFingerprint is null ? null : TransferFingerprint.From(copiedFingerprint);
            }

            // Import reattaches by fingerprint, so a row that never took one gets it from the file, read only.
            fingerprint ??= paths.Select(p => DemoContentFingerprint.TryCompute(p, TimeProvider.System))
                .FirstOrDefault(f => f is not null && f.Size == row.Size) is { } read
                ? TransferFingerprint.From(read)
                : null;

            foreach (string path in paths.Append(row.Path))
            {
                ShaByPath.TryAdd(path, sha);
            }

            exported.Add(sha);
            ExtensionData.AddRange(storeEntries);
            Demos.Add(new TransferDemo(sha, Path.GetFileName(row.Path), row.Size, files, fingerprint, row.Map,
                row.MatchTimeUtcTicks is { } ticks ? new DateTime(ticks, DateTimeKind.Utc) : null, dem,
                facetCounts.Count > 0 ? facetCounts : null));
            return new TransferSummaryItem(sha, entry.DisplayName, TransferOutcome.Transferred, reason);
        }

        // A demo the library has not hashed: no data leaves, but its .dem can travel as a plain demo. Hashing
        // the copy is not hashing the source: nothing is written back.
        private TransferSummaryItem PlainDemo(ExportPlanEntry entry, HashSet<string> exported)
        {
            const string notAnalysed = "It is not analysed yet, so it carries no data.";
            if (!plan.Request.IncludeDemFiles || entry.DemoPath is not { } path || entry.DemBytes <= 0)
            {
                return new TransferSummaryItem(null, entry.DisplayName, TransferOutcome.Skipped, notAnalysed);
            }

            if (fat32 && entry.DemBytes > TransferTarget.Fat32MaxFileBytes)
            {
                Fat32Refused(entry.DisplayName, null);
                return new TransferSummaryItem(null, entry.DisplayName, TransferOutcome.Skipped,
                    "Its .dem is too large for this FAT32 drive.");
            }

            string folder = TransferLayout.FullPath(bundle, TransferLayout.DemDirectory);
            Directory.CreateDirectory(folder);
            string temp = TempBeside(Path.Combine(folder, "plain.dem"));
            try
            {
                (string sha, long length) = StreamCopy(path, temp, FileShare.ReadWrite);
                DemoContentFingerprint fingerprint;
                using (FileStream copy = new(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fingerprint = DemoContentFingerprint.Compute(copy, DemoContentFingerprint.DefaultWindowBytes);
                }

                if (!exported.Add(sha))
                {
                    File.Delete(temp);
                    return new TransferSummaryItem(sha, entry.DisplayName, TransferOutcome.Skipped,
                        "The same demo is already in the bundle.");
                }

                File.Move(temp, TransferLayout.FullPath(bundle, TransferLayout.DemFile(sha)), true);
                Demos.Add(new TransferDemo(sha, Path.GetFileName(path), length, [],
                    TransferFingerprint.From(fingerprint), entry.Map, null,
                    new TransferDemFile(TransferLayout.DemFile(sha), length)));
                return new TransferSummaryItem(sha, entry.DisplayName, TransferOutcome.Transferred, notAnalysed);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        // Read once per extension, and only read: the export never loads a store.
        private IReadOnlyDictionary<(string Sha256, string Facet), int> IndexCounts(string root)
        {
            if (!_indexCounts.TryGetValue(root, out IReadOnlyDictionary<(string Sha256, string Facet), int>? counts))
            {
                _indexCounts[root] = counts = ExtensionDemoDataStore.CountsIn(root);
            }

            return counts;
        }

        // The demo's facet files, then its per-demo store files, whose manifest entries go to entries. A facet's
        // count, which only the source's index holds, goes to facetCounts under its main file's bundle path.
        private List<TransferFile> ExtensionFiles(string sha, List<TransferExtensionFile> entries,
            Dictionary<string, int> facetCounts)
        {
            List<TransferFile> files = [];
            foreach ((string extensionId, string root) in _extensions)
            {
                IReadOnlyList<DemoDataFile> data = ExtensionDemoDataStore.FilesFor(root, sha);
                if (data.Count == 0)
                {
                    continue;
                }

                if (!PortableExtension(extensionId))
                {
                    continue;
                }

                foreach (DemoDataFile file in data)
                {
                    // A facet named like the stores folder would be read back as store files.
                    if (string.Equals(file.Facet, TransferLayout.StoresDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_warnedStoresFacet)
                        {
                            _warnedStoresFacet = true;
                            Warnings.Add(new TransferWarning(TransferWarningKind.Other,
                                $"The '{file.Facet}' data of the extension '{extensionId}' was left out: the name "
                                + "is reserved in a transfer bundle."));
                        }

                        continue;
                    }

                    if (BundleName(file.FullPath, () => TransferLayout.ExtensionFacetFile(sha, extensionId, file.Facet,
                            TransferLayout.DemoFileName(file.Rest))) is not { } relative)
                    {
                        continue;
                    }

                    try
                    {
                        files.Add(CopyData(file.FullPath, relative, FileShare.Read));
                    }
                    catch (FileNotFoundException)
                    {
                        continue; // rewritten or dropped since it was listed; the stamps say it is stale
                    }

                    if (file.IsMain && IndexCounts(root).GetValueOrDefault((sha, file.Facet)) is > 0 and var count)
                    {
                        facetCounts[relative] = count;
                    }

                    if (file.IsMain && ExtensionDemoDataStore.WriterVersionOf(file.FullPath) is { } version)
                    {
                        Dictionary<string, int> counts = _extensionVersions.TryGetValue(extensionId,
                            out Dictionary<string, int>? known)
                            ? known
                            : _extensionVersions[extensionId] = new Dictionary<string, int>(StringComparer.Ordinal);
                        counts[version] = counts.GetValueOrDefault(version) + 1;
                    }
                    else
                    {
                        _extensionVersions.TryAdd(extensionId, new Dictionary<string, int>(StringComparer.Ordinal));
                    }
                }
            }

            foreach (TransferExtensionSource source in plan.ExtensionSources)
            {
                if (PortableExtension(source.ExtensionId))
                {
                    files.AddRange(CopyStoreFiles(_stores.PerDemo(source, sha), entries));
                }
            }

            return files;
        }

        /// <summary>
        ///     The enabled extensions' library-wide stores and own files, whole: a subset export still carries every
        ///     strat, team and lineup, since they are not keyed by demo.
        /// </summary>
        public void ExportLibrary()
        {
            foreach (TransferExtensionSource source in plan.ExtensionSources)
            {
                ct.ThrowIfCancellationRequested();
                if (!PortableExtension(source.ExtensionId))
                {
                    continue;
                }

                Report(source.Label);
                CopyStoreFiles(_stores.LibraryWide(source), ExtensionData);
            }
        }

        // Byte copies, each with its manifest entry added to entries. A file that cannot be read is left out
        // with a word, not the whole demo or library: the store keeps working on this machine either way.
        private List<TransferFile> CopyStoreFiles(List<ExtensionStoreSource> sources,
            List<TransferExtensionFile> entries)
        {
            List<TransferFile> copied = [];
            foreach (ExtensionStoreSource source in sources)
            {
                if (BundleName(source.FullPath, source.BundlePath) is not { } relative)
                {
                    continue;
                }

                TransferFile file;
                try
                {
                    // The owner may hold the file open for appending (a strat's history log).
                    file = CopyData(source.FullPath, relative, FileShare.ReadWrite);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    continue; // removed since it was listed
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Warnings.Add(new TransferWarning(TransferWarningKind.Other,
                        $"{Path.GetFileName(source.FullPath)} could not be read, so it was left out: {ex.Message}"));
                    continue;
                }

                copied.Add(file);
                entries.Add(source.Entry(file));
                _extensionVersions.TryAdd(source.ExtensionId, new Dictionary<string, int>(StringComparer.Ordinal));
            }

            return copied;
        }

        private bool PortableExtension(string extensionId)
        {
            if (TransferLayout.IsPortableSegment(extensionId))
            {
                return true;
            }

            if (_skippedExtensions.Add(extensionId))
            {
                Warnings.Add(new TransferWarning(TransferWarningKind.Other,
                    $"The data of the extension '{extensionId}' cannot be carried: its id is not a portable "
                    + "folder name."));
            }

            return false;
        }

        /// <summary>One entry per extension whose data was exported, with the version most of it was written by.</summary>
        public List<TransferExtensionVersion> ExtensionVersions() =>
        [
            .. _extensionVersions.OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new TransferExtensionVersion(e.Key,
                    e.Value.Count == 0
                        ? null
                        : e.Value.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal)
                            .First().Key))
        ];

        // The .dem of a hashed demo, verified against its hash as it streams.
        private (TransferDemFile? Dem, string? Reason, DemoContentFingerprint? Fingerprint) Dem(string sha, long size)
        {
            if (owner._planner.DemSource(sha, size) is not { } source)
            {
                return (null, "Its .dem file was not found, so only its data was exported.", null);
            }

            if (fat32 && size > TransferTarget.Fat32MaxFileBytes)
            {
                Fat32Refused(Path.GetFileName(source), sha);
                return (null, "Its .dem is too large for this FAT32 drive, so only its data was exported.", null);
            }

            string relative = TransferLayout.DemFile(sha);
            string target = TransferLayout.FullPath(bundle, relative);
            if (File.Exists(target) && new FileInfo(target).Length == size && HashOf(target, FileShare.Read) == sha)
            {
                Advance(size);
                return (new TransferDemFile(relative, size), null, null);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = TempBeside(target);
            try
            {
                (string copiedSha, long length) = StreamCopy(source, temp, FileShare.ReadWrite);
                if (copiedSha != sha || length != size)
                {
                    Warnings.Add(new TransferWarning(TransferWarningKind.Other,
                        $"{Path.GetFileName(source)} changed since it was analysed, so its .dem was not copied.",
                        Sha256: sha));
                    return (null, "Its .dem changed since it was analysed, so only its data was exported.", null);
                }

                DemoContentFingerprint fingerprint;
                using (FileStream copy = new(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fingerprint = DemoContentFingerprint.Compute(copy, DemoContentFingerprint.DefaultWindowBytes);
                }

                File.Move(temp, target, true);
                return (new TransferDemFile(relative, size), null, fingerprint);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        // A file whose name has a character some drive would refuse stays behind, with a word in the summary.
        private string? BundleName(string source, Func<string> name)
        {
            try
            {
                return name();
            }
            catch (ArgumentException)
            {
                Warnings.Add(new TransferWarning(TransferWarningKind.Other,
                    $"{Path.GetFileName(source)} has a name that is not portable, so it was left out."));
                return null;
            }
        }

        private void Fat32Refused(string name, string? sha) =>
            Warnings.Add(new TransferWarning(TransferWarningKind.Fat32FileTooLarge,
                $"{name} is 4 GB or larger, which this FAT32 drive cannot hold, so it was not copied. Format the "
                + "drive as exFAT to carry it.", Sha256: sha));

        // The breakpoint map and the review queue, cut down to the exported demos.
        public List<TransferFile> ExportShared()
        {
            HashSet<string> shas = new(Demos.Select(d => d.Sha256), StringComparer.Ordinal);
            List<TransferFile> shared = [];
            foreach ((string name, string source) in owner._planner.SharedSources())
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(source))
                {
                    continue;
                }

                Report(name);
                byte[]? filtered = name == TransferLayout.GraphBreakpointsFileName
                    ? ReadShared(source, true, text => FilterBreakpoints(text, shas))
                    : ReadShared(source, false, text => FilterReviewQueue(text, shas, ShaByPath));
                if (filtered is null)
                {
                    Warnings.Add(new TransferWarning(TransferWarningKind.SharedFileSkipped,
                        $"{Path.GetFileName(source)} could not be read, so it was left out of the bundle."));
                    continue;
                }

                if (filtered.Length > 0)
                {
                    shared.Add(WriteData(filtered, TransferLayout.SharedFile(name)));
                }
            }

            return shared;
        }

        // The filtered bytes, empty when nothing is left for the exported demos, or null when it will not parse.
        private static byte[]? ReadShared(string source, bool retry, Func<string, byte[]> filter)
        {
            for (int attempt = 0;; attempt++)
            {
                try
                {
                    using FileStream stream = new(source, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using StreamReader reader = new(stream);
                    return filter(reader.ReadToEnd());
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException
                                               or UnauthorizedAccessException)
                {
                    if (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        return [];
                    }

                    if (!retry || attempt > 0)
                    {
                        return null;
                    }
                }

                Thread.Sleep(_sharedRetryDelay);
            }
        }

        private static byte[] FilterBreakpoints(string text, HashSet<string> shas)
        {
            JsonObject all = JsonNode.Parse(text)?.AsObject() ?? throw new JsonException("Not an object.");
            JsonObject kept = new();
            foreach ((string key, JsonNode? value) in all)
            {
                if (shas.Contains(key))
                {
                    kept[key] = value?.DeepClone();
                }
            }

            return kept.Count == 0 ? [] : Encoding.UTF8.GetBytes(kept.ToJsonString(_indented));
        }

        // Entries for the exported demos, by hash or by one of their paths here; a path-only entry gets the hash
        // so the other machine can find its demo. Sections and other demos' clips stay behind.
        private static byte[] FilterReviewQueue(string text, HashSet<string> shas,
            Dictionary<string, string> shaByPath)
        {
            JsonObject file = JsonNode.Parse(text)?.AsObject() ?? throw new JsonException("Not an object.");
            JsonArray entries = file["entries"]?.AsArray() ?? new JsonArray();
            JsonArray kept = new();
            foreach (JsonNode? node in entries)
            {
                if (node is not JsonObject entry)
                {
                    continue;
                }

                string? sha = (entry["sha256"] as JsonValue)?.GetValue<string>()?.ToLowerInvariant();
                if (sha is null && (entry["demoPath"] as JsonValue)?.GetValue<string>() is { Length: > 0 } path)
                {
                    sha = shaByPath.GetValueOrDefault(path);
                }

                if (sha is null || !shas.Contains(sha))
                {
                    continue;
                }

                JsonObject copy = entry.DeepClone().AsObject();
                copy["sha256"] = sha;
                kept.Add(copy);
            }

            if (kept.Count == 0)
            {
                return [];
            }

            file["entries"] = kept;
            return Encoding.UTF8.GetBytes(file.ToJsonString(_indented));
        }

        // Copies one data file into the bundle unless the bundle already holds it, byte for byte.
        private TransferFile CopyData(string source, string relative, FileShare share)
        {
            ct.ThrowIfCancellationRequested();
            string target = TransferLayout.FullPath(bundle, relative);
            using FileStream input = new(source, FileMode.Open, FileAccess.Read, share | FileShare.Delete,
                1 << 16, FileOptions.SequentialScan);
            long length = input.Length;
            if (File.Exists(target) && new FileInfo(target).Length == length)
            {
                string sourceSha = HashOf(input);
                if (HashOf(target, FileShare.Read) == sourceSha)
                {
                    Advance(length);
                    return new TransferFile(relative, length, sourceSha);
                }

                input.Position = 0;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = TempBeside(target);
            try
            {
                (string sha, long written) = StreamCopy(input, temp);
                File.Move(temp, target, true);
                return new TransferFile(relative, written, sha);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private TransferFile WriteData(byte[] content, string relative)
        {
            string target = TransferLayout.FullPath(bundle, relative);
            string sha = Convert.ToHexStringLower(SHA256.HashData(content));
            if (File.Exists(target) && new FileInfo(target).Length == content.Length
                                    && HashOf(target, FileShare.Read) == sha)
            {
                return new TransferFile(relative, content.Length, sha);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = TempBeside(target);
            try
            {
                File.WriteAllBytes(temp, content);
                File.Move(temp, target, true);
            }
            finally
            {
                TryDelete(temp);
            }

            Advance(content.Length);
            return new TransferFile(relative, content.Length, sha);
        }

        private (string Sha256, long Length) StreamCopy(string source, string temp, FileShare share)
        {
            using FileStream input = new(source, FileMode.Open, FileAccess.Read, share | FileShare.Delete,
                CopyBufferSize, FileOptions.SequentialScan);
            return StreamCopy(input, temp);
        }

        // Streams input into a new temp file, hashing as it goes, flushed to the device before it is renamed.
        private (string Sha256, long Length) StreamCopy(FileStream input, string temp)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            long length = 0;
            try
            {
                using FileStream output = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    CopyBufferSize, FileOptions.SequentialScan);
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                    length += read;
                    Advance(read);
                }

                output.Flush(true);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return (Convert.ToHexStringLower(hash.GetHashAndReset()), length);
        }

        private void Advance(long bytes)
        {
            long before = _bytesDone;
            _bytesDone += bytes;
            // Every 8 MiB is plenty for a bar, and keeps a 3 GB copy from posting thousands of reports.
            if (before >> 23 != _bytesDone >> 23)
            {
                Report(null);
            }
        }

        private void Report(string? current) =>
            progress?.Report(new TransferProgress(_demosDone, DemosTotal, _bytesDone, plan.TotalBytes, current));
    }

    private static string HashOf(FileStream stream)
    {
        stream.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string HashOf(string path, FileShare share)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, share, 1 << 16,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // Starts with a dot, like every temp name the app writes, and never collides with a bundle name.
    private static string TempBeside(string target) =>
        Path.Combine(Path.GetDirectoryName(target)!,
            "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp name is never read; the next export into this bundle ignores it.
        }
    }
}
