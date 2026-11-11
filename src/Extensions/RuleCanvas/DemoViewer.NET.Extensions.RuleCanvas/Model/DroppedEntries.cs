#region

using System.Text.RegularExpressions;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.RuleAuthoring;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>An entry the text declares that the loaded document does not hold.</summary>
/// <param name="Id">The key as the text spells it.</param>
/// <param name="Section">The section it is under.</param>
/// <param name="Line">The line of its key, 1-based.</param>
/// <param name="Diagnostic">The error the list shows, positioned at the key.</param>
/// <param name="Consumed">The loader's own lines inside the entry, which the error carries and the list need not repeat.</param>
public sealed record DroppedEntry(string Id, EntrySection Section, int Line, RuleDiagnostic Diagnostic, IReadOnlyList<RuleDiagnostic> Consumed);

/// <summary>
///     The entries the loader dropped: keys under <c>stats:</c>, <c>highlights:</c> and <c>define:</c> that
///     the loaded document does not hold. A body the loader rejects outright never reaches the document, and
///     some shapes are rejected without a word, so the text is the only witness.
/// </summary>
public static class DroppedEntries
{
    private static readonly (string Key, EntrySection Section)[] _sections =
        [("stats", EntrySection.Stat), ("highlights", EntrySection.Highlight), ("define", EntrySection.Define)];

    /// <summary>
    ///     Each entry of <paramref name="text" /> missing from <paramref name="doc" />, with the loader's reason
    ///     from <paramref name="report" /> when one of its lines falls inside the entry; empty when the text
    ///     does not parse.
    /// </summary>
    public static IReadOnlyList<DroppedEntry> Of(string text, RulesetDoc doc, IReadOnlyList<RuleDiagnostic> report)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(report);
        YamlMappingNode? root;
        try
        {
            root = RulesetDocumentEditor.Open(text).Document.Find(YamlPath.Root) as YamlMappingNode;
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            return [];
        }

        if (root is null)
        {
            return [];
        }

        HashSet<string> stats = [.. doc.Stats.Select(s => s.Id)];
        HashSet<string> highlights = [.. doc.Highlights.Select(h => h.Id)];
        HashSet<string> defines = [.. doc.Defines.Select(d => d.Name)];
        List<(string Id, EntrySection Section, int Line)> keys = [];
        List<int> starts = [];
        foreach ((YamlNode key, YamlNode value) in root.Children)
        {
            starts.Add((int)key.Start.Line);
            string? name = (key as YamlScalarNode)?.Value;
            if (_sections.FirstOrDefault(s => s.Key == name) is not { Key: not null } section || value is not YamlMappingNode entries)
            {
                continue;
            }

            foreach ((YamlNode entryKey, YamlNode _) in entries.Children)
            {
                int line = (int)entryKey.Start.Line;
                starts.Add(line);
                if (entryKey is YamlScalarNode { Value: { Length: > 0 } id })
                {
                    keys.Add((id, section.Section, line));
                }
            }
        }

        starts.Sort();
        List<RuleDiagnostic> loader = [.. report.Where(r => r.RulesetId == doc.Id && r.Line > 0)];
        List<DroppedEntry> dropped = [];
        foreach ((string id, EntrySection section, int line) in keys)
        {
            HashSet<string> loaded = section switch
            {
                EntrySection.Highlight => highlights,
                EntrySection.Define => defines,
                _ => stats
            };
            if (Holds(loaded, id))
            {
                continue;
            }

            int end = starts.FirstOrDefault(s => s > line, int.MaxValue);
            List<RuleDiagnostic> inside = [.. loader.Where(r => r.Line >= line && r.Line < end)];
            string reason = inside.Count > 0 ? string.Join("; ", inside.Select(r => r.Message).Distinct(StringComparer.Ordinal)) : "its body was rejected";
            string message = $"{id} did not load: {reason}";
            dropped.Add(new DroppedEntry(id, section, line, new RuleDiagnostic(message, message, DiagnosticWeight.Error, doc.Id, line), inside));
        }

        return dropped;
    }

    // A for_each template is held under the ids it expands to, so any id its holes match counts.
    private static bool Holds(HashSet<string> loaded, string id)
    {
        if (loaded.Contains(id))
        {
            return true;
        }

        if (!id.Contains('{', StringComparison.Ordinal))
        {
            return false;
        }

        Regex template = new("^" + Regex.Replace(Regex.Escape(id), @"\\\{[^}]*}", ".+") + "$", RegexOptions.CultureInvariant);
        return loaded.Any(template.IsMatch);
    }
}
