#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     The Canvas section: a picker over the loadable rulesets, the chosen one drawn as nodes, its YAML
///     beside it, and edits from either surface written to the same file through the splice writer. The
///     composition root over the session (the open file and its history), the semantics (what a gesture
///     writes), the arranger (where nodes go) and the factory (what a node shows); this file holds the
///     state the view binds to and the redraw, the partials hold the commands. The corpus is read on first
///     activation and re-read after every write, on the UI thread until the SDK offers a demo-less job.
/// </summary>
public sealed partial class CanvasTabViewModel : ExtensionViewModel, IWorkspaceTabViewModel
{
    /// <summary>The Rule Workbench's tab id on the main strip, as the host declares it.</summary>
    public const string AuthoringTabId = "ruleworkbench.editor";

    private readonly Func<string, bool> _selectTab;
    private readonly Func<RulesetCorpus> _loadCorpus;
    private readonly RulesetDocumentSession _session = new();
    private readonly CanvasArranger _arranger;
    private readonly CanvasNodeFactory _factory;
    private RulesetCorpus? _corpus;
    private RulesetCorpus.Composed? _composed;
    private bool _redrawing;

    /// <param name="selectTab">Selects a host tab by id; the Authoring tab's id is <see cref="AuthoringTabId" />.</param>
    public CanvasTabViewModel(Func<string, bool> selectTab)
        : this(selectTab, static () => RulesetCorpus.Load(RulesetCorpus.DefaultDirectories()), static () => null)
    {
    }

    /// <param name="selectTab">Selects a host tab by id.</param>
    /// <param name="loadCorpus">Reads the rulesets the picker offers; called on first activation and after every write.</param>
    /// <param name="layoutFolder">
    ///     The extension's own config folder, where a shipped ruleset's arrangement is kept since its own
    ///     directory is not ours to write; null means shipped arrangements are not kept.
    /// </param>
    public CanvasTabViewModel(Func<string, bool> selectTab, Func<RulesetCorpus> loadCorpus, Func<string?> layoutFolder)
    {
        _selectTab = selectTab ?? throw new ArgumentNullException(nameof(selectTab));
        _loadCorpus = loadCorpus ?? throw new ArgumentNullException(nameof(loadCorpus));
        _arranger = new CanvasArranger(layoutFolder ?? throw new ArgumentNullException(nameof(layoutFolder)));
        _factory = new CanvasNodeFactory(WriteFacet, WriteField);
        SelectedNodes.CollectionChanged += (_, _) => JumpToSelection();
    }

    public ObservableCollection<CanvasNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<CanvasConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<RulesetCorpus.Entry> Rulesets { get; } = [];

    /// <summary>The editor's selection, written by the editor. The first selected node drives the source jump.</summary>
    public ObservableCollection<object> SelectedNodes { get; } = [];

    /// <summary>The catalog's views, which an added count stat fires on.</summary>
    public IReadOnlyList<string> Views { get; } = CanvasEditSemantics.ViewNames;

    /// <summary>The ruleset drawn. Setting it composes and redraws.</summary>
    [ObservableProperty]
    private RulesetCorpus.Entry? _selectedRuleset;

    /// <summary>The id opened first, when the corpus has it; otherwise the first entry.</summary>
    public string PreferredRulesetId { get; set; } = "player_round";

    /// <summary>What the canvas holds, in words: the node and wire counts, or why it is empty.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>The composition's diagnostics, one per line, or empty.</summary>
    [ObservableProperty]
    private string _diagnostics = "";

    /// <summary>The last write's outcome when it failed, or empty.</summary>
    [ObservableProperty]
    private string _editStatus = "";

    /// <summary>The selected ruleset's YAML as the file reads; empty when none is selected or it cannot be read.</summary>
    [ObservableProperty]
    private string _documentText = "";

    /// <summary>The selected ruleset's file name, for the source pane's header.</summary>
    [ObservableProperty]
    private string _documentName = "";

    /// <summary>Whether the source pane holds text that differs from the file. Canvas edits wait while it does.</summary>
    [ObservableProperty]
    private bool _sourceDirty;

    /// <summary>The line the source pane was last asked to show, 1-based; 0 before any jump.</summary>
    [ObservableProperty]
    private int _selectedLine;

    [ObservableProperty]
    private bool _showGallery = true;

    [ObservableProperty]
    private bool _showSource = true;

    /// <summary>The detailed style: facet rows on the node face. Off is the compact style: a facet count.</summary>
    [ObservableProperty]
    private bool _showFacets = true;

    /// <summary>True after a click found no Authoring tab to select: it is switched off, or the host has none.</summary>
    [ObservableProperty]
    private bool _authoringUnavailable;

    /// <summary>True while a saved arrangement is applied, so it can be reset to the computed one.</summary>
    [ObservableProperty]
    private bool _hasSavedLayout;

    public bool HasDiagnostics => Diagnostics.Length > 0;

    public bool HasEditStatus => EditStatus.Length > 0;

    public bool IsCompact => !ShowFacets;

    /// <summary>The selected ruleset is the user's own, so the canvas and the pane may write it.</summary>
    public bool IsUserRuleset => SelectedRuleset?.Source == "user";

    /// <summary>The selected ruleset is shipped: read-only here, editable as a copy under the user's rules.</summary>
    public bool IsShippedRuleset => SelectedRuleset is not null && !IsUserRuleset;

    /// <summary>Canvas editors write only into the user's own file, and only while the pane holds what the file holds.</summary>
    public bool IsEditable => IsUserRuleset && !SourceDirty;

    public bool CanUndo => _session.CanUndo;

    public bool CanRedo => _session.CanRedo;

    /// <summary>Raised on the UI thread after the node and wire collections were refilled, so the view can fit them.</summary>
    public event Action? GraphReplaced;

    /// <summary>Raised on the UI thread with a 1-based line when a selected node names where it is declared.</summary>
    public event Action<int>? JumpRequested;

    public void OnActivated(IModuleContext context)
    {
        if (_corpus is null)
        {
            _corpus = _loadCorpus();
            foreach (RulesetCorpus.Entry entry in _corpus.Entries)
            {
                Rulesets.Add(entry);
            }

            SelectedRuleset = Rulesets.FirstOrDefault(e => e.Id == PreferredRulesetId) ?? Rulesets.FirstOrDefault();
            if (SelectedRuleset is null)
            {
                Status = "No rulesets found beside this build.";
                AfterRedraw(null);
            }

            return;
        }

        // Another surface may have written the file since this tab was last shown; the Workbench does.
        if (SelectedRuleset is { } selected && !SourceDirty && _session.ChangedOnDisk)
        {
            Reload(selected.Id);
        }
    }

    public void OnDeactivated()
    {
    }

    // A reload after a write hands back a fresh Entry for the same file; only another file resets the
    // pane. The session resets the history on the same rule when the redraw opens the entry.
    partial void OnSelectedRulesetChanged(RulesetCorpus.Entry? value)
    {
        if (!string.Equals(value?.Path, _session.Entry?.Path, StringComparison.Ordinal))
        {
            SourceDirty = false;
            EditStatus = "";
        }

        NotifyEditability();
        Redraw(value, keepArrangement: false);
    }

    partial void OnDiagnosticsChanged(string value) => OnPropertyChanged(nameof(HasDiagnostics));

    // A refusal is said on the toast as well; a repeated one, which changes nothing here, is said by Report.
    partial void OnEditStatusChanged(string value)
    {
        OnPropertyChanged(nameof(HasEditStatus));
        if (value.Length > 0)
        {
            Notify(value);
        }
    }

    partial void OnSourceDirtyChanged(bool value) => NotifyEditability();

    partial void OnShowFacetsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCompact));
        foreach (CanvasNodeViewModel node in Nodes)
        {
            node.ShowFacets = value;
        }
    }

    [RelayCommand]
    private void ToggleGallery() => ShowGallery = !ShowGallery;

    [RelayCommand]
    private void SetStyle(string style) => ShowFacets = style != "compact";

    [RelayCommand]
    private void ToggleSource() => ShowSource = !ShowSource;

    [RelayCommand]
    private void OpenAuthoring() => AuthoringUnavailable = !_selectTab(AuthoringTabId);

    // What the session did, told to the surface: a refusal with a reason lands in the status, a write
    // re-reads the corpus so the picker, the chips and the wires all come from the file as it now is.
    private bool Report(EditOutcome outcome, RulesetCorpus.Entry entry)
    {
        switch (outcome.Kind)
        {
            case EditOutcomeKind.Refused:
                if (outcome.Message is { } message)
                {
                    if (EditStatus == message)
                    {
                        Notify(message);
                    }

                    EditStatus = message;
                }

                NotifyHistory();
                return false;
            case EditOutcomeKind.Written:
                EditStatus = "";
                Reload(entry.Id);
                NotifyHistory();
                return true;
            default:
                return true;
        }
    }

    // Re-reads every ruleset (a write may have changed what others see through use:) and redraws the
    // selected one with its arrangement and selection kept.
    private void Reload(string id)
    {
        _corpus = _loadCorpus();
        Rulesets.Clear();
        foreach (RulesetCorpus.Entry entry in _corpus.Entries)
        {
            Rulesets.Add(entry);
        }

        RulesetCorpus.Entry? again = Rulesets.FirstOrDefault(e => e.Id == id);
        if (again is null)
        {
            string reason = _corpus.Failures.Select(f => f.Reason).FirstOrDefault() ?? "it is not in the rules directories any more";
            EditStatus = $"{id} no longer loads as a ruleset: {reason}";
            Status = "Nothing to draw; the file no longer loads. Undo is still available.";
            MarkLoadFailure(id, reason);
            return;
        }

        _redrawing = true;
        try
        {
            SelectedRuleset = again;
        }
        finally
        {
            _redrawing = false;
        }

        NotifyEditability();
        Redraw(again, keepArrangement: true);
    }

    private void Redraw(RulesetCorpus.Entry? value, bool keepArrangement, bool recompose = true)
    {
        if (_redrawing)
        {
            return;
        }

        RulesetNodeLayout current = keepArrangement ? CurrentArrangement() : RulesetNodeLayout.Empty;
        HashSet<RulesetDocumentNodeKey> selected = [.. SelectedNodes.OfType<CanvasNodeViewModel>().Select(n => n.Key)];

        Nodes.Clear();
        Connections.Clear();
        SelectedNodes.Clear();
        SelectedLine = 0;
        Diagnostics = "";
        DocumentText = "";
        DocumentName = "";
        if (value is null || _corpus is null)
        {
            _session.Open(null);
            NotifyHistory();
            AfterRedraw(null);
            return;
        }

        ClearLoadFailure();
        DocumentName = Path.GetFileName(value.Path);
        _session.Open(value);
        DocumentText = _session.Text;
        NotifyHistory();

        if (recompose || _composed is null)
        {
            _composed = _corpus.Compose(value.Id);
        }

        OnPropertyChanged(nameof(SidecarPath));
        (RulesetCorpus.Composed composed, RulesetNodeLayout layout, bool hasSaved, bool focusDropped) =
            _arranger.Arrange(_composed, SidecarPath, current);
        HasSavedLayout = hasSaved;
        if (focusDropped)
        {
            NotifyFocus();
        }

        (IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires) =
            CanvasGraphBuilder.Build(composed, layout, _factory);
        foreach (CanvasNodeViewModel node in nodes)
        {
            node.ShowFacets = ShowFacets;
            node.IsEditable = IsEditable && node.IsAuthorable;
            Nodes.Add(node);
        }

        foreach (CanvasConnectionViewModel wire in wires)
        {
            Connections.Add(wire);
        }

        foreach (CanvasNodeViewModel node in nodes.Where(n => selected.Contains(n.Key)))
        {
            SelectedNodes.Add(node);
        }

        Diagnostics = string.Join('\n', composed.Diagnostics);
        int unwired = nodes.Count(n => n.Inputs.Count == 0 && n.Outputs.Count == 0);
        Status = nodes.Count == 0
            ? "Nothing composed; see the diagnostics."
            : $"{nodes.Count} nodes, {wires.Count} wires, {unwired} unwired";
        AfterRedraw(composed);
        GraphReplaced?.Invoke();
    }

    private void NotifyEditability()
    {
        OnPropertyChanged(nameof(IsUserRuleset));
        OnPropertyChanged(nameof(IsShippedRuleset));
        OnPropertyChanged(nameof(IsEditable));
        foreach (CanvasNodeViewModel node in Nodes)
        {
            node.IsEditable = IsEditable && node.IsAuthorable;
        }
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // Only a node the open document declares can be jumped to; a use: dependency's node lives in another
    // file, which the pane does not show.
    private void JumpToSelection()
    {
        if (SelectedNodes.OfType<CanvasNodeViewModel>().FirstOrDefault(n => n.IsAuthorable && n.SourceLine > 0) is { } node)
        {
            SelectedLine = node.SourceLine;
            JumpRequested?.Invoke(node.SourceLine);
        }
    }
}
