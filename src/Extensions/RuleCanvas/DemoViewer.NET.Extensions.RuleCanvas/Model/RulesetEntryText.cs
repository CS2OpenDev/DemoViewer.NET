#region

using DemoViewer.NET.RuleAuthoring;
using YamlDotNet.RepresentationModel;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     The text of one entry as the file holds it, for a duplicate: the body under the id line, dedented
///     to zero so the splice writer's add re-indents it wherever it lands. Comments inside the entry go
///     with it; the id line does not.
/// </summary>
public static class RulesetEntryText
{
    private static readonly YamlPath _highlights = YamlPath.Root.Key("highlights");

    /// <summary>The body of <paramref name="id" />'s entry at zero indentation, without its id line.</summary>
    /// <exception cref="InvalidOperationException">The entry is not in the document.</exception>
    public static string BodyOf(string text, string id, bool highlight)
    {
        ArgumentNullException.ThrowIfNull(text);
        YamlDocumentText doc = RulesetDocumentEditor.Open(text).Document;
        YamlPath path = highlight ? _highlights.Key(id) : RulesetDocumentEditor.StatPath(id);
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
