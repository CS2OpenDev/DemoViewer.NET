#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>One threshold of a tally: the inclusive minimum and the counter it increments.</summary>
public sealed partial class CanvasThresholdRowViewModel(CanvasThresholdsViewModel owner, string min, string target) : ObservableObject
{
    [ObservableProperty]
    private string _min = min;

    [ObservableProperty]
    private string _target = target;

    [ObservableProperty]
    private bool _isEditable;

    /// <summary>Writes the table when this row changed. Bound to Enter and to the editor losing focus.</summary>
    [RelayCommand]
    private void Commit() => owner.Commit();

    [RelayCommand]
    private void Remove() => owner.Remove(this);
}

/// <summary>
///     A tally's <c>thresholds:</c> as a small table. Every change writes the whole list back, in the order
///     shown, as one value the semantics turn into the block sequence of flow maps the corpus writes.
/// </summary>
public sealed partial class CanvasThresholdsViewModel : ObservableObject
{
    private readonly Action<string, string?> _write;
    private string _written;

    /// <param name="rows">The thresholds as written, in document order.</param>
    /// <param name="write">Writes the <c>thresholds</c> key with the table as text; see <see cref="Serialize" />.</param>
    public CanvasThresholdsViewModel(IReadOnlyList<(string Min, string Target)> rows, Action<string, string?> write)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _write = write;
        foreach ((string min, string target) in rows)
        {
            Rows.Add(new CanvasThresholdRowViewModel(this, min, target));
        }

        _written = Serialize(Rows.Select(r => (r.Min, r.Target)));
    }

    public ObservableCollection<CanvasThresholdRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private bool _isEditable;

    partial void OnIsEditableChanged(bool value)
    {
        foreach (CanvasThresholdRowViewModel row in Rows)
        {
            row.IsEditable = value;
        }
    }

    /// <summary>One row per line, the minimum and the target separated by one space.</summary>
    public static string Serialize(IEnumerable<(string Min, string Target)> rows) =>
        string.Join('\n', rows.Select(r => r.Min.Trim() + " " + r.Target.Trim()));

    /// <summary>
    ///     The rows of a <see cref="Serialize" /> text, one per line. A blank cell comes back empty rather
    ///     than dropping its row, so the semantics refuse it instead of writing the row out of the file.
    /// </summary>
    public static IReadOnlyList<(string Min, string Target)> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return [];
        }

        List<(string, string)> rows = [];
        foreach (string line in text.Split('\n'))
        {
            string[] parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            rows.Add((parts[0], parts.Length > 1 ? parts[1] : ""));
        }

        return rows;
    }

    /// <summary>Writes the table when any row changed.</summary>
    public void Commit()
    {
        if (!IsEditable)
        {
            return;
        }

        string now = Serialize(Rows.Select(r => (r.Min, r.Target)));
        if (!string.Equals(now, _written, StringComparison.Ordinal))
        {
            _written = now;
            _write("thresholds", now);
        }
    }

    /// <summary>Adds a row one above the highest minimum, named after it, and writes.</summary>
    [RelayCommand]
    private void Add()
    {
        if (!IsEditable)
        {
            return;
        }

        int next = Rows.Select(r => int.TryParse(r.Min, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0)
            .DefaultIfEmpty(0).Max() + 1;
        Rows.Add(new CanvasThresholdRowViewModel(this, next.ToString(CultureInfo.InvariantCulture), "at_least_" + next) { IsEditable = true });
        Commit();
    }

    /// <summary>Removes a row and writes; the last row cannot go, a tally needs one.</summary>
    public void Remove(CanvasThresholdRowViewModel row)
    {
        if (!IsEditable || Rows.Count <= 1 || !Rows.Remove(row))
        {
            return;
        }

        Commit();
    }
}
