#region

using DemoViewer.NET.RuleAuthoring;
using YamlDotNet.RepresentationModel;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     A captured sample written as a comment block directly above an entry's id line, each line opening
///     with <see cref="Marker" />. A comment is the only place the ruleset language has for one without a key
///     the engine would reject, so the loader never sees it. The block is identified by its marker: a second
///     freeze replaces it, and a comment above the entry that is not a fixture stays where it is.
/// </summary>
public static class RulesetFixtureComment
{
    /// <summary>What every line of the block opens with, after the entry's indentation.</summary>
    public const string Marker = "# fixture:";

    /// <summary>
    ///     Writes <paramref name="lines" /> as the fixture block above <paramref name="id" /> in
    ///     <paramref name="section" />, replacing the block already there. Each line is indented as the id
    ///     line is.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entry is not in the document.</exception>
    public static string Set(string text, EntrySection section, string id, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lines);
        RulesetDocumentEditor editor = RulesetDocumentEditor.Open(text);
        YamlDocumentText doc = editor.Document;
        (int blockStart, int lineStart) = BlockBefore(doc, section, id);
        string indent = doc.IndentOf(lineStart);
        string newLine = doc.NewLine;
        string block = string.Concat(lines.Select(l => indent + Marker + " " + l.Trim() + newLine));
        return editor.Apply([new TextEdit(blockStart, lineStart - blockStart, block, "fixture comment")]).Text;
    }

    /// <summary>The fixture lines above <paramref name="id" /> without their marker, in order; empty when there is no block or no entry.</summary>
    public static IReadOnlyList<string> Read(string text, EntrySection section, string id)
    {
        ArgumentNullException.ThrowIfNull(text);
        YamlDocumentText doc = RulesetDocumentEditor.Open(text).Document;
        if (doc.FindKey(RulesetEntryText.PathOf(section, id)) is null)
        {
            return [];
        }

        (int blockStart, int lineStart) = BlockBefore(doc, section, id);
        return
        [
            .. text[blockStart..lineStart].Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith(Marker, StringComparison.Ordinal))
                .Select(l => l[Marker.Length..].Trim())
        ];
    }

    // The span of the marker lines directly above the id line, empty when there are none: [blockStart, lineStart).
    private static (int BlockStart, int LineStart) BlockBefore(YamlDocumentText doc, EntrySection section, string id)
    {
        if (doc.FindKey(RulesetEntryText.PathOf(section, id)) is not YamlScalarNode key)
        {
            throw new InvalidOperationException($"{id} is not an entry of this document");
        }

        int lineStart = doc.StartOfLine((int)key.Start.Index);
        int blockStart = lineStart;
        while (blockStart > 0)
        {
            int previous = doc.StartOfLine(blockStart - 1);
            if (!doc.Text[previous..blockStart].Trim().StartsWith(Marker, StringComparison.Ordinal))
            {
                break;
            }

            blockStart = previous;
        }

        return (blockStart, lineStart);
    }
}
