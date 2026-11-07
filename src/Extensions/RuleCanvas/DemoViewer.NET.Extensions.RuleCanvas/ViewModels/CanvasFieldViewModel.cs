#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     One free-text field of a node face with suggestions: a sum's value, a streak's window, a bucket's
///     key, a rate's numerator, a highlight's group. Shows the engine's default dimmed when the document
///     omits the key; an emptied optional field removes the key, an emptied required one is handed to the
///     semantics, which refuse it with the reason.
/// </summary>
public sealed partial class CanvasFieldViewModel : ObservableObject
{
    private readonly Action<string, string?> _write;
    private bool _loading = true;

    /// <param name="key">The field as the writer addresses it: <c>sum</c>, <c>window</c>, <c>rate.of</c>.</param>
    /// <param name="label">What the face shows before the value.</param>
    /// <param name="text">The value as written, or null when the document omits the key.</param>
    /// <param name="suggestions">What the picker offers; the field takes any text besides.</param>
    /// <param name="defaultText">What the engine uses when the key is omitted, or null when nothing stands in.</param>
    /// <param name="write">Writes the field; null removes it.</param>
    /// <param name="locked">A value the face shows but cannot write, with <paramref name="lockedReason" /> as the tip.</param>
    /// <param name="lockedReason">Why the field is read-only on an editable node.</param>
    public CanvasFieldViewModel(string key, string label, string? text, IReadOnlyList<string> suggestions, string? defaultText,
        Action<string, string?> write, bool locked = false, string? lockedReason = null)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        Key = key;
        Label = label;
        Original = text ?? "";
        Suggestions = suggestions;
        DefaultText = defaultText;
        IsLocked = locked;
        LockedReason = lockedReason ?? "";
        _write = write;
        _text = Original;
        _loading = false;
    }

    public string Key { get; }

    public string Label { get; }

    /// <summary>The value as written, or empty.</summary>
    public string Original { get; }

    public IReadOnlyList<string> Suggestions { get; }

    public string? DefaultText { get; }

    public bool IsLocked { get; }

    public string LockedReason { get; }

    public bool HasValue => Original.Length > 0;

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>Whether the face shows the default dimmed in place of a value.</summary>
    public bool ShowsDefault => !HasValue && DefaultText is not null;

    /// <summary>The read-only face's text: the value, or the default marked as such.</summary>
    public string ReadText => HasValue ? Original : DefaultText is null ? "" : DefaultText + " (default)";

    /// <summary>What the empty editor says.</summary>
    public string Placeholder => DefaultText is null ? Label + " …" : DefaultText + " (default)";

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isEditable;

    /// <summary>The suggestion picked from the list, which commits at once.</summary>
    [ObservableProperty]
    private string? _picked;

    public bool CanEdit => IsEditable && !IsLocked;

    partial void OnIsEditableChanged(bool value) => OnPropertyChanged(nameof(CanEdit));

    partial void OnPickedChanged(string? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        Text = value;
        Commit();
    }

    /// <summary>Writes the text when it changed. Bound to Enter and to the editor losing focus.</summary>
    [RelayCommand]
    public void Commit()
    {
        if (_loading || !CanEdit)
        {
            return;
        }

        string text = Text.Trim();
        if (!string.Equals(text, Original, StringComparison.Ordinal))
        {
            _write(Key, text.Length == 0 ? null : text);
        }
    }
}
