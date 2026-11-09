#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     The condition editor opened on a wire. One operand row of the target's <c>when:</c> for a logic wire,
///     rewritten through the same row writer the face uses so every other byte of the condition stays; the
///     whole key as text for a comparison gate's <c>while:</c> or a <c>when:</c> that is not flat; the reason
///     alone for a wire that carries no condition. Every write goes through the target node's field writer,
///     so it is one edit in the session and one undo.
/// </summary>
public sealed partial class CanvasWireEditorViewModel : ObservableObject
{
    private readonly string _original;

    /// <param name="wire">The wire the editor sits on.</param>
    /// <param name="edit">What it edits, as the semantics decided.</param>
    /// <param name="current">The key's text as written, for the text mode and the row.</param>
    /// <param name="close">Closes the editor; called after a commit and on cancel.</param>
    public CanvasWireEditorViewModel(CanvasConnectionViewModel wire, WireEdit edit, string current, Action close)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(edit);
        Wire = wire;
        Edit = edit;
        Close = close;
        _original = current;
        _text = current;
        if (edit is { IsEnabled: true, Row: { } index })
        {
            // Its own rows over the same parsed text: a cancel drops them and the face's rows never move.
            Condition = new CanvasConditionViewModel(ConditionExpression.Parse(current), WriteText) { IsEditable = true };
            Row = index < Condition.Rows.Count ? Condition.Rows[index] : null;
        }

        Mode = !edit.IsEnabled ? WireEditorMode.Disabled : Row is not null ? WireEditorMode.Row : WireEditorMode.Text;
    }

    public CanvasConnectionViewModel Wire { get; }

    public WireEdit Edit { get; }

    public CanvasNodeViewModel Target => Edit.Target;

    public WireEditorMode Mode { get; }

    public bool IsRow => Mode == WireEditorMode.Row;

    public bool IsText => Mode == WireEditorMode.Text;

    public bool IsDisabled => Mode == WireEditorMode.Disabled;

    /// <summary>The rows of the target's <c>when:</c>, built for this editor; null in the text and disabled modes.</summary>
    public CanvasConditionViewModel? Condition { get; }

    /// <summary>The one row the wire stands for; null in the text and disabled modes.</summary>
    public CanvasConditionRowViewModel? Row { get; }

    /// <summary>The key and the node, as the header says: <c>when: on round_kast</c>.</summary>
    public string Title => Edit.IsEnabled ? $"{Edit.Key}: on {Target.Title}" : Target.Title;

    /// <summary>Why the key is edited as text, or empty: the gate is an expression, or the when: is not flat.</summary>
    public string Note => Mode switch
    {
        WireEditorMode.Text when Edit.Key == "while" => "the gate expression; empty removes it",
        WireEditorMode.Text => "edited as text: the when: is not a flat list of rows",
        _ => ""
    };

    public bool HasNote => Note.Length > 0;

    /// <summary>The reason shown in the disabled mode.</summary>
    public string Reason => Edit.Reason ?? "";

    /// <summary>What the text mode edits.</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>Closes the editor. Set by the tab.</summary>
    public Action Close { get; }

    /// <summary>Writes the row or the text when it changed, then closes. Enter and focus loss.</summary>
    [RelayCommand]
    public void Commit()
    {
        if (IsRow)
        {
            Row!.TryCommit();
        }
        else if (IsText && Target.IsEditable)
        {
            string text = Text.Trim();
            if (text != _original.Trim())
            {
                Target.WriteField(Edit.Key, text.Length == 0 ? null : text);
            }
        }

        Close();
    }

    /// <summary>Closes without a write. Escape.</summary>
    [RelayCommand]
    public void Cancel() => Close();

    // A row commit hands the whole condition back; the empty text removes the key, as the face does.
    private void WriteText(string text)
    {
        if (Target.IsEditable)
        {
            Target.WriteField(Edit.Key, text.Trim().Length == 0 ? null : text);
        }
    }
}

/// <summary>How the wire editor edits: one row, the whole text, or nothing with the reason shown.</summary>
public enum WireEditorMode
{
    Row,
    Text,
    Disabled
}
