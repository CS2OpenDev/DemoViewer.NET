#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Nodify.Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     The editor's input gestures per editability. A shipped ruleset keeps selection, panning, zooming
///     and keyboard navigation but loses every gesture that would write: connect, disconnect, cut. Split
///     is never bound, since a knot would be a second layer the file does not hold. Escape is never the
///     editor's either: its deselect runs on KeyUp from any source, so an Escape typed into a face editor
///     would drop the selection; the view's key map handles Escape and skips focused editors. The static
///     predicates below decide which input a face editor gives up to the editor; the view wires them.
/// </summary>
public static class CanvasGestures
{
    /// <summary>
    ///     Whether a wheel that landed on <paramref name="source" /> is the editor's zoom rather than the
    ///     picker's. A picker holding focus with its list closed steps its selection on the wheel, and every
    ///     picker on a face writes a step at once; an open list scrolls and is left alone.
    /// </summary>
    public static bool WheelOverClosedPicker(Visual? source) =>
        source?.GetSelfAndVisualAncestors().OfType<ComboBox>().FirstOrDefault() is { IsDropDownOpen: false };

    /// <summary>
    ///     The editable picker whose list a bare Up or Down pressed on <paramref name="source" /> opens, or
    ///     null. With the list closed the combo would step its selection instead, and a stepped gate or
    ///     field writes at once, taking the half-typed text with it. An open list is the combo's own.
    /// </summary>
    public static ComboBox? ListToOpen(Key key, KeyModifiers modifiers, Visual? source) =>
        key is Key.Down or Key.Up && modifiers == KeyModifiers.None
        && source?.GetSelfAndVisualAncestors().OfType<ComboBox>().FirstOrDefault() is { IsEditable: true, IsDropDownOpen: false } box
            ? box
            : null;

    /// <summary>
    ///     Whether <paramref name="face" /> keeps a press that landed on <paramref name="source" />: a left
    ///     press on one of its editors, which the node container would otherwise turn into a drag and
    ///     capture, so the editor never gets the release that opens it. A right press goes on to the
    ///     container, which selects the node before its menu opens, as a right press on the header does.
    /// </summary>
    public static bool FaceKeepsPress(Visual? source, Visual face, PointerPointProperties properties) =>
        properties.IsLeftButtonPressed && source is not null
        && source.GetSelfAndVisualAncestors().TakeWhile(v => !ReferenceEquals(v, face))
            .Any(v => v is ComboBox or TextBox or AutoCompleteBox or CheckBox or Button);

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
