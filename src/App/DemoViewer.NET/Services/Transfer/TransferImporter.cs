#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Playback2D.Pipeline.Review;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Review;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>
///     Imports a transfer bundle. Previewing reads and checks the manifest and every file it lists and writes
///     nothing. Running is one serial queue item that pauses background processing while it writes: cache rows
///     first, through the store so the orphan sweep keeps them, then extension data and user data, then the
///     <c>.dem</c> copies. The library rescan runs after the item, so a copied demo attaches to its imported row
///     by fingerprint and is never parsed for data it already has.
/// </summary>
internal sealed class TransferImporter : ITransferImporter
{
    private const int CopyBufferBytes = 1 << 20;
    private const string LegacyRecordSuffix = ".json";

    private readonly string? _annotationsRoot;
    private readonly GraphBreakpointStore? _breakpoints;
    private readonly DemoCacheStore _cache;
    private readonly DemoImportCurrency? _currency;
    private readonly Func<TransferEnvironment>? _environment;
    private readonly Func<string, ExtensionDemoDataStore?>? _extensionData;
    private readonly string? _extensionDataRoot;
    private readonly DemoLibraryService? _library;
    private readonly IDemoProcessingQueue? _queue;
    private readonly ReviewQueue? _review;
    private readonly Func<Func<Task>, Task> _onUi;
    private readonly TransferTarget _target;

    /// <param name="cache">The demo cache rows go into.</param>
    /// <param name="annotationsRoot">The app-data root annotations are written under (the config root); null skips them.</param>
    /// <param name="breakpoints">The graph breakpoints store; null skips them.</param>
    /// <param name="review">The review queue; null skips its entries.</param>
    /// <param name="library">The library the <c>.dem</c> copies join and which is rescanned; null skips both.</param>
    /// <param name="queue">The processing queue the import runs on and pauses; null runs it in place.</param>
    /// <param name="extensionData">The loaded extension's per-demo data store by extension id, or null when it is not loaded.</param>
    /// <param name="extensionDataRoot">
    ///     The cache's <c>extension-data</c> folder, where data of an extension that is not loaded is written for when
    ///     it is; null skips that data.
    /// </param>
    /// <param name="environment">This machine's rules and extensions, for the staleness warning; null skips it.</param>
    /// <param name="currency">How a merge tells current data here; null compares schemas and states only.</param>
    /// <param name="onUi">Runs UI-thread work (the review queue, the library's folders); inline when null.</param>
    /// <param name="target">The destination checks for the <c>.dem</c> copies.</param>
    public TransferImporter(DemoCacheStore cache, string? annotationsRoot = null,
        GraphBreakpointStore? breakpoints = null, ReviewQueue? review = null, DemoLibraryService? library = null,
        IDemoProcessingQueue? queue = null, Func<string, ExtensionDemoDataStore?>? extensionData = null,
        string? extensionDataRoot = null, Func<TransferEnvironment>? environment = null,
        DemoImportCurrency? currency = null, Func<Func<Task>, Task>? onUi = null, TransferTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _annotationsRoot = annotationsRoot;
        _breakpoints = breakpoints;
        _review = review;
        _library = library;
        _queue = queue;
        _extensionData = extensionData;
        _extensionDataRoot = extensionDataRoot;
        _environment = environment;
        _currency = currency;
        _onUi = onUi ?? (static f => f());
        _target = target ?? new TransferTarget();
    }

    /// <inheritdoc />
    public Task<ImportPreview> PreviewAsync(string bundleFolder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundleFolder);
        return Task.Run(() => Preview(Path.GetFullPath(bundleFolder), ct), ct);
    }

    /// <inheritdoc />
    public async Task<TransferSummary> RunAsync(ImportRequest request, ImportPreview preview,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preview);
        string bundle = Path.GetFullPath(request.BundleFolder);
        if (!string.Equals(bundle, Path.GetFullPath(preview.BundleFolder), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The request and the preview name different bundles.", nameof(request));
        }

        if (!preview.CanRun)
        {
            return new TransferSummary([], preview.Warnings, bundle);
        }

        string[] libraryFolders = [];
        if (_library is { } library)
        {
            await _onUi(() =>
            {
                libraryFolders = [.. library.Folders];
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        Run run = new(request, preview, bundle, libraryFolders);
        try
        {
            await ImportOnQueueAsync(run, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || run.Cancelled)
        {
            // Cancelled before it started or between steps: what finished is saved and summarised.
            run.Cancelled = true;
        }

        if (!run.Cancelled)
        {
            await ImportReviewAsync(run).ConfigureAwait(false);
            await JoinLibraryAsync(run).ConfigureAwait(false);
        }

        return new TransferSummary([.. run.Items.Values], [.. preview.Warnings, .. run.Warnings], bundle,
            run.Cancelled);
    }

    private async Task ImportOnQueueAsync(Run run, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (_queue is { } queue)
        {
            int count = run.Preview.Entries.Count;
            string title = string.Format(CultureInfo.InvariantCulture, "Import {0} demo{1}", count,
                count == 1 ? "" : "s");
            await TransferQueue.RunAsync(queue, title, run.Bundle, async (job, relay) =>
            {
                // Background passes wait while rows land, so none writes a demo's record mid-merge. A pause the
                // user set is theirs to lift.
                bool paused = !queue.IsPaused;
                if (paused)
                {
                    queue.Pause();
                }

                try
                {
                    await Task.Run(() => Import(run, relay, job.CancellationToken), job.CancellationToken)
                        .ConfigureAwait(false);
                    return true;
                }
                finally
                {
                    if (paused)
                    {
                        queue.Resume();
                    }
                }
            }, progress, true, ct).ConfigureAwait(false);
        }
        else
        {
            await Task.Run(() => Import(run, progress, ct), ct).ConfigureAwait(false);
        }
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private ImportPreview Preview(string bundle, CancellationToken ct)
    {
        if (!Directory.Exists(bundle))
        {
            throw new TransferManifestException("The bundle folder does not exist.");
        }

        TransferManifest manifest = TransferManifest.Read(bundle);
        if (TransferManifest.CheckAppVersion(manifest) is { } refusal)
        {
            throw new TransferManifestException(refusal);
        }

        foreach (TransferFile file in manifest.Demos.SelectMany(d => d.Files).Concat(manifest.Shared))
        {
            CheckOnDisk(bundle, file.Path, file.Size);
        }

        foreach (TransferDemo demo in manifest.Demos)
        {
            if (demo.Dem is { } dem)
            {
                CheckOnDisk(bundle, dem.Path, dem.Size);
            }
        }

        ct.ThrowIfCancellationRequested();
        List<TransferWarning> warnings = [];
        string? stale = _environment?.Invoke().DifferenceFrom(manifest);
        if (stale is not null)
        {
            warnings.Add(new TransferWarning(TransferWarningKind.StaleOnArrival, stale));
        }

        if (_queue?.Snapshot().Any(s => s.Kind == QueueJobKind.DemoProcessing
                && s.State is DemoQueueItemState.Queued or DemoQueueItemState.Running) == true)
        {
            warnings.Add(new TransferWarning(TransferWarningKind.DemosInFlight,
                "Demos are being processed right now. The import pauses that work while it writes; a demo "
                + "processed meanwhile may keep its own data instead of the imported data."));
        }

        Dictionary<string, List<PersistedGraphBreakpoint>> theirBreakpoints = ReadBreakpoints(bundle, manifest);
        IReadOnlyDictionary<string, List<PersistedGraphBreakpoint>> myBreakpoints =
            _breakpoints?.LoadAll() ?? new Dictionary<string, List<PersistedGraphBreakpoint>>();

        List<ImportPreviewEntry> entries = [];
        long dataBytes = manifest.Shared.Sum(f => f.Size);
        long demBytes = 0;
        foreach (TransferDemo demo in manifest.Demos)
        {
            ct.ThrowIfCancellationRequested();
            bool conflict = (demo.Files.Any(f => IsAnnotations(demo, f)) && LocalAnnotations(demo.Sha256) is not null)
                || (theirBreakpoints.ContainsKey(demo.Sha256) && myBreakpoints.ContainsKey(demo.Sha256));
            ImportEntryStatus status = conflict ? ImportEntryStatus.UserDataConflict
                : _cache.HoldsContent(demo.Sha256) ? ImportEntryStatus.AlreadyHere
                : ImportEntryStatus.New;
            bool here = _cache.TryGetByContentId(demo.Sha256) is not null;
            entries.Add(new ImportPreviewEntry(demo, status, demo.Dem is not null, here,
                stale is not null && demo.Files.Count > 0));
            dataBytes += demo.Files.Sum(f => f.Size);
            demBytes += demo.Dem is { } dem && !here ? dem.Size : 0;
        }

        return new ImportPreview(bundle, manifest, entries, dataBytes, demBytes, warnings);
    }

    // Every listed file is inside the bundle, a plain file, and the size the manifest says. Anything else is a
    // damaged or doctored bundle, refused whole before a byte is written.
    private static void CheckOnDisk(string bundle, string relative, long size)
    {
        string full;
        try
        {
            full = TransferLayout.FullPath(bundle, relative);
        }
        catch (ArgumentException ex)
        {
            throw new TransferManifestException($"'{relative}' is not a safe path inside the bundle.", ex);
        }

        FileInfo info = new(full);
        if (!info.Exists)
        {
            throw new TransferManifestException($"The bundle is incomplete: '{relative}' is missing.");
        }

        if (info.LinkTarget is not null)
        {
            throw new TransferManifestException($"'{relative}' is a link, which a bundle never holds.");
        }

        if (info.Length != size)
        {
            throw new TransferManifestException(
                $"The bundle is damaged: '{relative}' is {info.Length} bytes, not the {size} its manifest says.");
        }
    }

    // ── Import ───────────────────────────────────────────────────────────────

    private void Import(Run run, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        TransferManifest manifest = run.Preview.Manifest;
        Dictionary<string, List<PersistedGraphBreakpoint>> breakpoints = [];
        try
        {
            breakpoints = ReadBreakpoints(run.Bundle, manifest);
        }
        catch (TransferManifestException ex)
        {
            run.Warnings.Add(new TransferWarning(TransferWarningKind.SharedFileSkipped, ex.Message));
        }

        HashSet<string> staleShas = new(run.Preview.Entries.Where(e => e.Stale).Select(e => e.Sha256),
            StringComparer.Ordinal);
        long bytesTotal = run.Preview.DataBytes
                          + manifest.Demos.Where(d => run.Request.DemFilesToCopy.Contains(d.Sha256))
                              .Sum(d => d.Dem?.Size ?? 0);
        int done = 0;
        using (_cache.BeginBatch())
        {
            try
            {
                foreach (TransferDemo demo in manifest.Demos)
                {
                    if (ct.IsCancellationRequested)
                    {
                        run.Cancelled = true;
                        break;
                    }

                    progress?.Report(new TransferProgress(done, manifest.Demos.Count, run.BytesDone, bytesTotal,
                        demo.OriginalFileName));
                    run.Items[demo.Sha256] = ImportDemo(run, demo, staleShas.Contains(demo.Sha256));
                    done++;
                }
            }
            finally
            {
                // Once, after the batch: the rows are in memory already, so the sweep keeps their files meanwhile.
                _cache.SaveIndex();
            }
        }

        if (!run.Cancelled)
        {
            ImportBreakpoints(run, breakpoints);
            CopyDemFiles(run, progress, bytesTotal, ct);
        }

        progress?.Report(new TransferProgress(done, manifest.Demos.Count, run.BytesDone, bytesTotal, null));
    }

    private TransferSummaryItem ImportDemo(Run run, TransferDemo demo, bool stale)
    {
        string sha = demo.Sha256;
        string name = demo.OriginalFileName;
        List<string> notes = [];
        bool added = false, merged = false;
        try
        {
            string prefix = TransferLayout.DemoFolder(sha) + "/";
            TransferFile? record = null;
            List<TransferFile> siblings = [];
            Dictionary<(string Extension, string Facet), List<TransferFile>> facets = [];
            TransferFile? annotations = null;
            TransferFile? legacyRecord = null;
            foreach (TransferFile file in demo.Files)
            {
                string[] parts = file.Path[prefix.Length..].Split('/');
                switch (parts)
                {
                    case [TransferLayout.CacheDirectory, var fileName]
                        when TransferLayout.DemoFileRest(fileName) == DemoCacheStore.RecordSuffix:
                        record = file;
                        break;
                    // A record from before the cache was gzipped; the reader takes either form.
                    case [TransferLayout.CacheDirectory, var fileName]
                        when TransferLayout.DemoFileRest(fileName) == LegacyRecordSuffix:
                        legacyRecord = file;
                        break;
                    case [TransferLayout.CacheDirectory, var fileName]
                        when TransferLayout.DemoFileRest(fileName) is not null:
                        siblings.Add(file);
                        break;
                    case [TransferLayout.ExtensionDirectory, _, TransferLayout.StoresDirectory, ..]:
                        run.SkippedStores = true;
                        break;
                    case [TransferLayout.ExtensionDirectory, var extension, var facet, _]:
                        if (!facets.TryGetValue((extension, facet), out List<TransferFile>? list))
                        {
                            facets[(extension, facet)] = list = [];
                        }

                        list.Add(file);
                        break;
                    case [TransferLayout.AnnotationsFileName]:
                        annotations = file;
                        break;
                    default:
                        notes.Add($"'{file.Path}' is not a file this version imports");
                        break;
                }
            }

            record ??= legacyRecord;
            if (record is not null)
            {
                DemoImportResult result = _cache.ImportContent(sha, demo.Fingerprint?.ToFingerprint(),
                    Read(run, record),
                    [
                        .. siblings.Select(s => new KeyValuePair<string, byte[]>(
                            TransferLayout.DemoFileRest(BundleFileName(s))!, Read(run, s)))
                    ], _currency);
                added = result.Outcome == DemoImportOutcome.Added;
                merged = result.Outcome == DemoImportOutcome.Merged;
            }
            else
            {
                Skip(run, siblings);
            }

            foreach (((string extension, string facet), List<TransferFile> files) in facets)
            {
                ExtensionDemoDataStore? store = ExtensionStore(extension, out bool temporary);
                if (store is null)
                {
                    Skip(run, files);
                    notes.Add($"{extension} data was left out: there is no cache to write it to");
                    continue;
                }

                try
                {
                    merged |= store.Import(sha, facet,
                    [
                        .. files.Select(f => new KeyValuePair<string, byte[]>(StoreFileName(sha, f), Read(run, f)))
                    ]);
                }
                finally
                {
                    if (temporary)
                    {
                        store.StopFollowingLibrary();
                    }
                }
            }

            if (annotations is not null)
            {
                merged |= ImportAnnotations(run, sha, annotations, notes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or ArgumentException or JsonException)
        {
            return new TransferSummaryItem(sha, name, TransferOutcome.Failed, ex.Message);
        }

        string? reason = notes.Count > 0 ? string.Join("; ", notes) : null;
        if (!added && !merged)
        {
            return new TransferSummaryItem(sha, name, TransferOutcome.Skipped,
                reason ?? (demo.Files.Count == 0
                    ? "The bundle has no analysis data for this demo."
                    : "This machine already has this data, as current or newer."));
        }

        TransferOutcome outcome = stale ? TransferOutcome.Stale
            : added ? TransferOutcome.Transferred
            : TransferOutcome.Merged;
        return new TransferSummaryItem(sha, name, outcome, reason);
    }

    // The loaded extension's store, or one over its folder for an extension that is not loaded, which the
    // caller lets go of after the import.
    private ExtensionDemoDataStore? ExtensionStore(string extensionId, out bool temporary)
    {
        temporary = false;
        if (_extensionData?.Invoke(extensionId) is { } live)
        {
            return live;
        }

        if (_extensionDataRoot is null || !ExtensionFolders.IsSafeName(extensionId))
        {
            return null;
        }

        temporary = true;
        return new ExtensionDemoDataStore(extensionId, Path.Combine(_extensionDataRoot, extensionId), _cache,
            static _ => { }, null);
    }

    // Annotations land in app-data, never beside a demo this import did not put there; the annotation store
    // reads them from there while the demo's own folder has none.
    private bool ImportAnnotations(Run run, string sha, TransferFile file, List<string> notes)
    {
        byte[] bytes = Read(run, file);
        if (_annotationsRoot is null)
        {
            notes.Add("annotations were left out: there is no app-data folder");
            return false;
        }

        if (!AnnotationStore.TryReadDemoSha256(bytes, out string? named)
            || (named is not null && !string.Equals(named, sha, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The annotations file is not this demo's.");
        }

        ConflictPolicy policy = run.Request.Policy;
        if (LocalAnnotations(sha) is not { } mine)
        {
            Write(AnnotationStore.AppDataFile(_annotationsRoot, sha), bytes);
            return true;
        }

        if (policy == ConflictPolicy.KeepMine)
        {
            return false;
        }

        if (!mine.InAppData)
        {
            // The annotations beside the demo are the ones that load, and import never writes there.
            notes.Add("your annotations beside the demo were kept; the imported ones were not written there");
            return false;
        }

        byte[] next = policy == ConflictPolicy.TakeImported
            ? bytes
            : AnnotationStore.MergeDocuments(File.ReadAllBytes(mine.Path), bytes);
        Write(mine.Path, next);
        return true;
    }

    private (string Path, bool InAppData)? LocalAnnotations(string sha)
    {
        foreach (string demo in _cache.PathsOfContent(sha))
        {
            string beside = demo + AnnotationStore.SidecarExtension;
            if (File.Exists(beside))
            {
                return (beside, false);
            }
        }

        if (_annotationsRoot is not null && AnnotationStore.AppDataFile(_annotationsRoot, sha) is var appData
            && File.Exists(appData))
        {
            return (appData, true);
        }

        return null;
    }

    private void ImportBreakpoints(Run run, Dictionary<string, List<PersistedGraphBreakpoint>> imported)
    {
        Dictionary<string, List<PersistedGraphBreakpoint>> wanted = new(StringComparer.Ordinal);
        foreach ((string sha, List<PersistedGraphBreakpoint> list) in imported)
        {
            if (run.Items.TryGetValue(sha, out TransferSummaryItem? item) && item.Outcome != TransferOutcome.Failed)
            {
                wanted[sha] = list;
            }
        }

        if (_breakpoints is null || wanted.Count == 0)
        {
            return;
        }

        try
        {
            foreach (string sha in _breakpoints.ImportMerge(wanted, run.Request.Policy))
            {
                // Breakpoints that changed make a demo with otherwise current data a merge.
                if (run.Items[sha] is { Outcome: TransferOutcome.Skipped } item)
                {
                    run.Items[sha] = item with { Outcome = TransferOutcome.Merged, Reason = null };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Warnings.Add(new TransferWarning(TransferWarningKind.SharedFileSkipped,
                "Graph breakpoints were not imported: " + ex.Message));
        }
    }

    private async Task ImportReviewAsync(Run run)
    {
        if (_review is not { } review
            || run.Preview.Manifest.Shared.FirstOrDefault(f => f.Path == TransferLayout.SharedFile(
                TransferLayout.ReviewQueueFileName)) is not { } file)
        {
            return;
        }

        List<ReviewEntry> entries;
        try
        {
            ReviewQueueFile parsed = JsonSerializer.Deserialize<ReviewQueueFile>(Read(run, file),
                    ReviewQueueFile.JsonOptions)
                ?? throw new JsonException("Empty review queue file.");
            HashSet<string> imported = new(run.Items.Values.Where(i => i.Outcome != TransferOutcome.Failed)
                .Select(i => i.Sha256).OfType<string>(), StringComparer.Ordinal);
            entries =
            [
                .. parsed.Entries.Where(e => e.Kind == ReviewEntryKind.Section
                    || (e.Sha256 is { } sha && imported.Contains(sha.ToLowerInvariant())))
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or JsonException)
        {
            run.Warnings.Add(new TransferWarning(TransferWarningKind.SharedFileSkipped,
                "The review queue's clips were not imported: " + ex.Message));
            return;
        }

        await _onUi(() =>
        {
            review.ImportClips(entries, run.Request.Policy, sha => _cache.TryGetByContentId(sha)?.Path,
                path => _cache.TryGetIndex(path)?.Sha256);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    // ── .dem files ───────────────────────────────────────────────────────────

    private void CopyDemFiles(Run run, IProgress<TransferProgress>? progress, long bytesTotal, CancellationToken ct)
    {
        ImportRequest request = run.Request;
        List<TransferDemo> wanted =
        [
            .. run.Preview.Manifest.Demos.Where(d => d.Dem is not null && request.DemFilesToCopy.Contains(d.Sha256))
        ];
        if (wanted.Count == 0)
        {
            return;
        }

        if (request.DemDestination is not { Length: > 0 } destination)
        {
            run.Warnings.Add(new TransferWarning(TransferWarningKind.TargetMissing,
                "No folder was chosen for the demo files, so none was copied."));
            return;
        }

        destination = Path.GetFullPath(destination);
        run.Destination = destination;
        if (request.DemDestinationIsNewFolder)
        {
            try
            {
                Directory.CreateDirectory(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                run.Warnings.Add(new TransferWarning(TransferWarningKind.TargetNotWritable,
                    "The demo folder could not be created: " + ex.Message));
                return;
            }
        }

        List<TransferDemo> toCopy = [];
        foreach (TransferDemo demo in wanted)
        {
            if (_cache.TryGetByContentId(demo.Sha256) is not null)
            {
                Note(run, demo, "the library already has this demo file");
            }
            else
            {
                toCopy.Add(demo);
            }
        }

        TransferTargetCheck check = _target.Check(destination, toCopy.Sum(d => d.Dem!.Size),
            toCopy.Count == 0 ? 0 : toCopy.Max(d => d.Dem!.Size), [_annotationsRoot, _cache.CacheRoot]);
        if (!check.CanProceed)
        {
            run.Warnings.AddRange(check.Warnings);
            return;
        }

        run.Warnings.AddRange(check.Warnings);
        PortableNameSet names = PortableNameSet.ForFolder(destination);
        foreach (TransferDemo demo in toCopy)
        {
            if (ct.IsCancellationRequested)
            {
                run.Cancelled = true;
                return;
            }

            string name = names.Claim(demo.OriginalFileName);
            try
            {
                CopyDem(run, demo, Path.Combine(destination, name), progress, bytesTotal, ct);
                run.CopiedDems++;
                if (!run.Items.TryGetValue(demo.Sha256, out TransferSummaryItem? item)
                    || item.Outcome == TransferOutcome.Skipped)
                {
                    run.Items[demo.Sha256] = new TransferSummaryItem(demo.Sha256, demo.OriginalFileName,
                        TransferOutcome.Transferred, null);
                }
            }
            catch (OperationCanceledException)
            {
                run.Cancelled = true;
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                run.Warnings.Add(new TransferWarning(TransferWarningKind.Other,
                    $"{demo.OriginalFileName} was not copied: {ex.Message}", Sha256: demo.Sha256));
            }
        }
    }

    // Streams to a hidden temporary name, hashing as it goes, and takes the real name only once the bytes are
    // the demo's: a cut-short or corrupted copy never shows up in the library.
    private static void CopyDem(Run run, TransferDemo demo, string final, IProgress<TransferProgress>? progress,
        long bytesTotal, CancellationToken ct)
    {
        string source = TransferLayout.FullPath(run.Bundle, demo.Dem!.Path);
        string temp = Path.Combine(Path.GetDirectoryName(final)!,
            "." + Path.GetFileName(final) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                long length = 0;
                using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                           CopyBufferBytes, FileOptions.SequentialScan))
                using (FileStream output = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           CopyBufferBytes))
                {
                    byte[] buffer = new byte[CopyBufferBytes];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        hash.AppendData(buffer, 0, read);
                        length += read;
                        run.BytesDone += read;
                        progress?.Report(new TransferProgress(run.Items.Count, run.Preview.Manifest.Demos.Count,
                            run.BytesDone, bytesTotal, demo.OriginalFileName));
                    }
                }

                string sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (length != demo.SizeBytes || !string.Equals(sha, demo.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("the copy does not match the demo; the bundle may be damaged");
                }
            }

            File.Move(temp, final, false);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    // After the queue item: a copy into a folder outside the library adds it, which rescans; otherwise one
    // rescan lists the copies, and each attaches to its imported row by fingerprint.
    private async Task JoinLibraryAsync(Run run)
    {
        if (_library is not { } library || run.Items.Count == 0)
        {
            return;
        }

        bool add = run.CopiedDems > 0 && run.Destination is { } destination
            && (run.Request.DemDestinationIsNewFolder
                || !run.LibraryFolders.Any(f => TransferTarget.IsSameOrInside(f, destination)));
        await _onUi(() =>
        {
            // The user asked for this import, so its scan goes ahead of background work, paused or not.
            using (QueueWork.UserAction())
            {
                return add ? library.AddFoldersAsync([run.Destination!]) : library.RescanAsync();
            }
        }).ConfigureAwait(false);
    }

    // ── Bundle reads ─────────────────────────────────────────────────────────

    private static Dictionary<string, List<PersistedGraphBreakpoint>> ReadBreakpoints(string bundle,
        TransferManifest manifest)
    {
        if (manifest.Shared.FirstOrDefault(f =>
                f.Path == TransferLayout.SharedFile(TransferLayout.GraphBreakpointsFileName)) is not { } file)
        {
            return new Dictionary<string, List<PersistedGraphBreakpoint>>(StringComparer.Ordinal);
        }

        try
        {
            Dictionary<string, List<PersistedGraphBreakpoint>> all =
                GraphBreakpointStore.Parse(ReadVerified(bundle, file));
            HashSet<string> shas = new(manifest.Demos.Select(d => d.Sha256), StringComparer.Ordinal);
            return all.Where(p => shas.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or JsonException)
        {
            throw new TransferManifestException("The bundle's graph breakpoints do not read: " + ex.Message, ex);
        }
    }

    private static byte[] Read(Run run, TransferFile file)
    {
        byte[] bytes = ReadVerified(run.Bundle, file);
        run.BytesDone += bytes.Length;
        return bytes;
    }

    // The file's bytes, checked against its manifest entry as they are read: no separate verify pass.
    private static byte[] ReadVerified(string bundle, TransferFile file)
    {
        byte[] bytes = File.ReadAllBytes(TransferLayout.FullPath(bundle, file.Path));
        if (bytes.LongLength != file.Size
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{file.Path}' does not match the manifest; the bundle is damaged.");
        }

        return bytes;
    }

    private static void Skip(Run run, IEnumerable<TransferFile> files) => run.BytesDone += files.Sum(f => f.Size);

    private static void Note(Run run, TransferDemo demo, string note)
    {
        if (run.Items.TryGetValue(demo.Sha256, out TransferSummaryItem? item))
        {
            run.Items[demo.Sha256] = item with
            {
                Reason = item.Reason is null ? note : item.Reason + "; " + note
            };
        }
    }

    private static bool IsAnnotations(TransferDemo demo, TransferFile file) =>
        string.Equals(file.Path, TransferLayout.AnnotationsFile(demo.Sha256), StringComparison.Ordinal);

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, bytes);
    }

    // One run's state, shared by the queue item and the steps after it.
    private sealed class Run(ImportRequest request, ImportPreview preview, string bundle, string[] libraryFolders)
    {
        public ImportRequest Request { get; } = request;
        public ImportPreview Preview { get; } = preview;
        public string Bundle { get; } = bundle;
        public string[] LibraryFolders { get; } = libraryFolders;
        public Dictionary<string, TransferSummaryItem> Items { get; } = new(StringComparer.Ordinal);
        public List<TransferWarning> Warnings { get; } = [];
        public long BytesDone { get; set; }
        public int CopiedDems { get; set; }
        public string? Destination { get; set; }
        public bool Cancelled { get; set; }

        public bool SkippedStores
        {
            get;
            set
            {
                if (value && !field)
                {
                    Warnings.Add(new TransferWarning(TransferWarningKind.Other,
                        "Extension stores in this bundle are not imported by this version."));
                }

                field = value;
            }
        }
    }

    private static string BundleFileName(TransferFile file) => file.Path[(file.Path.LastIndexOf('/') + 1)..];

    // The bundle names a facet file demo<rest>; the store names it by the content hash.
    private static string StoreFileName(string sha, TransferFile file)
    {
        string name = BundleFileName(file);
        return TransferLayout.DemoFileRest(name) is { } rest ? sha + rest : name;
    }
}
