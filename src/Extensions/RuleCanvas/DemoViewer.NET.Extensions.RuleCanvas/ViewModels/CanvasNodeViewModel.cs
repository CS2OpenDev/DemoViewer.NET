#region

using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CS2DemoKit.Analysis.Rules;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     One node on the canvas: a stat or a highlight. The slot model is the design review's: the values a
///     node reads from siblings are its inputs and the value it produces is its output, so wires are value
///     references only; the trigger, the gate and the scope are chips, not ports; a <c>match:</c> block is
///     a typed form on the node face, scoped to the view the node fires on.
/// </summary>
public sealed partial class CanvasNodeViewModel : ObservableObject
{
    private Action<string, string?>? _writeFacet;

    /// <summary>The <c>stats:</c> or <c>highlights:</c> key.</summary>
    public required string Id { get; init; }

    /// <summary>The ruleset that declares the node; with <see cref="Id" /> it is the node's identity in a layout.</summary>
    public string Ruleset { get; init; } = "";

    /// <summary>The stat kind (<c>count</c>, <c>sum</c>, <c>compute</c> and so on), or <c>highlight</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The author's <c>label:</c> or a highlight's <c>title:</c>, or null.</summary>
    public string? Label { get; init; }

    /// <summary>The view the node fires on, as the author named it, or null.</summary>
    public string? View { get; init; }

    /// <summary>The header chips: the trigger, the gate and the scope, in that order, each one that is declared.</summary>
    public IReadOnlyList<string> Chips { get; init; } = [];

    /// <summary>The <c>match:</c> facets the document binds, in the catalog's order, then any the view does not know.</summary>
    public IReadOnlyList<CanvasFacetViewModel> Facets { get; init; } = [];

    /// <summary>The view's facets the document does not bind yet, for the add-facet picker.</summary>
    public IReadOnlyList<string> AvailableFacets { get; init; } = [];

    /// <summary>Answers the catalog type of an available facet, for the initial value an added one gets.</summary>
    public Func<string, RulesTypeKind>? AvailableFacetKind { get; init; }

    /// <summary>The <c>compute:</c>, flag, <c>where:</c> or <c>when:</c> text, kept textual; lines joined by newline.</summary>
    public string? Expression { get; init; }

    /// <summary>The line the entry starts on in its file, for the jump. Zero when unknown.</summary>
    public int SourceLine { get; init; }

    /// <summary>Whether the open document declares this node; a <c>use:</c> dependency's node is drawn read-only.</summary>
    public bool IsAuthorable { get; init; } = true;

    /// <summary>The diagnostics the composition raised inside this entry; empty when none.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

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

    /// <summary>Whether the node's editors write: the document is the user's and the source pane is clean.</summary>
    [ObservableProperty]
    private bool _isEditable;

    /// <summary>The facet the add-facet picker holds; adding clears it again.</summary>
    [ObservableProperty]
    private string? _facetToAdd;

    public bool IsHighlight => Kind == "highlight";

    public bool IsCompute => Kind == "compute";

    public bool HasFacets => Facets.Count > 0;

    public bool HasAvailableFacets => AvailableFacets.Count > 0;

    public bool HasExpression => Expression is not null;

    public bool HasDiagnostics => Diagnostics.Count > 0;

    public string DiagnosticsText => string.Join('\n', Diagnostics);

    /// <summary>The compact style's facet count shows only where there are facets to count.</summary>
    public bool ShowFacetSummary => !ShowFacets && HasFacets;

    public string KindLabel => Kind.ToUpperInvariant();

    /// <summary>The compact style's one-line stand-in for the facet rows.</summary>
    public string FacetSummary => Facets.Count switch
    {
        0 => "",
        1 => "1 facet",
        var n => $"{n} facets"
    };

    /// <summary>Hooks the writer every facet row and the add-facet picker write through.</summary>
    public void AttachWriter(Action<string, string?> writeFacet) => _writeFacet = writeFacet;

    /// <summary>What a facet row calls: a value to set, or null to remove the facet.</summary>
    public void WriteFacet(string facet, string? value) => _writeFacet?.Invoke(facet, value);

    partial void OnShowFacetsChanged(bool value) => OnPropertyChanged(nameof(ShowFacetSummary));

    partial void OnIsEditableChanged(bool value)
    {
        foreach (CanvasFacetViewModel facet in Facets)
        {
            facet.IsEditable = value;
        }
    }

    // A new facet is written with a value of its type the author then edits in place: a bool is on, a
    // number is zero, and text is a placeholder that reads as one.
    partial void OnFacetToAddChanged(string? value)
    {
        if (value is null || !IsEditable)
        {
            return;
        }

        string initial = AvailableFacetKind?.Invoke(value) switch
        {
            RulesTypeKind.Bool => "true",
            RulesTypeKind.Int or RulesTypeKind.Float => "0",
            _ => "value"
        };
        WriteFacet(value, initial);
        FacetToAdd = null;
    }
}
