#region

using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     One column of a show node: the entry it surfaces, its label, and the read input the surfaced entry
///     is wired into. The builder gives it its port once the connectors exist; the tab gives it the surfaced
///     stat's live badge for the selected instance, so a table reads as a table.
/// </summary>
public sealed partial class CanvasColumnViewModel(ShowReference reference) : ObservableObject, ILiveBadge
{
    public ShowReference Reference { get; } = reference ?? throw new ArgumentNullException(nameof(reference));

    /// <summary>What the column is called: the <c>label:</c>, or the reference when there is none.</summary>
    public string Label => Reference.Label ?? Reference.Stat;

    /// <summary>The entry as written in the file.</summary>
    public string Stat => Reference.Stat;

    /// <summary>The scoreboard group, or empty.</summary>
    public string Group => Reference.Group ?? "";

    public bool HasGroup => Group.Length > 0;

    /// <summary>The <c>as:</c> chip: <c>as seconds</c>, or empty for the raw value.</summary>
    public string AsChip => Reference.As is { } format ? "as " + format : "";

    public bool HasAs => Reference.As is not null;

    /// <summary>The scoreboard <c>boards:</c> chip: <c>boards round, match</c>, or empty for the default from the stat's <c>per:</c>.</summary>
    public string BoardsChip => Reference.Boards is { Count: > 0 } boards ? "boards " + string.Join(", ", boards) : "";

    public bool HasBoards => Reference.Boards is { Count: > 0 };

    /// <summary>Whether the label differs from the reference, so both are worth showing.</summary>
    public bool ShowsStat => !string.Equals(Label, Stat, StringComparison.Ordinal);

    /// <summary>The read input the surfaced entry's wire lands on; null until the builder sets it.</summary>
    [ObservableProperty]
    private CanvasConnectorViewModel? _port;

    /// <summary>The surfaced stat's value from the last run, for the selected instance; empty when there is none. Set by the tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLive))]
    private string _liveBadge = "";

    /// <summary>What the badge says on hover: the stat's own tip.</summary>
    [ObservableProperty]
    private string _liveTip = "";

    /// <summary>True when the badge comes from a run over text that has since changed; the badge greys.</summary>
    [ObservableProperty]
    private bool _isLiveStale;

    public bool HasLive => LiveBadge.Length > 0;
}
