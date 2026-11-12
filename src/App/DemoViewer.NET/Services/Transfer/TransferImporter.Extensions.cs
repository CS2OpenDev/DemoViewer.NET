#region

using System.Globalization;
using System.IO.Enumeration;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>
///     The extension stores step of an import. Each extension the bundle carries stores of, and that is installed and
///     switched on here, gets them once every cache row is in: a store it left to the host is merged file by file,
///     and the rest is handed to its own merge as read-only views of the bundle, one extension at a time on the import
///     job. Its after-import callbacks run once its stores are in. An extension that is missing or off is skipped, and
///     the preview says so.
/// </summary>
internal sealed partial class TransferImporter
{
    private const string MergeSite = "data import";
    private const int ReportLinesShown = 20;

    // One file of an extension's stores or own folders in the bundle. A per-demo store file carries its demo; an own
    // file carries its root and no store.
    private sealed record ExtensionFile(
        TransferFile File,
        string? Sha256,
        string? StoreId,
        StoreRoot? OwnRoot,
        string RelativePath);

    // What the host's file merge did for one extension.
    private sealed class FileMergeCounts
    {
        public int Added;
        public int Merged;
        public int Skipped;
        public int Conflicts;
        public int Failed;
        public bool Wrote => Added + Merged > 0;
    }

    // The bundle's store and own-folder files, by extension id. Anything shaped otherwise is not a store file.
    private static Dictionary<string, List<ExtensionFile>> ExtensionFiles(TransferManifest manifest)
    {
        Dictionary<string, List<ExtensionFile>> byExtension = new(StringComparer.Ordinal);

        void Add(string extensionId, ExtensionFile file)
        {
            if (!byExtension.TryGetValue(extensionId, out List<ExtensionFile>? list))
            {
                byExtension[extensionId] = list = [];
            }

            list.Add(file);
        }

        foreach (TransferDemo demo in manifest.Demos)
        {
            int prefix = TransferLayout.DemoFolder(demo.Sha256).Length + 1;
            foreach (TransferFile file in demo.Files)
            {
                if (file.Path[prefix..].Split('/') is
                    [TransferLayout.ExtensionDirectory, var extension, TransferLayout.StoresDirectory, var store, _, ..]
                    parts)
                {
                    Add(extension, new ExtensionFile(file, demo.Sha256, store, null, string.Join('/', parts[4..])));
                }
            }
        }

        int shared = TransferLayout.SharedDirectory.Length + 1;
        foreach (TransferFile file in manifest.Shared)
        {
            string[] parts = file.Path[shared..].Split('/');
            switch (parts)
            {
                case [TransferLayout.ExtensionDirectory, var extension, TransferLayout.StoresDirectory, var store, _, ..]:
                    Add(extension, new ExtensionFile(file, null, store, null, string.Join('/', parts[4..])));
                    break;
                case [TransferLayout.ExtensionDirectory, var extension, TransferLayout.OwnDirectory, var root, _, ..]
                    when TransferLayout.OwnRoot(root) is { } ownRoot:
                    Add(extension, new ExtensionFile(file, null, null, ownRoot, string.Join('/', parts[4..])));
                    break;
            }
        }

        return byExtension;
    }

    private IReadOnlyList<HostDataTransfer> LocalExtensions() => _extensions?.Extensions() ?? [];

    private IEnumerable<TransferWarning> UnavailableExtensions(TransferManifest manifest)
    {
        IReadOnlyList<HostDataTransfer> local = LocalExtensions();
        foreach (string extensionId in ExtensionFiles(manifest).Keys)
        {
            if (Unavailable(local, extensionId, out _) is { } reason)
            {
                yield return new TransferWarning(TransferWarningKind.ExtensionUnavailable, reason);
            }
        }
    }

    // Null when the extension is installed and on here, else why its stores are skipped.
    private string? Unavailable(IReadOnlyList<HostDataTransfer> local, string extensionId,
        out HostDataTransfer? transfer)
    {
        transfer = local.FirstOrDefault(t => string.Equals(t.Pack.Pack.Id, extensionId, StringComparison.Ordinal));
        if (transfer is null)
        {
            return $"The bundle carries data of the extension {extensionId}, which is not installed here, so that "
                   + "data is skipped.";
        }

        return _extensions?.IsEnabled(transfer.FeatureId) != false
            ? null
            : $"{transfer.Label} is switched off here, so its data in this bundle is skipped. Switch it on and "
              + "import the bundle again to bring it in.";
    }

    private async Task ImportExtensionsAsync(Run run, CancellationToken ct)
    {
        Dictionary<string, List<ExtensionFile>> byExtension = ExtensionFiles(run.Preview.Manifest);
        if (byExtension.Count == 0)
        {
            return;
        }

        IReadOnlyList<HostDataTransfer> local = LocalExtensions();
        HashSet<string> shas = new(run.Preview.Manifest.Demos.Select(d => d.Sha256), StringComparer.Ordinal);
        foreach ((string extensionId, List<ExtensionFile> files) in byExtension)
        {
            if (ct.IsCancellationRequested)
            {
                run.Cancelled = true;
                return;
            }

            if (Unavailable(local, extensionId, out HostDataTransfer? transfer) is { } reason)
            {
                Skip(run, files.Select(f => f.File));
                run.ExtensionItems.Add(new TransferSummaryItem(null, transfer?.Label ?? extensionId,
                    TransferOutcome.Skipped, reason));
                continue;
            }

            run.ExtensionItems.Add(await ImportExtensionAsync(run, transfer!, files, shas, ct).ConfigureAwait(false));
        }
    }

    private async Task<TransferSummaryItem> ImportExtensionAsync(Run run, HostDataTransfer transfer,
        List<ExtensionFile> files, IReadOnlySet<string> shas, CancellationToken ct)
    {
        string label = transfer.Label;
        Dictionary<string, StoreDescriptor> stores = new(StringComparer.Ordinal);
        foreach (StoreDescriptor store in transfer.Stores)
        {
            stores.TryAdd(store.Id, store);
        }

        Dictionary<string, Dictionary<string, TransferFile>> staged = new(StringComparer.Ordinal);
        Dictionary<StoreRoot, Dictionary<string, TransferFile>> own = [];
        List<TransferFile> handedOver = [];
        List<string> notes = [];
        FileMergeCounts counts = new();
        int refused = 0;
        foreach (ExtensionFile file in files)
        {
            if (file.OwnRoot is { } root)
            {
                if (transfer.OwnPaths.Any(o => o.Root == root && SamePath(o.Path, file.RelativePath)))
                {
                    Slot(own, root)[file.RelativePath] = file.File;
                    handedOver.Add(file.File);
                }
                else
                {
                    Skip(run, [file.File]);
                    refused++;
                }

                continue;
            }

            if (!stores.TryGetValue(file.StoreId!, out StoreDescriptor? declared) || !Admits(declared, file))
            {
                Skip(run, [file.File]);
                refused++;
            }
            else if (declared.ExtensionMerges)
            {
                Slot(staged, declared.Id)[file.RelativePath] = file.File;
                handedOver.Add(file.File);
            }
            else
            {
                MergeFile(run, declared, file, counts, notes);
            }
        }

        if (refused > 0)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture,
                "{0} file{1} {2} does not declare for transfer {3} left out", refused, refused == 1 ? "" : "s",
                label, refused == 1 ? "was" : "were"));
        }

        ExtensionImportResult? result = null;
        bool mergeFailed = false, mergeRan = false;
        List<string> report = [];
        if (handedOver.Count > 0)
        {
            if (transfer.Pack.DataPortabilityContribution is not { } portability)
            {
                Skip(run, handedOver);
                notes.Add($"{label} does not merge imports, so the stores it merges itself were left out");
            }
            else
            {
                ExtensionImport import = new(run.Request.Policy.ToSdk(), shas,
                    staged.ToDictionary(p => p.Key,
                        p => new StagedBundleFolder(StagedPath(run, transfer, TransferLayout.StoresDirectory, p.Key),
                            run.Bundle, p.Value), StringComparer.Ordinal),
                    own.ToDictionary(p => p.Key,
                        p => new StagedBundleFolder(StagedPath(run, transfer, TransferLayout.OwnDirectory,
                            TransferLayout.OwnRootName(p.Key)), run.Bundle, p.Value)),
                    report);
                string? failure = null;
                mergeRan = true;
                result = await transfer.Pack.Guard.RunAsync<ExtensionImportResult?>(MergeSite, async () =>
                {
                    try
                    {
                        return await portability.MergeAsync(import, ct).ConfigureAwait(false)
                               ?? throw new InvalidOperationException("The merge returned no result.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        failure = ex.Message;
                        throw;
                    }
                }, null, ct).ConfigureAwait(false);
                Skip(run, handedOver);
                if (result is null && ct.IsCancellationRequested)
                {
                    run.Cancelled = true;
                    notes.Insert(0, "the import was cancelled during its merge; what it wrote before then stays");
                }
                else if (result is null)
                {
                    mergeFailed = true;
                    notes.Insert(0, $"its merge failed ({failure ?? "no reason given"}); what it wrote before then stays");
                }
            }
        }

        if (mergeRan || counts.Wrote)
        {
            transfer.Pack.RaiseDataImported();
        }

        return Summarise(label, counts, result, mergeFailed, notes, report);
    }

    private static TransferSummaryItem Summarise(string label, FileMergeCounts counts, ExtensionImportResult? result,
        bool mergeFailed, List<string> notes, List<string> report)
    {
        int added = counts.Added + Math.Max(0, result?.Added ?? 0);
        int merged = counts.Merged + Math.Max(0, result?.Merged ?? 0);
        int skipped = counts.Skipped + Math.Max(0, result?.Skipped ?? 0);
        int conflicts = counts.Conflicts + Math.Max(0, result?.Conflicts ?? 0);
        List<string> lines =
        [
            .. notes,
            string.Format(CultureInfo.InvariantCulture, "{0} added, {1} merged, {2} unchanged, {3} kept as yours",
                added, merged, skipped, conflicts)
        ];
        lock (report)
        {
            lines.AddRange(report.Take(ReportLinesShown));
            if (report.Count > ReportLinesShown)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture, "and {0} more",
                    report.Count - ReportLinesShown));
            }
        }

        TransferOutcome outcome = mergeFailed || counts.Failed > 0 ? TransferOutcome.Failed
            : added > 0 && merged == 0 ? TransferOutcome.Transferred
            : added + merged > 0 ? TransferOutcome.Merged
            : TransferOutcome.Skipped;
        return new TransferSummaryItem(null, label, outcome, string.Join("; ", lines));
    }

    // The host's merge for a store the extension leaves to it: a file this machine lacks is copied, the same file is
    // left alone, and a different one is settled by the policy. Every write replaces the file whole.
    private void MergeFile(Run run, StoreDescriptor store, ExtensionFile file, FileMergeCounts counts,
        List<string> notes)
    {
        string? root = store.Root == StoreRoot.Config ? _extensions?.ConfigRoot : _extensions?.CacheRoot;
        string? target = root is null ? null : PathSafety.FullPathInside(root, file.RelativePath);
        if (target is null)
        {
            Skip(run, [file.File]);
            counts.Skipped++;
            return;
        }

        try
        {
            byte[] bytes = Read(run, file.File);
            if (!File.Exists(target))
            {
                Write(target, bytes);
                counts.Added++;
            }
            else if (File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                counts.Skipped++;
            }
            else
            {
                switch (run.Request.Policy)
                {
                    case ConflictPolicy.TakeImported:
                        Write(target, bytes);
                        counts.Merged++;
                        break;
                    case ConflictPolicy.KeepBoth when WriteBeside(target, bytes):
                        counts.Added++;
                        break;
                    case ConflictPolicy.KeepBoth:
                        counts.Skipped++;
                        break;
                    default:
                        counts.Conflicts++;
                        break;
                }
            }
        }
        catch (Exception ex) when (IsStepFailure(ex))
        {
            counts.Failed++;
            notes.Add($"{file.RelativePath} was not imported: {ex.Message}");
        }
    }

    // Keep both: the imported copy goes beside this machine's as "<name> (imported)<extension>", the extension taken
    // from the first dot so a compound one (.tagpalette.json) still matches what the store lists. False when that
    // copy is there already, so a second import adds nothing.
    private static bool WriteBeside(string target, byte[] bytes)
    {
        string folder = Path.GetDirectoryName(target)!;
        string name = Path.GetFileName(target);
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            dot = name.Length;
        }

        for (int n = 1;; n++)
        {
            string suffix = n == 1 ? " (imported)" : string.Format(CultureInfo.InvariantCulture, " (imported {0})", n);
            string beside = Path.Combine(folder, name[..dot] + suffix + name[dot..]);
            if (!File.Exists(beside))
            {
                Write(beside, bytes);
                return true;
            }

            if (File.ReadAllBytes(beside).AsSpan().SequenceEqual(bytes))
            {
                return false;
            }
        }
    }

    // A bundle file belongs to the store it names only where that store declares it: a demo's file at one of the
    // store's templates for that demo, a library-wide file under one of its paths and outside its excludes. Anything
    // else could write past what the extension owns.
    private static bool Admits(StoreDescriptor store, ExtensionFile file)
    {
        string relative = file.RelativePath;
        if (PathSafety.SafeRelativePath(relative) is null)
        {
            return false;
        }

        if (file.Sha256 is { } sha)
        {
            return store.Portability == StorePortability.PerDemo
                   && store.DemoKeyedPaths.Any(t => string.Equals(
                       t.Replace('\\', '/').Trim().Replace(PackContributions.Sha256Placeholder, sha,
                           StringComparison.Ordinal), relative, StringComparison.OrdinalIgnoreCase));
        }

        return store.Portability == StorePortability.LibraryWide
               && !relative.Split('/').Any(s => store.PortableExcludes.Contains(s, StringComparer.OrdinalIgnoreCase))
               && store.Paths.Any(p => Under(p, relative));
    }

    private static bool Under(string declared, string relative)
    {
        string path = declared.Replace('\\', '/').Trim().TrimEnd('/');
        if (!path.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(path, relative, StringComparison.OrdinalIgnoreCase)
                   || relative.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);
        }

        // A pattern names files in one folder.
        int slash = relative.LastIndexOf('/');
        int patternSlash = path.LastIndexOf('/');
        string folder = slash < 0 ? "" : relative[..slash];
        string patternFolder = patternSlash < 0 ? "" : path[..patternSlash];
        return !patternFolder.Contains('*', StringComparison.Ordinal)
               && string.Equals(folder, patternFolder, StringComparison.OrdinalIgnoreCase)
               && FileSystemName.MatchesSimpleExpression(path.AsSpan(patternSlash + 1), relative.AsSpan(slash + 1));
    }

    private static bool SamePath(string declared, string relative) =>
        string.Equals(PathSafety.SafeRelativePath(declared.Trim())?.Replace('\\', '/'), relative,
            StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, TransferFile> Slot<TKey>(Dictionary<TKey, Dictionary<string, TransferFile>> all,
        TKey key) where TKey : notnull
    {
        if (!all.TryGetValue(key, out Dictionary<string, TransferFile>? files))
        {
            all[key] = files = new Dictionary<string, TransferFile>(PathSafety.CaseInsensitivePaths);
        }

        return files;
    }

    private static string StagedPath(Run run, HostDataTransfer transfer, string kind, string name) =>
        Path.Combine(run.Bundle, TransferLayout.SharedDirectory, TransferLayout.ExtensionDirectory,
            transfer.Pack.Pack.Id, kind, name);

    // What one extension's merge is handed. Its report may be written from any thread the merge runs on.
    private sealed class ExtensionImport(
        ImportConflictPolicy policy,
        IReadOnlySet<string> shas,
        Dictionary<string, StagedBundleFolder> staged,
        Dictionary<StoreRoot, StagedBundleFolder> own,
        List<string> report) : IExtensionImport
    {
        public ImportConflictPolicy Policy => policy;

        public IReadOnlySet<string> DemoSha256s => shas;

        public IExtensionFolder? Staged(string storeId) =>
            storeId is not null && staged.TryGetValue(storeId, out StagedBundleFolder? folder) ? folder : null;

        public IExtensionFolder? StagedOwn(StoreRoot root) =>
            own.TryGetValue(root, out StagedBundleFolder? folder) ? folder : null;

        public void Report(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (report)
            {
                report.Add(line);
            }
        }
    }
}
