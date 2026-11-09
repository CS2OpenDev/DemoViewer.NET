#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Building from blank primitives: the floating event nodes the gallery's Blank event places. They live in
///     the layout sidecar, not the YAML, until a wire into a stat writes the trigger.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    /// <summary>The Blank event was dropped at this point; the view asks which view to place there.</summary>
    public event Action<Point>? EventPickRequested;

    /// <summary>The views the Blank event offers, each placing its event as a floating node.</summary>
    public IReadOnlyList<GalleryItem> BlankEventOptions { get; } = CanvasGallery.BlankEventOptions(EmbeddedCatalog.Root);

    private void PlaceEvent(GalleryItem item, Point at)
    {
        if (item.Name == CanvasGallery.BlankEventName)
        {
            EventPickRequested?.Invoke(at);
        }
        else
        {
            PlaceScaffold(item.Name, at);
        }
    }

    /// <summary>
    ///     Places <paramref name="view" />'s event as a floating node at <paramref name="at" />, kept in the sidecar.
    ///     Refused when that event is already on the canvas, since one event is one node.
    /// </summary>
    public void PlaceScaffold(string view, Point at)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!IsEditable || CanvasEditSemantics.ScaffoldKey(view) is not { } key)
        {
            return;
        }

        if (Nodes.Any(n => n.Key == key))
        {
            Notify($"{key.Id} is already on the canvas; wire it, or drop the {view} view from the gallery on the stat.");
            return;
        }

        if (HasFocus)
        {
            FocusKey = null;
        }

        RulesetNodeLayout before = CurrentArrangement();
        RulesetNodeLayout after = before.WithScaffold(new CanvasScaffold(view, at)).With(key, at);
        RecordLayoutChange(before, after);
        CommitLayout(after);
    }

    // Floating nodes have no text to remove: they leave the sidecar, as one undoable layout change.
    private void RemoveScaffolds(IReadOnlyList<CanvasNodeViewModel> floating)
    {
        RulesetNodeLayout before = CurrentArrangement();
        RulesetNodeLayout after = before;
        foreach (CanvasNodeViewModel node in floating)
        {
            after = after.WithoutScaffold(node.FloatingView!).Without(node.Key);
        }

        RecordLayoutChange(before, after);
        CommitLayout(after);
    }
}
