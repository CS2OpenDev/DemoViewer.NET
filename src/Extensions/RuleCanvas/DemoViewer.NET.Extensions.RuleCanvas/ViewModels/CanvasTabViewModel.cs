#region

using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
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
public sealed partial class CanvasTabViewModel : ExtensionViewModel, IWorkspaceTabViewModel, IDisposable
{
    /// <summary>The Rule Workbench's tab id on the main strip, as the host declares it.</summary>
    public const string AuthoringTabId = "ruleworkbench.editor";

    private readonly Func<string, bool> _selectTab;
    private readonly Func<RulesetCorpus> _loadCorpus;
    private readonly RulesetDocumentSession _session = new();
    private readonly CanvasArranger _arranger;
    private readonly CanvasNodeFactory _factory;
    private readonly Func<Action, Task>? _marshal;
    private RulesetCorpus? _corpus;
    private RulesetCorpus.Composed? _composed;
    private RulesWatcher? _watcher;
    private bool _redrawing;

    // The watcher's batches that arrived while the pane was dirty, replayed when it is clean again.
    private readonly HashSet<string> _deferred = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The status shown when the open file changed on disk while the source pane holds unsaved text.</summary>
    public const string ChangedOnDiskStatus = "file changed on disk; Revert to load it";

    /// <param name="selectTab">Selects a host tab by id; the Authoring tab's id is <see cref="AuthoringTabId" />.</param>
    public CanvasTabViewModel(Func<string, bool> selectTab)
        : this(selectTab, static () => RulesetCorpus.Load(RulesetCorpus.DefaultDirectories()), static () => null)
    {
    }

    /// <param name="selectTab">Selects a host tab by id.</param>
    /// <param name="loadCorpus">Reads the rulesets the picker offers; called on first activation and when a file appears or goes.</param>
    /// <param name="layoutFolder">
    ///     The extension's own config folder, where a shipped ruleset's arrangement is kept since its own
    ///     directory is not ours to write; null means shipped arrangements are not kept.
    /// </param>
    /// <param name="marshal">
    ///     Runs the file watcher's report on the UI thread. Null means the user rules directory is not
    ///     watched at all: a report run where the watcher fires would race the thread that edits, so a test
    ///     that wants the watcher passes a marshal that runs the report inline and drives it alone.
    /// </param>
    /// <param name="live">
    ///     The host's queue and open demo, for live values; null means no demo is ever open here and the
    ///     canvas draws without badges.
    /// </param>
    public CanvasTabViewModel(Func<string, bool> selectTab, Func<RulesetCorpus> loadCorpus, Func<string?> layoutFolder,
        Func<Action, Task>? marshal = null, LiveValuesHost? live = null)
    {
        _selectTab = selectTab ?? throw new ArgumentNullException(nameof(selectTab));
        _loadCorpus = loadCorpus ?? throw new ArgumentNullException(nameof(loadCorpus));
        _arranger = new CanvasArranger(layoutFolder ?? throw new ArgumentNullException(nameof(layoutFolder)));
        _factory = new CanvasNodeFactory(WriteFacet, WriteField);
        _marshal = marshal;
        InitLive(live);
        SelectedNodes.CollectionChanged += (_, _) => JumpToSelection();
        SelectedNodes.CollectionChanged += (_, _) => RefreshInspector();
        SelectedWires.CollectionChanged += (_, _) => RefreshInspector();
        Connections.CollectionChanged += OnWiresChanged;
    }

    /// <summary>How long the user rules directory must be quiet before a change is acted on. Read when watching starts.</summary>
    public TimeSpan WatcherDebounce { get; set; } = RulesWatcher.DefaultDebounce;

    /// <summary>Whether the user rules directory is being watched; false before activation, after deactivation, or where the platform cannot.</summary>
    public bool IsWatching => _watcher?.IsWatching == true;

    public ObservableCollection<CanvasNodeViewModel> Nodes { get; } = [];

    /// <summary>The frames drawn under the nodes, from the layout sidecar.</summary>
    public ObservableCollection<CanvasGroupViewModel> Groups { get; } = [];

    /// <summary>What the editor draws: the frames first, so they sit under the nodes, then every node.</summary>
    public ObservableCollection<object> Items { get; } = [];

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

    /// <summary>Whether the strip has a badge to show: any line in the list, the unwired warnings included.</summary>
    public bool HasDiagnostics => DiagnosticEntries.Count > 0;

    public bool HasEditStatus => EditStatus.Length > 0;

    public bool IsCompact => !ShowFacets;

    /// <summary>The selected ruleset is the user's own, so the canvas and the pane may write it.</summary>
    public bool IsUserRuleset => SelectedRuleset?.Source == "user";

    /// <summary>The selected ruleset is shipped: read-only here, editable as a copy under the user's rules.</summary>
    public bool IsShippedRuleset => SelectedRuleset is not null && !IsUserRuleset;

    /// <summary>Canvas editors write only into the user's own file, and only while the pane holds what the file holds.</summary>
    public bool IsEditable => IsUserRuleset && !SourceDirty;

    /// <summary>A document write or a layout change can be taken back.</summary>
    public bool CanUndo => _session.CanUndo || HasLayoutUndo;

    public bool CanRedo => _session.CanRedo || HasLayoutRedo;

    /// <summary>
    ///     Raised on the UI thread after the node and wire collections were drawn fresh (another ruleset, a
    ///     focus change, a layout reset), so the view can fit them. A redraw that kept the arrangement, which
    ///     every write and reload of the same ruleset is, does not raise it: the viewport stays where it was.
    /// </summary>
    public event Action? GraphReplaced;

    /// <summary>A wire from <c>source</c> was dropped on empty canvas; the view offers the nodes it can start there.</summary>
    public event Action<CanvasNodeViewModel>? WireDropped;

    /// <summary>Raised on the UI thread with a 1-based line when a selected node names where it is declared.</summary>
    public event Action<int>? JumpRequested;

    public void OnActivated(IModuleContext context)
    {
        StartPlayhead(context);
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

            StartWatching();
            return;
        }

        StartWatching();

        // Another surface may have written any file while this tab was not shown and not watching; the
        // Workbench does. What the watcher would have reported goes through the same handler.
        FilesChanged(_corpus.ChangedPaths());
    }

    /// <summary>Nothing is reloaded while another tab is shown; the next activation re-checks the directories and the playhead.</summary>
    public void OnDeactivated()
    {
        StopWatching();
        StopPlayhead();
    }

    public void Dispose()
    {
        StopWatching();
        StopPlayhead();
    }

    /// <summary>
    ///     Files under the user rules directory were touched, as the watcher reports them. A file that appeared
    ///     or went means a full load; one the corpus knows is re-read alone. The open file is skipped when it
    ///     holds what the session wrote (the canvas's own write), and nothing is reloaded while the source
    ///     pane is dirty: the status names the change, the batch is held, and Revert or Save loads it.
    /// </summary>
    public void FilesChanged(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (_corpus is null || paths.Count == 0)
        {
            return;
        }

        string? open = _session.Entry?.Path;
        if (SourceDirty)
        {
            _deferred.UnionWith(paths);
            if (open is not null && paths.Any(p => SamePath(p, open)) && _session.ChangedOnDisk)
            {
                EditStatus = ChangedOnDiskStatus;
            }

            return;
        }

        bool full = false;
        List<string> changed = [];
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool exists = File.Exists(path);
            bool known = _corpus.Knows(path);
            if (known && exists)
            {
                if (open is null || !SamePath(path, open) || _session.ChangedOnDisk)
                {
                    changed.Add(path);
                }
            }
            else if (known || exists)
            {
                full = true;
            }
        }

        if (full)
        {
            Refresh(SelectedRuleset?.Id, _loadCorpus);
        }
        else if (changed.Count > 0)
        {
            Refresh(SelectedRuleset?.Id, () => changed.Aggregate(_corpus, (corpus, path) => corpus.Replace(path) ?? _loadCorpus()));
        }
    }

    private void StartWatching()
    {
        if (_marshal is null || _watcher is not null || _corpus?.UserDirectory is not { } directory || !Directory.Exists(directory))
        {
            return;
        }

        _watcher = new RulesWatcher(directory, _marshal, WatcherDebounce);
        _watcher.Changed += FilesChanged;
    }

    private void StopWatching()
    {
        if (_watcher is { } watcher)
        {
            _watcher = null;
            watcher.Changed -= FilesChanged;
            watcher.Dispose();
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // A reload after a write hands back a fresh Entry for the same file; only another file resets the
    // pane. The session resets the history on the same rule when the redraw opens the entry. While the
    // picker is refilled its binding pushes null and then the entry back; neither is a selection.
    partial void OnSelectedRulesetChanged(RulesetCorpus.Entry? value)
    {
        if (_redrawing)
        {
            return;
        }

        if (!string.Equals(value?.Path, _session.Entry?.Path, StringComparison.Ordinal))
        {
            SourceDirty = false;
            EditStatus = "";
            ClearLayoutHistory();
            ClearSearch();
        }

        NotifyEditability();
        Redraw(value, keepArrangement: false);
        ReplayDeferred();
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

    // A revert over a file that changed on disk loads it, with every batch held while the pane was dirty;
    // the same-path guard keeps a ruleset switch, which also clears the flag, from reloading the file
    // that was open before. The switch replays the held batch itself, once the new one is drawn.
    partial void OnSourceDirtyChanged(bool value)
    {
        NotifyEditability();
        if (!value && SelectedRuleset is { } selected && _session.Entry is { } open && SamePath(open.Path, selected.Path))
        {
            if (_session.ChangedOnDisk)
            {
                EditStatus = "";
                _deferred.Add(selected.Path);
            }

            ReplayDeferred();
        }
    }

    private void ReplayDeferred()
    {
        if (_deferred.Count == 0 || SourceDirty)
        {
            return;
        }

        List<string> paths = [.. _deferred];
        _deferred.Clear();
        FilesChanged(paths);
    }

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
                // A fresh write ends the redo line, layout changes included; a history move keeps it.
                if (!_movingHistory)
                {
                    ClearLayoutRedo();
                }

                // A copy of a shipped file is a file that appeared; the user file written is re-read alone.
                Reload(entry.Id, entry.Source == "user" ? entry.Path : null);
                NotifyHistory();
                return true;
            default:
                return true;
        }
    }

    // After a write: the file written is re-read alone when the corpus knows it, every file otherwise, and
    // the selected ruleset is redrawn with its arrangement and selection kept. The live run, now stale, is
    // replaced after the pause.
    private void Reload(string id, string? writtenPath = null)
    {
        Refresh(id, () => (writtenPath is null ? null : _corpus?.Replace(writtenPath)) ?? _loadCorpus());
        ScheduleLiveRun();
    }

    // Takes the corpus load gives, refills the picker, and redraws id (or selects one when nothing was).
    private void Refresh(string? id, Func<RulesetCorpus> load)
    {
        RulesetCorpus.Entry? was = SelectedRuleset;
        _corpus = load();
        Refill();
        if (id is null)
        {
            RulesetCorpus.Entry? first = Rulesets.FirstOrDefault(e => e.Id == PreferredRulesetId) ?? Rulesets.FirstOrDefault();
            if (first is null)
            {
                Redraw(null, keepArrangement: false);
            }
            else
            {
                SelectedRuleset = first;
            }

            return;
        }

        RulesetCorpus.Entry? again = Rulesets.FirstOrDefault(e => e.Id == id);
        if (again is null)
        {
            string reason = _corpus.Failures.Select(f => f.Reason).FirstOrDefault() ?? "it is not in the rules directories any more";
            // The file stays open on the text it now holds, so the pane shows it and a corrected save or an
            // undo passes the write-time guard; the history goes when the break was another writer's.
            if (was is not null && was.Id == id)
            {
                Select(was);
                _session.Open(was);
                DocumentText = _session.Text;
                NotifyHistory();
            }

            EditStatus = $"{id} no longer loads as a ruleset: {reason}";
            Status = _session.CanUndo
                ? "Nothing to draw; the file no longer loads. Undo is still available."
                : "Nothing to draw; the file no longer loads.";
            MarkLoadFailure(id, reason);
            return;
        }

        Select(again);
        NotifyEditability();
        // The same ruleset read again keeps its arrangement; another one is drawn fresh and fitted.
        Redraw(again, keepArrangement: was?.Id == again.Id);
    }

    // The picker's binding pushes null into SelectedRuleset while its items are cleared, and the entry
    // back when it is set; the guard keeps either from reopening the session or redrawing.
    private void Refill()
    {
        _redrawing = true;
        try
        {
            Rulesets.Clear();
            foreach (RulesetCorpus.Entry entry in _corpus?.Entries ?? [])
            {
                Rulesets.Add(entry);
            }
        }
        finally
        {
            _redrawing = false;
        }
    }

    private void Select(RulesetCorpus.Entry? entry)
    {
        _redrawing = true;
        try
        {
            SelectedRuleset = entry;
        }
        finally
        {
            _redrawing = false;
        }
    }

    // arrangement stands in for what is on screen when given: a layout change restored by Undo, which the
    // node positions do not hold yet. refit defaults to a fresh draw: a kept arrangement, or a restored one,
    // leaves the viewport where it is.
    private void Redraw(RulesetCorpus.Entry? value, bool keepArrangement, bool recompose = true, RulesetNodeLayout? arrangement = null,
        bool? refit = null)
    {
        if (_redrawing)
        {
            return;
        }

        RulesetNodeLayout current = arrangement ?? (keepArrangement ? CurrentArrangement() : RulesetNodeLayout.Empty);
        HashSet<RulesetDocumentNodeKey> selected = [.. SelectedNodes.OfType<CanvasNodeViewModel>().Select(n => n.Key)];

        Nodes.Clear();
        Groups.Clear();
        Items.Clear();
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
            RefreshInspector();
            SyncLive(null, recompose, [], []);
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
        Arrangement arranged = _arranger.Arrange(_composed, SidecarPath, current);
        RulesetCorpus.Composed composed = arranged.Composed;
        RulesetNodeLayout layout = arranged.Layout;
        HasSavedLayout = arranged.HasSavedLayout;
        if (arranged.FocusDropped)
        {
            NotifyFocus();
        }

        (IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires) =
            CanvasGraphBuilder.Build(composed, layout, _factory);
        HashSet<RulesetDocumentNodeKey> drawn = [.. nodes.Select(n => n.Key)];
        // Frames first, so the editor draws them under the nodes. A frame whose every member is gone is not drawn.
        foreach (CanvasGroup group in layout.Groups.Where(g => g.Members.Any(drawn.Contains)))
        {
            CanvasGroupViewModel frame = new(group, _ => SaveLayout());
            Groups.Add(frame);
            Items.Add(frame);
        }

        // On a slice, the saved frames whose members are all here, around the slice's own positions and
        // read-only: the slice's placement is never written, so neither is a frame drawn on it.
        Dictionary<RulesetDocumentNodeKey, CanvasNodeViewModel> placedNodes = nodes.ToDictionary(n => n.Key);
        foreach (CanvasGroup group in arranged.SliceFrames)
        {
            Rect box = BoxOf([.. group.Members.Select(m => placedNodes[m])]);
            CanvasGroupViewModel frame = new(group with { Rect = FrameAround(box) }) { IsReadOnly = true };
            Groups.Add(frame);
            Items.Add(frame);
        }

        foreach (CanvasNodeViewModel node in nodes)
        {
            node.ShowFacets = ShowFacets;
            node.IsEditable = IsEditable && node.IsAuthorable;
            node.IsCollapsed = layout.IsCollapsed(node.Key);
            node.IsViewerPinned = layout.IsPinned(node.Key);
            node.IsZoomedOut = IsZoomedOut;
            node.IsSearchMatch = node.Matches(CanvasSearch);
            Nodes.Add(node);
            Items.Add(node);
        }

        foreach (CanvasConnectionViewModel wire in wires)
        {
            Connections.Add(wire);
        }

        // The rename moved the arrangement to the new key on screen; the sidecar learns it here.
        if (arranged.Carried)
        {
            SaveLayout();
        }

        ApplySurfacing(nodes);
        SyncLive(value, recompose, nodes, wires);
        foreach (CanvasNodeViewModel node in nodes.Where(n => selected.Contains(n.Key)))
        {
            SelectedNodes.Add(node);
        }

        RefreshInspector();
        Diagnostics = string.Join('\n', composed.Diagnostics);
        // An unwired entry is a warning on the node, counted with the diagnostics, not a bare number on the strip.
        Status = nodes.Count == 0
            ? "Nothing composed; see the diagnostics."
            : $"{nodes.Count} nodes, {wires.Count} wires";
        AfterRedraw(composed);
        RefreshSearchMatches();
        if (refit ?? !keepArrangement)
        {
            GraphReplaced?.Invoke();
        }
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

        RefreshInspector();
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
