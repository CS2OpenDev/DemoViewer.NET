#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     The ruleset files the canvas can draw: every <c>*.rules.yaml</c> in the shipped and the user rules
///     directories, loaded once, and composed one document at a time with its transitive <c>use:</c>
///     dependencies, the way the Workbench and the parked graph tests compose it.
/// </summary>
public sealed class RulesetCorpus
{
    /// <summary>One ruleset file the picker offers.</summary>
    /// <param name="Id">The <c>ruleset:</c> key.</param>
    /// <param name="Title">The <c>title:</c>, or the id when there is none.</param>
    /// <param name="Path">The file.</param>
    /// <param name="Source">Where it came from: <c>shipped</c> or <c>user</c>.</param>
    public sealed record Entry(string Id, string Title, string Path, string Source);

    /// <summary>What composing one ruleset produced.</summary>
    /// <param name="Graph">The nodes and edges; the declared entries alone, unwired, when the composition failed.</param>
    /// <param name="Documents">Every document that took part, by id, for the chips and facets a checked stat does not carry.</param>
    /// <param name="Diagnostics">The composition's diagnostics and the loader's for every document taking part, as text.</param>
    /// <param name="PositionedDiagnostics">The same diagnostics that carry a line, with the ruleset they are about.</param>
    /// <param name="Chain">The engine's lowered chain for the same documents; empty when the build failed, which the diagnostics then say.</param>
    public sealed record Composed(
        RulesetDocumentGraph Graph,
        IReadOnlyDictionary<string, RulesetDoc> Documents,
        IReadOnlyList<string> Diagnostics,
        IReadOnlyList<(string RulesetId, int Line, string Message)> PositionedDiagnostics,
        RulesetChainGraph Chain)
    {
        /// <summary>
        ///     This composition cut down to one node's slice of the chain, or null when the key names no node
        ///     of it. Declared entries outside the slice are left out too, so the canvas draws the slice alone.
        /// </summary>
        public Composed? Focused(RulesetDocumentNodeKey key)
        {
            RulesetChainGraph slice = Chain.Slice(key);
            if (slice.Nodes.Count == 0)
            {
                return null;
            }

            HashSet<RulesetDocumentNodeKey> keys = [.. slice.Nodes.Select(n => n.Key)];
            RulesetDocumentGraph graph = new(
                [.. Graph.Nodes.Where(n => keys.Contains(n.Key))],
                [.. Graph.Edges.Where(e => keys.Contains(e.Source) && keys.Contains(e.Target))]);
            return this with { Graph = graph, Chain = slice };
        }
    }

    // The registries are immutable and cost a build each; the builder itself keeps state while it previews
    // templates, so one is made per composition.
    private static readonly Lazy<EventRegistry> _events = new(EventRegistry.Build);
    private static readonly Lazy<EntityValueProviderRegistry> _entityProviders = new(EntityValueProviderRegistry.CreateDefault);
    private static readonly Lazy<PerPlayerEntityValueProviderRegistry> _perPlayerProviders = new(PerPlayerEntityValueProviderRegistry.CreateDefault);

    // Every file that loaded, in directory order, overridden ones included, so one file can be re-read
    // without a walk and a user file that loses a shipped id hands the shipped entry back.
    private sealed record LoadedFile(string Path, string Source, RulesetDoc Doc, IReadOnlyList<RulesetDiagnostic> Diagnostics);

    private readonly IReadOnlyList<(string Directory, string Source)> _directories;
    private readonly List<LoadedFile> _files;
    private readonly List<(string Path, string Reason)> _failures;
    private readonly Dictionary<string, DateTime> _readAt;
    private readonly Dictionary<string, RulesetDoc> _docs;

    // What the loader said about each document it still loaded: a stray key, a wrong shape. By id, as _docs.
    private readonly Dictionary<string, IReadOnlyList<RulesetDiagnostic>> _loaderDiagnostics;

    /// <summary>The demo-source profile every demo-less composition runs under, by the id the catalog keys its views on.</summary>
    internal static string ProfileName => DemoSourceProfileRegistry.DefaultFallback.GetType().Name;

    private RulesetCorpus(IReadOnlyList<(string Directory, string Source)> directories, List<LoadedFile> files, string? userDirectory,
        List<(string Path, string Reason)> failures, Dictionary<string, DateTime> readAt)
    {
        _directories = directories;
        _files = files;
        _failures = failures;
        _readAt = readAt;
        UserDirectory = userDirectory;
        List<Entry> entries = [];
        _docs = new Dictionary<string, RulesetDoc>(StringComparer.Ordinal);
        _loaderDiagnostics = new Dictionary<string, IReadOnlyList<RulesetDiagnostic>>(StringComparer.Ordinal);
        foreach (LoadedFile file in files)
        {
            // A later directory overrides an earlier one by id, which is the host's own rule: a user file
            // with a shipped ruleset's id replaces it, so an editable copy takes the shipped one's place.
            RulesetDoc doc = file.Doc;
            Entry entry = new(doc.Id, string.IsNullOrWhiteSpace(doc.Title) ? doc.Id : doc.Title, file.Path, file.Source);
            int existing = entries.FindIndex(e => e.Id == doc.Id);
            if (existing >= 0)
            {
                entries[existing] = entry;
            }
            else
            {
                entries.Add(entry);
            }

            _docs[doc.Id] = doc;
            _loaderDiagnostics[doc.Id] = file.Diagnostics;
        }

        Entries = entries;
    }

    /// <summary>The files that did not load as rulesets, with the loader's reason, so a broken file is named rather than silently absent.</summary>
    public IReadOnlyList<(string Path, string Reason)> Failures => _failures;

    /// <summary>Whether <paramref name="path" /> is a file this corpus read, loaded or not, which <see cref="Replace" /> can re-read alone.</summary>
    public bool Knows(string path) =>
        _files.Any(f => SamePath(f.Path, path)) || _failures.Any(f => SamePath(f.Path, path));

    /// <summary>
    ///     Whether <paramref name="text" /> loads as the ruleset <paramref name="expectedId" />, and if not, why:
    ///     the check a save makes before it writes, so a typo cannot take the file out of the picker.
    /// </summary>
    public static string? WhyNotLoadable(string text, string path, string expectedId)
    {
        RulesetDocumentLoader.Outcome outcome;
        try
        {
            outcome = RulesetDocumentLoader.Load(text, path);
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            return ex.Message;
        }

        if (outcome.Doc is null)
        {
            return outcome.Diagnostics.Count > 0
                ? string.Join("; ", outcome.Diagnostics.Select(d => d.Message))
                : "the text does not load as a ruleset";
        }

        return outcome.Doc.Id == expectedId ? null : $"the ruleset: key changed to {outcome.Doc.Id}; rename the file to match it instead";
    }

    /// <summary>The loadable rulesets in the order their directories were given, a later directory's file replacing an earlier one with the same id.</summary>
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>The directory tagged <c>user</c>, where an editable copy of a shipped ruleset goes, or null.</summary>
    public string? UserDirectory { get; }

    /// <summary>
    ///     The directories the app reads rulesets from, in the order they are offered: the shipped set, then
    ///     the user's own. Either can be missing on a given machine, so each is probed.
    /// </summary>
    public static IReadOnlyList<(string Directory, string Source)> DefaultDirectories()
    {
        List<(string, string)> dirs = [];
        string? shipped = null;
        try
        {
            shipped = RuleSetLocator.ResolveShippedRulesDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // No shipped rules beside this build; the picker is simply empty.
        }

        if (shipped is not null && Directory.Exists(shipped))
        {
            dirs.Add((shipped, "shipped"));
            try
            {
                // Named even when empty: an editable copy of a shipped ruleset is written here.
                string user = RuleSetLocator.EnsureUserRulesDirectory(shipped);
                if (!string.Equals(user, shipped, StringComparison.OrdinalIgnoreCase))
                {
                    dirs.Add((user, "user"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A user directory that cannot be created is left out; the shipped set still draws.
            }
        }

        return dirs;
    }

    /// <summary>Loads every ruleset file under <paramref name="directories" />. A file that does not load is skipped.</summary>
    public static RulesetCorpus Load(IEnumerable<(string Directory, string Source)> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        List<(string Directory, string Source)> dirs = [.. directories];
        List<LoadedFile> files = [];
        List<(string Path, string Reason)> failures = [];
        Dictionary<string, DateTime> readAt = new(StringComparer.OrdinalIgnoreCase);
        string? userDirectory = null;
        foreach ((string directory, string source) in dirs)
        {
            if (source == "user")
            {
                userDirectory ??= directory;
            }

            foreach (string path in RulesFilesIn(directory))
            {
                if (ReadFile(path, source, failures, readAt) is { } file)
                {
                    files.Add(file);
                }
            }
        }

        return new RulesetCorpus(dirs, files, userDirectory, failures, readAt);
    }

    /// <summary>
    ///     The files on disk this corpus does not reflect: every <c>*.rules.yaml</c> under its directories that
    ///     appeared, went, or has a write time other than the one it was read at. What a watcher would have
    ///     reported while nothing was watching, for the owner to hand to the same handler.
    /// </summary>
    public IReadOnlyList<string> ChangedPaths()
    {
        List<string> changed = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string directory, string _) in _directories)
        {
            foreach (string path in RulesFilesIn(directory))
            {
                string full = Path.GetFullPath(path);
                seen.Add(full);
                if (!_readAt.TryGetValue(full, out DateTime at) || at != WriteTimeOf(path))
                {
                    changed.Add(path);
                }
            }
        }

        changed.AddRange(_readAt.Keys.Where(p => !seen.Contains(p)));
        return changed;
    }

    private static IEnumerable<string> RulesFilesIn(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.rules.yaml").OrderBy(p => p, StringComparer.Ordinal)
            : [];

    private static DateTime WriteTimeOf(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    ///     The corpus with <paramref name="path" /> read again and every other file as it was: what a write
    ///     to one file needs, where a full load re-parses every ruleset. The file keeps its place in the
    ///     directory order, so the override by id comes out as a full load would have it; a file that no
    ///     longer loads moves to <see cref="Failures" />. Null when the path is not one this corpus read,
    ///     which is a file that appeared and needs a full load.
    /// </summary>
    public RulesetCorpus? Replace(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        int index = _files.FindIndex(f => SamePath(f.Path, path));
        string? source = index >= 0 ? _files[index].Source : _failures.Any(f => SamePath(f.Path, path)) ? SourceOf(path) : null;
        if (source is null)
        {
            return null;
        }

        List<LoadedFile> files = [.. _files];
        List<(string Path, string Reason)> failures = [.. _failures.Where(f => !SamePath(f.Path, path))];
        Dictionary<string, DateTime> readAt = new(_readAt, StringComparer.OrdinalIgnoreCase);
        LoadedFile? again = ReadFile(index >= 0 ? _files[index].Path : path, source, failures, readAt);
        if (index >= 0)
        {
            files.RemoveAt(index);
        }

        if (again is not null)
        {
            // A file that failed before has no slot of its own; after its directory's last file keeps the
            // override by id right, which is all the order decides.
            files.Insert(index >= 0 ? index : files.FindLastIndex(f => f.Source == source) + 1, again);
        }

        return new RulesetCorpus(_directories, files, UserDirectory, failures, readAt);
    }

    private string? SourceOf(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        return directory is not null && UserDirectory is not null && SamePath(directory, UserDirectory) ? "user" : "shipped";
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // The write time is taken before the read, so a write that lands between the two reads as a change.
    private static LoadedFile? ReadFile(string path, string source, List<(string Path, string Reason)> failures,
        Dictionary<string, DateTime> readAt)
    {
        readAt[Path.GetFullPath(path)] = WriteTimeOf(path);
        try
        {
            RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(File.ReadAllText(path), path);
            if (outcome.Doc is { } doc)
            {
                return new LoadedFile(path, source, doc, outcome.Diagnostics);
            }

            failures.Add((path, string.Join("; ", outcome.Diagnostics.Select(d => d.Message))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
        {
            failures.Add((path, ex.Message));
        }

        return null;
    }

    /// <summary>
    ///     Copies <paramref name="entry" /> into the user directory under its own file name, which is what
    ///     the Workbench's Save-As does for a shipped file; the copy overrides the shipped one by id on the
    ///     next load. Refused when there is no user directory, the copy already exists, or the disk says no.
    /// </summary>
    public EditOutcome CopyToUser(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (UserDirectory is not { } userDir)
        {
            return EditOutcome.Refused("There is no user rules directory to copy into.");
        }

        string target = Path.Combine(userDir, Path.GetFileName(entry.Path));
        try
        {
            Directory.CreateDirectory(userDir);
            if (File.Exists(target))
            {
                return EditOutcome.Refused($"{Path.GetFileName(target)} already exists in your rules; open it from the picker.");
            }

            File.Copy(entry.Path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EditOutcome.Refused("The copy could not be written: " + ex.Message);
        }

        return EditOutcome.Written;
    }

    /// <summary>
    ///     Composes <paramref name="id" /> with its transitive <c>use:</c> dependencies against the embedded
    ///     catalog, at the demo-less tick rate and the fallback profile, and projects the result.
    /// </summary>
    public Composed Compose(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!_docs.TryGetValue(id, out RulesetDoc? open))
        {
            return new Composed(RulesetDocumentGraph.Empty, new Dictionary<string, RulesetDoc>(StringComparer.Ordinal),
                [$"{id} is not a loaded ruleset"], [], RulesetChainGraph.Empty);
        }

        List<RulesetDoc> docs = [open];
        Dictionary<string, RulesetDoc> taking = new(StringComparer.Ordinal) { [open.Id] = open };
        Queue<string> pending = new(open.Use);
        while (pending.Count > 0)
        {
            string next = pending.Dequeue();
            if (!taking.ContainsKey(next) && _docs.TryGetValue(next, out RulesetDoc? dep))
            {
                taking[next] = dep;
                docs.Add(dep);
                foreach (string further in dep.Use)
                {
                    pending.Enqueue(further);
                }
            }
        }

        RulesetComposition.Result composed = RulesetComposition.Compose(docs, EmbeddedCatalog.Adapter, 64.0, ProfileName);
        List<string> diagnostics = [.. composed.Diagnostics.Select(d => d.ToString() ?? "")];
        List<(string, int, string)> positioned = [];
        foreach (RulesetCompositionDiagnostic diagnostic in composed.AttributedDiagnostics)
        {
            if (diagnostic.Position is not { Line: > 0 } position)
            {
                continue;
            }

            // A cross-ruleset cycle is attributed to no ruleset; its file still names one.
            string? ruleset = diagnostic.RulesetId
                              ?? taking.Values.FirstOrDefault(d => position.File is not null && d.Position.File == position.File)?.Id;
            if (ruleset is not null)
            {
                positioned.Add((ruleset, position.Line, diagnostic.Message));
            }
        }

        // The loader keeps a document with a stray key or a wrong shape and drops the part it did not
        // understand; the engine runs what remains, so the face must say what was ignored.
        foreach (RulesetDoc doc in docs)
        {
            foreach (RulesetDiagnostic diagnostic in _loaderDiagnostics.GetValueOrDefault(doc.Id) ?? [])
            {
                diagnostics.Add(diagnostic.ToString());
                if (diagnostic.Position.Line > 0)
                {
                    positioned.Add((doc.Id, diagnostic.Position.Line, diagnostic.Message));
                }
            }
        }

        // One unresolved reference anywhere empties the checked set; the entries are still in the documents.
        RulesetDocumentGraph graph = composed.Rulesets.Count == 0
            ? RulesetDocumentGraph.FromDocuments(docs, open.Id)
            : RulesetDocumentGraph.Build(composed.Rulesets, open.Id);
        RulesetChainGraph chain = RulesetChainGraph.Empty;
        if (composed.Rulesets.Count > 0)
        {
            try
            {
                RuleChainBuilder builder = new(_events.Value, null, entityProviders: _entityProviders.Value,
                    perPlayerEntityProviders: _perPlayerProviders.Value);
                BuildResult build = builder.Build([.. composed.Rulesets]);
                chain = RulesetChainGraph.Build(build, composed.Rulesets);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException
                                           or KeyNotFoundException or InvalidCastException or NullReferenceException)
            {
                // A document the resolver passed can still fail to lower; the canvas then draws the declared
                // entries alone and says why.
                diagnostics.Add("The rule chain could not be built: " + ex.Message);
            }
        }

        return new Composed(graph, taking, diagnostics, positioned, chain);
    }
}
