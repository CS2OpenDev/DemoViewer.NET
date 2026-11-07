#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Catalog;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>What a line's path completion is scoped to: the node's view and the sibling stats it may read.</summary>
/// <param name="View">The view the node fires on, or null for none.</param>
/// <param name="SiblingIds">Stat ids of the same document.</param>
/// <param name="Catalog">The catalog to read the view from; null for the embedded one.</param>
public sealed record CanvasExpressionContext(string? View, IReadOnlyList<string> SiblingIds, CatalogRoot? Catalog = null);

/// <summary>
///     One expression line on a node face: a <c>where:</c>, a flag's or highlight's <c>when:</c>, a
///     <c>compute:</c> formula, or a highlight's <c>title:</c> or <c>score:</c>. A condition is shown as
///     rows while it is flat, with the text under them; a formula or a nested condition is edited as text.
///     Every write goes back through the owning node with the field's key: a row edit regenerates the whole
///     text from the parsed expression, so what the user did not touch keeps its bytes.
/// </summary>
public sealed partial class CanvasExpressionViewModel : ObservableObject
{
    private readonly Action<string, string?> _write;
    private readonly string _original;

    /// <param name="key">The YAML key the line edits: <c>where</c>, <c>when</c>, <c>compute</c>, <c>title</c> or <c>score</c>.</param>
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
        Condition = ConditionExpression.Parse(text);
        Builder = new CanvasConditionViewModel(Condition, WriteText);
    }

    public string Key { get; }

    public string Prefix { get; }

    /// <summary>What the empty editor says: the field it would add.</summary>
    public string Placeholder => Key + " …";

    /// <summary>A <c>where:</c> or <c>when:</c>: a condition the face can show as rows.</summary>
    public bool IsCondition => Key is "where" or "when";

    /// <summary>A condition or a formula: the editor completes paths of the node's view.</summary>
    public bool HasCompletion => IsCondition || Key == "compute";

    /// <summary>The text parsed; follows <see cref="Text" />.</summary>
    public ConditionExpression Condition { get; private set; }

    /// <summary>The rows and their commands.</summary>
    public CanvasConditionViewModel Builder { get; }

    public ObservableCollection<CanvasConditionRowViewModel> Rows => Builder.Rows;

    public bool IsFlat => Condition.IsFlat;

    public ConditionConnective? Connective => Condition.Connective;

    /// <summary>The paths the editors complete from; empty until a <see cref="Context" /> is set.</summary>
    public IReadOnlyList<string> Paths => Builder.Paths;

    /// <summary>The rows show for a flat condition unless the user asked for the text.</summary>
    public bool ShowRows => IsCondition && IsFlat && !EditAsText;

    public bool ShowTextEditor => !ShowRows;

    /// <summary>A flat condition can be switched between rows and text; the rest is text only.</summary>
    public bool CanToggleText => IsCondition && IsFlat;

    /// <summary>Why a condition shows no rows.</summary>
    public bool ShowTextNote => IsCondition && !IsFlat;

    public string TextNote => Condition.Error is { } error ? "edited as text: " + error : "edited as text";

    public string ToggleLabel => EditAsText ? "edit as rows" : "edit as text";

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isEditable;

    /// <summary>The user's choice to type the condition rather than edit its rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRows))]
    [NotifyPropertyChangedFor(nameof(ShowTextEditor))]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    private bool _editAsText;

    /// <summary>Where the completion reads from. Set by whoever builds the node; null leaves the paths empty.</summary>
    [ObservableProperty]
    private CanvasExpressionContext? _context;

    [RelayCommand]
    public void AddRow() => Builder.AddRow();

    [RelayCommand]
    public void RemoveRow(CanvasConditionRowViewModel? row) => Builder.RemoveRow(row);

    [RelayCommand]
    public void ToggleConnective() => Builder.ToggleConnective();

    [RelayCommand]
    public void ToggleEditAsText() => EditAsText = !EditAsText;

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

    // A row edit hands the regenerated text whole; an emptied condition removes the field.
    private void WriteText(string text)
    {
        if (IsEditable)
        {
            _write(Key, text.Trim().Length == 0 ? null : text);
        }
    }

    partial void OnTextChanged(string value)
    {
        Condition = ConditionExpression.Parse(value);
        Builder.Load(Condition);
        OnPropertyChanged(nameof(Condition));
        OnPropertyChanged(nameof(IsFlat));
        OnPropertyChanged(nameof(Connective));
        OnPropertyChanged(nameof(ShowRows));
        OnPropertyChanged(nameof(ShowTextEditor));
        OnPropertyChanged(nameof(CanToggleText));
        OnPropertyChanged(nameof(ShowTextNote));
        OnPropertyChanged(nameof(TextNote));
    }

    partial void OnIsEditableChanged(bool value) => Builder.IsEditable = value;

    partial void OnContextChanged(CanvasExpressionContext? value)
    {
        Builder.Paths = value is null
            ? []
            : ConditionVocabulary.PathsFor(value.Catalog ?? EmbeddedCatalog.Root, value.View, value.SiblingIds);
        OnPropertyChanged(nameof(Paths));
    }
}
