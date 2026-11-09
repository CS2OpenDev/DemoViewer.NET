#region

using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Edits made on a wire rather than on a node: the condition editor a wire opens, and a gallery item
///     dropped on a wire. The editor edits the target node's key through the node's own writer, so the
///     write, the redraw and the undo are the face's; the redraw replaces the wires, which closes it.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    private CanvasWireEditorViewModel? _wireEditor;

    /// <summary>The condition editor open on a wire, or null.</summary>
    public CanvasWireEditorViewModel? WireEditor
    {
        get => _wireEditor;
        private set
        {
            if (SetProperty(ref _wireEditor, value))
            {
                OnPropertyChanged(nameof(HasWireEditor));
            }
        }
    }

    public bool HasWireEditor => WireEditor is not null;

    /// <summary>Raised when an editor opens on a wire, so the view can place it at the wire's label.</summary>
    public event Action<CanvasWireEditorViewModel>? WireEditorOpened;

    /// <summary>
    ///     Opens the condition editor on <paramref name="wire" />: the operand row or the key text it edits,
    ///     or the reason it edits nothing. One editor at a time; opening another commits nothing of the first.
    /// </summary>
    public void BeginEditWire(CanvasConnectionViewModel wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        WireEditor = null;
        if (BuildWireEditor(wire, CloseWireEditor) is { } editor)
        {
            WireEditor = editor;
            WireEditorOpened?.Invoke(editor);
        }
    }

    // The editor over a wire, for the canvas and the Inspector alike: the semantics' decision, with the
    // surface's own refusal in front of it while the file cannot be written here.
    private CanvasWireEditorViewModel? BuildWireEditor(CanvasConnectionViewModel wire, Action close)
    {
        WireEdit? edit = wire.ConditionEdit ?? CanvasEditSemantics.WireEditOf(wire);
        if (edit is null)
        {
            return null;
        }

        if (!IsEditable && edit.IsEnabled)
        {
            edit = edit with { Reason = IsUserRuleset ? "Save or revert the YAML tab to edit here." : "a shipped ruleset is read only here" };
        }

        string current = edit.Key switch
        {
            "when" => edit.Target.Lines.FirstOrDefault(l => l.Key == "when")?.Text ?? "",
            "while" => edit.Target.Gate,
            _ => ""
        };
        return new CanvasWireEditorViewModel(wire, edit, current, close);
    }

    /// <summary>Opens the editor on the wire the menu or the gesture named.</summary>
    [RelayCommand]
    private void EditWire(object? wire)
    {
        if (wire is CanvasConnectionViewModel connection)
        {
            BeginEditWire(connection);
        }
    }

    /// <summary>Writes what the open editor holds and closes it; nothing happens when none is open.</summary>
    [RelayCommand]
    private void CommitWireEditor() => WireEditor?.Commit();

    /// <summary>Closes the open editor without a write.</summary>
    [RelayCommand]
    private void CancelWireEditor() => WireEditor?.Cancel();

    private void CloseWireEditor() => WireEditor = null;

    // The redraw after a write replaces the wires: an editor over a wire that is gone has nothing to write
    // to, and a selection of gone wires would show the Inspector a stale target.
    private void OnWiresChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (WireEditor is { } editor && !Connections.Contains(editor.Wire))
        {
            WireEditor = null;
        }

        foreach (object stale in SelectedWires.Where(w => !Connections.Contains(w)).ToList())
        {
            SelectedWires.Remove(stale);
        }
    }

    /// <summary>
    ///     A gallery item dropped on a wire: the define goes in front of a trigger wire's target, a gate
    ///     becomes the target's <c>while:</c>, a view retargets a trigger wire's target. An item with no
    ///     meaning on a wire is placed at <paramref name="at" /> as a drop on bare canvas is.
    /// </summary>
    public void DropOnWire(GalleryItem item, CanvasConnectionViewModel wire, Avalonia.Point at)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(wire);
        if (!IsEditable)
        {
            EditStatus = $"{item.Name} can only be dropped on a define or a stat of your own ruleset.";
            return;
        }

        EditRequest? request = CanvasEditSemantics.DropOnWire(item, wire, FreshId(CanvasEditSemantics.PlacedIdFor(item)));
        if (request is null)
        {
            PlaceItem(item, at);
        }
        else
        {
            Apply(request);
        }
    }
}
