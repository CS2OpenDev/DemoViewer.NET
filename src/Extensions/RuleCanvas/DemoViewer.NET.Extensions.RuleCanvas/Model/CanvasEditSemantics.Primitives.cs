#region

using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>The writes that finish an entry built from a blank primitive.</summary>
public static partial class CanvasEditSemantics
{
    // Every key the loader reads as a stat's kind; a draft holding two of them is as kind-less as one holding none.
    private static readonly string[] _kindKeys = ["flag", "count", "sum", "capture", "compute", "tally", "streak", "bucket", "rate", "burst"];

    /// <summary>
    ///     A draft stat's kind picked on its face: the entry takes that kind's key with an empty value, which
    ///     loads as an unfinished stat of the kind with its on port and rows ready. Any kind key already
    ///     there goes in the same write.
    /// </summary>
    public static EditRequest PickKind(CanvasNodeViewModel node, string? kind)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.IsDraft || !node.IsAuthorable)
        {
            return EditRequest.Ignore;
        }

        if (kind is null || !DraftFace.PickableKinds.Contains(kind, StringComparer.Ordinal))
        {
            return EditRequest.Refuse($"Pick one of {string.Join(", ", DraftFace.PickableKinds)} for {node.Id}.");
        }

        string id = node.Id;
        return EditRequest.Of(text =>
        {
            foreach (string key in _kindKeys)
            {
                text = RulesetEdits.RemoveFieldIfPresent(text, id, key);
            }

            return RulesetEdits.SetField(text, id, kind, "");
        }, $"make {id} a {kind}");
    }

    /// <summary>
    ///     Takes <paramref name="target" />'s trigger away: the trigger key is blanked, never removed, so the entry
    ///     loads unfinished with its on port ready (a define without <c>on:</c> would load as a lookup map). Null
    ///     for a flag, whose trigger and condition are one choice, and for a stat that fires on nothing.
    /// </summary>
    public static EditRequest? DetachTrigger(CanvasNodeViewModel target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAuthorable)
        {
            return null;
        }

        if (target.IsDefine)
        {
            string name = target.Title;
            return EditRequest.Of(text => RulesetEdits.SetDefineField(text, name, "on", ""), $"detach define {name} from its trigger");
        }

        if (target.Kind == "flag" || TriggerKeyFor(target) is not { } key)
        {
            return null;
        }

        string id = target.Id;
        return EditRequest.Of(text => RulesetEdits.SetField(text, id, key, ""), $"detach {id} from its trigger");
    }

    /// <summary>
    ///     The entries <paramref name="wires" /> say fire on <paramref name="eventNode" />, detached in one write;
    ///     null when nothing it drives can be detached.
    /// </summary>
    public static EditRequest? DetachAll(CanvasNodeViewModel eventNode, IEnumerable<CanvasConnectionViewModel> wires)
    {
        ArgumentNullException.ThrowIfNull(eventNode);
        ArgumentNullException.ThrowIfNull(wires);
        List<EditRequest> detaches = [.. TriggerTargets(eventNode, wires).Select(DetachTrigger).OfType<EditRequest>()];
        if (detaches.Count == 0)
        {
            return null;
        }

        return EditRequest.Of(text => detaches.Aggregate(text, (t, d) => d.Transform!(t)), $"detach {detaches.Count} from {eventNode.Id}");
    }

    /// <summary>The distinct nodes <paramref name="wires" /> carry a trigger into from <paramref name="eventNode" />.</summary>
    public static IReadOnlyList<CanvasNodeViewModel> TriggerTargets(CanvasNodeViewModel eventNode, IEnumerable<CanvasConnectionViewModel> wires) =>
        [.. wires.Where(w => ReferenceEquals(w.Source.Owner, eventNode) && IsTriggerWire(w)).Select(w => w.Target.Owner).OfType<CanvasNodeViewModel>().Distinct()];

    /// <summary>The event node a floating view is drawn as: the chain's own key for the view's event, or null for an unknown view.</summary>
    public static RulesetDocumentNodeKey? ScaffoldKey(string view) =>
        EmbeddedCatalog.Root.Views.FirstOrDefault(v => v.Name == view) is { } found ? new RulesetDocumentNodeKey("~event", found.Event) : null;

    // The floating node carries the view it was placed for, so the event's other views do not make the wire ambiguous.
    private static EditRequest ConnectFloating(string view, CanvasNodeViewModel target)
    {
        if (target.IsDefine)
        {
            return Retarget(target, "on", view) with { Description = $"fire define {target.Title} on {view}" };
        }

        return TriggerKeyFor(target) is { } key
            ? Retarget(target, key, view) with { Description = $"fire {target.Id} on {view}" }
            : EditRequest.Refuse($"A {target.Kind} fires on no view; it cannot fire on {view}.");
    }
}
