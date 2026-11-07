#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     One expression line on a node face: a <c>where:</c>, a flag's <c>when:</c> or a <c>compute:</c>
///     formula, kept textual. The write goes back through the owning node with the field's key.
/// </summary>
public sealed partial class CanvasExpressionViewModel : ObservableObject
{
    private readonly Action<string, string?> _write;
    private readonly string _original;

    /// <param name="key">The YAML key the line edits: <c>where</c>, <c>when</c> or <c>compute</c>.</param>
    /// <param name="prefix">What the face shows before the text.</param>
    /// <param name="text">The text as written, or empty for a field the entry does not have yet.</param>
    /// <param name="write">Writes the field; null removes it.</param>
    public CanvasExpressionViewModel(string key, string prefix, string text, Action<string, string?> write)
    {
        Key = key;
        Prefix = prefix;
        _original = text;
        _text = text;
        _write = write;
    }

    public string Key { get; }

    public string Prefix { get; }

    /// <summary>What the empty editor says: the field it would add.</summary>
    public string Placeholder => Key + " …";

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isEditable;

    /// <summary>Writes the text when it changed. Bound to Enter and to the editor losing focus.</summary>
    [RelayCommand]
    public void Commit()
    {
        if (!IsEditable)
        {
            return;
        }

        string text = Text.Trim();
        if (text != _original)
        {
            _write(Key, text.Length == 0 ? null : text);
        }
    }
}
