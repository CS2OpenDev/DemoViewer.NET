#region

using DemoViewer.NET.RuleAuthoring;
using YamlDotNet.RepresentationModel;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>The document section an entry lives in.</summary>
public enum EntrySection
{
    Stat,
    Highlight,
    Define
}

/// <summary>One YAML key of an entry with its value as the file spells it, on one line.</summary>
/// <param name="Key">The key.</param>
/// <param name="Value">The value's source text, collapsed to one line.</param>
public sealed record EntryKey(string Key, string Value)
{
    public override string ToString() => Key + ": " + Value;
}

/// <summary>
///     The text of one entry as the file holds it, for a duplicate: the body under the id line, dedented
///     to zero so the splice writer's add re-indents it wherever it lands. Comments inside the entry go
///     with it; the id line does not.
/// </summary>
public static class RulesetEntryText
{
    private static readonly YamlPath _highlights = YamlPath.Root.Key("highlights");

    private static readonly YamlPath _define = YamlPath.Root.Key("define");

    /// <summary>The body of <paramref name="id" />'s entry at zero indentation, without its id line.</summary>
    /// <exception cref="InvalidOperationException">The entry is not in the document.</exception>
    public static string BodyOf(string text, string id, bool highlight) =>
        BodyAt(text, highlight ? _highlights.Key(id) : RulesetDocumentEditor.StatPath(id), id);

    /// <summary>The body of the define <paramref name="name" /> at zero indentation, without its name line.</summary>
    /// <exception cref="InvalidOperationException">The define is not in the document.</exception>
    public static string DefineBodyOf(string text, string name) => BodyAt(text, _define.Key(name), name);

    /// <summary>The 1-based line of <paramref name="ruleset" /> in the document's <c>use:</c> list, or of the key itself; zero when neither is there.</summary>
    public static int UseLineOf(string text, string ruleset)
    {
        ArgumentNullException.ThrowIfNull(text);
        YamlDocumentText doc;
        try
        {
            doc = RulesetDocumentEditor.Open(text).Document;
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            return 0;
        }

        YamlPath use = YamlPath.Root.Key("use");
        if (doc.Find(use) is YamlSequenceNode list && list.Children.OfType<YamlScalarNode>().FirstOrDefault(n => n.Value == ruleset) is { } item)
        {
            return (int)item.Start.Line;
        }

        return doc.FindKey(use) is { } key ? (int)key.Start.Line : 0;
    }

    /// <summary>The path of <paramref name="id" />'s entry in <paramref name="section" />.</summary>
    public static YamlPath PathOf(EntrySection section, string id) => section switch
    {
        EntrySection.Highlight => _highlights.Key(id),
        EntrySection.Define => _define.Key(id),
        _ => RulesetDocumentEditor.StatPath(id)
    };

    /// <summary>The section a node's key names: its define, highlight or stat section.</summary>
    public static EntrySection SectionOf(RulesetDocumentNodeKey key, bool highlight) =>
        key.IsDefine ? EntrySection.Define : highlight ? EntrySection.Highlight : EntrySection.Stat;

    /// <summary>
    ///     The top-level keys of <paramref name="id" />'s entry with their values as written, in document
    ///     order; empty when the document has no such entry, which is also what an expanded id gets.
    /// </summary>
    public static IReadOnlyList<EntryKey> KeysOf(string text, EntrySection section, string id)
    {
        ArgumentNullException.ThrowIfNull(text);
        return KeysOf(RulesetDocumentEditor.Open(text).Document, section, id);
    }

    /// <summary>As <see cref="KeysOf(string, EntrySection, string)" />, over a document already parsed.</summary>
    public static IReadOnlyList<EntryKey> KeysOf(YamlDocumentText doc, EntrySection section, string id)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Find(PathOf(section, id)) is not YamlMappingNode map)
        {
            return [];
        }

        List<EntryKey> keys = [];
        foreach ((YamlNode key, YamlNode value) in map.Children)
        {
            if (key is YamlScalarNode { Value: { } name })
            {
                keys.Add(new EntryKey(name, OneLine(doc, value)));
            }
        }

        return keys;
    }

    /// <summary>
    ///     The entry ids of <paramref name="section" /> whose id holds a <c>{key}</c> hole: the templates the
    ///     loader expands, which the canvas draws under their expanded ids.
    /// </summary>
    public static IReadOnlyList<string> TemplateIds(string text, EntrySection section) =>
        TemplateIds(RulesetDocumentEditor.Open(text).Document, section);

    /// <summary>As <see cref="TemplateIds(string, EntrySection)" />, over a document already parsed.</summary>
    public static IReadOnlyList<string> TemplateIds(YamlDocumentText doc, EntrySection section)
    {
        ArgumentNullException.ThrowIfNull(doc);
        YamlPath path = section switch { EntrySection.Highlight => _highlights, EntrySection.Define => _define, _ => YamlPath.Root.Key("stats") };
        return doc.Find(path) is YamlMappingNode map
            ? [.. map.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).OfType<string>().Where(k => k.Contains('{'))]
            : [];
    }

    /// <summary>
    ///     The template an expanded id came from: the entry whose <c>{key}</c> holes match <paramref name="id" />
    ///     once each hole stands for one of the axis's values. Null for an id the document declares outright
    ///     and for one no template produces.
    /// </summary>
    public static string? TemplateOf(string text, EntrySection section, string id)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TemplateOf(RulesetDocumentEditor.Open(text).Document, section, id);
    }

    /// <summary>As <see cref="TemplateOf(string, EntrySection, string)" />, over a document already parsed.</summary>
    public static string? TemplateOf(YamlDocumentText doc, EntrySection section, string id)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Find(PathOf(section, id)) is not null)
        {
            return null;
        }

        foreach (string template in TemplateIds(doc, section))
        {
            IReadOnlyList<(string Key, IReadOnlyList<string> Values)> axes = RulesetEdits.ForEachOf(doc, section, template);
            if (axes.Count == 0)
            {
                continue;
            }

            foreach (IReadOnlyDictionary<string, string> combination in Combinations(axes))
            {
                string expanded = template;
                foreach ((string key, string value) in combination)
                {
                    expanded = expanded.Replace("{" + key + "}", value, StringComparison.Ordinal);
                }

                if (expanded == id)
                {
                    return template;
                }
            }
        }

        return null;
    }

    // Row-major, last axis fastest, as the loader expands.
    private static IEnumerable<IReadOnlyDictionary<string, string>> Combinations(IReadOnlyList<(string Key, IReadOnlyList<string> Values)> axes)
    {
        int[] at = new int[axes.Count];
        while (true)
        {
            Dictionary<string, string> row = new(StringComparer.Ordinal);
            for (int i = 0; i < axes.Count; i++)
            {
                row[axes[i].Key] = axes[i].Values[at[i]];
            }

            yield return row;
            int axis = axes.Count - 1;
            while (axis >= 0 && ++at[axis] == axes[axis].Values.Count)
            {
                at[axis--] = 0;
            }

            if (axis < 0)
            {
                yield break;
            }
        }
    }

    // A value as the file spells it, on one line: a scalar's text, a collection's source with its line breaks folded.
    private static string OneLine(YamlDocumentText doc, YamlNode value)
    {
        (int start, int length) = doc.SpanOf(value);
        string source = length > 0 && start + length <= doc.Text.Length ? doc.Text.Substring(start, length) : (value as YamlScalarNode)?.Value ?? "";
        return string.Join(' ', source.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    private static string BodyAt(string text, YamlPath path, string id)
    {
        ArgumentNullException.ThrowIfNull(text);
        YamlDocumentText doc = RulesetDocumentEditor.Open(text).Document;
        if (doc.FindKey(path) is not { } key || doc.Find(path) is not YamlMappingNode value)
        {
            throw new InvalidOperationException($"{id} is not an entry of this document");
        }

        int from = doc.StartOfLine((int)key.Start.Index);
        int to = doc.StartOfNextLine(doc.EndOf(value));
        string[] lines = text[from..to].Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // A flow mapping sits on the id line itself: `id: { count: kill }`.
        string rest = lines[0][(lines[0].IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
        if (rest.Length > 0)
        {
            return rest;
        }

        List<string> body = [.. lines.Skip(1)];
        while (body.Count > 0 && string.IsNullOrWhiteSpace(body[^1]))
        {
            body.RemoveAt(body.Count - 1);
        }

        int indent = body.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', body.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }
}
