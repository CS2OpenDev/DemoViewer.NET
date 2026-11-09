#region

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Structure and navigation on a big canvas: frames around nodes, faces folded to their header, the
///     level of detail the zoom picks, a tidy of the selection, and the search. None of it touches the
///     YAML: every change here is a layout change, kept in the sidecar and taken back by the same Undo as a
///     document write, ordered against the session's history by depth.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    /// <summary>The zoom under which every face draws as its header alone. Under the wire labels' own floor.</summary>
    public const double HeaderOnlyZoom = 0.5;

    private const double GroupPad = 24;
    private const double GroupHeader = 30;

    /// <summary>What the toast says when a frame is made, changed or removed while a slice is focused.</summary>
    public const string FramesReadOnlyWhileFocused = "Frames are read-only while a slice is focused; clear the focus to change them";

    // The frame around a box of nodes: the pad on every side and the header above.
    private static Rect FrameAround(Rect box) =>
        new(box.X - GroupPad, box.Y - GroupPad - GroupHeader, box.Width + 2 * GroupPad, box.Height + 2 * GroupPad + GroupHeader);

    // A layout change: the arrangement before and after, and how deep the session's undo stack was when it
    // was made, which orders it against document writes.
    private sealed record LayoutChange(RulesetNodeLayout Before, RulesetNodeLayout After, int Depth);

    private readonly Stack<LayoutChange> _layoutUndo = new();
    private readonly Stack<LayoutChange> _layoutRedo = new();
    private bool _movingHistory;
    private List<CanvasNodeViewModel> _searchMatches = [];
    private int _searchIndex = -1;

    /// <summary>The editor's zoom, written by the view. Only the crossing of <see cref="HeaderOnlyZoom" /> reaches the nodes.</summary>
    [ObservableProperty]
    private double _viewportZoom = 1;

    /// <summary>What the strip's search box holds; narrows the canvas by id, label, event and define name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    private string _canvasSearch = "";

    /// <summary>Below the level of detail: every face reads as a block.</summary>
    public bool IsZoomedOut => ViewportZoom < HeaderOnlyZoom;

    public bool IsSearching => CanvasSearch.Trim().Length > 0;

    /// <summary>The nodes the search names, in canvas order; every node when there is no search.</summary>
    public IReadOnlyList<CanvasNodeViewModel> SearchMatches => _searchMatches;

    /// <summary>What the strip says beside the search box: the match count, or nothing.</summary>
    public string SearchSummary => !IsSearching ? "" : _searchMatches.Count switch
    {
        0 => "no match",
        1 => "1 match",
        var n => $"{n} matches"
    };

    public bool HasLayoutUndo => _layoutUndo.Count > 0;

    public bool HasLayoutRedo => _layoutRedo.Count > 0;

    // ---- groups ----

    /// <summary>
    ///     Frames the selected nodes, two or more: a group titled by the view they share, or Group, around
    ///     their box. The nodes stay selected. Ctrl+G and the node menu.
    /// </summary>
    [RelayCommand]
    private void GroupSelection() => GroupSelection(null);

    /// <summary>
    ///     As <see cref="GroupSelectionCommand" />, around <paramref name="bounds" /> when the view measured the
    ///     selection; the estimated sizes otherwise.
    /// </summary>
    public void GroupSelection(Rect? bounds)
    {
        if (HasFocus)
        {
            Notify(FramesReadOnlyWhileFocused);
            return;
        }

        List<CanvasNodeViewModel> nodes = [.. SelectedNodes.OfType<CanvasNodeViewModel>()];
        if (nodes.Count < 2)
        {
            return;
        }

        Rect box = bounds ?? BoxOf(nodes);
        CanvasGroup group = new(CommonView(nodes) ?? "Group", [.. nodes.Select(n => n.Key)], FrameAround(box));
        RulesetNodeLayout before = CurrentArrangement();
        RulesetNodeLayout after = before.WithGroup(group);
        RecordLayoutChange(before, after);
        CommitLayout(after);
    }

    /// <summary>Removes <paramref name="frame" /> and leaves its nodes where they are. One Undo frames them again.</summary>
    public void Ungroup(CanvasGroupViewModel frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (HasFocus)
        {
            Notify(FramesReadOnlyWhileFocused);
            return;
        }

        RulesetNodeLayout before = CurrentArrangement();
        RulesetNodeLayout after = before.WithoutGroup(frame.ToGroup(frame.Members));
        if (ReferenceEquals(after, before))
        {
            return;
        }

        RecordLayoutChange(before, after);
        CommitLayout(after);
    }

    /// <summary>Removes every selected frame; nothing in the file is touched.</summary>
    public void UngroupSelected()
    {
        List<CanvasGroupViewModel> frames = [.. SelectedNodes.OfType<CanvasGroupViewModel>()];
        if (frames.Count == 0)
        {
            return;
        }

        if (HasFocus)
        {
            Notify(FramesReadOnlyWhileFocused);
            return;
        }

        RulesetNodeLayout before = CurrentArrangement();
        RulesetNodeLayout after = frames.Aggregate(before, (layout, frame) => layout.WithoutGroup(frame.ToGroup(frame.Members)));
        RecordLayoutChange(before, after);
        CommitLayout(after);
    }

    // ---- collapse ----

    /// <summary>Folds <paramref name="node" /> to its header, or unfolds it, and writes the sidecar. The chevron and the header's double click.</summary>
    public void ToggleCollapse(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.IsCollapsed = !node.IsCollapsed;
        SaveLayout();
    }

    // ---- zoom level of detail ----

    partial void OnViewportZoomChanged(double oldValue, double newValue)
    {
        bool was = oldValue < HeaderOnlyZoom;
        bool now = newValue < HeaderOnlyZoom;
        if (was == now)
        {
            return;
        }

        OnPropertyChanged(nameof(IsZoomedOut));
        foreach (CanvasNodeViewModel node in Nodes)
        {
            node.IsZoomedOut = now;
        }
    }

    // ---- tidy ----

    /// <summary>
    ///     Re-places the selected nodes, two or more, among themselves with the first placement's layering,
    ///     inside their box and anchored at its top-left; nothing else moves. One Undo puts them back.
    /// </summary>
    [RelayCommand]
    private void TidySelection()
    {
        List<CanvasNodeViewModel> nodes = [.. SelectedNodes.OfType<CanvasNodeViewModel>()];
        if (nodes.Count < 2 || HasFocus)
        {
            return;
        }

        List<CanvasPlacement.Item> items = [.. nodes.Select(n => new CanvasPlacement.Item(n.Key, n.EstimatedSize.Width, n.EstimatedSize.Height))];
        List<RulesetDocumentEdge> edges = [.. Connections
            .Where(w => w.Source.Owner is not null && w.Target.Owner is not null)
            .Select(w => new RulesetDocumentEdge(w.Source.Owner!.Key, w.Target.Owner!.Key, w.Text))];
        IReadOnlyDictionary<RulesetDocumentNodeKey, Point> placed = CanvasArranger.Tidy(items, edges, BoxOf(nodes).TopLeft);
        RulesetNodeLayout before = CurrentArrangement();
        RulesetNodeLayout after = placed.Aggregate(before, (layout, p) => layout.With(p.Key, p.Value));
        RecordLayoutChange(before, after);
        CommitLayout(after);
    }

    // ---- search ----

    partial void OnCanvasSearchChanged(string value)
    {
        _searchIndex = -1;
        RefreshSearchMatches();
    }

    /// <summary>Escape in the search box: every node back at full strength.</summary>
    [RelayCommand]
    private void ClearSearch() => CanvasSearch = "";

    /// <summary>
    ///     Enter in the search box: selects the next match, the first on the first press, around again after
    ///     the last. The view frames the selection. Null when nothing matches.
    /// </summary>
    public CanvasNodeViewModel? AdvanceSearch()
    {
        if (_searchMatches.Count == 0 || !IsSearching)
        {
            return null;
        }

        _searchIndex = (_searchIndex + 1) % _searchMatches.Count;
        CanvasNodeViewModel match = _searchMatches[_searchIndex];
        SelectedNodes.Clear();
        SelectedNodes.Add(match);
        return match;
    }

    private void RefreshSearchMatches()
    {
        string query = CanvasSearch;
        foreach (CanvasNodeViewModel node in Nodes)
        {
            node.IsSearchMatch = node.Matches(query);
        }

        _searchMatches = IsSearching ? [.. Nodes.Where(n => n.IsSearchMatch)] : [.. Nodes];
        if (_searchIndex >= _searchMatches.Count)
        {
            _searchIndex = -1;
        }

        OnPropertyChanged(nameof(SearchMatches));
        OnPropertyChanged(nameof(SearchSummary));
    }

    // ---- layout history ----

    // Whether the next Undo takes back a layout change rather than a write: the change was made at or after
    // the session's current depth, or there is no write left to take back.
    private bool LayoutUndoIsNext => _layoutUndo.TryPeek(out LayoutChange? top) && (top.Depth >= _session.UndoDepth || !_session.CanUndo);

    private bool LayoutRedoIsNext => _layoutRedo.TryPeek(out LayoutChange? top) && (top.Depth <= _session.UndoDepth || !_session.CanRedo);

    private void RecordLayoutChange(RulesetNodeLayout before, RulesetNodeLayout after)
    {
        _layoutUndo.Push(new LayoutChange(before, after, _session.UndoDepth));
        _layoutRedo.Clear();
        NotifyHistory();
    }

    private void UndoLayout()
    {
        LayoutChange change = _layoutUndo.Pop();
        _layoutRedo.Push(change);
        CommitLayout(change.Before);
        NotifyHistory();
    }

    private void RedoLayout()
    {
        LayoutChange change = _layoutRedo.Pop();
        _layoutUndo.Push(change);
        CommitLayout(change.After);
        NotifyHistory();
    }

    private void ClearLayoutHistory()
    {
        _layoutUndo.Clear();
        _layoutRedo.Clear();
    }

    private void ClearLayoutRedo() => _layoutRedo.Clear();

    // Writes the arrangement and draws it, viewport untouched. An empty one is the absence of a sidecar.
    private void CommitLayout(RulesetNodeLayout layout)
    {
        if (layout.IsEmpty)
        {
            CanvasArranger.ResetLayout(SidecarPath);
            HasSavedLayout = false;
        }
        else
        {
            HasSavedLayout = _arranger.SaveLayout(SidecarPath, layout);
        }

        Redraw(SelectedRuleset, keepArrangement: true, recompose: false, arrangement: layout, refit: false);
    }

    private static Rect BoxOf(List<CanvasNodeViewModel> nodes)
    {
        Rect box = new(nodes[0].Location, nodes[0].EstimatedSize);
        foreach (CanvasNodeViewModel node in nodes.Skip(1))
        {
            box = box.Union(new Rect(node.Location, node.EstimatedSize));
        }

        return box;
    }

    // The view the authored nodes share, when they share one; a frame around one event's stats is named for it.
    private static string? CommonView(List<CanvasNodeViewModel> nodes)
    {
        List<string> views = [.. nodes.Where(n => !n.IsScaffold).Select(n => n.View).OfType<string>().Distinct(StringComparer.Ordinal)];
        return views.Count == 1 ? views[0] : null;
    }
}
