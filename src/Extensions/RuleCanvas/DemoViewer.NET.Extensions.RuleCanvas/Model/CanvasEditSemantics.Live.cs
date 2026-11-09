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
}
