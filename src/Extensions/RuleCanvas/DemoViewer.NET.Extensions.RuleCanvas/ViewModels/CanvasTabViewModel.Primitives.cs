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

    /// <summary>The action a refused event delete carries: <c>Detach 2 stats</c>.</summary>
    public static string DetachLabel(IReadOnlyList<CanvasNodeViewModel> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        string noun = targets.All(t => !t.IsDefine)
            ? targets.Count == 1 ? "stat" : "stats"
            : targets.Count == 1 ? "entry" : "entries";
        return $"Detach {targets.Count} {noun}";
    }

    // An event node is the chain's: deleting it means taking away the triggers that draw it, which the toast offers.
    private void RefuseEventDelete(IReadOnlyList<CanvasNodeViewModel> events)
    {
        List<CanvasNodeViewModel> targets = [.. events.SelectMany(e => CanvasEditSemantics.TriggerTargets(e, Connections)).Distinct()];
        List<EditRequest> detaches = [.. events.Select(e => CanvasEditSemantics.DetachAll(e, Connections)).OfType<EditRequest>()];
        string names = string.Join(", ", events.Select(e => e.Id));
        if (targets.Count == 0 || detaches.Count == 0)
        {
            Notify($"{names} is drawn because something reads it; it goes when nothing does.");
            return;
        }

        string? path = _session.Entry?.Path;
        EditRequest detach = EditRequest.Of(text => detaches.Aggregate(text, (t, d) => d.Transform!(t)), $"detach {targets.Count} from {names}");
        Notify($"{names} drives {string.Join(", ", targets.Select(t => t.Title))}; detach them to remove it.", null, DetachLabel(targets), () =>
        {
            if (IsEditable && path is not null && string.Equals(_session.Entry?.Path, path, StringComparison.Ordinal))
            {
                Apply(detach);
            }
            else
            {
                Notify("The ruleset changed since that delete was refused; select the event and delete it again.");
            }
        });
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
