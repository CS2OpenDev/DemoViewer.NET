#region

using Avalonia;
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
    [Arguments(Key.G, KeyModifiers.Control, CanvasKeyAction.GroupSelection)]
    [Arguments(Key.G, KeyModifiers.Meta, CanvasKeyAction.GroupSelection)]
    [Arguments(Key.G, KeyModifiers.Control | KeyModifiers.Shift, CanvasKeyAction.None)]
    [Arguments(Key.T, KeyModifiers.Control | KeyModifiers.Shift, CanvasKeyAction.TidySelection)]
    [Arguments(Key.T, KeyModifiers.Meta | KeyModifiers.Shift, CanvasKeyAction.TidySelection)]
    [Arguments(Key.T, KeyModifiers.Control, CanvasKeyAction.None)]
    [Arguments(Key.F, KeyModifiers.Control, CanvasKeyAction.Search)]
    [Arguments(Key.F, KeyModifiers.Meta, CanvasKeyAction.Search)]
    [Arguments(Key.F, KeyModifiers.Control | KeyModifiers.Shift, CanvasKeyAction.None)]
    [Arguments(Key.A, KeyModifiers.Control, CanvasKeyAction.None)]
    [Arguments(Key.F, KeyModifiers.Shift, CanvasKeyAction.None)]
    [Arguments(Key.Delete, KeyModifiers.Control, CanvasKeyAction.None)]
    [Arguments(Key.Z, KeyModifiers.None, CanvasKeyAction.None)]
    [Arguments(Key.S, KeyModifiers.Control, CanvasKeyAction.None)]
    public async Task TheKeyMap_ReadsEachChord(Key key, KeyModifiers modifiers, CanvasKeyAction expected) =>
        await Assert.That(CanvasKeyMap.Resolve(key, modifiers)).IsEqualTo(expected);

    // F frames the selection: the zoom fits the padded bounds, never above 1 and never below the editor's own
    // floor, and the bounds sit at the viewport's centre. The readable floor is a fit-on-open rule, not F's.
    [Test]
    public async Task FramingASelection_FitsItsZoom_AndCentresTheBounds()
    {
        Size viewport = new(1000, 500);

        ReadableFit small = ReadableFit.OfSelection(new Rect(100, 100, 200, 100), viewport, 0.1);
        ReadableFit wide = ReadableFit.OfSelection(new Rect(0, 0, 4000, 500), viewport, 0.1);
        ReadableFit vast = ReadableFit.OfSelection(new Rect(0, 0, 40000, 500), viewport, 0.1);
        using (Assert.Multiple())
        {
            await Assert.That(small.Zoom).IsEqualTo(1.0).Because("a selection that fits is never zoomed in past 1");
            await Assert.That(small.Location).IsEqualTo(new Point(200 - 500, 150 - 250)).Because("the bounds' centre is the viewport's");
            await Assert.That(wide.Zoom).IsEqualTo(0.25).Within(1e-9).Because("the width is the binding side");
            await Assert.That(wide.Location).IsEqualTo(new Point(0, 250 - 1000)).Because("the frame is 1000 graph units tall at 0.25");
            await Assert.That(vast.Zoom).IsEqualTo(0.1).Because("the editor's floor wins over the fit");
            await Assert.That(vast.FramesSourceToStat).IsFalse();
        }
    }

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

    // An editable combo with its list closed steps its selection on Up and Down, and the gate and field
    // combos write a step at once, over the half-typed text.
    [Test]
    public async Task UpOrDownInAClosedEditableCombo_OpensItsList_AndNothingElseDoes()
    {
        ComboBox gate = new() { IsEditable = true };
        ComboBox picker = new();
        TextBox literal = new();
        StackPanel face = new() { Children = { gate, picker, literal } };
        using (Assert.Multiple())
        {
            await Assert.That(ReferenceEquals(CanvasGestures.ListToOpen(Key.Down, KeyModifiers.None, gate), gate)).IsTrue();
            await Assert.That(ReferenceEquals(CanvasGestures.ListToOpen(Key.Up, KeyModifiers.None, gate), gate)).IsTrue();
            await Assert.That(CanvasGestures.ListToOpen(Key.Down, KeyModifiers.Alt, gate)).IsNull().Because("Alt+Down is the combo's own toggle");
            await Assert.That(CanvasGestures.ListToOpen(Key.Enter, KeyModifiers.None, gate)).IsNull();
            await Assert.That(CanvasGestures.ListToOpen(Key.Down, KeyModifiers.None, picker)).IsNull().Because("a plain picker is left to the combo");
            await Assert.That(CanvasGestures.ListToOpen(Key.Down, KeyModifiers.None, literal)).IsNull();
            await Assert.That(CanvasGestures.ListToOpen(Key.Down, KeyModifiers.None, face)).IsNull();
            await Assert.That(CanvasGestures.ListToOpen(Key.Down, KeyModifiers.None, null)).IsNull();
            gate.IsDropDownOpen = true;
            await Assert.That(CanvasGestures.ListToOpen(Key.Down, KeyModifiers.None, gate)).IsNull().Because("an open list is the combo's");
        }
    }

    // The container turns any press it sees into a drag and captures the pointer, so a left press on an
    // editor stops at the face. A right press must go on: the container is what selects the node before
    // the menu opens, and a menu over an unselected node acts on the old selection.
    [Test]
    public async Task TheFaceKeepsALeftPressOnAnEditor_AndLetsARightPressReachTheContainer()
    {
        CheckBox toggle = new();
        Button remove = new();
        TextBlock caption = new();
        StackPanel face = new() { Children = { toggle, remove, caption } };
        PointerPointProperties left = new(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        PointerPointProperties right = new(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed);
        using (Assert.Multiple())
        {
            await Assert.That(CanvasGestures.FaceKeepsPress(toggle, face, left)).IsTrue();
            await Assert.That(CanvasGestures.FaceKeepsPress(remove, face, left)).IsTrue();
            await Assert.That(CanvasGestures.FaceKeepsPress(caption, face, left)).IsFalse().Because("a press on plain text drags the node");
            await Assert.That(CanvasGestures.FaceKeepsPress(toggle, face, right)).IsFalse().Because("the container selects on the right press");
            await Assert.That(CanvasGestures.FaceKeepsPress(remove, face, right)).IsFalse();
            await Assert.That(CanvasGestures.FaceKeepsPress(null, face, left)).IsFalse();
        }
    }

    // The toast's close button promises Esc, but the toast only reads Escape when it holds focus, which
    // nothing gives it; the key lands in the editor as ClearFocusAndSelection, so that command takes the
    // toast with the selection and the control hides on the null.
    [Test]
    public async Task EscapesCommand_TakesTheToastWithTheSelection()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.SelectedNodes.Add(vm.Nodes.First());
        vm.Notify("a refusal");
        await Assert.That(vm.Toast).IsNotNull();

        await Assert.That(CanvasKeyMap.Resolve(Key.Escape, KeyModifiers.None)).IsEqualTo(CanvasKeyAction.ClearFocusAndSelection);
        vm.ClearFocusAndSelectionCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Toast).IsNull().Because("Esc dismisses the toast from the editor, as its tooltip says");
            await Assert.That(vm.SelectedNodes).IsEmpty();
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
