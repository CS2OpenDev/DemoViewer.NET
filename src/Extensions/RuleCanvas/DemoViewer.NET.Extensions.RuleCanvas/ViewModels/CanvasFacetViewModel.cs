#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Rules;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     One <c>match:</c> facet row on a node face: the facet's name and type from the catalog, the value the
///     author wrote, and the editor the type calls for. A write goes back through the owning node, so the
///     row knows nothing about documents.
/// </summary>
public sealed partial class CanvasFacetViewModel : ObservableObject
{
    private readonly Action<string, string?> _write;
    private bool _loading = true;

    /// <param name="name">The facet name.</param>
    /// <param name="kind">The facet's type from the catalog; <see cref="RulesTypeKind.None" /> for a key the view does not know.</param>
    /// <param name="value">The written value, or null for a facet the document does not bind.</param>
    /// <param name="write">Writes a new value (<c>true</c>, <c>&gt; 2</c>, <c>awp</c>) or removes the facet on null.</param>
    public CanvasFacetViewModel(string name, RulesTypeKind kind, string? value, Action<string, string?> write)
    {
        Name = name;
        Kind = kind;
        Value = value ?? "";
        _write = write;
        (string comparator, string literal) = Split(value ?? "");
        _comparator = comparator;
        _literal = literal;
        _toggle = kind == RulesTypeKind.Bool && value is not null
            ? value == "true" ? true : value == "false" ? false : null
            : null;
        _loading = false;
    }

    public string Name { get; }

    public RulesTypeKind Kind { get; }

    /// <summary>The value as written, for the read-only face.</summary>
    public string Value { get; }

    public bool IsBool => Kind == RulesTypeKind.Bool;

    public bool IsNumber => Kind is RulesTypeKind.Int or RulesTypeKind.Float;

    public bool IsText => !IsBool && !IsNumber;

    /// <summary>The comparators a numeric facet takes; equality is written bare, the rest as written.</summary>
    public static IReadOnlyList<string> Comparators { get; } = ["==", "!=", ">", ">=", "<", "<="];

    /// <summary>Whether the row's editor writes. False on a shipped ruleset's node, or while the source pane is dirty.</summary>
    [ObservableProperty]
    private bool _isEditable;

    [ObservableProperty]
    private bool? _toggle;

    [ObservableProperty]
    private string _comparator;

    [ObservableProperty]
    private string _literal;

    partial void OnToggleChanged(bool? value)
    {
        if (!_loading && value is { } on)
        {
            _write(Name, on ? "true" : "false");
        }
    }

    partial void OnComparatorChanged(string value)
    {
        if (!_loading && IsNumber && Literal.Length > 0)
        {
            Commit();
        }
    }

    /// <summary>Writes the typed literal with its comparator. Bound to Enter and to the editor losing focus.</summary>
    [RelayCommand]
    public void Commit()
    {
        if (_loading || !IsEditable)
        {
            return;
        }

        string literal = Literal.Trim();
        if (literal.Length == 0)
        {
            return;
        }

        string written = IsNumber && Comparator != "==" ? Comparator + " " + literal : literal;
        if (written != Value)
        {
            _write(Name, written);
        }
    }

    [RelayCommand]
    private void Remove() => _write(Name, null);

    // `<= 320` splits into its comparator and literal; `320` and `true` are equality on the literal.
    private static (string Comparator, string Literal) Split(string value)
    {
        foreach (string comparator in Comparators.OrderByDescending(c => c.Length))
        {
            if (value.StartsWith(comparator, StringComparison.Ordinal))
            {
                return (comparator, value[comparator.Length..].Trim());
            }
        }

        return ("==", value);
    }
}
