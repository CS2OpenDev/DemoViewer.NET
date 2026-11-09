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
}
