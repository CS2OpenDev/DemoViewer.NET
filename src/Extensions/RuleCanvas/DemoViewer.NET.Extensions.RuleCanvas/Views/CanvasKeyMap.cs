#region

using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>What a key press on the canvas asks for.</summary>
public enum CanvasKeyAction
{
    None,
    DeleteSelection,
    Undo,
    Redo,
    Duplicate,
    ClearFocusAndSelection,
    FrameSelection,
    FitAll
}

/// <summary>
///     The canvas's own keys, apart from the editor's built-in set (Ctrl+A, Home, arrows). Escape is
///     here rather than the editor's, which would read it from a face editor too. Ctrl and Cmd are the
///     same chord, so the map reads the same on every platform.
/// </summary>
public static class CanvasKeyMap
{
    private const KeyModifiers Primary = KeyModifiers.Control | KeyModifiers.Meta;

    public static CanvasKeyAction Resolve(Key key, KeyModifiers modifiers)
    {
        bool primary = (modifiers & Primary) != 0;
        bool shift = (modifiers & KeyModifiers.Shift) != 0;
        bool bare = modifiers == KeyModifiers.None;
        return key switch
        {
            Key.Delete or Key.Back when bare => CanvasKeyAction.DeleteSelection,
            Key.Z when primary && !shift => CanvasKeyAction.Undo,
            Key.Z when primary && shift => CanvasKeyAction.Redo,
            Key.Y when primary && !shift => CanvasKeyAction.Redo,
            Key.D when primary && !shift => CanvasKeyAction.Duplicate,
            Key.Escape => CanvasKeyAction.ClearFocusAndSelection,
            Key.F when bare => CanvasKeyAction.FrameSelection,
            Key.A when bare => CanvasKeyAction.FitAll,
            _ => CanvasKeyAction.None
        };
    }
}
