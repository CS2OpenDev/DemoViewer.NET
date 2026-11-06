#region

using DemoViewer.NET.RuleAuthoring;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     The edits the canvas makes, each as a function from the document's text to the document's text,
///     through the splice writer so comments, key order and quoting are untouched. Every operation
///     throws <see cref="InvalidOperationException" /> or <see cref="NotSupportedException" /> rather than
///     guessing when the document is not shaped as expected; the caller reports and leaves the file alone.
/// </summary>
public static class RulesetEdits
{
    private static readonly YamlPath _highlights = YamlPath.Root.Key("highlights");

    /// <summary>
    ///     Sets one <c>match:</c> facet of a stat to <paramref name="value" />, written as the author would
    ///     write it (<c>true</c>, <c>1</c>, <c>&lt;= 320</c>). A stat with no <c>match:</c> block gets one,
    ///     in the flow form the corpus writes.
    /// </summary>
    public static string SetFacet(string text, string statId, string facet, string value)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        YamlPath match = RulesetDocumentEditor.StatPath(statId).Key("match");
        if (doc.Find(match) is YamlMappingNode map)
        {
            // An existing value keeps the style its author chose; the writer handles that case whole. A value
            // that is a collection (a range or a list) is refused: appending would write a duplicate key.
            YamlNode? existing = doc.Find(match.Key(facet));
            if (existing is YamlScalarNode)
            {
                return editor.Apply([YamlEdits.SetScalar(doc, match.Key(facet), value)]).Text;
            }

            if (existing is not null)
            {
                throw new InvalidOperationException(
                    $"{facet} holds a {(existing is YamlSequenceNode ? "list or range" : "collection")}; edit it in the source pane");
            }

            // A NEW facet is appended as written. The writer's own append would quote a value YAML reads as
            // a boolean or a number, which is right for a label and wrong here: `enemy: "true"` is not the
            // facet test `enemy: true`.
            TextEdit append;
            if (map.Style == MappingStyle.Flow)
            {
                append = map.Children.Count > 0
                    ? new TextEdit(doc.EndOf(map.Children.Last().Value), 0, $", {facet}: {value}", $"add {match.Key(facet)}")
                    : new TextEdit((int)map.Start.Index + 1, 0, $"{facet}: {value}", $"add {match.Key(facet)}");
            }
            else
            {
                KeyValuePair<YamlNode, YamlNode> last = map.Children.Last();
                string indent = doc.IndentOf((int)map.Children.First().Key.Start.Index);
                int at = doc.StartOfNextLine(doc.EndOf(last.Value));
                append = new TextEdit(at, 0, $"{indent}{facet}: {value}{doc.NewLine}", $"add {match.Key(facet)}");
            }

            return editor.Apply([append]).Text;
        }

        if (doc.Find(RulesetDocumentEditor.StatPath(statId)) is not YamlMappingNode)
        {
            throw new InvalidOperationException($"{statId} is not a stat in this document");
        }

        return editor.Apply([YamlEdits.AddEntry(doc, RulesetDocumentEditor.StatPath(statId),
            $"match: {{ {facet}: {value} }}")]).Text;
    }

    /// <summary>
    ///     Removes one facet from a stat's <c>match:</c> block. The last facet takes the block with it. A
    ///     flow block (<c>{ a: 1, b: 2 }</c>) is edited in place here, since the writer refuses that case on
    ///     its own; a block-style one goes through the writer.
    /// </summary>
    public static string RemoveFacet(string text, string statId, string facet)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlPath statPath = RulesetDocumentEditor.StatPath(statId);
        YamlPath match = statPath.Key("match");
        if (editor.Document.Find(match) is not YamlMappingNode map)
        {
            throw new InvalidOperationException($"{statId} has no match: block");
        }

        if (!map.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == facet))
        {
            throw new InvalidOperationException($"{statId} has no facet {facet}");
        }

        if (map.Children.Count == 1)
        {
            return editor.RemoveStatField(statId, "match").Text;
        }

        if (map.Style != MappingStyle.Flow)
        {
            return editor.Apply([YamlEdits.RemoveKey(editor.Document, match, facet)]).Text;
        }

        // `{ a: 1, b: 2, c: 3 }`: an entry that is not last goes with the separator after it, from its key
        // to the next key; the last one goes with the separator before it, from the previous value's end.
        List<KeyValuePair<YamlNode, YamlNode>> entries = [.. map.Children];
        int index = entries.FindIndex(e => (e.Key as YamlScalarNode)?.Value == facet);
        int from, to;
        if (index < entries.Count - 1)
        {
            from = (int)entries[index].Key.Start.Index;
            to = (int)entries[index + 1].Key.Start.Index;
        }
        else
        {
            from = editor.Document.EndOf(entries[index - 1].Value);
            to = editor.Document.EndOf(entries[index].Value);
        }

        return editor.Apply([new TextEdit(from, to - from, "", $"remove {match.Key(facet)}")]).Text;
    }

    /// <summary>Sets one scalar field of a stat (<c>while</c>, <c>on</c>, <c>count</c>), adding or replacing it.</summary>
    public static string SetField(string text, string statId, string key, string value)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        if (editor.Document.Find(RulesetDocumentEditor.StatPath(statId)) is not YamlMappingNode)
        {
            throw new InvalidOperationException($"{statId} is not a stat in this document");
        }

        return editor.SetStatField(statId, key, value).Text;
    }

    /// <summary>The scalar a stat holds under <paramref name="key" /> (dotted for a nested key), or null.</summary>
    public static string? Field(string text, string statId, string key)
    {
        YamlPath path = RulesetDocumentEditor.StatPath(statId);
        foreach (string part in key.Split('.'))
        {
            path = path.Key(part);
        }

        return RulesetDocumentEditor.Open(text).Scalar(path);
    }

    /// <summary>Removes a stat's scalar field when it has one; the text is unchanged otherwise.</summary>
    public static string RemoveFieldIfPresent(string text, string statId, string key)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        return editor.Document.Find(RulesetDocumentEditor.StatPath(statId).Key(key)) is null
            ? text
            : editor.RemoveStatField(statId, key).Text;
    }

    /// <summary>
    ///     Sets a flag's <c>when:</c>, whichever of the two spellings the author used: nested under
    ///     <c>flag:</c>, or <c>flag: true</c> with <c>when:</c> beside it.
    /// </summary>
    public static string SetFlagWhen(string text, string statId, string when)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlPath stat = RulesetDocumentEditor.StatPath(statId);
        // The writer keeps an existing scalar's quoting and quotes a new one as the text needs.
        if (editor.Document.Find(stat.Key("flag")) is YamlMappingNode)
        {
            return editor.Apply([YamlEdits.SetKey(editor.Document, stat.Key("flag"), "when", when)]).Text;
        }

        if (editor.Document.Find(stat) is not YamlMappingNode)
        {
            throw new InvalidOperationException($"{statId} is not a stat in this document");
        }

        return editor.Apply([YamlEdits.SetKey(editor.Document, stat, "when", when)]).Text;
    }

    /// <summary>Appends a stat block (composed at zero indentation) under <c>stats:</c>.</summary>
    public static string AddStat(string text, string block) => RulesetDocumentEditor.Open(text).AddStat(block).Text;

    /// <summary>Removes a stat entry whole, its comments inside it included; a comment above it stays.</summary>
    public static string RemoveStat(string text, string statId) =>
        RulesetDocumentEditor.Open(text).RemoveStat(statId).Text;

    /// <summary>Removes a highlight entry whole.</summary>
    public static string RemoveHighlight(string text, string highlightId)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        return editor.Apply([YamlEdits.RemoveKey(editor.Document, _highlights, highlightId)]).Text;
    }

    /// <summary>The stat ids the document declares, in document order.</summary>
    public static IReadOnlyList<string> StatIds(string text) => RulesetDocumentEditor.Open(text).StatIds;
}
