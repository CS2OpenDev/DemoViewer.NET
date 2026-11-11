#region

using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     One scoreboard entry or table column in the Inspector's Surfacing section: the reference as written,
///     its label and group as editable fields, and its removal. Every write goes through the Inspector.
/// </summary>
public sealed partial class ShowEntryViewModel
{
    private readonly Action<ShowReference> _remove;

    public ShowEntryViewModel(ShowReference reference, bool editable, Action<ShowReference, string, string?> write, Action<ShowReference> remove)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(write);
        Reference = reference;
        _remove = remove;
        Label = new InspectorFieldViewModel("label", "label", (_, value) => write(reference, "label", value)) { IsEditable = editable };
        Label.Load(reference.Label);
        Group = new InspectorFieldViewModel("group", "group", (_, value) => write(reference, "group", value)) { IsEditable = editable };
        Group.Load(reference.Group, reference.IsScoreboard);
        As = new InspectorFieldViewModel("as", "as", (_, value) => write(reference, "as", value), AsOptions,
            "display of a tick-valued column: ticks, seconds or m:ss time; none shows the raw value") { IsEditable = editable };
        As.Load(reference.As);
        Boards = new InspectorFieldViewModel("boards", "boards", (_, value) => write(reference, "boards", value), BoardsOptions,
            "the boards the column lands on; none infers them from the stat's per:") { IsEditable = editable };
        Boards.Load(reference.Boards is { Count: > 0 } boards ? string.Join(", ", boards) : null, reference.IsScoreboard);
        IsEditable = editable;
    }

    /// <summary>The <c>as:</c> picker's options; the empty one removes the key.</summary>
    public static IReadOnlyList<string> AsOptions { get; } = ["", .. ShowReference.AsOptions];

    /// <summary>The <c>boards:</c> picker's options, each the list as the field spells it; the empty one removes the key.</summary>
    public static IReadOnlyList<string> BoardsOptions { get; } = ["", "round", "match", "round, match"];

    /// <summary>The boards a picker's text spells (<c>round, match</c>); empty for none.</summary>
    public static IReadOnlyList<string> ParseBoards(string? text) =>
        [.. (text ?? "").Split(',').Select(b => b.Trim()).Where(b => b.Length > 0)];

    public ShowReference Reference { get; }

    /// <summary>Where the entry shows: <c>scoreboard</c> or <c>table player_round</c>.</summary>
    public string Surface => Reference.IsScoreboard ? "scoreboard" : "table " + Reference.Table;

    /// <summary>The reference as written in the file.</summary>
    public string Stat => Reference.Stat;

    public InspectorFieldViewModel Label { get; }

    public InspectorFieldViewModel Group { get; }

    /// <summary>The <c>as:</c> display format, a picker.</summary>
    public InspectorFieldViewModel As { get; }

    /// <summary>The scoreboard <c>boards:</c> override, a picker; a column hides it.</summary>
    public InspectorFieldViewModel Boards { get; }

    public bool IsEditable { get; }

    [RelayCommand]
    private void Remove() => _remove(Reference);
}
