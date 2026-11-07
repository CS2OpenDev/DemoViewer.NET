#region

using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>One entry of a choice picker: what the file gets, and what the picker shows.</summary>
/// <param name="Value">What the YAML gets; empty for the entry that removes the key.</param>
/// <param name="Display">What the picker shows: the value, or the default marked as such.</param>
public sealed record CanvasChoice(string Value, string Display);

/// <summary>
///     One closed-set field of a node face: a capture's <c>keep:</c>, a compute's <c>format:</c>, a bucket's
///     <c>reduce:</c>, a highlight's <c>kind:</c>. The first entry stands for the omitted key and shows the
///     engine's default; picking it removes the key.
/// </summary>
public sealed partial class CanvasChoiceViewModel : ObservableObject
{
    private readonly Action<string, string?> _write;
    private readonly bool _loading;

    /// <param name="key">The YAML key.</param>
    /// <param name="label">What the face shows before the value.</param>
    /// <param name="options">The legal values, in the order the picker lists them.</param>
    /// <param name="current">The value as written, or null when the document omits the key.</param>
    /// <param name="defaultText">What the engine uses when the key is omitted, or null for nothing.</param>
    /// <param name="write">Writes the key; null removes it.</param>
    public CanvasChoiceViewModel(string key, string label, IReadOnlyList<string> options, string? current, string? defaultText,
        Action<string, string?> write)
    {
        ArgumentNullException.ThrowIfNull(options);
        _loading = true;
        Key = key;
        Label = label;
        Current = current ?? "";
        DefaultText = defaultText;
        Options = [new CanvasChoice("", defaultText is null ? "none" : defaultText + " (default)"), .. options.Select(o => new CanvasChoice(o, o))];
        _write = write;
        _selected = Options.FirstOrDefault(o => string.Equals(o.Value, Current, StringComparison.Ordinal))
                    ?? new CanvasChoice(Current, Current);
        if (!Options.Contains(_selected))
        {
            // A value outside the list stays visible and selected rather than being read as the default.
            Options = [.. Options, _selected];
        }

        _loading = false;
    }

    public string Key { get; }

    public string Label { get; }

    /// <summary>The value as written, or empty.</summary>
    public string Current { get; }

    public string? DefaultText { get; }

    public IReadOnlyList<CanvasChoice> Options { get; }

    public bool IsDefault => Current.Length == 0;

    /// <summary>The read-only face's text: the value, or the default marked as such.</summary>
    public string ReadText => IsDefault ? Options[0].Display : Current;

    [ObservableProperty]
    private CanvasChoice? _selected;

    [ObservableProperty]
    private bool _isEditable;

    partial void OnSelectedChanged(CanvasChoice? value)
    {
        if (!_loading && IsEditable && value is not null && !string.Equals(value.Value, Current, StringComparison.Ordinal))
        {
            _write(Key, value.Value.Length == 0 ? null : value.Value);
        }
    }
}
