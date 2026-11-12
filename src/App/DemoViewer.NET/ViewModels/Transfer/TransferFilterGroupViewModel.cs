#region

using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.ViewModels.Library;

#endregion

namespace DemoViewer.NET.ViewModels.Transfer;

/// <summary>
///     A contributed Library filter (the Team filter) on the export picker, as chips: the Library tab picks one of
///     its choices, the picker any number, and a demo shows when it matches one of them.
/// </summary>
public sealed class TransferFilterGroupViewModel
{
    private readonly ContributedLibraryFilter _source;

    /// <param name="source">The filter and how it is handed a demo.</param>
    /// <param name="onChanged">Runs when a chip is picked or unpicked.</param>
    public TransferFilterGroupViewModel(ContributedLibraryFilter source, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onChanged);
        _source = source;
        Label = source.Filter.Label;
        Tooltip = source.Filter.Tooltip ?? source.Filter.Label;
        // The "" choice is the drop-down's All; with chips, none picked is all.
        Chips =
        [
            .. source.Filter.Items.Where(i => i.Key.Length > 0).Select(i => new TransferFilterChip(i.Key, i.Display))
        ];
        foreach (TransferFilterChip chip in Chips)
        {
            chip.PropertyChanged += (_, _) => onChanged();
        }
    }

    /// <summary>The filter's label, e.g. "Team".</summary>
    public string Label { get; }

    /// <summary>The filter's tooltip.</summary>
    public string Tooltip { get; }

    /// <summary>One chip per choice.</summary>
    public IReadOnlyList<TransferFilterChip> Chips { get; }

    /// <summary>The picked chips as the shared predicate takes them.</summary>
    public ContributedFilterChoice Choice => _source.Pick([.. Chips.Where(c => c.IsSelected).Select(c => c.Key)]);
}

/// <summary>One choice of a contributed filter on the export picker.</summary>
public sealed partial class TransferFilterChip(string key, string display) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>What the filter matches by.</summary>
    public string Key { get; } = key;

    /// <summary>What the chip shows.</summary>
    public string Display { get; } = display;
}
