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

    /// <summary>The keys of a stat's <c>match:</c> block, in document order; empty when it has none.</summary>
    public static IReadOnlyList<string> FacetKeys(string text, string statId) =>
        KeysUnder(RulesetDocumentEditor.Open(text).Document, RulesetDocumentEditor.StatPath(statId).Key("match"));

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

    /// <summary>
    ///     Sets <paramref name="key" /> inside the mapping a stat holds under <paramref name="parent" /> (a
    ///     <c>rate: { of, per }</c>), adding the key when the mapping lacks it. The mapping itself must exist.
    /// </summary>
    public static string SetNestedField(string text, string statId, string parent, string key, string value)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlPath path = RulesetDocumentEditor.StatPath(statId).Key(parent);
        if (editor.Document.Find(path) is not YamlMappingNode)
        {
            throw new InvalidOperationException($"{statId}.{parent} is not a mapping in this document");
        }

        return editor.Apply([YamlEdits.SetKey(editor.Document, path, key, value)]).Text;
    }

    /// <summary>
    ///     Sets a compute's formula in whichever spelling the author used: the scalar, or <c>value:</c> of the
    ///     <c>compute: { value, live }</c> mapping, whose <c>live:</c> stays.
    /// </summary>
    public static string SetComputeFormula(string text, string statId, string formula)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlPath compute = RulesetDocumentEditor.StatPath(statId).Key("compute");
        return editor.Document.Find(compute) is YamlMappingNode
            ? editor.Apply([YamlEdits.SetKey(editor.Document, compute, "value", formula)]).Text
            : SetField(text, statId, "compute", formula);
    }

    /// <summary>
    ///     Sets or, on null, removes a compute's <c>live:</c> cadence. A scalar <c>compute:</c> becomes the
    ///     <c>{ value, live }</c> mapping when the cadence is set; removing it from a mapping leaves the mapping.
    /// </summary>
    public static string SetComputeLive(string text, string statId, string? live)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        YamlPath compute = RulesetDocumentEditor.StatPath(statId).Key("compute");
        switch (doc.Find(compute))
        {
            case YamlMappingNode map:
                if (live is not null)
                {
                    return editor.Apply([YamlEdits.SetKey(doc, compute, "live", live)]).Text;
                }

                if (doc.Find(compute.Key("live")) is null)
                {
                    return text;
                }

                return map.Style == MappingStyle.Flow
                    ? editor.Apply([RemoveFlowKey(doc, map, "live", compute)]).Text
                    : editor.Apply([YamlEdits.RemoveKey(doc, compute, "live")]).Text;
            case YamlScalarNode scalar:
                if (live is null)
                {
                    return text;
                }

                if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded)
                {
                    throw new NotSupportedException($"{statId}.compute is a block scalar; edit it in the source pane");
                }

                // A quoted formula is carried verbatim; a plain one is quoted where the flow form needs it.
                (int start, int length) = doc.SpanOf(scalar);
                string formula = scalar.Style == ScalarStyle.Plain ? PlainOrQuoted(scalar.Value ?? "", inFlow: true) : doc.Text.Substring(start, length);
                return editor.Apply([new TextEdit(start, length, $"{{ value: {formula}, live: {live} }}", $"set {compute}.live")]).Text;
            default:
                throw new InvalidOperationException($"{statId} is not a compute in this document");
        }
    }

    /// <summary>
    ///     Replaces a tally's <c>thresholds:</c> with <paramref name="rows" />, written as the block sequence of
    ///     flow maps the corpus uses, in the order given; the key is added when the stat lacks it. Only the
    ///     items are replaced, so the key's own line and a comment on it stay.
    /// </summary>
    public static string SetThresholds(string text, string statId, IReadOnlyList<(string Min, string Target)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("a tally needs at least one threshold");
        }

        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        YamlPath stat = RulesetDocumentEditor.StatPath(statId);
        if (doc.Find(stat) is not YamlMappingNode)
        {
            throw new InvalidOperationException($"{statId} is not a stat in this document");
        }

        string[] items = [.. rows.Select(r => $"- {{ min: {r.Min}, target: {r.Target} }}")];
        YamlPath path = stat.Key("thresholds");
        if (doc.Find(path) is not { } existing)
        {
            string block = "thresholds:" + "\n" + string.Join("\n", items.Select(i => "  " + i));
            return editor.Apply([YamlEdits.AddEntry(doc, stat, block)]).Text;
        }

        if (existing is not YamlSequenceNode { Style: SequenceStyle.Block } sequence || sequence.Children.Count == 0)
        {
            throw new InvalidOperationException($"{statId}.thresholds is not a block list; edit it in the source pane");
        }

        int start = (int)sequence.Start.Index;
        string indent = doc.IndentOf(start);
        string replacement = string.Join(doc.NewLine + indent, items);
        return editor.Apply([new TextEdit(start, doc.EndOf(sequence) - start, replacement, $"set {path}")]).Text;
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
    ///     Sets an expression flag's condition, whichever of the two spellings the author used: nested as
    ///     <c>flag: { when: }</c>, or the scalar <c>flag: "&lt;expr&gt;"</c>. A trigger flag (<c>flag: true</c>
    ///     with <c>on:</c>) has no condition to set; the loader takes no stat-level <c>when:</c>.
    /// </summary>
    public static string SetFlagWhen(string text, string statId, string when)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlPath stat = RulesetDocumentEditor.StatPath(statId);
        // The writer keeps an existing scalar's quoting and quotes a new one as the text needs.
        switch (editor.Document.Find(stat.Key("flag")))
        {
            case YamlMappingNode:
                return editor.Apply([YamlEdits.SetKey(editor.Document, stat.Key("flag"), "when", when)]).Text;
            case YamlScalarNode { Value: not ("true" or "false") }:
                return editor.Apply([YamlEdits.SetScalar(editor.Document, stat.Key("flag"), when)]).Text;
            case YamlScalarNode:
                throw new InvalidOperationException($"{statId} is a trigger flag; it fires on its on: and has no when: to set");
            default:
                throw new InvalidOperationException($"{statId} is not a flag in this document");
        }
    }

    /// <summary>
    ///     An expression flag's condition as written, from either spelling, or null for a trigger flag and
    ///     for a stat that is not a flag.
    /// </summary>
    public static string? FlagWhen(string text, string statId)
    {
        YamlPath flag = RulesetDocumentEditor.StatPath(statId).Key("flag");
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        return editor.Document.Find(flag) switch
        {
            YamlMappingNode => editor.Scalar(flag.Key("when")),
            YamlScalarNode { Value: not (null or "true" or "false") } scalar => scalar.Value,
            _ => null
        };
    }

    /// <summary>Appends a stat block (composed at zero indentation) under <c>stats:</c>. An empty <c>stats: {}</c> is opened for it.</summary>
    public static string AddStat(string text, string block)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        if (editor.Document.Find(_stats) is YamlMappingNode { Style: MappingStyle.Flow, Children.Count: 0 } empty)
        {
            (int start, int length) = editor.Document.SpanOf(empty);
            editor = editor.Apply([new TextEdit(start, length, "", "open stats")]);
        }

        return editor.AddStat(block).Text;
    }

    /// <summary>Removes a stat entry whole, its comments inside it included; a comment above it stays.</summary>
    public static string RemoveStat(string text, string statId) =>
        RulesetDocumentEditor.Open(text).RemoveStat(statId).Text;

    private static YamlPath HighlightPath(string id) => _highlights.Key(id);

    /// <summary>Sets one scalar field of a highlight (<c>when</c>, <c>per</c>, <c>title</c>, <c>score</c>), adding or replacing it.</summary>
    public static string SetHighlightField(string text, string highlightId, string key, string value)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        if (editor.Document.Find(HighlightPath(highlightId)) is not YamlMappingNode)
        {
            throw new InvalidOperationException($"{highlightId} is not a highlight in this document");
        }

        return editor.Apply([YamlEdits.SetKey(editor.Document, HighlightPath(highlightId), key, value)]).Text;
    }

    /// <summary>Removes a highlight's scalar field when it has one; the text is unchanged otherwise.</summary>
    public static string RemoveHighlightFieldIfPresent(string text, string highlightId, string key)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        return editor.Document.Find(HighlightPath(highlightId).Key(key)) is null
            ? text
            : editor.Apply([YamlEdits.RemoveKey(editor.Document, HighlightPath(highlightId), key)]).Text;
    }

    /// <summary>The scalar a highlight holds under <paramref name="key" />, or null.</summary>
    public static string? HighlightField(string text, string highlightId, string key) =>
        RulesetDocumentEditor.Open(text).Scalar(HighlightPath(highlightId).Key(key));

    /// <summary>
    ///     Appends a highlight (its body at one indentation under the id) under <c>highlights:</c>, adding the
    ///     section at the end of the document when there is none.
    /// </summary>
    public static string AddHighlight(string text, string highlightId, string body)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        string entry = highlightId + ":\n" + string.Join('\n', body.Split('\n').Select(l => "  " + l));
        if (editor.Document.Find(_highlights) is YamlMappingNode)
        {
            return editor.Apply([YamlEdits.AddEntry(editor.Document, _highlights, entry)]).Text;
        }

        string section = "highlights:\n" + string.Join('\n', entry.Split('\n').Select(l => "  " + l));
        return editor.Apply([YamlEdits.AddEntry(editor.Document, YamlPath.Root, section)]).Text;
    }

    /// <summary>Removes a highlight entry whole.</summary>
    public static string RemoveHighlight(string text, string highlightId)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        return editor.Apply([YamlEdits.RemoveKey(editor.Document, _highlights, highlightId)]).Text;
    }

    /// <summary>The stat ids the document declares, in document order.</summary>
    public static IReadOnlyList<string> StatIds(string text) => RulesetDocumentEditor.Open(text).StatIds;

    /// <summary>The highlight ids the document declares, in document order.</summary>
    public static IReadOnlyList<string> HighlightIds(string text) => KeysUnder(RulesetDocumentEditor.Open(text).Document, _highlights);

    /// <summary>Every id the document declares, stats then highlights: the one namespace a new id must be free in.</summary>
    public static IReadOnlyList<string> EntryIds(string text)
    {
        YamlDocumentText doc = RulesetDocumentEditor.Open(text).Document;
        return [.. KeysUnder(doc, _stats), .. KeysUnder(doc, _highlights)];
    }

    // ---- ids and renaming ----

    private static readonly YamlPath _stats = YamlPath.Root.Key("stats");
    private static readonly YamlPath _show = YamlPath.Root.Key("show");
    private static readonly YamlPath _scoreboard = _show.Key("scoreboard");
    private static readonly YamlPath _tables = _show.Key("tables");

    // The top-level keys in the order a ruleset writes them; a new one is inserted after the nearest earlier one present.
    private static readonly string[] _headerKeys = ["ruleset", "title", "summary", "for", "enabled", "use", "exports"];

    /// <summary>Whether <paramref name="id" /> can be a stat, highlight or ruleset id: a word of letters, digits and underscores.</summary>
    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || !(char.IsAsciiLetter(id[0]) || id[0] == '_'))
        {
            return false;
        }

        return id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    /// <summary>
    ///     Renames a stat and rewrites every reference to it in the document: the bare and the self-qualified
    ///     spelling in sibling <c>compute:</c>, <c>when:</c>, <c>where:</c>, <c>while:</c>, <c>tally:</c>,
    ///     <c>rate:</c>, <c>key:</c> and <c>value:</c> texts, and in <c>show:</c>. Only the reference tokens
    ///     move; quoting, comments and everything around them stay.
    /// </summary>
    public static string RenameStat(string text, string oldId, string newId) => Rename(text, _stats, oldId, newId, "stat");

    /// <summary>Renames a highlight and rewrites its <c>id</c> and <c>id.count</c> references, as <see cref="RenameStat" /> does.</summary>
    public static string RenameHighlight(string text, string oldId, string newId) => Rename(text, _highlights, oldId, newId, "highlight");

    /// <summary>
    ///     The ids of the sibling entries whose expressions read <paramref name="id" />, in document order. The
    ///     <c>show:</c> block is not a sibling; <see cref="ShowSurface" /> answers for it.
    /// </summary>
    public static IReadOnlyList<string> SiblingReaders(string text, string id)
    {
        YamlDocumentText doc = RulesetDocumentEditor.Open(text).Document;
        string ruleset = (doc.Find(YamlPath.Root.Key("ruleset")) as YamlScalarNode)?.Value ?? "";
        List<string> readers = [];
        foreach ((string owner, YamlPath site) in ReferenceSites(doc, includeShow: false))
        {
            if (owner != id && !readers.Contains(owner) && doc.Find(site) is YamlScalarNode { Value: { } value }
                && RewriteReferences(value, id, id + "_", ruleset) != value)
            {
                readers.Add(owner);
            }
        }

        return readers;
    }

    private static string Rename(string text, YamlPath section, string oldId, string newId, string what)
    {
        ArgumentNullException.ThrowIfNull(newId);
        newId = newId.Trim();
        if (!IsValidId(newId))
        {
            throw new InvalidOperationException($"'{newId}' is not a valid id: letters, digits and underscores, not starting with a digit");
        }

        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        if (doc.FindKey(section.Key(oldId)) is not { } key)
        {
            throw new InvalidOperationException($"{oldId} is not a {what} in this document");
        }

        if (oldId == newId)
        {
            return text;
        }

        if (EntryIds(text).Contains(newId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{newId} is already declared in this ruleset");
        }

        string ruleset = (doc.Find(YamlPath.Root.Key("ruleset")) as YamlScalarNode)?.Value ?? "";
        (int start, int length) = doc.SpanOf(key);
        List<TextEdit> edits = [new TextEdit(start, length, newId, $"rename {oldId}")];
        foreach ((_, YamlPath site) in ReferenceSites(doc, includeShow: true))
        {
            if (doc.Find(site) is YamlScalarNode { Value: { } value })
            {
                string rewritten = RewriteReferences(value, oldId, newId, ruleset);
                if (rewritten != value)
                {
                    edits.Add(YamlEdits.SetScalar(doc, site, rewritten));
                }
            }
        }

        return editor.Apply(edits).Text;
    }

    // Every scalar that may read an entry by id, with the entry that owns it (a show: site is owned by "show").
    private static IEnumerable<(string Owner, YamlPath Site)> ReferenceSites(YamlDocumentText doc, bool includeShow)
    {
        foreach (string id in KeysUnder(doc, _stats))
        {
            YamlPath stat = _stats.Key(id);
            foreach (string key in new[] { "compute", "where", "while", "when", "tally", "key", "value", "of" })
            {
                yield return (id, stat.Key(key));
            }

            // flag: true names no sibling; flag: <expr> and flag: { when: <expr> } do.
            if (doc.Find(stat.Key("flag")) is YamlScalarNode { Value: not ("true" or "false") })
            {
                yield return (id, stat.Key("flag"));
            }

            yield return (id, stat.Key("flag").Key("when"));
            yield return (id, stat.Key("rate").Key("of"));
            yield return (id, stat.Key("rate").Key("per"));
        }

        foreach (string id in KeysUnder(doc, _highlights))
        {
            yield return (id, _highlights.Key(id).Key("when"));
        }

        if (!includeShow)
        {
            yield break;
        }

        if (doc.Find(_scoreboard) is YamlSequenceNode scoreboard)
        {
            for (int i = 0; i < scoreboard.Children.Count; i++)
            {
                yield return ("show", _scoreboard.Index(i).Key("stat"));
            }
        }

        foreach (string table in KeysUnder(doc, _tables))
        {
            YamlPath columns = _tables.Key(table).Key("columns");
            if (doc.Find(columns) is YamlSequenceNode seq)
            {
                for (int i = 0; i < seq.Children.Count; i++)
                {
                    yield return ("show", columns.Index(i).Key("stat"));
                }
            }
        }
    }

    /// <summary>
    ///     <paramref name="text" /> with every reference token naming <paramref name="oldId" /> renamed: a dotted
    ///     identifier whose first segment is the id (<c>kills</c>, <c>ace.count</c>) or whose first two are the
    ///     ruleset and the id. Text inside quotes is a literal and is left alone, as is <c>event.kills</c>.
    /// </summary>
    public static string RewriteReferences(string text, string oldId, string newId, string rulesetId)
    {
        ArgumentNullException.ThrowIfNull(text);
        System.Text.StringBuilder sb = new(text.Length + 8);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                int close = text.IndexOf(c, i + 1);
                int end = close < 0 ? text.Length : close + 1;
                sb.Append(text, i, end - i);
                i = end;
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                {
                    i++;
                }

                string token = text[start..i];
                string[] parts = token.Split('.');
                if (parts[0] == oldId)
                {
                    parts[0] = newId;
                }
                else if (parts.Length > 1 && parts[0] == rulesetId && parts[1] == oldId)
                {
                    parts[1] = newId;
                }

                sb.Append(string.Join('.', parts));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                // A number's dot is not a member access, and nothing in it is an id.
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                {
                    sb.Append(text[i++]);
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    // ---- the document header ----

    /// <summary>The scalar at a top-level key (<c>title</c>, <c>summary</c>, <c>for</c>), or null.</summary>
    public static string? DocumentField(string text, string key) => RulesetDocumentEditor.Open(text).Scalar(YamlPath.Root.Key(key));

    /// <summary>The <c>use:</c> list as written, or empty.</summary>
    public static IReadOnlyList<string> Uses(string text) =>
        RulesetDocumentEditor.Open(text).Document.Find(YamlPath.Root.Key("use")) is YamlSequenceNode seq
            ? [.. seq.Children.OfType<YamlScalarNode>().Select(n => n.Value ?? "")]
            : [];

    /// <summary>
    ///     Sets a top-level scalar, replacing it in place or inserting it where a ruleset writes it: after
    ///     the nearest earlier header key present (<c>title</c> after <c>ruleset</c>, <c>for</c> after
    ///     <c>summary</c>), not at the end of the file.
    /// </summary>
    public static string SetDocumentField(string text, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        YamlNode? existing = doc.Find(YamlPath.Root.Key(key));
        if (existing is YamlScalarNode { Value: not (null or "") })
        {
            return editor.Apply([YamlEdits.SetScalar(doc, YamlPath.Root.Key(key), value)]).Text;
        }

        if (existing is not null and not YamlScalarNode)
        {
            throw new InvalidOperationException($"{key} holds a collection; edit it in the YAML tab");
        }

        // `key:` with nothing after it is replaced whole rather than appended to.
        if (existing is YamlScalarNode)
        {
            editor = editor.Apply([YamlEdits.RemoveKey(doc, YamlPath.Root, key)]);
            doc = editor.Document;
        }

        return editor.Apply([InsertHeaderLine(doc, key, $"{key}: {PlainOrQuoted(value)}")]).Text;
    }

    /// <summary>Removes a top-level key when it is there; the text is unchanged otherwise.</summary>
    public static string RemoveDocumentField(string text, string key)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        return editor.Document.Find(YamlPath.Root.Key(key)) is null
            ? text
            : editor.Apply([YamlEdits.RemoveKey(editor.Document, YamlPath.Root, key)]).Text;
    }

    /// <summary>Adds <paramref name="ruleset" /> to <c>use:</c>, creating the list after the header when there is none.</summary>
    public static string AddUse(string text, string ruleset)
    {
        if (!IsValidId(ruleset))
        {
            throw new InvalidOperationException($"'{ruleset}' is not a ruleset id");
        }

        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        YamlPath use = YamlPath.Root.Key("use");
        switch (doc.Find(use))
        {
            case YamlSequenceNode seq:
                return seq.Children.OfType<YamlScalarNode>().Any(n => n.Value == ruleset)
                    ? text
                    : editor.Apply([YamlEdits.AddSequenceItem(doc, use, ruleset)]).Text;
            case YamlScalarNode:
                editor = editor.Apply([YamlEdits.RemoveKey(doc, YamlPath.Root, "use")]);
                doc = editor.Document;
                break;
            case not null:
                throw new InvalidOperationException("use: is not a list; edit it in the YAML tab");
        }

        return editor.Apply([InsertHeaderLine(doc, "use", $"use: [{ruleset}]")]).Text;
    }

    /// <summary>Removes <paramref name="ruleset" /> from <c>use:</c>; the last one takes the key with it.</summary>
    public static string RemoveUse(string text, string ruleset)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        YamlPath use = YamlPath.Root.Key("use");
        if (doc.Find(use) is not YamlSequenceNode seq)
        {
            return text;
        }

        int index = seq.Children.ToList().FindIndex(n => (n as YamlScalarNode)?.Value == ruleset);
        if (index < 0)
        {
            return text;
        }

        return seq.Children.Count == 1
            ? editor.Apply([YamlEdits.RemoveKey(doc, YamlPath.Root, "use")]).Text
            : editor.Apply([RemoveSequenceItem(doc, seq, index, use)]).Text;
    }

    // ---- show: ----

    /// <summary>
    ///     Appends <c>{ stat: id, label: label }</c> to <c>show.scoreboard</c>, creating the block or the
    ///     section when the document has none. An entry already there is left as it is.
    /// </summary>
    public static string AddToScoreboard(string text, string statRef, string label)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        string item = $"{{ stat: {statRef}, label: {PlainOrQuoted(label, inFlow: true)} }}";
        if (doc.Find(_scoreboard) is YamlSequenceNode seq)
        {
            return seq.Children.OfType<YamlMappingNode>().Any(m => StatOf(m) == statRef)
                ? text
                : editor.Apply([AppendFlowItem(doc, seq, item, _scoreboard)]).Text;
        }

        return editor.Apply([AddUnder(doc, _show, "scoreboard", item)]).Text;
    }

    /// <summary>Appends a column to <c>show.tables.&lt;table&gt;.columns</c>; the table must already be declared.</summary>
    public static string AddToTable(string text, string table, string statRef, string label)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        YamlPath tablePath = _tables.Key(table);
        if (doc.Find(tablePath) is not YamlMappingNode)
        {
            throw new InvalidOperationException($"show: has no table named {table}; declare it in the YAML tab first");
        }

        string item = $"{{ stat: {statRef}, label: {PlainOrQuoted(label, inFlow: true)} }}";
        YamlPath columns = tablePath.Key("columns");
        if (doc.Find(columns) is YamlSequenceNode seq)
        {
            return seq.Children.OfType<YamlMappingNode>().Any(m => StatOf(m) == statRef)
                ? text
                : editor.Apply([AppendFlowItem(doc, seq, item, columns)]).Text;
        }

        return editor.Apply([AddUnder(doc, tablePath, "columns", item)]).Text;
    }

    /// <summary>Sets or, on null, removes <c>label:</c> or <c>group:</c> of one scoreboard entry or table column.</summary>
    public static string SetShowEntryField(string text, string? table, int index, string key, string? value)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        YamlPath entry = (table is null ? _scoreboard : _tables.Key(table).Key("columns")).Index(index);
        if (doc.Find(entry) is not YamlMappingNode map)
        {
            throw new InvalidOperationException($"show: has no entry {index} in {table ?? "scoreboard"}");
        }

        if (value is not null)
        {
            return editor.Apply([YamlEdits.SetKey(doc, entry, key, value)]).Text;
        }

        if (doc.Find(entry.Key(key)) is null)
        {
            return text;
        }

        return map.Style == MappingStyle.Flow
            ? editor.Apply([RemoveFlowKey(doc, map, key, entry)]).Text
            : editor.Apply([YamlEdits.RemoveKey(doc, entry, key)]).Text;
    }

    /// <summary>Removes one scoreboard entry or table column; an emptied list, table or section goes with it.</summary>
    public static string RemoveShowItem(string text, string? table, int index) => RemoveShowItems(text, [(table, index)]);

    /// <summary>Removes every <c>show:</c> entry that names <paramref name="id" />, by any spelling <see cref="ShowSurface.Refers" /> accepts.</summary>
    public static string RemoveShowReference(string text, string id)
    {
        YamlDocumentText doc = RulesetDocumentEditor.Open(text).Document;
        string ruleset = (doc.Find(YamlPath.Root.Key("ruleset")) as YamlScalarNode)?.Value ?? "";
        List<(string?, int)> items = [];
        if (doc.Find(_scoreboard) is YamlSequenceNode scoreboard)
        {
            for (int i = 0; i < scoreboard.Children.Count; i++)
            {
                if (scoreboard.Children[i] is YamlMappingNode m && StatOf(m) is { } stat && ShowSurface.Refers(stat, id, ruleset))
                {
                    items.Add((null, i));
                }
            }
        }

        foreach (string table in KeysUnder(doc, _tables))
        {
            if (doc.Find(_tables.Key(table).Key("columns")) is YamlSequenceNode columns)
            {
                for (int i = 0; i < columns.Children.Count; i++)
                {
                    if (columns.Children[i] is YamlMappingNode m && StatOf(m) is { } stat && ShowSurface.Refers(stat, id, ruleset))
                    {
                        items.Add((table, i));
                    }
                }
            }
        }

        return items.Count == 0 ? text : RemoveShowItems(text, items);
    }

    /// <summary>The smallest ruleset the loader accepts, for a file the user names: the id, a title, the scope and no stats.</summary>
    public static string NewRulesetText(string id, string title)
    {
        if (!IsValidId(id))
        {
            throw new InvalidOperationException($"'{id}' is not a valid ruleset id: letters, digits and underscores, not starting with a digit");
        }

        return $"ruleset: {id}\ntitle: {PlainOrQuoted(title)}\nfor: each_player\n\nstats: {{}}\n";
    }

    // Items are removed from the same text in one batch, then whatever they emptied is taken out level by level.
    private static string RemoveShowItems(string text, IReadOnlyList<(string? Table, int Index)> items)
    {
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        List<TextEdit> edits = [];
        foreach ((string? table, int index) in items)
        {
            YamlPath list = table is null ? _scoreboard : _tables.Key(table).Key("columns");
            if (doc.Find(list) is not YamlSequenceNode seq || index < 0 || index >= seq.Children.Count)
            {
                throw new InvalidOperationException($"show: has no entry {index} in {table ?? "scoreboard"}");
            }

            edits.Add(RemoveSequenceItem(doc, seq, index, list));
        }

        editor = editor.Apply(edits);
        doc = editor.Document;
        if (IsEmptyCollection(doc.Find(_scoreboard)))
        {
            editor = editor.Apply([YamlEdits.RemoveKey(doc, _show, "scoreboard")]);
            doc = editor.Document;
        }

        foreach (string table in KeysUnder(doc, _tables))
        {
            if (IsEmptyCollection(doc.Find(_tables.Key(table).Key("columns"))))
            {
                editor = editor.Apply([YamlEdits.RemoveKey(doc, _tables, table)]);
                doc = editor.Document;
            }
        }

        if (IsEmptyCollection(doc.Find(_tables)))
        {
            editor = editor.Apply([YamlEdits.RemoveKey(doc, _show, "tables")]);
            doc = editor.Document;
        }

        if (IsEmptyCollection(doc.Find(_show)))
        {
            editor = editor.Apply([YamlEdits.RemoveKey(doc, YamlPath.Root, "show")]);
        }

        return editor.Text;
    }

    // A block collection whose entries were all removed parses as an empty scalar; a flow one keeps its brackets.
    private static bool IsEmptyCollection(YamlNode? node) => node switch
    {
        YamlScalarNode { Value: null or "" } => true,
        YamlSequenceNode { Children.Count: 0 } => true,
        YamlMappingNode { Children.Count: 0 } => true,
        _ => false
    };

    private static string? StatOf(YamlMappingNode map) =>
        map.Children.FirstOrDefault(e => (e.Key as YamlScalarNode)?.Value == "stat").Value is YamlScalarNode s ? s.Value : null;

    // `scoreboard:` under `show:`, or `show:` itself, with the first item composed at zero indentation.
    private static TextEdit AddUnder(YamlDocumentText doc, YamlPath parent, string key, string item)
    {
        if (doc.Find(parent) is YamlMappingNode { Style: not MappingStyle.Flow } || IsEmptyCollection(doc.Find(parent)) && doc.FindKey(parent) is not null)
        {
            return YamlEdits.AddEntry(doc, parent, $"{key}:\n  - {item}");
        }

        if (doc.Find(parent) is null && parent.Parent is { } grand)
        {
            string head = parent.Segments[^1].Key ?? "";
            return YamlEdits.AddEntry(doc, grand, $"{head}:\n  {key}:\n    - {item}");
        }

        throw new InvalidOperationException($"{parent} is not a block mapping; edit it in the YAML tab");
    }

    // The appended item is a flow mapping, which the writer's own append would quote as a string.
    private static TextEdit AppendFlowItem(YamlDocumentText doc, YamlSequenceNode seq, string item, YamlPath path)
    {
        if (seq.Style == SequenceStyle.Flow)
        {
            int inner = seq.Children.Count > 0 ? doc.EndOf(seq.Children[^1]) : (int)seq.Start.Index + 1;
            return new TextEdit(inner, 0, seq.Children.Count > 0 ? ", " + item : item, $"append to {path}");
        }

        int indentAt = seq.Children.Count > 0 ? (int)seq.Children[0].Start.Index : (int)seq.Start.Index;
        string indent = doc.IndentOf(indentAt);
        int at = doc.StartOfNextLine(doc.EndOf(seq));
        string lead = at == 0 || doc.Text[at - 1] is '\n' or '\r' ? "" : doc.NewLine;
        return new TextEdit(at, 0, $"{lead}{indent}- {item}{doc.NewLine}", $"append to {path}");
    }

    // A block item goes with its whole line range; a flow item with the separator on one side of it.
    private static TextEdit RemoveSequenceItem(YamlDocumentText doc, YamlSequenceNode seq, int index, YamlPath path)
    {
        YamlNode item = seq.Children[index];
        if (seq.Style != SequenceStyle.Flow)
        {
            int from = doc.StartOfLine((int)item.Start.Index);
            int to = doc.StartOfNextLine(doc.EndOf(item));
            return new TextEdit(from, to - from, "", $"remove {path.Index(index)}");
        }

        int start, end;
        if (index < seq.Children.Count - 1)
        {
            start = (int)item.Start.Index;
            end = (int)seq.Children[index + 1].Start.Index;
        }
        else if (index > 0)
        {
            start = doc.EndOf(seq.Children[index - 1]);
            end = doc.EndOf(item);
        }
        else
        {
            start = (int)item.Start.Index;
            end = doc.EndOf(item);
        }

        return new TextEdit(start, end - start, "", $"remove {path.Index(index)}");
    }

    // `{ a: 1, b: 2 }` minus one key, the way RemoveFacet does it for match:.
    private static TextEdit RemoveFlowKey(YamlDocumentText doc, YamlMappingNode map, string key, YamlPath path)
    {
        List<KeyValuePair<YamlNode, YamlNode>> entries = [.. map.Children];
        int index = entries.FindIndex(e => (e.Key as YamlScalarNode)?.Value == key);
        if (index < 0)
        {
            throw new InvalidOperationException($"{path} has no {key}");
        }

        if (entries.Count == 1)
        {
            throw new InvalidOperationException($"{key} is the only key of {path}; remove the entry instead");
        }

        int from, to;
        if (index < entries.Count - 1)
        {
            from = (int)entries[index].Key.Start.Index;
            to = (int)entries[index + 1].Key.Start.Index;
        }
        else
        {
            from = doc.EndOf(entries[index - 1].Value);
            to = doc.EndOf(entries[index].Value);
        }

        return new TextEdit(from, to - from, "", $"remove {path.Key(key)}");
    }

    // A header line lands after the nearest earlier header key the document has, so title: follows ruleset:
    // and for: follows summary:, not the show: block at the end of the file.
    private static TextEdit InsertHeaderLine(YamlDocumentText doc, string key, string line)
    {
        int rank = Array.IndexOf(_headerKeys, key);
        YamlNode? anchor = null;
        for (int i = (rank < 0 ? _headerKeys.Length : rank) - 1; i >= 0 && anchor is null; i--)
        {
            anchor = doc.Find(YamlPath.Root.Key(_headerKeys[i]));
        }

        anchor ??= doc.Find(YamlPath.Root.Key("ruleset"))
                   ?? throw new InvalidOperationException("the document has no ruleset: key");
        int at = doc.StartOfNextLine(doc.EndOf(anchor));
        string lead = at == 0 || doc.Text[at - 1] is '\n' or '\r' ? "" : doc.NewLine;
        return new TextEdit(at, 0, lead + line + doc.NewLine, $"add {key}");
    }

    // Plain where YAML reads it back as the same string; double-quoted otherwise. Stricter than the writer's
    // own rule, which is private, so a value is sometimes quoted that need not be; never the reverse.
    private static string PlainOrQuoted(string value, bool inFlow = false)
    {
        bool plain = value.Length > 0 && value == value.Trim()
                     && (char.IsAsciiLetter(value[0]) || value[0] == '_')
                     && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or ' ' or '-' or '.' or '/' or '%' or '(' or ')' or '\'' or ',' or '+')
                     && !value.Contains(": ", StringComparison.Ordinal) && !value.EndsWith(':')
                     && !value.Contains(" #", StringComparison.Ordinal)
                     && !(inFlow && value.Contains(','))
                     && !bool.TryParse(value, out _)
                     && !value.Equals("null", StringComparison.OrdinalIgnoreCase)
                     && !double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _);
        return plain ? value : "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static List<string> KeysUnder(YamlDocumentText doc, YamlPath path)
    {
        if (doc.Find(path) is not YamlMappingNode map)
        {
            return [];
        }

        return [.. map.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value).OfType<string>()];
    }
}
