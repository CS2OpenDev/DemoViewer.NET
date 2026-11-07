#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     A flat condition as rows on a node face: one row per comparison or bare path, joined by one
///     connective. Every edit regenerates the text through <see cref="ConditionExpression" />'s row
///     writers, so the rows the user did not touch keep their bytes, and hands the text to the owning line,
///     which writes the field. A text that is not flat has no rows; the line shows its text editor instead.
/// </summary>
public sealed partial class CanvasConditionViewModel : ObservableObject
{
    private readonly Action<string> _write;

    /// <param name="expression">The parsed text.</param>
    /// <param name="write">Writes the regenerated text; the owner maps an empty text to a removed field.</param>
    public CanvasConditionViewModel(ConditionExpression expression, Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(expression);
        _write = write;
        Expression = expression;
        Load(expression);
    }

    /// <summary>The expression the rows show.</summary>
    public ConditionExpression Expression { get; private set; }

    /// <summary>One row per leaf, then any row added and not yet written.</summary>
    public ObservableCollection<CanvasConditionRowViewModel> Rows { get; } = [];

    public bool IsFlat => Expression.IsFlat;

    public ConditionConnective? Connective => Expression.Connective;

    /// <summary>What the join between two rows says: the author's own spelling, or the kind an added row would get.</summary>
    public string ConnectiveLabel => Expression.ConnectiveSpelling ?? (Connective == ConditionConnective.Or ? "or" : "and");

    /// <summary>The paths the row editors complete from.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _paths = [];

    /// <summary>Whether the rows write. Follows the line's.</summary>
    [ObservableProperty]
    private bool _isEditable;

    /// <summary>Rebuilds the rows from <paramref name="expression" />; a row added and not written is dropped.</summary>
    /// <param name="expression">The parsed text.</param>
    public void Load(ConditionExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        Expression = expression;
        Rows.Clear();
        if (expression.IsFlat)
        {
            foreach (ConditionRow row in expression.Rows)
            {
                Rows.Add(new CanvasConditionRowViewModel(this, row) { IsEditable = IsEditable });
            }
        }

        Renumber();
        OnPropertyChanged(nameof(Expression));
        OnPropertyChanged(nameof(IsFlat));
        OnPropertyChanged(nameof(Connective));
        OnPropertyChanged(nameof(ConnectiveLabel));
    }

    /// <summary>Adds an empty row the user fills in; it is written once it has a path.</summary>
    [RelayCommand]
    public void AddRow()
    {
        if (!IsEditable || !IsFlat)
        {
            return;
        }

        Rows.Add(new CanvasConditionRowViewModel(this, null) { IsEditable = true });
        Renumber();
    }

    /// <summary>Removes a row: a written one from the text, an unwritten one from the list.</summary>
    /// <param name="row">The row.</param>
    [RelayCommand]
    public void RemoveRow(CanvasConditionRowViewModel? row)
    {
        if (row is null || !IsEditable || !Rows.Contains(row))
        {
            return;
        }

        if (row.IsPending)
        {
            Rows.Remove(row);
            Renumber();
            return;
        }

        Write(Expression.WithRowRemoved(Rows.IndexOf(row)));
    }

    /// <summary>Joins every row by the other connective. One row has no join to switch.</summary>
    [RelayCommand]
    public void ToggleConnective()
    {
        if (!IsEditable || !IsFlat || Connective is not { } current)
        {
            return;
        }

        Write(Expression.WithConnective(current == ConditionConnective.And ? ConditionConnective.Or : ConditionConnective.And));
    }

    /// <summary>Writes a row's parts into the text: a new row is appended, a written one replaced in place.</summary>
    /// <param name="row">The row.</param>
    /// <returns>Whether a text was written.</returns>
    public bool CommitRow(CanvasConditionRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!IsEditable || !IsFlat || !Rows.Contains(row))
        {
            return false;
        }

        string path = row.Path.Trim();
        string? comparator = row.IsBare ? null : row.Comparator;
        string? literal = row.IsBare ? null : row.Literal.Trim();
        if (path.Length == 0 || (comparator is not null && literal!.Length == 0))
        {
            return false;
        }

        string text = row.IsPending
            ? Expression.WithRowAdded(path, comparator, literal, Connective ?? ConditionConnective.And)
            : Expression.WithRow(Rows.IndexOf(row), path, comparator, literal);
        return Write(text);
    }

    private bool Write(string text)
    {
        if (string.Equals(text, Expression.Text, StringComparison.Ordinal))
        {
            return false;
        }

        _write(text);
        return true;
    }

    partial void OnIsEditableChanged(bool value)
    {
        foreach (CanvasConditionRowViewModel row in Rows)
        {
            row.IsEditable = value;
        }
    }

    partial void OnPathsChanged(IReadOnlyList<string> value)
    {
        foreach (CanvasConditionRowViewModel row in Rows)
        {
            row.PathsChanged();
        }
    }

    private void Renumber()
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].Index = i;
        }
    }
}

/// <summary>
///     One row of a flat condition: a path, a comparator and a literal, or a bare path. A bare row is
///     spelled <see cref="Bare" /> in the comparator picker; a <c>true</c> or <c>false</c> literal is
///     shown as a toggle. A row made by the add button is pending until it has a path and is committed.
/// </summary>
public sealed partial class CanvasConditionRowViewModel : ObservableObject
{
    /// <summary>The comparator picker's entry for a bare path: the row holds when the path is true.</summary>
    public const string Bare = "is true";

    private readonly CanvasConditionViewModel _owner;

    /// <param name="owner">The condition the row belongs to.</param>
    /// <param name="row">The row as parsed, or null for a row not yet written.</param>
    public CanvasConditionRowViewModel(CanvasConditionViewModel owner, ConditionRow? row)
    {
        _owner = owner;
        IsPending = row is null;
        _path = row?.Path ?? "";
        _comparator = row?.Comparator ?? Bare;
        _literal = row?.Literal ?? "";
    }

    /// <summary>The bare form, then the facet comparators, then membership.</summary>
    public static IReadOnlyList<string> Comparators { get; } = [Bare, .. CanvasFacetViewModel.Comparators, "in"];

    /// <summary>Whether the row was added on the face and not written yet.</summary>
    public bool IsPending { get; }

    /// <summary>The row's position; the join shows above every row but the first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirst))]
    private int _index;

    public bool IsFirst => Index == 0;

    /// <summary>The paths the path editor completes from.</summary>
    public IReadOnlyList<string> Paths => _owner.Paths;

    /// <summary>The join above this row, the condition's one spelling.</summary>
    public string ConnectiveLabel => _owner.ConnectiveLabel;

    [ObservableProperty]
    private bool _isEditable;

    [ObservableProperty]
    private string _path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBare))]
    [NotifyPropertyChangedFor(nameof(IsBoolLiteral))]
    [NotifyPropertyChangedFor(nameof(ShowLiteralBox))]
    private string _comparator;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoolLiteral))]
    [NotifyPropertyChangedFor(nameof(ShowLiteralBox))]
    [NotifyPropertyChangedFor(nameof(BoolLiteral))]
    private string _literal;

    public bool IsBare => Comparator == Bare;

    /// <summary>A <c>true</c> or <c>false</c> literal, shown as a toggle rather than a text box.</summary>
    public bool IsBoolLiteral => !IsBare && Literal is ("true" or "false");

    public bool ShowLiteralBox => !IsBare && !IsBoolLiteral;

    /// <summary>The toggle over a boolean literal; setting it writes the row.</summary>
    public bool BoolLiteral
    {
        get => Literal == "true";
        set
        {
            if (!IsBoolLiteral)
            {
                return;
            }

            Literal = value ? "true" : "false";
            Commit();
        }
    }

    /// <summary>The join button above the row switches the whole condition between and and or.</summary>
    [RelayCommand]
    public void ToggleConnective() => _owner.ToggleConnective();

    /// <summary>Writes the row. Bound to Enter and to an editor losing focus.</summary>
    [RelayCommand]
    public void Commit()
    {
        if (IsEditable)
        {
            _owner.CommitRow(this);
        }
    }

    [RelayCommand]
    private void Remove() => _owner.RemoveRow(this);

    internal void PathsChanged() => OnPropertyChanged(nameof(Paths));

    // Switching the form writes at once when the row is complete; a new literal is still to be typed.
    partial void OnComparatorChanged(string value)
    {
        if (IsEditable && (IsBare || Literal.Trim().Length > 0))
        {
            Commit();
        }
    }
}
