#region

using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     One column of a show node: the entry it surfaces, its label, and the read input the surfaced entry
///     is wired into. The builder gives it its port once the connectors exist.
/// </summary>
public sealed partial class CanvasColumnViewModel(ShowReference reference) : ObservableObject
{
    public ShowReference Reference { get; } = reference ?? throw new ArgumentNullException(nameof(reference));

    /// <summary>What the column is called: the <c>label:</c>, or the reference when there is none.</summary>
    public string Label => Reference.Label ?? Reference.Stat;

    /// <summary>The entry as written in the file.</summary>
    public string Stat => Reference.Stat;

    /// <summary>The scoreboard group, or empty.</summary>
    public string Group => Reference.Group ?? "";

    public bool HasGroup => Group.Length > 0;

    /// <summary>Whether the label differs from the reference, so both are worth showing.</summary>
    public bool ShowsStat => !string.Equals(Label, Stat, StringComparison.Ordinal);

    /// <summary>The read input the surfaced entry's wire lands on; null until the builder sets it.</summary>
    [ObservableProperty]
    private CanvasConnectorViewModel? _port;
}
