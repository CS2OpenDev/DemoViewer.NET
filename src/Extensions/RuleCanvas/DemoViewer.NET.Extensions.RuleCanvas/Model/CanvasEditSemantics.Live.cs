#region

using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>The write decisions live values ask for: what a frozen value writes.</summary>
public static partial class CanvasEditSemantics
{
    /// <summary>
    ///     The fixture block for <paramref name="node" />'s entry: <paramref name="lines" /> as a comment above
    ///     it, replacing any block already there. Only an entry of the open document can hold one.
    /// </summary>
    public static EditRequest FreezeFixture(CanvasNodeViewModel node, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(lines);
        if (!node.IsEntry)
        {
            return EditRequest.Refuse("Only a stat, a highlight or a define of your own ruleset can hold a fixture.");
        }

        EntrySection section = RulesetEntryText.SectionOf(node.Key, node.IsHighlight);
        string id = node.Title;
        return EditRequest.Of(text => RulesetFixtureComment.Set(text, section, id, lines), $"freeze {id} as a fixture");
    }

    /// <summary>Removes the fixture comment above <paramref name="node" />'s entry.</summary>
    public static EditRequest ClearFixture(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsEntry)
        {
            return EditRequest.Refuse("Only a stat, a highlight or a define of your own ruleset holds a fixture.");
        }

        EntrySection section = RulesetEntryText.SectionOf(node.Key, node.IsHighlight);
        string id = node.Title;
        return EditRequest.Of(text => RulesetFixtureComment.Clear(text, section, id), $"clear the fixture of {id}");
    }

    /// <summary>The fixture lines above <paramref name="node" />'s entry in <paramref name="text" />; empty when it has none or is not an entry.</summary>
    public static IReadOnlyList<string> FixtureOf(CanvasNodeViewModel node, string text)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(text);
        if (!node.IsEntry || !text.Contains(RulesetFixtureComment.Marker, StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            return RulesetFixtureComment.Read(text, RulesetEntryText.SectionOf(node.Key, node.IsHighlight), node.Title);
        }
        catch (Exception ex) when (ex is InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            return [];
        }
    }
}
