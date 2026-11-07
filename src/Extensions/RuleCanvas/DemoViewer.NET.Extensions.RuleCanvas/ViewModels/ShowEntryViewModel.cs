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
        IsEditable = editable;
    }

    public ShowReference Reference { get; }

    /// <summary>Where the entry shows: <c>scoreboard</c> or <c>table player_round</c>.</summary>
    public string Surface => Reference.IsScoreboard ? "scoreboard" : "table " + Reference.Table;

    /// <summary>The reference as written in the file.</summary>
    public string Stat => Reference.Stat;

    public InspectorFieldViewModel Label { get; }

    public InspectorFieldViewModel Group { get; }

    public bool IsEditable { get; }

    [RelayCommand]
    private void Remove() => _remove(Reference);
}
