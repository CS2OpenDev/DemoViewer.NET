#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     The <c>use:</c> dependencies on the canvas: each is one folded node until the user expands it into its
///     entries, drawn read-only in place, and folds it back. Which are expanded is a layout fact, kept in the
///     sidecar beside the positions and taken back by the same Undo. Open switches the picker to the dependency.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    /// <summary>The ruleset ids drawn in full rather than as one node, as the canvas now shows them.</summary>
    public IReadOnlySet<string> ExpandedDependencies =>
        Nodes.Where(n => n.IsForeign).Select(n => n.Ruleset).ToHashSet(StringComparer.Ordinal);

    /// <summary>Unfolds the dependency node <paramref name="node" /> into its entries, or folds a drawn entry's ruleset back.</summary>
    public void ToggleDependency(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsDependency)
        {
            ExpandDependency(node.Id, node.Location);
        }
        else if (node.IsForeign)
        {
            CollapseDependency(node.Ruleset);
        }
    }

    /// <summary>
    ///     Draws the dependency <paramref name="ruleset" />'s entries in place of its node, tidied from the node's
    ///     corner when one is given. The whole chain is shown first: a slice says nothing about the folds.
    /// </summary>
    public void ExpandDependency(string ruleset, Point? anchor = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(ruleset);
        if (!IsDependencyOfOpen(ruleset))
        {
            return;
        }

        if (HasFocus)
        {
            FocusKey = null;
        }

        anchor ??= Nodes.FirstOrDefault(n => n.IsDependency && n.Id == ruleset)?.Location;
        RulesetNodeLayout before = CurrentArrangement();
        RulesetNodeLayout after = before.WithExpanded(ruleset, true);
        if (ReferenceEquals(after, before))
        {
            return;
        }

        RecordLayoutChange(before, after);
        CommitLayout(after);
        if (anchor is { } at)
        {
            PlaceExpanded(ruleset, at, before);
        }
    }

    /// <summary>Folds the dependency <paramref name="ruleset" /> back to one node; its entries' positions stay in the sidecar.</summary>
    public void CollapseDependency(string ruleset)
    {
        ArgumentException.ThrowIfNullOrEmpty(ruleset);
        if (HasFocus)
        {
            FocusKey = null;
        }

        RulesetNodeLayout before = CurrentArrangement();
        RulesetNodeLayout after = before.WithExpanded(ruleset, false);
        if (ReferenceEquals(after, before))
        {
            return;
        }

        RecordLayoutChange(before, after);
        CommitLayout(after);
    }

    /// <summary>Switches the picker to <paramref name="ruleset" />; false when the corpus has no such ruleset.</summary>
    public bool OpenRuleset(string ruleset)
    {
        ArgumentException.ThrowIfNullOrEmpty(ruleset);
        if (Rulesets.FirstOrDefault(r => r.Id == ruleset) is not { } entry)
        {
            return false;
        }

        if (!ReferenceEquals(entry, SelectedRuleset))
        {
            SelectedRuleset = entry;
        }

        return true;
    }

    private bool IsDependencyOfOpen(string ruleset) => _composed?.DependencyIds.Contains(ruleset, StringComparer.Ordinal) == true;

    // The entries just drawn are placed among themselves at the folded node's corner, as one layout change
    // with the unfold, so one Undo folds them back and puts the node where it was.
    private void PlaceExpanded(string ruleset, Point anchor, RulesetNodeLayout before)
    {
        List<CanvasNodeViewModel> drawn = [.. Nodes.Where(n => n.IsForeign && n.Ruleset == ruleset)];
        if (drawn.Count == 0)
        {
            return;
        }

        List<CanvasPlacement.Item> items = [.. drawn.Select(n => new CanvasPlacement.Item(n.Key, n.EstimatedSize.Width, n.EstimatedSize.Height))];
        List<RulesetDocumentEdge> edges = [.. Connections
            .Where(w => w.Source.Owner is not null && w.Target.Owner is not null)
            .Select(w => new RulesetDocumentEdge(w.Source.Owner!.Key, w.Target.Owner!.Key, w.Text))];
        IReadOnlyDictionary<RulesetDocumentNodeKey, Point> placed = CanvasArranger.Tidy(items, edges, anchor);
        RulesetNodeLayout after = placed.Aggregate(CurrentArrangement(), (layout, p) => layout.With(p.Key, p.Value));
        ReplaceLayoutChange(before, after);
        CommitLayout(after);
    }
}
