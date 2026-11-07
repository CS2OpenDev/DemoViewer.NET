#region

using Nodify.Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     The editor's input gestures per editability. A shipped ruleset keeps selection, panning, zooming
///     and keyboard navigation but loses every gesture that would write: connect, disconnect, cut. Split
///     is never bound, since a knot would be a second layer the file does not hold. Escape is never the
///     editor's either: its deselect runs on KeyUp from any source, so an Escape typed into a face editor
///     would drop the selection; the view's key map handles Escape and skips focused editors.
/// </summary>
public static class CanvasGestures
{
    /// <summary>The full set for the user's own ruleset.</summary>
    public static EditorGestures Editing()
    {
        EditorGestures gestures = new();
        gestures.Connection.Split.Unbind();
        gestures.Editor.Keyboard.DeselectAll.Unbind();
        return gestures;
    }

    /// <summary>The set for a shipped ruleset: nothing that writes.</summary>
    public static EditorGestures ReadOnly()
    {
        EditorGestures gestures = Editing();
        gestures.Connector.Connect.Unbind();
        gestures.Connector.Disconnect.Unbind();
        gestures.Connection.Disconnect.Unbind();
        gestures.Editor.Cutting.Unbind();
        return gestures;
    }
}
