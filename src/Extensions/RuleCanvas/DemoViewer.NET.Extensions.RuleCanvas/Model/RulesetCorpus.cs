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
    /// <param name="Diagnostics">The composition's diagnostics, as text.</param>
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

    private readonly Dictionary<string, RulesetDoc> _docs;

    /// <summary>The demo-source profile every demo-less composition runs under, by the id the catalog keys its views on.</summary>
    internal static string ProfileName => DemoSourceProfileRegistry.DefaultFallback.GetType().Name;

    private RulesetCorpus(IReadOnlyList<Entry> entries, Dictionary<string, RulesetDoc> docs, string? userDirectory,
        IReadOnlyList<(string Path, string Reason)> failures)
    {
        Entries = entries;
        _docs = docs;
        UserDirectory = userDirectory;
        Failures = failures;
    }

    /// <summary>The files that did not load as rulesets, with the loader's reason, so a broken file is named rather than silently absent.</summary>
    public IReadOnlyList<(string Path, string Reason)> Failures { get; }

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
        List<Entry> entries = [];
        Dictionary<string, RulesetDoc> docs = new(StringComparer.Ordinal);
        List<(string, string)> failures = [];
        string? userDirectory = null;
        foreach ((string directory, string source) in directories)
        {
            if (source == "user")
            {
                userDirectory ??= directory;
            }

            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(directory, "*.rules.yaml").OrderBy(p => p, StringComparer.Ordinal))
            {
                RulesetDoc? doc;
                try
                {
                    RulesetDocumentLoader.Outcome outcome = RulesetDocumentLoader.Load(File.ReadAllText(path), path);
                    doc = outcome.Doc;
                    if (doc is null)
                    {
                        failures.Add((path, string.Join("; ", outcome.Diagnostics.Select(d => d.Message))));
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
                {
                    failures.Add((path, ex.Message));
                    continue;
                }

                // A later directory overrides an earlier one by id, which is the host's own rule: a user file
                // with a shipped ruleset's id replaces it, so an editable copy takes the shipped one's place.
                Entry entry = new(doc.Id, string.IsNullOrWhiteSpace(doc.Title) ? doc.Id : doc.Title, path, source);
                int existing = entries.FindIndex(e => e.Id == doc.Id);
                if (existing >= 0)
                {
                    entries[existing] = entry;
                }
                else
                {
                    entries.Add(entry);
                }

                docs[doc.Id] = doc;
            }
        }

        return new RulesetCorpus(entries, docs, userDirectory, failures);
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
