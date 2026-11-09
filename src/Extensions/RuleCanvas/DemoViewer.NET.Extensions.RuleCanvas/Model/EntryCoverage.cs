namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     What the file says about one entry that its face does not show: the <c>for_each:</c> axes the loader
///     expanded it from, and every other top-level key the face does not render. Read from the text, since
///     the loaded document is the expanded one and carries neither.
/// </summary>
/// <param name="EntryId">The id the text declares the entry under: its own, or the template it was expanded from.</param>
/// <param name="Axes">The <c>for_each:</c> axes, in document order; empty for an entry with none.</param>
/// <param name="Unrendered">The keys the face does not render, with their values as written, in document order.</param>
public sealed record EntryCoverage(string EntryId, IReadOnlyList<(string Key, IReadOnlyList<string> Values)> Axes, IReadOnlyList<EntryKey> Unrendered)
{
    public static EntryCoverage None { get; } = new("", [], []);

    public bool IsExpanded => Axes.Count > 0;

    /// <summary>One chip per axis: <c>for each weapon</c>, with the values and the template on hover.</summary>
    public IReadOnlyList<(string Text, string Tip)> Chips =>
        [.. Axes.Select(a => ("for each " + a.Key, $"for_each: {a.Key}: [{string.Join(", ", a.Values)}]\nexpanded from {EntryId}"))];

    /// <summary>
    ///     The coverage of <paramref name="face" />'s entry in <paramref name="text" />. The entry is looked
    ///     up under the face's id, then under the template whose expansion yields it; a face with no entry
    ///     in the text (another ruleset's, an engine node's) has none.
    /// </summary>
    public static EntryCoverage Of(string text, NodeFace face)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(face);
        if (face.IsScaffold || face.IsShow || face.IsParams || !face.IsAuthorable)
        {
            return None;
        }

        EntrySection section = RulesetEntryText.SectionOf(face.Key, face.IsHighlight);
        string id = face.Key.Name;
        IReadOnlyList<EntryKey> keys;
        try
        {
            keys = RulesetEntryText.KeysOf(text, section, id);
            if (keys.Count == 0)
            {
                if (RulesetEntryText.TemplateOf(text, section, id) is not { } template)
                {
                    return None;
                }

                id = template;
                keys = RulesetEntryText.KeysOf(text, section, id);
            }

            IReadOnlyList<(string, IReadOnlyList<string>)> axes = RulesetEdits.ForEachOf(text, section, id);
            HashSet<string> covered = new(face.CoveredKeys, StringComparer.Ordinal) { "for_each" };
            return new EntryCoverage(id, axes, [.. keys.Where(k => !covered.Contains(k.Key))]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            return None;
        }
    }
}
