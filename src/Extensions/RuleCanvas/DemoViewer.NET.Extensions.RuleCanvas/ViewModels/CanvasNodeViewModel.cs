#region

using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Rules;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>One header chip: its text and what it says on hover.</summary>
public sealed record CanvasChip(string Text, string? Tip)
{
    public bool HasTip => !string.IsNullOrEmpty(Tip);
}

/// <summary>
///     One node on the canvas: a stat or a highlight. The slot model is the design review's: the values a
///     node reads from siblings are its inputs and the value it produces is its output, so wires are value
///     references only; the trigger, the gate and the scope are chips, not ports; a <c>match:</c> block is
///     a typed form on the node face, scoped to the view the node fires on. While editable, the trigger,
///     the scope and the gate are pickers in place of their chips, and every kind-specific key is a row.
/// </summary>
public sealed partial class CanvasNodeViewModel : ObservableObject
{
    private Action<string, string?>? _writeFacet;
    private Action<string, string?>? _writeField;

    /// <summary>The scopes a stat can be kept per.</summary>
    public static IReadOnlyList<string> PerOptions { get; } = ["round", "match"];

    /// <summary>The gates every stat can run while, with the empty entry for none; a node adds its ruleset's flags.</summary>
    public static IReadOnlyList<string> GateOptions { get; } = ["", .. CanvasEditSemantics.GatePaths.Select(g => g.Path)];

    /// <summary>The typed face this node was built from, or null for a node made by hand.</summary>
    public NodeFace? Face { get; init; }

    /// <summary>The <c>stats:</c> or <c>highlights:</c> key, or <c>define.&lt;name&gt;</c> for a define.</summary>
    public required string Id { get; init; }

    /// <summary>What the header shows: the define's name, or the id.</summary>
    public string Title => Key.Name;

    /// <summary>The node's identity in a layout, a selection and a focus.</summary>
    public RulesetDocumentNodeKey Key => new(Ruleset, Id);

    /// <summary>The ruleset that declares the node; with <see cref="Id" /> it is the node's identity in a layout.</summary>
    public string Ruleset { get; init; } = "";

    /// <summary>The stat kind (<c>count</c>, <c>sum</c>, <c>compute</c> and so on), or <c>highlight</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The author's <c>label:</c> or a highlight's <c>title:</c>, or null.</summary>
    public string? Label { get; init; }

    /// <summary>The view the node fires on, as the resolver named it, or null.</summary>
    public string? View { get; init; }

    /// <summary>The value the node produces, as the checked model types it, or null when unknown.</summary>
    public string? ValueType { get; init; }

    /// <summary>The output connector's title: the value type when the face knows it.</summary>
    public string OutputTitle => ValueType ?? "";

    /// <summary>The header chips: the trigger, the actor binding, the gate and the scope, in that order, each one that is declared.</summary>
    public IReadOnlyList<string> Chips { get; init; } = [];

    /// <summary>What a chip says on hover, by its text; a chip with no entry has no tip.</summary>
    public IReadOnlyDictionary<string, string> ChipTips { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The <c>match:</c> facets the document binds, in the catalog's order, then any the view does not know.</summary>
    public IReadOnlyList<CanvasFacetViewModel> Facets { get; init; } = [];

    /// <summary>The view's facets the document does not bind yet, for the add-facet picker.</summary>
    public IReadOnlyList<string> AvailableFacets { get; init; } = [];

    /// <summary>Answers the catalog type of an available facet, for the initial value an added one gets.</summary>
    public Func<string, RulesTypeKind>? AvailableFacetKind { get; init; }

    /// <summary>Rows the engine adds and the author cannot remove: <c>not a suicide</c> on a view that bakes it in.</summary>
    public IReadOnlyList<string> FixedRows { get; init; } = [];

    /// <summary>What a fixed row says on hover, by its text: where it comes from and where it can be edited.</summary>
    public IReadOnlyDictionary<string, string> FixedRowTips { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The fixed rows with their tips, for the template.</summary>
    public IReadOnlyList<CanvasChip> FixedRowItems => [.. FixedRows.Select(r => new CanvasChip(r, FixedRowTips.GetValueOrDefault(r)))];

    /// <summary>The <c>compute:</c>, flag, <c>where:</c> or <c>when:</c> text, kept textual; lines joined by newline.</summary>
    public string? Expression { get; init; }

    /// <summary>The same fields as editors, for an authorable stat: <c>where</c> always, <c>when</c> for a flag, <c>compute</c> for a compute.</summary>
    public IReadOnlyList<CanvasExpressionViewModel> Lines { get; init; } = [];

    /// <summary>The trigger picker, for a stat that fires on something; null for a compute, a rate and an expression flag.</summary>
    public CanvasTriggerViewModel? Trigger { get; init; }

    /// <summary>The deactivator picker of a flag with <c>off:</c>, writing that key; null for every other face.</summary>
    public CanvasTriggerViewModel? OffTrigger { get; init; }

    /// <summary>The kind's free-text keys: a sum's value, a streak's window, a rate's buckets, a highlight's group.</summary>
    public IReadOnlyList<CanvasFieldViewModel> Fields { get; init; } = [];

    /// <summary>The kind's closed-set keys: <c>keep</c>, <c>format</c>, <c>reduce</c>, a highlight's <c>kind</c>.</summary>
    public IReadOnlyList<CanvasChoiceViewModel> Choices { get; init; } = [];

    /// <summary>A tally's thresholds table; null for every other kind.</summary>
    public CanvasThresholdsViewModel? Thresholds { get; init; }

    /// <summary>The engine defaults the document leaves implicit, in words: <c>score 50</c>, <c>per round</c>.</summary>
    public IReadOnlyList<string> Defaults { get; init; } = [];

    /// <summary>The role the view binds to this player, or null when there is no binding to switch off.</summary>
    public string? ViewActor { get; init; }

    /// <summary>The <c>per:</c> scope as written, for the picker; null when the picker has no selection.</summary>
    [ObservableProperty]
    private string? _per = "";

    /// <summary>The <c>while:</c> gate as written, or empty.</summary>
    [ObservableProperty]
    private string _gate = "";

    /// <summary>The gate editor's text; written on commit, not on every keystroke.</summary>
    [ObservableProperty]
    private string _gateText = "";

    /// <summary>The gate picked from the list, which lands in the editor; null while the text is free.</summary>
    [ObservableProperty]
    private string? _gateChoice;

    /// <summary>The gates this node can pick: none, the context paths, and the flags of its ruleset and its dependencies.</summary>
    public IReadOnlyList<string> GateChoices { get; init; } = GateOptions;

    /// <summary>Whether the gate as written is one the list offers; an unlisted gate keeps its chip beside the editor.</summary>
    public bool IsGateListed => Gate.Length == 0 || GateChoices.Contains(Gate, StringComparer.Ordinal);

    /// <summary>The label editor's text.</summary>
    [ObservableProperty]
    private string _labelText = "";

    /// <summary>True while the header shows the label editor instead of the label.</summary>
    [ObservableProperty]
    private bool _isEditingLabel;

    /// <summary>The actor toggle: true binds the event to this player (the view's own binding), false writes <c>actor: any</c>.</summary>
    [ObservableProperty]
    private bool? _bindsActor;

    /// <summary>Whether the face shows the scope and gate pickers' row: an editable stat, highlight or define.</summary>
    public bool ShowFieldPickers => IsEditable && IsAuthorable && !IsScaffold;

    /// <summary>A define has no scope of its own; the stats firing on it keep theirs.</summary>
    public bool ShowPerPicker => ShowFieldPickers && !IsDefine;

    /// <summary>A highlight has no gate; its <c>per:</c> is the rising edge's reset scope.</summary>
    public bool ShowGatePicker => !IsHighlight;

    /// <summary>The trigger picker shows while editable, on a stat that fires on something.</summary>
    public bool ShowTriggerPicker => IsEditable && Trigger is not null;

    /// <summary>The off: picker shows while editable, under the on: row of a flag that has one.</summary>
    public bool ShowOffTriggerPicker => IsEditable && OffTrigger is not null;

    /// <summary>The add-facet picker: an editable stat whose view still has facets to offer.</summary>
    public bool ShowFacetPicker => IsEditable && !IsHighlight && HasAvailableFacets;

    /// <summary>The actor toggle: an editable stat on a view that binds a role.</summary>
    public bool ShowActorToggle => IsEditable && ViewActor is not null;

    /// <summary>The label can be edited in place on an editable stat; a highlight's title has its own line, and a define has no label key.</summary>
    public bool CanEditLabel => IsEditable && !IsHighlight && !IsScaffold && !IsDefine;

    public bool ShowLabelText => !IsEditingLabel && Label is not null;

    public bool ShowLabelEditor => CanEditLabel && IsEditingLabel;

    /// <summary>
    ///     The chips without the ones a picker stands in for while editable: the trigger, the scope, and a
    ///     gate the list offers. A gate outside the list keeps its chip, so what the file says stays in view.
    /// </summary>
    public IReadOnlyList<string> VisibleChips => IsEditable
        ? [.. Chips.Where(c => !IsPickedChip(c))]
        : Chips;

    /// <summary>The visible chips with their tips, for the template.</summary>
    public IReadOnlyList<CanvasChip> VisibleChipItems => [.. VisibleChips.Select(c => new CanvasChip(c, ChipTips.GetValueOrDefault(c)))];

    public bool ShowExpressionText => HasExpression && !IsEditable;

    public bool ShowLines => IsEditable && Lines.Count > 0;

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

    /// <summary>
    ///     Where <c>show:</c> surfaces this entry, in words (<c>shown: scoreboard as Kills</c>, <c>not shown</c>),
    ///     set by the tab from <see cref="ShowSurface" /> after every redraw; empty for a node that is not the author's.
    /// </summary>
    [ObservableProperty]
    private string _surfacing = "";

    /// <summary>Whether <c>show:</c> references this entry somewhere.</summary>
    public bool IsSurfaced => Surfacing.StartsWith("shown", StringComparison.Ordinal);

    public bool IsHighlight => Kind == "highlight";

    /// <summary>A trigger-bodied <c>define:</c> of the open document; its stats fire through it.</summary>
    public bool IsDefine => Key.IsDefine;

    /// <summary>A node the engine made rather than the author: an event, an enrichment, a context value.</summary>
    public bool IsScaffold => Ruleset.StartsWith('~');

    public bool IsCompute => Kind == "compute";

    public bool HasFacets => Facets.Count > 0;

    public bool HasAvailableFacets => AvailableFacets.Count > 0;

    public bool HasFixedRows => FixedRows.Count > 0;

    public bool HasFields => Fields.Count > 0;

    public bool HasChoices => Choices.Count > 0;

    public bool HasThresholds => Thresholds is not null;

    public bool HasDefaults => Defaults.Count > 0;

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

    /// <summary>Hooks the writers: facet rows and the add-facet picker, then the scope, gate and expression editors.</summary>
    public void AttachWriters(Action<string, string?> writeFacet, Action<string, string?> writeField)
    {
        _writeFacet = writeFacet;
        _writeField = writeField;
    }

    /// <summary>What a field editor calls: a value to set, or null to remove the field.</summary>
    public void WriteField(string key, string? value) => _writeField?.Invoke(key, value);

    /// <summary>What a facet row calls: a value to set, or null to remove the facet.</summary>
    public void WriteFacet(string facet, string? value) => _writeFacet?.Invoke(facet, value);

    /// <summary>Writes the gate editor's text: a gate to set, or nothing to remove it. Bound to Enter and to losing focus.</summary>
    [RelayCommand]
    public void CommitGate()
    {
        if (_writeField is null || !IsEditable)
        {
            return;
        }

        string text = GateText.Trim();
        if (!string.Equals(text, Gate, StringComparison.Ordinal))
        {
            WriteField("while", text.Length == 0 ? null : text);
        }
    }

    [RelayCommand]
    private void BeginEditLabel()
    {
        if (CanEditLabel)
        {
            LabelText = Label ?? "";
            IsEditingLabel = true;
        }
    }

    /// <summary>Writes the label editor's text: a label to set, or nothing to remove it. Bound to Enter and to losing focus.</summary>
    [RelayCommand]
    public void CommitLabel()
    {
        IsEditingLabel = false;
        if (_writeField is null || !CanEditLabel)
        {
            return;
        }

        string text = LabelText.Trim();
        if (!string.Equals(text, Label ?? "", StringComparison.Ordinal))
        {
            WriteField("label", text.Length == 0 ? null : text);
        }
    }

    // The pickers write only once a writer is attached, so the values the builder sets are not written back.
    // A cleared picker hands null; a scope is never removed, so only a value is written.
    partial void OnPerChanged(string? value)
    {
        if (_writeField is not null && IsEditable && value is { Length: > 0 })
        {
            WriteField("per", value);
        }
    }

    partial void OnGateChanged(string value)
    {
        OnPropertyChanged(nameof(IsGateListed));
        if (_writeField is not null && IsEditable)
        {
            WriteField("while", value.Length == 0 ? null : value);
        }
    }

    // A pick lands in the editor and commits with it. The combo also selects on the keystroke whose
    // text equals a listed gate, so a pick cannot be told from a prefix still being typed.
    partial void OnGateChoiceChanged(string? value)
    {
        if (value is not null)
        {
            GateText = value;
        }
    }

    // The toggle writes the key that switches the view's binding off, or takes it away again.
    partial void OnBindsActorChanged(bool? value)
    {
        if (_writeFacet is not null && IsEditable && ViewActor is not null && value is { } binds)
        {
            bool suppressed = Chips.Contains("any player", StringComparer.Ordinal);
            if (binds == suppressed)
            {
                WriteFacet("actor", binds ? null : "any");
            }
        }
    }

    partial void OnIsEditingLabelChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLabelText));
        OnPropertyChanged(nameof(ShowLabelEditor));
    }

    partial void OnShowFacetsChanged(bool value) => OnPropertyChanged(nameof(ShowFacetSummary));

    partial void OnSurfacingChanged(string value) => OnPropertyChanged(nameof(IsSurfaced));

    partial void OnIsEditableChanged(bool value)
    {
        foreach (CanvasFacetViewModel facet in Facets)
        {
            facet.IsEditable = value;
        }

        foreach (CanvasExpressionViewModel line in Lines)
        {
            line.IsEditable = value;
        }

        foreach (CanvasFieldViewModel field in Fields)
        {
            field.IsEditable = value;
        }

        foreach (CanvasChoiceViewModel choice in Choices)
        {
            choice.IsEditable = value;
        }

        if (Trigger is not null)
        {
            Trigger.IsEditable = value;
        }

        if (OffTrigger is not null)
        {
            OffTrigger.IsEditable = value;
        }

        if (Thresholds is not null)
        {
            Thresholds.IsEditable = value;
        }

        OnPropertyChanged(nameof(ShowFieldPickers));
        OnPropertyChanged(nameof(ShowPerPicker));
        OnPropertyChanged(nameof(ShowTriggerPicker));
        OnPropertyChanged(nameof(ShowOffTriggerPicker));
        OnPropertyChanged(nameof(ShowFacetPicker));
        OnPropertyChanged(nameof(ShowActorToggle));
        OnPropertyChanged(nameof(CanEditLabel));
        OnPropertyChanged(nameof(ShowLabelEditor));
        OnPropertyChanged(nameof(VisibleChips));
        OnPropertyChanged(nameof(VisibleChipItems));
        OnPropertyChanged(nameof(ShowExpressionText));
        OnPropertyChanged(nameof(ShowLines));
    }

    // A new facet is written with a value of its type the author then edits in place: a bool is on, a
    // number is zero, and text is a placeholder that reads as one.
    partial void OnFacetToAddChanged(string? value)
    {
        if (value is null || !IsEditable)
        {
            return;
        }

        RulesTypeKind kind = AvailableFacetKind?.Invoke(value) ?? RulesTypeKind.None;
        if (value.StartsWith("event.", StringComparison.Ordinal))
        {
            (string comparator, string literal) = kind switch
            {
                RulesTypeKind.Bool => ("==", "true"),
                RulesTypeKind.Int or RulesTypeKind.Float => (">", "0"),
                _ => ("==", "\"value\"")
            };
            Lines.FirstOrDefault(l => l.Key == "where")?.AppendRow(value, comparator, literal);
            FacetToAdd = null;
            return;
        }

        string initial = kind switch
        {
            RulesTypeKind.Bool => "true",
            RulesTypeKind.Int or RulesTypeKind.Float => "0",
            _ => "value"
        };
        WriteFacet(value, initial);
        FacetToAdd = null;
    }

    private bool IsPickedChip(string chip)
    {
        if (chip.StartsWith("per ", StringComparison.Ordinal))
        {
            return true;
        }

        if (chip.StartsWith("while ", StringComparison.Ordinal))
        {
            return IsGateListed;
        }

        if (chip.StartsWith("off ", StringComparison.Ordinal))
        {
            return OffTrigger is not null;
        }

        return Trigger is not null && chip.StartsWith("on ", StringComparison.Ordinal);
    }
}
