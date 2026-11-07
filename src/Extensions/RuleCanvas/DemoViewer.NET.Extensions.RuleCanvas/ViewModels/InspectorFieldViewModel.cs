#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     One field of the Inspector: a YAML key with its value as text, written back through the owner when it
///     changes. A field with options is a picker and writes on selection; a free one writes on Enter or when
///     its editor loses focus. An empty text removes the key, unless the owner refuses that.
/// </summary>
public sealed partial class InspectorFieldViewModel : ObservableObject
{
    private readonly Action<InspectorFieldViewModel, string?> _write;
    private string _original = "";
    private bool _loading;

    /// <param name="key">The YAML key the field edits.</param>
    /// <param name="title">What the Inspector calls it.</param>
    /// <param name="write">Writes the value, or null to remove the key.</param>
    /// <param name="options">The values a picker offers, or null for free text; an empty option stands for none.</param>
    /// <param name="hint">One line under the editor, or empty.</param>
    public InspectorFieldViewModel(string key, string title, Action<InspectorFieldViewModel, string?> write,
        IReadOnlyList<string>? options = null, string hint = "")
    {
        Key = key;
        Title = title;
        _write = write;
        Options = options;
        Hint = hint;
    }

    public string Key { get; }

    public string Title { get; }

    public string Hint { get; }

    public bool HasHint => Hint.Length > 0;

    public IReadOnlyList<string>? Options { get; }

    public bool HasOptions => Options is not null;

    public bool IsFreeText => Options is null;

    /// <summary>The value as written, or empty for a key the entry does not have.</summary>
    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isEditable;

    /// <summary>Why the last value typed was not written, or empty.</summary>
    [ObservableProperty]
    private string _error = "";

    public bool HasError => Error.Length > 0;

    /// <summary>Whether the text differs from what the file holds.</summary>
    public bool IsDirty => Text.Trim() != _original;

    /// <summary>Puts the file's value in the field without writing it back.</summary>
    public void Load(string? value, bool visible = true)
    {
        _loading = true;
        try
        {
            _original = value ?? "";
            Text = _original;
            IsVisible = visible;
            Error = "";
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Writes the text when it changed. Bound to Enter and to the editor losing focus.</summary>
    [RelayCommand]
    public void Commit()
    {
        if (!IsEditable || _loading)
        {
            return;
        }

        string text = Text.Trim();
        if (text != _original)
        {
            _write(this, text.Length == 0 ? null : text);
        }
    }

    /// <summary>Puts the file's value back, dropping what was typed.</summary>
    [RelayCommand]
    public void Revert() => Text = _original;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    // A picker writes as soon as a value is chosen; a text field waits for Enter or focus loss.
    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsDirty));
        if (HasOptions)
        {
            Commit();
        }
    }
}
