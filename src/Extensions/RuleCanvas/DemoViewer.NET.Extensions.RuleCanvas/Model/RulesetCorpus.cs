#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.Rules;
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
    /// <param name="Source">Where it came from: <c>shipped</c>, <c>user</c>, or <c>testbed</c> for a user directory an environment variable points at.</param>
    public sealed record Entry(string Id, string Title, string Path, string Source)
    {
        /// <summary>Whether the file is in the user's directory, which is the only kind the canvas writes.</summary>
        public bool IsUser => IsUserSource(Source);
    }

    /// <summary>The tag of the user directory when <c>CS2DEMOKIT_USER_RULES_DIR</c> or its legacy name moves it.</summary>
    public const string TestbedSource = "testbed";

    /// <summary>Whether <paramref name="source" /> tags the user's editable directory.</summary>
    public static bool IsUserSource(string source) => source is "user" or TestbedSource;

    /// <summary>What composing one ruleset produced.</summary>
    /// <param name="Graph">The nodes and edges; the declared entries alone, unwired, when the composition failed.</param>
    /// <param name="Documents">Every document that took part, by id, for the chips and facets a checked stat does not carry.</param>
    /// <param name="Report">
    ///     Everything said about the composition, weighted: the composition's own diagnostics, the loader's for
    ///     every document taking part, the build's skips, and the canvas's warnings about unfinished entries.
    /// </param>
    /// <param name="Chain">The engine's lowered chain for the same documents; empty when the build failed, which the report then says.</param>
    public sealed record Composed(
        RulesetDocumentGraph Graph,
        IReadOnlyDictionary<string, RulesetDoc> Documents,
        IReadOnlyList<RuleDiagnostic> Report,
        RulesetChainGraph Chain)
    {
        /// <summary>The report as text, one line each.</summary>
        public IReadOnlyList<string> Diagnostics { get; } = [.. Report.Select(r => r.Text)];

        /// <summary>The report's entries that carry a line, with the ruleset they are about.</summary>
        public IReadOnlyList<(string RulesetId, int Line, string Message)> PositionedDiagnostics { get; } =
            [.. Report.Where(r => r.IsPositioned).Select(r => (r.RulesetId!, r.Line, r.Message))];

        /// <summary>
        ///     The documents as the engine can take them: the open one without its unfinished entries, which a
        ///     live run evaluates in place of <see cref="Documents" />. Empty when nothing composed.
        /// </summary>
        public IReadOnlyList<RulesetDoc> Evaluable { get; init; } = [];

        /// <summary>
        ///     The key lines of the open document's entries the loader dropped. The document does not hold them,
        ///     so without these a dropped entry's lines read as the end of the entry above it.
        /// </summary>
        public IReadOnlyList<int> DroppedLines { get; init; } = [];

        /// <summary>The id of the document composed, whose show: and params: blocks are drawn; null when nothing loaded.</summary>
        public string? OpenId { get; init; }

        /// <summary>The open document, or null when it is not among the documents.</summary>
        public RulesetDoc? Open => OpenId is { } id && Documents.TryGetValue(id, out RulesetDoc? doc) ? doc : null;

        /// <summary>
        ///     The documents taking part whose file in the user's rules overrides a shipped file of the same id,
        ///     with both paths, so a dependency node can say which copy runs.
        /// </summary>
        public IReadOnlyDictionary<string, (string UserPath, string ShippedPath)> Overrides { get; init; } =
            new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        /// <summary>The ids of the <c>use:</c> dependencies taking part: every document but the open one.</summary>
        public IEnumerable<string> DependencyIds => Documents.Keys.Where(id => id != OpenId);

        /// <summary>Every key a redraw gives a node: the declared entries and the chain's own, scaffolds and surfaces alike.</summary>
        public IEnumerable<RulesetDocumentNodeKey> DrawnKeys => Graph.Nodes.Select(n => n.Key).Concat(Chain.Nodes.Select(n => n.Key)).Distinct();

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
    private sealed record LoadedFile(string Path, string Source, RulesetDoc Doc, IReadOnlyList<RulesetDiagnostic> Diagnostics, string Text);

    private readonly IReadOnlyList<(string Directory, string Source)> _directories;
    private readonly List<LoadedFile> _files;
    private readonly List<(string Path, string Reason)> _failures;
    private readonly Dictionary<string, DateTime> _readAt;
    private readonly Dictionary<string, RulesetDoc> _docs;

    // What the loader said about each document it still loaded: a stray key, a wrong shape. By id, as _docs.
    private readonly Dictionary<string, IReadOnlyList<RulesetDiagnostic>> _loaderDiagnostics;

    // The ids whose user file stands in for a shipped one: the user's path and the shipped path it hides.
    private readonly Dictionary<string, (string UserPath, string ShippedPath)> _overrides;

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
        _overrides = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (LoadedFile file in files)
        {
            // A later directory overrides an earlier one by id, which is the host's own rule: a user file
            // with a shipped ruleset's id replaces it, so an editable copy takes the shipped one's place.
            RulesetDoc doc = file.Doc;
            Entry entry = new(doc.Id, string.IsNullOrWhiteSpace(doc.Title) ? doc.Id : doc.Title, file.Path, file.Source);
            int existing = entries.FindIndex(e => e.Id == doc.Id);
            if (existing >= 0)
            {
                if (entries[existing].Source == "shipped" && IsUserSource(file.Source))
                {
                    _overrides[doc.Id] = (file.Path, entries[existing].Path);
                }

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

    /// <summary>The text <paramref name="id" />'s file held when it loaded, the overriding file's for an overridden id; null when it did not load.</summary>
    public string? TextOf(string id) => _files.LastOrDefault(f => f.Doc.Id == id)?.Text;

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

    /// <summary>How many lines the loader says about <paramref name="text" /> while still loading it; zero when it does not load.</summary>
    public static int LoaderWarningCount(string text, string path)
    {
        try
        {
            RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(text, path);
            return outcome.Doc is null ? 0 : outcome.Diagnostics.Count;
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>The loadable rulesets in the order their directories were given, a later directory's file replacing an earlier one with the same id.</summary>
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>The ids whose file in the user's rules overrides a shipped file of the same id, with both paths.</summary>
    public IReadOnlyDictionary<string, (string UserPath, string ShippedPath)> Overrides => _overrides;

    /// <summary>The directory tagged <c>user</c> or <c>testbed</c>, where an editable copy of a shipped ruleset goes, or null.</summary>
    public string? UserDirectory { get; }

    /// <summary>
    ///     The user directory's tag: <see cref="TestbedSource" /> when an environment variable moves the user
    ///     rules, so the picker says which folder is live, and <c>user</c> otherwise. The engine's precedence.
    /// </summary>
    public static string UserSourceTag(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return !string.IsNullOrEmpty(environment(RuleSetLocator.UserRulesDirEnvVar) ?? environment("DEMOVIEWER_USER_RULES_DIR"))
            ? TestbedSource
            : "user";
    }

    /// <summary>
    ///     The directories the app reads rulesets from, in the order they are offered: the shipped set, then
    ///     the user's own. Either can be missing on a given machine, so each is probed. The browser offers the
    ///     shipped set alone, read-only.
    /// </summary>
    public static IReadOnlyList<(string Directory, string Source)> DefaultDirectories() => DefaultDirectories(OperatingSystem.IsBrowser());

    internal static IReadOnlyList<(string Directory, string Source)> DefaultDirectories(bool browser)
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
            // The browser host gives analysis no user folder, so an edit written there would never run.
            if (browser)
            {
                return dirs;
            }

            try
            {
                // Named even when empty: an editable copy of a shipped ruleset is written here.
                string user = RuleSetLocator.EnsureUserRulesDirectory(shipped);
                if (!string.Equals(user, shipped, StringComparison.OrdinalIgnoreCase))
                {
                    dirs.Add((user, UserSourceTag(Environment.GetEnvironmentVariable)));
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
            if (IsUserSource(source))
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
        return directory is not null && UserDirectory is not null && SamePath(directory, UserDirectory)
            ? _directories.First(d => SamePath(d.Directory, UserDirectory)).Source
            : "shipped";
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
            string text = File.ReadAllText(path);
            RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(text, path);
            if (outcome.Doc is { } doc)
            {
                return new LoadedFile(path, source, doc, outcome.Diagnostics, text);
            }

            failures.Add((path, string.Join("; ", outcome.Diagnostics.Select(d => d.Message))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException
                                       or InvalidOperationException)
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
                [RuleDiagnostic.Of($"{id} is not a loaded ruleset", DiagnosticWeight.Error)], RulesetChainGraph.Empty) { OpenId = id };
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

        // An unfinished entry of the open document is a warning at the entry, not a failed composition:
        // the entries the resolver cannot take are kept out of what it is handed and drawn from the document.
        List<RuleDiagnostic> report = [.. Incompleteness.Of(open)];
        List<RulesetDoc> evaluable = [Incompleteness.Stripped(open), .. docs.Skip(1)];
        HashSet<RulesetDocumentNodeKey> undrawn = [.. Incompleteness.StrippedKeys(open)];

        RulesetComposition.Result composed = RulesetComposition.Compose(evaluable, EmbeddedCatalog.Adapter, 64.0, ProfileName);
        foreach (RulesetCompositionDiagnostic diagnostic in composed.AttributedDiagnostics)
        {
            // A cross-ruleset cycle is attributed to no ruleset; its file still names one.
            int line = diagnostic.Position is { Line: > 0 } at ? at.Line : 0;
            string? file = line > 0 ? diagnostic.Position.File : null;
            string? ruleset = diagnostic.RulesetId
                              ?? taking.Values.FirstOrDefault(d => file is not null && d.Position.File == file)?.Id;
            report.Add(new RuleDiagnostic(diagnostic.ToString() ?? diagnostic.Message, diagnostic.Message, WeightOf(diagnostic.Severity),
                ruleset, ruleset is null ? 0 : line));
        }

        // The loader keeps a document with a stray key or a wrong shape and drops the part it did not
        // understand; the engine runs what remains, so the face must say what was ignored.
        IReadOnlySet<int> drafts = Incompleteness.DraftLines(open);
        foreach (RulesetDoc doc in docs)
        {
            foreach (RulesetDiagnostic diagnostic in _loaderDiagnostics.GetValueOrDefault(doc.Id) ?? [])
            {
                if (ReferenceEquals(doc, open) && diagnostic.Code == RulesetDiagnosticCodes.BadKind && drafts.Contains(diagnostic.Position.Line))
                {
                    continue;
                }

                report.Add(new RuleDiagnostic(diagnostic.ToString(), diagnostic.Message, DiagnosticWeight.Warning, doc.Id, diagnostic.Position.Line));
            }
        }

        // One unresolved reference anywhere empties the checked set; the entries are still in the documents.
        RulesetDocumentGraph graph = composed.Rulesets.Count == 0
            ? RulesetDocumentGraph.FromDocuments(docs, open.Id)
            : RulesetDocumentGraph.Build(composed.Rulesets, open.Id, docs);
        RulesetChainGraph chain = RulesetChainGraph.Empty;
        if (composed.Rulesets.Count > 0)
        {
            try
            {
                RuleChainBuilder builder = new(_events.Value, null, entityProviders: _entityProviders.Value,
                    perPlayerEntityProviders: _perPlayerProviders.Value);
                BuildResult build = builder.Build([.. composed.Rulesets]);
                chain = RulesetChainGraph.Build(build, composed.Rulesets, open);
                foreach (string message in chain.Diagnostics)
                {
                    report.Add(RuleDiagnostic.Of("The rule graph could not draw everything: " + message, DiagnosticWeight.Warning));
                }

                undrawn.UnionWith(BuildSkips(build, report));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException
                                           or KeyNotFoundException or InvalidCastException or NullReferenceException)
            {
                // A document the resolver passed can still fail to lower; the canvas then draws the declared
                // entries alone and says why.
                report.Add(RuleDiagnostic.Of("The rule chain could not be built: " + ex.Message, DiagnosticWeight.Error));
            }
        }

        graph = WithUndrawn(graph, docs, open.Id, undrawn);

        // A build that failed, or never ran, still has the document's show: and params: blocks to draw.
        if (chain.Nodes.Count == 0)
        {
            List<ChainNode> surfaces = [.. SurfaceNodes.ShowNodes(open)];
            if (SurfaceNodes.ParamsNode(open) is { } parameters)
            {
                surfaces.Insert(0, parameters);
            }

            if (surfaces.Count > 0)
            {
                HashSet<RulesetDocumentNodeKey> declared = [.. graph.Nodes.Select(n => n.Key)];
                chain = new RulesetChainGraph(surfaces, [.. SurfaceNodes.ParamsEdges(open, declared), .. SurfaceNodes.ShowEdges(open, declared)]);
            }
        }

        return new Composed(graph, taking, report, chain)
        {
            OpenId = open.Id,
            Evaluable = evaluable,
            DroppedLines = [.. DroppedEntries.Of(_files.Last(f => f.Doc.Id == open.Id).Text, open, report).Select(d => d.Line)],
            Overrides = _overrides.Where(o => taking.ContainsKey(o.Key)).ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal)
        };
    }

    // Composition emits errors; a lint tier would come as warnings, and nothing else is a blank canvas.
    private static DiagnosticWeight WeightOf(DiagnosticSeverity severity) =>
        severity == DiagnosticSeverity.Error ? DiagnosticWeight.Error : DiagnosticWeight.Warning;

    /// <summary>
    ///     Says what the build skipped or dropped, at the entry it is about, so the text lands on the node and
    ///     in the strip: every coverage skip (a view that does not bind on the profile, a tally target or a
    ///     reader skipped with it, a contact read with no geometry) and every ruleset that could not be
    ///     lowered. An entry that cannot run is an error. Returns the entries skipped, to draw from the document.
    /// </summary>
    private static IEnumerable<RulesetDocumentNodeKey> BuildSkips(BuildResult build, List<RuleDiagnostic> report)
    {
        foreach (RulesetCoverageDiagnostic skip in build.RulesetCoverage ?? [])
        {
            string text = CoverageText(skip);
            report.Add(new RuleDiagnostic(text, text, DiagnosticWeight.Error, skip.Ruleset.Id, skip.Position.Line));
            yield return new RulesetDocumentNodeKey(skip.Ruleset.Id, skip.NodeId);
        }

        foreach (ExcludedRuleset excluded in build.ExcludedRulesets)
        {
            foreach (RulesetCompositionDiagnostic reason in excluded.Diagnostics)
            {
                string text = $"{excluded.Id} is left out of the chain: {Plain(reason.Message)}";
                report.Add(new RuleDiagnostic(text, text, DiagnosticWeight.Error, reason.RulesetId ?? excluded.Id,
                    reason.Position is { Line: > 0 } position ? position.Line : 0));
            }
        }
    }

    // An entry the checked set does not hold (skipped by the build, or kept out as unfinished) is drawn from
    // its document, unwired, so its warning has a node to sit on.
    private static RulesetDocumentGraph WithUndrawn(RulesetDocumentGraph graph, IReadOnlyList<RulesetDoc> docs, string openId,
        HashSet<RulesetDocumentNodeKey> keys)
    {
        keys.ExceptWith(graph.Nodes.Select(n => n.Key));
        if (keys.Count == 0)
        {
            return graph;
        }

        IEnumerable<RulesetDocumentNode> undrawn = RulesetDocumentGraph.FromDocuments(docs, openId).Nodes.Where(n => keys.Contains(n.Key));
        return new RulesetDocumentGraph([.. graph.Nodes, .. undrawn], graph.Edges);
    }

    private static string CoverageText(RulesetCoverageDiagnostic skip) =>
        skip.ViewName.Length > 0
        && (skip.Message.Contains("does not bind", StringComparison.Ordinal) || skip.Message.Contains("no binding", StringComparison.Ordinal))
            ? $"view {skip.ViewName} does not bind on profile {skip.ProfileId}; {skip.NodeId} has no wires"
            : $"{skip.NodeId}: {Plain(skip.Message)}";

    // The engine joins its clauses with a dash; the strip reads them as one sentence.
    private static string Plain(string message) => message.Replace(" " + (char)0x2014 + " ", "; ", StringComparison.Ordinal);
}
