#region

using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     One node on the canvas: a stat or a highlight. The slot model is the design review's: the values a
///     node reads from siblings are its inputs and the value it produces is its output, so wires are value
///     references only; the trigger, the gate and the scope are chips, not ports; a <c>match:</c> block is
///     a list of facet rows on the node face.
/// </summary>
public sealed partial class CanvasNodeViewModel : ObservableObject
{
    /// <summary>The <c>stats:</c> or <c>highlights:</c> key.</summary>
    public required string Id { get; init; }

    /// <summary>The stat kind (<c>count</c>, <c>sum</c>, <c>compute</c> and so on), or <c>highlight</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The author's <c>label:</c> or a highlight's <c>title:</c>, or null.</summary>
    public string? Label { get; init; }

    /// <summary>The header chips: the trigger, the gate and the scope, in that order, each one that is declared.</summary>
    public IReadOnlyList<string> Chips { get; init; } = [];

    /// <summary>The <c>match:</c> facets, each as the facet's name and its written value.</summary>
    public IReadOnlyList<CanvasFacetViewModel> Facets { get; init; } = [];

    /// <summary>The <c>compute:</c>, flag, <c>where:</c> or <c>when:</c> text, kept textual; lines joined by newline.</summary>
    public string? Expression { get; init; }

    /// <summary>The line the entry starts on in its file, for the jump to come. Zero when unknown.</summary>
    public int SourceLine { get; init; }

    /// <summary>Whether the open document declares this node; a <c>use:</c> dependency's node is drawn read-only.</summary>
    public bool IsAuthorable { get; init; } = true;

    /// <summary>The values this node reads from siblings, one input connector each.</summary>
    public ObservableCollection<CanvasConnectorViewModel> Inputs { get; } = [];

    /// <summary>The value this node produces, when a sibling reads it.</summary>
    public ObservableCollection<CanvasConnectorViewModel> Outputs { get; } = [];

    /// <summary>Graph position; the container writes it back on drag.</summary>
    [ObservableProperty]
    private Point _location;

    /// <summary>The size the placement assumed, so the viewport can be fitted before the containers are measured.</summary>
    public Size EstimatedSize { get; set; } = new(250, 80);

    /// <summary>Whether the facet rows show. The detailed style shows them; the compact one keeps the count only.</summary>
    [ObservableProperty]
    private bool _showFacets = true;

    public bool IsHighlight => Kind == "highlight";

    public bool IsCompute => Kind == "compute";

    public bool HasFacets => Facets.Count > 0;

    public bool HasExpression => Expression is not null;

    /// <summary>The compact style's facet count shows only where there are facets to count.</summary>
    public bool ShowFacetSummary => !ShowFacets && HasFacets;

    partial void OnShowFacetsChanged(bool value) => OnPropertyChanged(nameof(ShowFacetSummary));

    public string KindLabel => Kind.ToUpperInvariant();

    /// <summary>The compact style's one-line stand-in for the facet rows.</summary>
    public string FacetSummary => Facets.Count switch
    {
        0 => "",
        1 => "1 facet",
        var n => $"{n} facets"
    };
}

/// <summary>One <c>match:</c> facet as the author wrote it.</summary>
/// <param name="Name">The facet name.</param>
/// <param name="Value">The written value: <c>true</c>, <c>1</c>, <c>&lt;= 320</c>.</param>
public sealed record CanvasFacetViewModel(string Name, string Value);
