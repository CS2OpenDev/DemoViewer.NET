#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>What one redraw draws and where: the composition, possibly cut to a slice, and the positions that win over the computed ones.</summary>
/// <param name="Composed">The composition to draw: the whole chain, or the focused slice of it.</param>
/// <param name="Layout">The positions that override the placement, per node key, with the folds and the frames.</param>
/// <param name="HasSavedLayout">Whether the sidecar held an arrangement.</param>
/// <param name="FocusDropped">Whether a focus named a node no longer in the chain and was cleared.</param>
/// <param name="Carried">Whether a renamed node took its arrangement to its new key, which the sidecar does not hold yet.</param>
public sealed record Arrangement(RulesetCorpus.Composed Composed, RulesetNodeLayout Layout, bool HasSavedLayout, bool FocusDropped, bool Carried = false);

/// <summary>
///     The column of the canvas a node reads in: the first placement flows left to right from the events
///     through the defines and the entries to the show nodes, and an item added without a drop point joins
///     the column its kind belongs to.
/// </summary>
public enum PlacementColumn
{
    /// <summary>The named triggers, between the events and the entries.</summary>
    Define,

    /// <summary>The stats and highlights the document declares.</summary>
    Entry,

    /// <summary>The tables and the scoreboard, right of everything they surface.</summary>
    Show
}

/// <summary>
///     Where nodes go: the focus and its slice, the position a dropped entry is waiting to take, the
///     arrangement on screen, and the sidecar an arrangement is kept in. No view model and no Avalonia
///     beyond the point a position is.
/// </summary>
public sealed class CanvasArranger(Func<string?> layoutFolder)
{
    private const double StackGap = 40;
    private const double ColumnGap = 140;
    private const double DefineWidth = 360;

    /// <summary>The column a gallery item lands in when it is added without a drop point.</summary>
    public static PlacementColumn ColumnOf(GalleryItemKind kind) => kind switch
    {
        GalleryItemKind.Define => PlacementColumn.Define,
        GalleryItemKind.Table => PlacementColumn.Show,
        _ => PlacementColumn.Entry
    };

    /// <summary>
    ///     Where an item added without a drop point goes: under the lowest node of its column, level with the
    ///     column's left edge. An empty column opens beside the entries, a define one layer left of them and
    ///     a show node one layer right, at the entries' top; with nothing in any column, below everything
    ///     drawn, or the origin on an empty canvas. A node in no column (an engine node) only counts there.
    /// </summary>
    public static Point NextFreeSpot(PlacementColumn column, IReadOnlyList<(PlacementColumn? Column, Rect Box)> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        List<Rect> mine = [.. nodes.Where(n => n.Column == column).Select(n => n.Box)];
        if (mine.Count > 0)
        {
            return new Point(mine.Min(b => b.X), mine.Max(b => b.Bottom) + StackGap);
        }

        List<Rect> entries = [.. nodes.Where(n => n.Column == PlacementColumn.Entry).Select(n => n.Box)];
        if (entries.Count > 0 && column != PlacementColumn.Entry)
        {
            double top = entries.Min(b => b.Y);
            return column == PlacementColumn.Show
                ? new Point(entries.Max(b => b.Right) + ColumnGap, top)
                : new Point(entries.Min(b => b.X) - ColumnGap - DefineWidth, top);
        }

        if (nodes.Count == 0)
        {
            return new Point(0, 0);
        }

        return new Point(nodes.Min(n => n.Box.X), nodes.Max(n => n.Box.Bottom) + StackGap);
    }
    private readonly Func<string?> _layoutFolder = layoutFolder ?? throw new ArgumentNullException(nameof(layoutFolder));

    // Where a dropped entry should land, by the key it will get. Kept until a redraw draws that key, so a
    // focus redraw or a refused write in between does not lose the drop point.
    private readonly Dictionary<RulesetDocumentNodeKey, Point> _pending = new();

    // Renames written but not yet drawn: the arrangement under the old key moves to the new one on the draw
    // that holds it, so a rename does not send the node back to auto-layout.
    private readonly Dictionary<RulesetDocumentNodeKey, RulesetDocumentNodeKey> _renames = new();

    /// <summary>The node whose slice of the chain is drawn, or null for the whole chain.</summary>
    public RulesetDocumentNodeKey? FocusKey { get; set; }

    public bool HasFocus => FocusKey is not null;

    /// <summary>The placements recorded and not yet drawn.</summary>
    public IReadOnlyDictionary<RulesetDocumentNodeKey, Point> Pending => _pending;

    /// <summary>Records where the entry that the next write adds should be drawn.</summary>
    public void PlaceNext(RulesetDocumentNodeKey key, Point at) => _pending[key] = at;

    /// <summary>Records that the next write renames <paramref name="from" /> to <paramref name="to" />, so its arrangement follows.</summary>
    public void CarryNext(RulesetDocumentNodeKey from, RulesetDocumentNodeKey to) => _renames[from] = to;

    /// <summary>Drops every recorded placement and rename.</summary>
    public void ForgetPending()
    {
        _pending.Clear();
        _renames.Clear();
    }

    /// <summary>The on-screen positions as an arrangement, which a redraw after an edit keeps.</summary>
    public static RulesetNodeLayout Capture(IEnumerable<KeyValuePair<RulesetDocumentNodeKey, Point>> positions) =>
        RulesetNodeLayout.From(positions);

    /// <summary>Everything on screen as an arrangement: the positions, the folded nodes and the frames.</summary>
    public static RulesetNodeLayout Capture(IEnumerable<KeyValuePair<RulesetDocumentNodeKey, Point>> positions,
        IEnumerable<RulesetDocumentNodeKey> collapsed, IEnumerable<CanvasGroup> groups) =>
        RulesetNodeLayout.From(positions, collapsed, groups);

    /// <summary>
    ///     Where an entry's arrangement lives: beside a user ruleset, under the extension's own folder for a
    ///     shipped one since its directory is not ours to write, or null when there is nowhere to keep it.
    /// </summary>
    public string? SidecarFor(RulesetCorpus.Entry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        if (entry.Source == "user")
        {
            return RulesetLayoutStore.SidecarPathFor(entry.Path);
        }

        string? folder = _layoutFolder();
        return folder is null ? null : Path.Combine(folder, "layouts", entry.Id + RulesetLayoutStore.SidecarSuffix);
    }

    /// <summary>
    ///     Decides one redraw. A focus draws one slice, freshly placed, so the sidecar's arrangement of the
    ///     whole chain does not apply to it; a focus that names a node no longer there (deleted, or another
    ///     ruleset) is dropped, and the slice's arrangement with it. The arrangement on screen wins over the
    ///     sidecar, and a pending placement over both; the sidecar alone decides on open, when nothing is on
    ///     screen. A recorded rename moves the old key's arrangement to the new one once the new key is drawn.
    /// </summary>
    public Arrangement Arrange(RulesetCorpus.Composed composed, string? sidecar, RulesetNodeLayout onScreen)
    {
        ArgumentNullException.ThrowIfNull(composed);
        ArgumentNullException.ThrowIfNull(onScreen);
        RulesetNodeLayout saved = sidecar is null ? RulesetNodeLayout.Empty : RulesetLayoutStore.LoadSidecar(sidecar);
        RulesetNodeLayout layout = saved;
        bool focusDropped = false;
        if (FocusKey is { } focusKey)
        {
            if (composed.Focused(focusKey) is { } focused)
            {
                composed = focused;
                layout = RulesetNodeLayout.Empty;
            }
            else
            {
                // The slice's positions were its own; the whole chain comes back from the sidecar alone.
                FocusKey = null;
                focusDropped = true;
                onScreen = RulesetNodeLayout.Empty;
            }
        }

        layout = layout.Overlaid(onScreen);

        // A pending placement is spent by the first draw that holds its key; one that names no node drawn
        // here (a slice, a refused write) waits for the draw that does.
        HashSet<RulesetDocumentNodeKey> drawn = [.. composed.DrawnKeys];
        foreach ((RulesetDocumentNodeKey key, Point at) in _pending.Where(p => drawn.Contains(p.Key)).ToList())
        {
            layout = layout.With(key, at);
            _pending.Remove(key);
        }

        bool carried = false;
        foreach ((RulesetDocumentNodeKey from, RulesetDocumentNodeKey to) in _renames.Where(r => drawn.Contains(r.Value) && !drawn.Contains(r.Key)).ToList())
        {
            layout = layout.Renamed(from, to);
            _renames.Remove(from);
            carried = true;
        }

        return new Arrangement(composed, layout, !saved.IsEmpty, focusDropped, carried);
    }

    /// <summary>
    ///     Writes <paramref name="layout" /> to the sidecar and says whether a saved arrangement now applies. A
    ///     slice's placement is its own, so nothing is written while focused.
    /// </summary>
    public bool SaveLayout(string? sidecar, RulesetNodeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (HasFocus || sidecar is null || layout.IsEmpty)
        {
            return false;
        }

        return RulesetLayoutStore.SaveSidecar(sidecar, layout) && !layout.IsEmpty;
    }

    /// <summary>Drops the saved arrangement, which is the absence of a sidecar.</summary>
    public static void ResetLayout(string? sidecar)
    {
        if (sidecar is not null)
        {
            RulesetLayoutStore.SaveSidecar(sidecar, RulesetNodeLayout.Empty);
        }
    }

    /// <summary>
    ///     Re-places <paramref name="items" /> among themselves with the first placement's layering, inside
    ///     the box they occupy now: the result is anchored at <paramref name="anchor" />, the box's top-left,
    ///     and nothing outside the selection is touched. Edges to nodes outside the selection do not count.
    /// </summary>
    public static IReadOnlyDictionary<RulesetDocumentNodeKey, Point> Tidy(IReadOnlyList<CanvasPlacement.Item> items,
        IReadOnlyList<RulesetDocumentEdge> edges, Point anchor)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(edges);
        HashSet<RulesetDocumentNodeKey> keys = [.. items.Select(i => i.Key)];
        List<RulesetDocumentEdge> inside = [.. edges.Where(e => keys.Contains(e.Source) && keys.Contains(e.Target))];
        IReadOnlyDictionary<RulesetDocumentNodeKey, Point> placed = CanvasPlacement.Place(items, inside);
        if (placed.Count == 0)
        {
            return placed;
        }

        double left = placed.Values.Min(p => p.X);
        double top = placed.Values.Min(p => p.Y);
        return placed.ToDictionary(p => p.Key, p => new Point(Math.Round(anchor.X + p.Value.X - left), Math.Round(anchor.Y + p.Value.Y - top)));
    }
}
