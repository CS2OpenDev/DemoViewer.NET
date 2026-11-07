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
///     corpus is read on first activation and re-read after every write; a composition is a YAML parse
///     plus a static projection and a first placement, tens of milliseconds at the corpus's sizes, and runs
///     on the UI thread until the SDK offers a demo-less job.
/// </summary>
public sealed partial class CanvasTabViewModel : ExtensionViewModel, IWorkspaceTabViewModel
{
    /// <summary>The Rule Workbench's tab id on the main strip, as the host declares it.</summary>
    public const string AuthoringTabId = "ruleworkbench.editor";

    private readonly Func<string, bool> _selectTab;
    private readonly Func<RulesetCorpus> _loadCorpus;
    private readonly Func<string?> _layoutFolder;
    private readonly RulesetDocumentSession _session = new();
    private RulesetCorpus? _corpus;
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
        _layoutFolder = layoutFolder ?? throw new ArgumentNullException(nameof(layoutFolder));
        SelectedNodes.CollectionChanged += (_, _) => JumpToSelection();
    }

    public ObservableCollection<CanvasNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<CanvasConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<RulesetCorpus.Entry> Rulesets { get; } = [];

    /// <summary>The catalog's views, which an added count stat fires on.</summary>
    public IReadOnlyList<string> Views { get; } = CanvasEditSemantics.ViewNames;

    /// <summary>What the gallery offers to drag: stat kinds, the catalog's views, the gates, and prefabs.</summary>
    public IReadOnlyList<GalleryGroup> Gallery { get; } = CanvasGallery.Build(EmbeddedCatalog.Root);

    // Where a dropped entry should land, by the id it will get; read once by the redraw that follows the write.
    private readonly Dictionary<RulesetDocumentNodeKey, Avalonia.Point> _pendingPlacement = new();

    /// <summary>Whether the gallery pane shows.</summary>
    [ObservableProperty]
    private bool _showGallery = true;

    /// <summary>The node whose slice of the chain is in focus, by its layout key, or null for the whole chain.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFocus))]
    [NotifyPropertyChangedFor(nameof(FocusLabel))]
    private RulesetDocumentNodeKey? _focusKey;

    public bool HasFocus => FocusKey is not null;

    public string FocusLabel => FocusKey?.Id ?? "";

    /// <summary>The editor's selection, written by the editor. The first selected node drives the source jump.</summary>
    public ObservableCollection<object> SelectedNodes { get; } = [];

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

    /// <summary>Whether the source pane shows beside the canvas.</summary>
    [ObservableProperty]
    private bool _showSource = true;

    /// <summary>Whether the source pane holds text that differs from the file. Canvas edits wait while it does.</summary>
    [ObservableProperty]
    private bool _sourceDirty;

    /// <summary>The text the source pane holds while dirty.</summary>
    private string _pendingSourceText = "";

    /// <summary>The line the source pane was last asked to show, 1-based; 0 before any jump.</summary>
    [ObservableProperty]
    private int _selectedLine;

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

    /// <summary>
    ///     Where the selected ruleset's arrangement lives: beside a user ruleset, under the extension's own
    ///     folder for a shipped one, or null when there is nowhere to keep it.
    /// </summary>
    public string? SidecarPath => SidecarFor(SelectedRuleset);

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

    partial void OnEditStatusChanged(string value) => OnPropertyChanged(nameof(HasEditStatus));

    partial void OnSourceDirtyChanged(bool value) => NotifyEditability();

    partial void OnShowFacetsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCompact));
        foreach (CanvasNodeViewModel node in Nodes)
        {
            node.ShowFacets = value;
        }
    }

    /// <summary>The source pane reports its text on every change; a difference from the file makes it dirty.</summary>
    public void SourceEdited(string text)
    {
        _pendingSourceText = text;
        SourceDirty = text != DocumentText;
    }

    /// <summary>Writes the source pane's text to the file, as the Workbench's Save does.</summary>
    [RelayCommand]
    private void SaveSource()
    {
        if (SelectedRuleset is not { } selected || !IsUserRuleset || !SourceDirty)
        {
            return;
        }

        // Refused with the loader's reason when the text no longer loads as this ruleset; the pane stays dirty.
        if (Report(_session.Save(_pendingSourceText), selected))
        {
            SourceDirty = false;
        }
    }

    /// <summary>Drops the source pane's unsaved text; the view puts the file's text back.</summary>
    [RelayCommand]
    private void RevertSource()
    {
        SourceDirty = false;
        OnPropertyChanged(nameof(DocumentText));
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (SelectedRuleset is { } selected)
        {
            Report(_session.Undo(), selected);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (SelectedRuleset is { } selected)
        {
            Report(_session.Redo(), selected);
        }
    }

    /// <summary>
    ///     Copies the shipped ruleset into the user's rules directory under its own name and opens the copy,
    ///     which is what the Workbench's Save-As does for a shipped file. The copy overrides the shipped one by id.
    /// </summary>
    [RelayCommand]
    private void EditCopy()
    {
        if (SelectedRuleset is not { } selected || !IsShippedRuleset || _corpus?.UserDirectory is not { } userDir)
        {
            EditStatus = "There is no user rules directory to copy into.";
            return;
        }

        string target = Path.Combine(userDir, Path.GetFileName(selected.Path));
        try
        {
            Directory.CreateDirectory(userDir);
            if (File.Exists(target))
            {
                EditStatus = $"{Path.GetFileName(target)} already exists in your rules; open it from the picker.";
                return;
            }

            File.Copy(selected.Path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EditStatus = "The copy could not be written: " + ex.Message;
            return;
        }

        Reload(selected.Id);
    }

    /// <summary>Removes the selected node's entry from the document.</summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        if (!IsEditable || SelectedNodes.OfType<CanvasNodeViewModel>().FirstOrDefault(n => n.IsAuthorable) is not { } node)
        {
            return;
        }

        Apply(CanvasEditSemantics.Delete(node));
    }

    /// <summary>Adds a count stat on <paramref name="view" /> with a fresh id, at the end of <c>stats:</c>.</summary>
    [RelayCommand]
    private void AddStat(string? view)
    {
        if (!IsEditable || string.IsNullOrWhiteSpace(view))
        {
            return;
        }

        Apply(CanvasEditSemantics.AddStat(view, FreshId("new_" + view)));
    }

    /// <summary>
    ///     A gallery item dropped on the canvas: a stat kind, a view or a prefab becomes a new entry placed at
    ///     <paramref name="at" />. A gate has nothing to attach to on bare canvas and is refused with the reason.
    /// </summary>
    public void PlaceItem(GalleryItem item, Avalonia.Point at)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsEditable || SelectedRuleset is not { } selected)
        {
            return;
        }

        string id = FreshId(CanvasEditSemantics.PlacedIdFor(item));
        EditRequest request = CanvasEditSemantics.PlaceItem(item, id);
        if (request.IsRefused)
        {
            Apply(request);
            return;
        }

        _pendingPlacement[new RulesetDocumentNodeKey(selected.Id, id)] = at;
        if (!Apply(request))
        {
            _pendingPlacement.Clear();
        }
    }

    /// <summary>
    ///     A gallery item dropped on a stat: a view retargets the trigger (the kind's own argument for a count,
    ///     <c>on:</c> for a stat whose argument is a value), and a gate becomes its <c>while:</c>.
    /// </summary>
    public void DropOnNode(GalleryItem item, CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(node);
        if (!IsEditable)
        {
            EditStatus = $"{item.Name} can only be dropped on a stat of your own ruleset.";
            return;
        }

        Apply(CanvasEditSemantics.DropOnNode(item, node));
    }

    /// <summary>
    ///     A wire drawn from one node to another, as the editor reports it: the two endpoints' view models, or
    ///     a node when the wire was dropped on its container. What the wire stands for is written to the file:
    ///     an event or a view sets the trigger, a context flag sets <c>while:</c>, an enrichment binds the
    ///     facet that reads it, and a stat into a flag extends its <c>when:</c>. Anything else is refused with
    ///     the reason, and the chain redraws from the file either way, so the wire shown is the wire run.
    /// </summary>
    [RelayCommand]
    private void Connect(object? endpoints)
    {
        (CanvasNodeViewModel? source, CanvasNodeViewModel? target) = endpoints switch
        {
            Tuple<object, object> pair => (NodeOf(pair.Item1), NodeOf(pair.Item2)),
            ValueTuple<object, object> pair => (NodeOf(pair.Item1), NodeOf(pair.Item2)),
            _ => (null, null)
        };
        if (source is null || target is null)
        {
            return;
        }

        Connect(source, target);
    }

    /// <summary>Writes what a wire from <paramref name="source" /> to <paramref name="target" /> stands for.</summary>
    public void Connect(CanvasNodeViewModel source, CanvasNodeViewModel target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (IsEditable)
        {
            Apply(CanvasEditSemantics.Connect(source, target));
        }
    }

    /// <summary>
    ///     A wire removed from the canvas: the gate wire takes <c>while:</c> with it, and an enrichment's read
    ///     takes the facet that reads it. Trigger wires and logic inputs are the stat's own definition and stay.
    /// </summary>
    [RelayCommand]
    private void Disconnect(object? wire)
    {
        if (wire is CanvasConnectionViewModel connection && IsEditable)
        {
            Apply(CanvasEditSemantics.Disconnect(connection));
        }
    }

    private static CanvasNodeViewModel? NodeOf(object endpoint) => endpoint switch
    {
        CanvasConnectorViewModel connector => connector.Owner,
        CanvasNodeViewModel node => node,
        _ => null
    };

    /// <summary>Focuses the first selected node's slice: itself, everything upstream of it, and everything it feeds.</summary>
    [RelayCommand]
    private void FocusSelected()
    {
        if (SelectedNodes.OfType<CanvasNodeViewModel>().FirstOrDefault() is { } node)
        {
            FocusNode(node);
        }
    }

    [RelayCommand]
    private void ClearFocus() => FocusKey = null;

    /// <summary>Focuses <paramref name="node" />'s slice; the rest of the chain is drawn faint until the focus is cleared.</summary>
    public void FocusNode(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        FocusKey = new RulesetDocumentNodeKey(node.Ruleset, node.Id);
    }

    // A focus change redraws from the composition already in hand: the file did not change.
    partial void OnFocusKeyChanged(RulesetDocumentNodeKey? value) => Redraw(SelectedRuleset, keepArrangement: false, recompose: false);

    private string FreshId(string wanted) => CanvasEditSemantics.FreshId(wanted, Nodes.Select(n => n.Id));

    [RelayCommand]
    private void ToggleGallery() => ShowGallery = !ShowGallery;

    [RelayCommand]
    private void SetStyle(string style) => ShowFacets = style != "compact";

    [RelayCommand]
    private void ToggleSource() => ShowSource = !ShowSource;

    [RelayCommand]
    private void OpenAuthoring() => AuthoringUnavailable = !_selectTab(AuthoringTabId);

    /// <summary>Writes every node's position to the sidecar. Bound to the editor's drag-completed command.</summary>
    [RelayCommand]
    private void SaveLayout()
    {
        if (HasFocus)
        {
            // A slice's placement is its own; the sidecar keeps the whole chain's.
            return;
        }

        string? sidecar = SidecarPath;
        if (sidecar is null || Nodes.Count == 0)
        {
            return;
        }

        RulesetNodeLayout layout = CurrentArrangement();
        HasSavedLayout = RulesetLayoutStore.SaveSidecar(sidecar, layout) && layout.Count > 0;
    }

    /// <summary>Drops the saved arrangement and places the ruleset again.</summary>
    [RelayCommand]
    private void ResetLayout()
    {
        string? sidecar = SidecarPath;
        if (sidecar is not null)
        {
            RulesetLayoutStore.SaveSidecar(sidecar, RulesetNodeLayout.Empty);
        }

        Redraw(SelectedRuleset, keepArrangement: false);
    }

    // A facet or field write from a node face: through the splice writer, then the document is re-read
    // and redrawn with every node where it was and the same node selected.
    private void WriteFacet(CanvasNodeViewModel node, string facet, string? value)
    {
        if (IsEditable)
        {
            Apply(CanvasEditSemantics.WriteFacet(node, facet, value));
        }
    }

    private void WriteField(CanvasNodeViewModel node, string key, string? value)
    {
        if (IsEditable)
        {
            Apply(CanvasEditSemantics.WriteField(node, key, value));
        }
    }

    // Everything the writer and the parser throw on a document that moved under us lands in the status.
    private bool Apply(EditRequest request) =>
        SelectedRuleset is { } selected && Report(_session.Apply(request), selected);

    // What the session did, told to the surface: a refusal lands in the status, a write re-reads the
    // corpus so the picker, the chips and the wires all come from the file as it now is.
    private bool Report(EditOutcome outcome, RulesetCorpus.Entry entry)
    {
        switch (outcome.Kind)
        {
            case EditOutcomeKind.Refused:
                if (outcome.Message is { } message)
                {
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

    private RulesetCorpus.Composed? _composed;

    private void Redraw(RulesetCorpus.Entry? value, bool keepArrangement, bool recompose = true)
    {
        if (_redrawing)
        {
            return;
        }

        RulesetNodeLayout current = keepArrangement ? CurrentArrangement() : RulesetNodeLayout.Empty;
        HashSet<RulesetDocumentNodeKey> selected = new(SelectedNodes.OfType<CanvasNodeViewModel>()
            .Select(n => new RulesetDocumentNodeKey(n.Ruleset, n.Id)));

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
            return;
        }

        DocumentName = Path.GetFileName(value.Path);
        _session.Open(value);
        DocumentText = _session.Text;
        NotifyHistory();

        if (recompose || _composed is null)
        {
            _composed = _corpus.Compose(value.Id);
        }

        RulesetCorpus.Composed composed = _composed;
        string? sidecar = SidecarFor(value);
        RulesetNodeLayout saved = sidecar is null ? RulesetNodeLayout.Empty : RulesetLayoutStore.LoadSidecar(sidecar);
        HasSavedLayout = saved.Count > 0;
        OnPropertyChanged(nameof(SidecarPath));

        // A focus draws one slice, freshly placed, so the sidecar's arrangement of the whole chain does not
        // apply to it. A focus that names a node no longer there (deleted, or another ruleset) is dropped.
        RulesetNodeLayout layout = saved;
        if (FocusKey is { } focusKey)
        {
            if (composed.Focused(focusKey) is { } focused)
            {
                composed = focused;
                layout = RulesetNodeLayout.Empty;
            }
            else
            {
                _redrawing = true;
                try
                {
                    FocusKey = null;
                }
                finally
                {
                    _redrawing = false;
                }
            }
        }

        // The arrangement on screen wins over the sidecar for a redraw after an edit; the sidecar wins on open.
        foreach ((RulesetDocumentNodeKey key, Avalonia.Point at) in current.Positions)
        {
            layout = layout.With(key, at);
        }

        foreach ((RulesetDocumentNodeKey key, Avalonia.Point at) in _pendingPlacement)
        {
            layout = layout.With(key, at);
        }

        _pendingPlacement.Clear();

        (IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires) =
            CanvasGraphBuilder.Build(composed, layout);
        foreach (CanvasNodeViewModel node in nodes)
        {
            node.ShowFacets = ShowFacets;
            node.AttachWriters((facet, facetValue) => WriteFacet(node, facet, facetValue),
                (key, fieldValue) => WriteField(node, key, fieldValue));
            node.IsEditable = IsEditable && node.IsAuthorable;
            Nodes.Add(node);
        }

        foreach (CanvasConnectionViewModel wire in wires)
        {
            Connections.Add(wire);
        }

        foreach (CanvasNodeViewModel node in nodes.Where(n => selected.Contains(new RulesetDocumentNodeKey(n.Ruleset, n.Id))))
        {
            SelectedNodes.Add(node);
        }

        Diagnostics = string.Join('\n', composed.Diagnostics);
        int unwired = nodes.Count(n => n.Inputs.Count == 0 && n.Outputs.Count == 0);
        Status = nodes.Count == 0
            ? "Nothing composed; see the diagnostics."
            : $"{nodes.Count} nodes, {wires.Count} wires, {unwired} unwired";
        GraphReplaced?.Invoke();
    }

    private RulesetNodeLayout CurrentArrangement() =>
        RulesetNodeLayout.From(Nodes.Select(n =>
            new KeyValuePair<RulesetDocumentNodeKey, Avalonia.Point>(new RulesetDocumentNodeKey(n.Ruleset, n.Id), n.Location)));

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

    private string? SidecarFor(RulesetCorpus.Entry? entry)
    {
        if (entry is null)
        {
            return null;
        }

        if (entry.Source == "user")
        {
            return RulesetLayoutStore.SidecarPathFor(entry.Path);
        }

        string? folder = _layoutFolder();
        return folder is null ? null : Path.Combine(folder, "layouts", entry.Id + RulesetLayoutStore.SidecarSuffix);
    }
}
