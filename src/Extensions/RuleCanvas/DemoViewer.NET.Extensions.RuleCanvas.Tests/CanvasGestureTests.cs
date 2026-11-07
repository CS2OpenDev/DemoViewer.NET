#region

using Avalonia.Controls;
using Avalonia.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;
using Nodify.Avalonia;
using Nodify.Avalonia.Helpers.Gestures;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The editor's gesture sets and the canvas's own keys. A shipped ruleset loses every gesture that
///     would write and keeps the rest; Split is never bound; the key map reads the same under Ctrl and Cmd.
/// </summary>
public class CanvasGestureTests
{
    private static bool IsBound(InputGestureRef gesture) => !ReferenceEquals(gesture.Value, MultiGesture.None);

    [Test]
    public async Task TheEditingSet_HasEveryGesture_ButSplitAndEscape()
    {
        EditorGestures gestures = CanvasGestures.Editing();
        using (Assert.Multiple())
        {
            await Assert.That(IsBound(gestures.Connection.Split)).IsFalse().Because("a knot would be a second layer");
            await Assert.That(IsBound(gestures.Editor.Keyboard.DeselectAll)).IsFalse()
                .Because("the editor reads Escape on KeyUp from a face editor too; the key map owns it");
            await Assert.That(IsBound(gestures.Connector.Connect)).IsTrue();
            await Assert.That(IsBound(gestures.Connector.Disconnect)).IsTrue();
            await Assert.That(IsBound(gestures.Connection.Disconnect)).IsTrue();
            await Assert.That(IsBound(gestures.Editor.Cutting)).IsTrue();
            await Assert.That(IsBound(gestures.Editor.Selection.Replace)).IsTrue();
            await Assert.That(IsBound(gestures.Editor.Pan)).IsTrue();
        }
    }

    [Test]
    public async Task TheReadOnlySet_KeepsSelectionPanZoomAndKeyboard_AndLosesEveryWrite()
    {
        EditorGestures gestures = CanvasGestures.ReadOnly();
        using (Assert.Multiple())
        {
            await Assert.That(IsBound(gestures.Connector.Connect)).IsFalse();
            await Assert.That(IsBound(gestures.Connector.Disconnect)).IsFalse();
            await Assert.That(IsBound(gestures.Connection.Disconnect)).IsFalse();
            await Assert.That(IsBound(gestures.Connection.Split)).IsFalse();
            await Assert.That(IsBound(gestures.Editor.Keyboard.DeselectAll)).IsFalse();
            await Assert.That(IsBound(gestures.Editor.Cutting)).IsFalse();
            await Assert.That(IsBound(gestures.Editor.Selection.Replace)).IsTrue();
            await Assert.That(IsBound(gestures.Editor.Selection.Append)).IsTrue();
            await Assert.That(IsBound(gestures.Editor.SelectAll)).IsTrue();
            await Assert.That(IsBound(gestures.Editor.Pan)).IsTrue();
            await Assert.That(IsBound(gestures.Editor.ZoomIn)).IsTrue();
            await Assert.That(IsBound(gestures.Editor.FitToScreen)).IsTrue();
            await Assert.That(IsBound(gestures.Editor.Keyboard.NavigateSelection.Right)).IsTrue();
            await Assert.That(IsBound(gestures.ItemContainer.Selection.Replace)).IsTrue();
            await Assert.That(IsBound(gestures.ItemContainer.Drag)).IsTrue();
            await Assert.That(IsBound(gestures.Minimap.DragViewport)).IsTrue();
        }
    }

    [Test]
    public async Task TheTwoSets_AreSeparateInstances_SoSwappingOneLeavesTheGlobalsAlone()
    {
        EditorGestures editing = CanvasGestures.Editing();
        EditorGestures readOnly = CanvasGestures.ReadOnly();
        using (Assert.Multiple())
        {
            await Assert.That(ReferenceEquals(editing, readOnly)).IsFalse();
            await Assert.That(ReferenceEquals(editing, EditorGestures.Mappings)).IsFalse();
            await Assert.That(IsBound(EditorGestures.Mappings.Connector.Connect)).IsTrue().Because("the global defaults are untouched");
        }
    }

    [Test]
    [Arguments(Key.Delete, KeyModifiers.None, CanvasKeyAction.DeleteSelection)]
    [Arguments(Key.Back, KeyModifiers.None, CanvasKeyAction.DeleteSelection)]
    [Arguments(Key.Z, KeyModifiers.Control, CanvasKeyAction.Undo)]
    [Arguments(Key.Z, KeyModifiers.Meta, CanvasKeyAction.Undo)]
    [Arguments(Key.Z, KeyModifiers.Control | KeyModifiers.Shift, CanvasKeyAction.Redo)]
    [Arguments(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift, CanvasKeyAction.Redo)]
    [Arguments(Key.Y, KeyModifiers.Control, CanvasKeyAction.Redo)]
    [Arguments(Key.D, KeyModifiers.Control, CanvasKeyAction.Duplicate)]
    [Arguments(Key.D, KeyModifiers.Meta, CanvasKeyAction.Duplicate)]
    [Arguments(Key.Escape, KeyModifiers.None, CanvasKeyAction.ClearFocusAndSelection)]
    [Arguments(Key.F, KeyModifiers.None, CanvasKeyAction.FrameSelection)]
    [Arguments(Key.A, KeyModifiers.None, CanvasKeyAction.FitAll)]
    [Arguments(Key.A, KeyModifiers.Control, CanvasKeyAction.None)]
    [Arguments(Key.F, KeyModifiers.Shift, CanvasKeyAction.None)]
    [Arguments(Key.Delete, KeyModifiers.Control, CanvasKeyAction.None)]
    [Arguments(Key.Z, KeyModifiers.None, CanvasKeyAction.None)]
    [Arguments(Key.S, KeyModifiers.Control, CanvasKeyAction.None)]
    public async Task TheKeyMap_ReadsEachChord(Key key, KeyModifiers modifiers, CanvasKeyAction expected) =>
        await Assert.That(CanvasKeyMap.Resolve(key, modifiers)).IsEqualTo(expected);

    // A picker that holds focus steps its selection on the wheel, and a stepped picker writes at once.
    [Test]
    public async Task AWheelOverAClosedPicker_IsTheEditorsZoom_AndAnOpenListOrPlainTextIsNot()
    {
        ComboBox picker = new();
        TextBlock caption = new();
        StackPanel face = new() { Children = { picker, caption } };
        using (Assert.Multiple())
        {
            await Assert.That(CanvasGestures.WheelOverClosedPicker(picker)).IsTrue().Because("the picker would step and write");
            await Assert.That(CanvasGestures.WheelOverClosedPicker(caption)).IsFalse().Because("the editor zooms on its own there");
            await Assert.That(CanvasGestures.WheelOverClosedPicker(face)).IsFalse();
            await Assert.That(CanvasGestures.WheelOverClosedPicker(null)).IsFalse();
            picker.IsDropDownOpen = true;
            await Assert.That(CanvasGestures.WheelOverClosedPicker(picker)).IsFalse().Because("an open list scrolls");
        }
    }

    // The editor completes a connection on a plain click: the release hit-tests the press anchor and finds
    // the port it started from, so the command sees the same endpoint twice.
    [Test]
    public async Task AClickOnAPort_ArrivesAsAWireToItself_AndWritesNothing()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        string before = File.ReadAllText(rules.UserPath("player_round"));
        CanvasNodeViewModel flag = vm.Nodes.Single(n => n.Id == "round_kast");
        CanvasConnectorViewModel port = new() { Title = "", Owner = flag };

        vm.ConnectCommand.Execute(new ValueTuple<object, object>(port, port));
        string after = File.ReadAllText(rules.UserPath("player_round"));
        using (Assert.Multiple())
        {
            await Assert.That(after).IsEqualTo(before).Because("the preview promised nothing would land");
            await Assert.That(RulesetEdits.Field(after, "round_kast", "flag.when")).DoesNotContain("round_kast > 0");
            await Assert.That(vm.Toast).IsNull().Because("a click is not a refused wire");
        }
    }
}
