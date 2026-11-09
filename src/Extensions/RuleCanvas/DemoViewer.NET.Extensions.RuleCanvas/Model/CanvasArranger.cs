#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>What one redraw draws and where: the composition, possibly cut to a slice, and the positions that win over the computed ones.</summary>
/// <param name="Composed">The composition to draw: the whole chain, or the focused slice of it.</param>
/// <param name="Layout">The positions that override the placement, per node key.</param>
/// <param name="HasSavedLayout">Whether the sidecar held an arrangement.</param>
/// <param name="FocusDropped">Whether a focus named a node no longer in the chain and was cleared.</param>
public sealed record Arrangement(RulesetCorpus.Composed Composed, RulesetNodeLayout Layout, bool HasSavedLayout, bool FocusDropped);

/// <summary>
///     Where nodes go: the focus and its slice, the position a dropped entry is waiting to take, the
///     arrangement on screen, and the sidecar an arrangement is kept in. No view model and no Avalonia
///     beyond the point a position is.
/// </summary>
public sealed class CanvasArranger(Func<string?> layoutFolder)
{
    private readonly Func<string?> _layoutFolder = layoutFolder ?? throw new ArgumentNullException(nameof(layoutFolder));

    // Where a dropped entry should land, by the key it will get. Kept until a redraw draws that key, so a
    // focus redraw or a refused write in between does not lose the drop point.
    private readonly Dictionary<RulesetDocumentNodeKey, Point> _pending = new();

    /// <summary>The node whose slice of the chain is drawn, or null for the whole chain.</summary>
    public RulesetDocumentNodeKey? FocusKey { get; set; }

    public bool HasFocus => FocusKey is not null;

    /// <summary>The placements recorded and not yet drawn.</summary>
    public IReadOnlyDictionary<RulesetDocumentNodeKey, Point> Pending => _pending;

    /// <summary>Records where the entry that the next write adds should be drawn.</summary>
    public void PlaceNext(RulesetDocumentNodeKey key, Point at) => _pending[key] = at;

    /// <summary>Drops every recorded placement.</summary>
    public void ForgetPending() => _pending.Clear();

    /// <summary>The on-screen positions as an arrangement, which a redraw after an edit keeps.</summary>
    public static RulesetNodeLayout Capture(IEnumerable<KeyValuePair<RulesetDocumentNodeKey, Point>> positions) =>
        RulesetNodeLayout.From(positions);

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
    ///     screen.
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

        foreach ((RulesetDocumentNodeKey key, Point at) in onScreen.Positions)
        {
            layout = layout.With(key, at);
        }

        // A pending placement is spent by the first draw that holds its key; one that names no node drawn
        // here (a slice, a refused write) waits for the draw that does.
        HashSet<RulesetDocumentNodeKey> drawn = [.. composed.DrawnKeys];
        foreach ((RulesetDocumentNodeKey key, Point at) in _pending.Where(p => drawn.Contains(p.Key)).ToList())
        {
            layout = layout.With(key, at);
            _pending.Remove(key);
        }

        return new Arrangement(composed, layout, saved.Count > 0, focusDropped);
    }

    /// <summary>
    ///     Writes <paramref name="layout" /> to the sidecar and says whether a saved arrangement now applies. A
    ///     slice's placement is its own, so nothing is written while focused.
    /// </summary>
    public bool SaveLayout(string? sidecar, RulesetNodeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (HasFocus || sidecar is null || layout.Count == 0)
        {
            return false;
        }

        return RulesetLayoutStore.SaveSidecar(sidecar, layout) && layout.Count > 0;
    }

    /// <summary>Drops the saved arrangement, which is the absence of a sidecar.</summary>
    public static void ResetLayout(string? sidecar)
    {
        if (sidecar is not null)
        {
            RulesetLayoutStore.SaveSidecar(sidecar, RulesetNodeLayout.Empty);
        }
    }
}
