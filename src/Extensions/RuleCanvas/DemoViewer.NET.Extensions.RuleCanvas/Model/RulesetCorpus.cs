#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Profiles;
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
    /// <param name="Graph">The nodes and edges; empty when the composition failed.</param>
    /// <param name="Documents">Every document that took part, by id, for the chips and facets a checked stat does not carry.</param>
    /// <param name="Diagnostics">The composition's diagnostics, as text.</param>
    /// <param name="PositionedDiagnostics">The same diagnostics that carry a line, with the ruleset they are about.</param>
    public sealed record Composed(
        RulesetDocumentGraph Graph,
        IReadOnlyDictionary<string, RulesetDoc> Documents,
        IReadOnlyList<string> Diagnostics,
        IReadOnlyList<(string RulesetId, int Line, string Message)> PositionedDiagnostics);

    private readonly Dictionary<string, RulesetDoc> _docs;

    private RulesetCorpus(IReadOnlyList<Entry> entries, Dictionary<string, RulesetDoc> docs, string? userDirectory)
    {
        Entries = entries;
        _docs = docs;
        UserDirectory = userDirectory;
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
                    doc = RulesetDocumentLoader.Load(File.ReadAllText(path), path).Doc;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (doc is null)
                {
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

        return new RulesetCorpus(entries, docs, userDirectory);
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
                [$"{id} is not a loaded ruleset"], []);
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

        RulesetComposition.Result composed = RulesetComposition.Compose(docs,
            CatalogScopeAdapter.From(CatalogResource.Load()), 64.0,
            DemoSourceProfileRegistry.DefaultFallback.GetType().Name);
        List<string> diagnostics = [.. composed.Diagnostics.Select(d => d.ToString() ?? "")];
        List<(string, int, string)> positioned = [];
        foreach (RulesetDiagnostic diagnostic in composed.Diagnostics)
        {
            if (diagnostic.Position.Line > 0 && diagnostic.Position.File is { } file)
            {
                string? ruleset = taking.Values.FirstOrDefault(d => d.Position.File == file)?.Id;
                if (ruleset is not null)
                {
                    positioned.Add((ruleset, diagnostic.Position.Line, diagnostic.Message));
                }
            }
        }

        RulesetDocumentGraph graph = composed.Rulesets.Count == 0
            ? RulesetDocumentGraph.Empty
            : RulesetDocumentGraph.Build(composed.Rulesets, open.Id);
        return new Composed(graph, taking, diagnostics, positioned);
    }
}
