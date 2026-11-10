#region

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.Highlights;

/// <summary>A ruleset an extension contributed, as the host keeps it.</summary>
/// <param name="RulesetId">The qualified id the YAML must carry.</param>
/// <param name="Owner">The extension's id.</param>
/// <param name="FeatureId">The feature that switches it.</param>
/// <param name="ReadYaml">Reads the YAML; null when the extension could not supply it.</param>
public sealed record ContributedRuleset(string RulesetId, string Owner, string FeatureId, Func<string?> ReadYaml)
{
    /// <summary>The pass of the owner that reads its tables off the run; null when they are library facts.</summary>
    public string? ReadBy { get; init; }

    /// <summary>True when the YAML reads player commands, so the reader's visit keeps them in a held parse.</summary>
    public bool ReadsUserCommands { get; init; }
}

/// <summary>
///     The three rule layers in order: the shipped rules, then the extensions' rulesets, then the user's own
///     folder, where a same-id file overrides either. An extension ruleset is left out, with a log line, when it
///     does not load, carries another id than its qualified one, takes a shipped id, or declares a table another
///     ruleset already declares; nothing else in the set moves.
/// </summary>
public static class RuleLayers
{
    private static readonly ConditionalWeakTable<RulesetDoc, string> _sources = new();

    /// <summary>
    ///     <see cref="YamlConfigLoader.LoadWithOverlay" /> that survives a user file YamlDotNet cannot scan. The
    ///     scanner throws InvalidOperationException, not YamlException, on an unclosed flow sequence such as
    ///     <c>ruleset: [</c>, and the engine catches only the latter. The loader reads a folder whole, so the user
    ///     tier is set aside until the file is fixed, with an error naming it.
    /// </summary>
    /// <param name="shippedDir">The shipped rules folder.</param>
    /// <param name="userDir">The user's rules folder, or null for none.</param>
    public static RuleConfigLoadResult LoadWithOverlay(string shippedDir, string? userDir)
    {
        try
        {
            return YamlConfigLoader.LoadWithOverlay(shippedDir, userDir);
        }
        catch (InvalidOperationException ex) when (userDir is not null)
        {
            RuleConfigLoadResult shipped = YamlConfigLoader.LoadWithOverlay(shippedDir, null);
            List<RuleConfigError> broken = UnscannableFiles(userDir, ex);
            return shipped with
            {
                Errors = [.. shipped.Errors, .. broken],
                FailedFiles = [.. shipped.FailedFiles, .. broken.Select(b => b.FilePath!)]
            };
        }
    }

    /// <summary><see cref="YamlConfigLoader.TryLoadDirectory" /> with the same guard as <see cref="LoadWithOverlay" />.</summary>
    /// <param name="directory">The rules folder.</param>
    public static RuleConfigLoadResult TryLoadDirectory(string directory)
    {
        try
        {
            return YamlConfigLoader.TryLoadDirectory(directory);
        }
        catch (InvalidOperationException ex)
        {
            List<RuleConfigError> broken = UnscannableFiles(directory, ex);
            return new RuleConfigLoadResult(broken, [], [.. broken.Select(b => b.FilePath!)]);
        }
    }

    // Finds the files the scanner cannot read; a YamlException is the engine's to report, so only the other
    // failures count. Falls back to one error on the folder when no file reproduces the throw.
    private static List<RuleConfigError> UnscannableFiles(string directory, Exception thrown)
    {
        List<RuleConfigError> errors = [];
        IEnumerable<string> files;
        try
        {
            files = Directory.GetFiles(directory, "*.yaml").Concat(Directory.GetFiles(directory, "*.yml")).Order(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = [];
        }

        foreach (string file in files)
        {
            try
            {
                using StreamReader reader = new(file);
                new YamlDotNet.RepresentationModel.YamlStream().Load(reader);
            }
            catch (YamlDotNet.Core.YamlException)
            {
                // The engine reports these itself.
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                errors.Add(new RuleConfigError(file,
                    $"the YAML could not be read ({ex.Message}); fix or remove this file, the user rules folder is skipped until then"));
            }
        }

        if (errors.Count == 0)
        {
            errors.Add(new RuleConfigError(directory,
                $"the user rules folder could not be read ({thrown.Message}); it is skipped until the files in it load"));
        }

        return errors;
    }

    /// <summary>Reads the shipped folder with the user's overlay, and places the extension rulesets between them.</summary>
    /// <param name="shippedDir">The shipped rules folder.</param>
    /// <param name="userDir">The user's rules folder, or null for none.</param>
    /// <param name="extensions">The contributed rulesets.</param>
    /// <param name="log">Where a left-out ruleset is reported.</param>
    public static RuleConfigLoadResult Load(string shippedDir, string? userDir,
        IReadOnlyList<ContributedRuleset> extensions, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(log);
        RuleConfigLoadResult merged = LoadWithOverlay(shippedDir, userDir);
        if (extensions.Count == 0)
        {
            return merged;
        }

        HashSet<string> shippedIds = new(TryLoadDirectory(shippedDir).Rulesets.Select(r => r.Id),
            StringComparer.Ordinal);
        HashSet<string> userIds = userDir is not null && Directory.Exists(userDir)
            ? new HashSet<string>(TryLoadDirectory(userDir).Rulesets.Select(r => r.Id),
                StringComparer.Ordinal)
            : [];
        return WithExtensions(merged, shippedIds, userIds, extensions, log);
    }

    /// <summary>
    ///     Places <paramref name="extensions" /> into an already merged read. <paramref name="userIds" /> are the ids
    ///     the user's folder declares, enabled or not: a user file of an extension's id wins, and one that disables
    ///     it removes it.
    /// </summary>
    public static RuleConfigLoadResult WithExtensions(RuleConfigLoadResult merged, IReadOnlySet<string> shippedIds,
        IReadOnlySet<string> userIds, IReadOnlyList<ContributedRuleset> extensions, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(merged);
        List<RulesetDoc> docs = [.. merged.Rulesets];
        HashSet<string> tables = new(docs.SelectMany(TablesOf), StringComparer.Ordinal);
        HashSet<string> taken = new(docs.Select(d => d.Id), StringComparer.Ordinal);
        foreach (ContributedRuleset contributed in extensions)
        {
            if (shippedIds.Contains(contributed.RulesetId))
            {
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner,
                    "it takes the id of a shipped ruleset");
                continue;
            }

            if (userIds.Contains(contributed.RulesetId))
            {
                continue;
            }

            string? yaml = contributed.ReadYaml();
            if (yaml is null)
            {
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner, "the extension supplied no YAML");
                continue;
            }

            RuleConfigLoadResult read = YamlConfigLoader.LoadDocuments([
                (contributed.Owner + "/" + contributed.RulesetId + ".rules.yaml", yaml)
            ]);
            if (!read.Success || read.Rulesets.Count != 1)
            {
                string why = read.Errors.Count > 0
                    ? string.Join("; ", read.Errors.Take(3).Select(e => e.ToString()))
                    : "it holds no ruleset, or more than one";
                string reason = "it does not load: " + why;
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner, reason);
                continue;
            }

            RulesetDoc doc = read.Rulesets[0];
            if (!string.Equals(doc.Id, contributed.RulesetId, StringComparison.Ordinal))
            {
                string reason = $"its ruleset: key is '{doc.Id}'";
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner, reason);
                continue;
            }

            if (taken.Contains(doc.Id))
            {
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner, "another ruleset has its id");
                continue;
            }

            if (TablesOf(doc).FirstOrDefault(tables.Contains) is { } clash)
            {
                string reason = $"another ruleset declares its table '{clash}'";
                RuleLayersLog.LeftOut(log, contributed.RulesetId, contributed.Owner, reason);
                continue;
            }

            if (!doc.Enabled)
            {
                continue;
            }

            _sources.AddOrUpdate(doc, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))));
            docs.Add(doc);
            taken.Add(doc.Id);
            tables.UnionWith(TablesOf(doc));
        }

        return new RuleConfigLoadResult(merged.Errors, merged.LoadedFiles, merged.FailedFiles) { Rulesets = docs };
    }

    /// <summary>The SHA-256 of the YAML an extension ruleset was read from, or null for a ruleset read from a file.</summary>
    /// <param name="doc">The ruleset.</param>
    public static string? SourceOf(RulesetDoc doc) => _sources.TryGetValue(doc, out string? source) ? source : null;

    private static IEnumerable<string> TablesOf(RulesetDoc doc) => doc.Show?.Tables.Select(t => t.Name) ?? [];
}

/// <summary>The rule layers' log seams.</summary>
internal static partial class RuleLayersLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "rules: ruleset {rulesetId} of {extension} was left out: {reason}")]
    public static partial void LeftOut(ILogger logger, string rulesetId, string extension, string reason);
}
