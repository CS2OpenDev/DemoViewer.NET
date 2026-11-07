#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     The edits: every gesture and face editor asks <see cref="CanvasEditSemantics" /> what to write and
///     hands it to the session; the source pane's save, revert and edit-a-copy; undo and redo. Each command
///     checks the one guard the semantics cannot know, whether this surface may write at all, and reports.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    /// <summary>The text the source pane holds while dirty.</summary>
    private string _pendingSourceText = "";

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

    /// <summary>Copies the shipped ruleset into the user's rules and opens the copy, which overrides the shipped one by id.</summary>
    [RelayCommand]
    private void EditCopy()
    {
        if (SelectedRuleset is not { } selected || !IsShippedRuleset || _corpus is null)
        {
            EditStatus = "There is no user rules directory to copy into.";
            return;
        }

        Report(_corpus.CopyToUser(selected), selected);
    }

    /// <summary>Removes the selected node's entry from the document.</summary>
    [RelayCommand]
    private void DeleteSelected()
    {
        List<CanvasNodeViewModel> nodes = [.. SelectedNodes.OfType<CanvasNodeViewModel>().Where(n => n.IsAuthorable)];
        if (IsEditable && nodes.Count > 0 && _composed is { } composed)
        {
            Apply(CanvasEditSemantics.Delete(nodes, composed));
        }
    }

    /// <summary>Adds a count stat on <paramref name="view" /> with a fresh id, at the end of <c>stats:</c>.</summary>
    [RelayCommand]
    private void AddStat(string? view)
    {
        if (IsEditable && !string.IsNullOrWhiteSpace(view))
        {
            Apply(CanvasEditSemantics.AddStat(view, FreshId("new_" + view)));
        }
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

        // A slice would hide the new entry and lose its drop position, so the whole chain comes back first.
        if (HasFocus)
        {
            FocusKey = null;
        }

        // The placement outlives a refused write: the arranger spends it on the first draw that holds the key.
        _arranger.PlaceNext(new RulesetDocumentNodeKey(selected.Id, id), at);
        Apply(request);
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
    ///     a node when the wire was dropped on its container. What the wire stands for is written to the file,
    ///     and the chain redraws from the file either way, so the wire shown is the wire run.
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
        // A plain click on a port comes back as a wire from the port to itself.
        if (source is not null && target is not null && !ReferenceEquals(source, target))
        {
            Connect(source, target);
        }
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
    ///     A wire removed from the canvas: what it stood for is taken out of the file, or the removal is
    ///     refused. While a cut is in progress the wires are held and taken out together when it ends.
    /// </summary>
    [RelayCommand]
    private void Disconnect(object? wire)
    {
        if (wire is not CanvasConnectionViewModel connection || !IsEditable)
        {
            return;
        }

        if (_cutting is not null)
        {
            _cutting.Add(connection);
            return;
        }

        Apply(CanvasEditSemantics.Disconnect(connection));
    }

    /// <summary>The wire being dragged, written by the editor; its preview text is the semantics' answer.</summary>
    public CanvasPendingConnectionViewModel Pending { get; } = new();

    /// <summary>The editor's wire selection, written by the editor. Delete removes them when no node is selected.</summary>
    public ObservableCollection<object> SelectedWires { get; } = [];

    /// <summary>The wires crossed by the cutting line so far, or null between cuts.</summary>
    private List<CanvasConnectionViewModel>? _cutting;

    /// <summary>The cutting line was started: wires it crosses are held until it ends.</summary>
    [RelayCommand]
    private void CuttingStarted() => _cutting = [];

    /// <summary>The cutting line ended: every wire it crossed is taken out, refusals folded into one line.</summary>
    [RelayCommand]
    private void CuttingCompleted()
    {
        List<CanvasConnectionViewModel> cut = _cutting ?? [];
        _cutting = null;
        DisconnectWires(cut);
    }

    /// <summary>Removes the selected wires through <see cref="CanvasEditSemantics.Disconnect" />, refusals folded into one line.</summary>
    [RelayCommand]
    private void DeleteWires() => DisconnectWires([.. SelectedWires.OfType<CanvasConnectionViewModel>()]);

    /// <summary>
    ///     The Delete key: the selected nodes' entries when any is the author's, else the selected wires. A
    ///     selection of engine nodes alone has nothing in the file to delete.
    /// </summary>
    [RelayCommand]
    private void DeleteSelection()
    {
        if (SelectedNodes.OfType<CanvasNodeViewModel>().Any(n => n.IsAuthorable))
        {
            DeleteSelected();
        }
        else if (SelectedWires.Count > 0)
        {
            DeleteWires();
        }
    }

    /// <summary>Copies the first selected entry under a fresh id, at the end of its section.</summary>
    [RelayCommand]
    private void Duplicate()
    {
        if (SelectedNodes.OfType<CanvasNodeViewModel>().FirstOrDefault(n => n.IsAuthorable && !n.IsScaffold) is { } node)
        {
            DuplicateNode(node);
        }
    }

    /// <summary>Copies <paramref name="node" />'s entry under a fresh id, which the redraw then selects.</summary>
    public void DuplicateNode(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!IsEditable)
        {
            return;
        }

        string id = FreshId(node.Id + "_copy");
        if (Apply(CanvasEditSemantics.Duplicate(node, id)) && Nodes.FirstOrDefault(n => n.Id == id && n.Ruleset == node.Ruleset) is { } copy)
        {
            SelectedNodes.Clear();
            SelectedNodes.Add(copy);
        }
    }

    /// <summary>Selects every node drawn.</summary>
    [RelayCommand]
    private void SelectAll()
    {
        SelectedNodes.Clear();
        foreach (CanvasNodeViewModel node in Nodes)
        {
            SelectedNodes.Add(node);
        }
    }

    /// <summary>Escape: the whole chain comes back and nothing stays selected.</summary>
    [RelayCommand]
    private void ClearFocusAndSelection()
    {
        SelectedNodes.Clear();
        SelectedWires.Clear();
        FocusKey = null;
    }

    /// <summary>Adds a count stat on <paramref name="view" /> placed at <paramref name="at" />, as the canvas menu asks.</summary>
    public void AddStatAt(string view, Avalonia.Point at)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (Gallery.SelectMany(g => g.Items).FirstOrDefault(i => i.Kind == GalleryItemKind.View && i.Name == view) is { } item)
        {
            PlaceItem(item, at);
        }
    }

    /// <summary>Adds the gallery's blank highlight placed at <paramref name="at" />, as the canvas menu asks.</summary>
    public void AddHighlightAt(Avalonia.Point at)
    {
        List<GalleryItem> highlights = [.. Gallery.SelectMany(g => g.Items).Where(i => i.Kind == GalleryItemKind.Highlight)];
        GalleryItem? item = highlights.FirstOrDefault(i => i.Name == "highlight") ?? highlights.FirstOrDefault();
        if (item is not null)
        {
            PlaceItem(item, at);
        }
    }

    // Each wire is its own write and redraw; a refused one is kept and said once, with the count that went.
    private void DisconnectWires(List<CanvasConnectionViewModel> wires)
    {
        if (!IsEditable || wires.Count == 0)
        {
            return;
        }

        List<string> refusals = [];
        int removed = 0;
        foreach (CanvasConnectionViewModel wire in wires)
        {
            EditRequest request = CanvasEditSemantics.Disconnect(wire);
            if (request.IsRefused)
            {
                if (request.Refusal is { } reason && !refusals.Contains(reason))
                {
                    refusals.Add(reason);
                }
            }
            else if (Apply(request))
            {
                removed++;
            }
            else if (EditStatus.Length > 0 && !refusals.Contains(EditStatus))
            {
                refusals.Add(EditStatus);
            }
        }

        SelectedWires.Clear();
        if (refusals.Count > 0)
        {
            string kept = wires.Count - removed == 1 ? "1 wire kept" : $"{wires.Count - removed} wires kept";
            string went = removed == 0 ? "" : removed == 1 ? "1 wire removed; " : $"{removed} wires removed; ";
            EditStatus = $"{went}{kept}: {string.Join(" ", refusals)}";
        }
    }

    private static CanvasNodeViewModel? NodeOf(object endpoint) => endpoint switch
    {
        CanvasConnectorViewModel connector => connector.Owner,
        CanvasNodeViewModel node => node,
        _ => null
    };

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

    private bool Apply(EditRequest request) =>
        SelectedRuleset is { } selected && Report(_session.Apply(request), selected);

    private string FreshId(string wanted) => CanvasEditSemantics.FreshId(wanted, Nodes.Select(n => n.Id));

    // ---- the right dock: the Inspector and the YAML tab ----

    private InspectorViewModel? _inspector;

    /// <summary>The Inspector over the selected node, or over the document when nothing is selected.</summary>
    public InspectorViewModel Inspector => _inspector ??= new InspectorViewModel(ApplyForInspector, CreateRuleset, SelectByKey);

    /// <summary>Whether the Inspector tab is offered in the right dock.</summary>
    [ObservableProperty]
    private bool _showInspector = true;

    /// <summary>The dock's selected tab: 0 is the Inspector, 1 is the YAML.</summary>
    [ObservableProperty]
    private int _dockTab;

    /// <summary>The right dock shows while either of its tabs is on.</summary>
    public bool ShowDock => ShowSource || ShowInspector;

    [RelayCommand]
    private void ToggleInspector() => ShowInspector = !ShowInspector;

    /// <summary>Writes a new ruleset under <paramref name="id" /> into the user's rules and opens it.</summary>
    [RelayCommand]
    private void NewRuleset(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            CreateStarterRuleset();
        }
        else
        {
            CreateRuleset(id);
        }
    }

    // Turning a tab on brings it to the front; turning it off falls back to the other one.
    partial void OnShowInspectorChanged(bool value)
    {
        if (value)
        {
            DockTab = 0;
        }
        else if (ShowSource)
        {
            DockTab = 1;
        }

        OnPropertyChanged(nameof(ShowDock));
    }

    partial void OnShowSourceChanged(bool value)
    {
        if (value)
        {
            DockTab = 1;
        }
        else if (ShowInspector)
        {
            DockTab = 0;
        }

        OnPropertyChanged(nameof(ShowDock));
    }

    /// <summary>Shows the Inspector the selection and the file as they now are. Called after every redraw and selection change.</summary>
    private void RefreshInspector()
    {
        CanvasNodeViewModel? node = SelectedNodes.OfType<CanvasNodeViewModel>().FirstOrDefault();
        RulesetDoc? doc = null;
        if (SelectedRuleset is { } selected && _composed is { } composed)
        {
            composed.Documents.TryGetValue(selected.Id, out doc);
        }

        Inspector.Show(node, _session.Text, doc, SelectedRuleset, IsEditable, SourceDirty, _corpus?.UserDirectory is not null);
    }

    // The surfacing badge on every node of the author's, from the loaded document's show: block.
    private void ApplySurfacing(IEnumerable<CanvasNodeViewModel> nodes)
    {
        RulesetDoc? doc = null;
        if (SelectedRuleset is { } selected && _composed is { } composed)
        {
            composed.Documents.TryGetValue(selected.Id, out doc);
        }

        ShowSurface surface = ShowSurface.Of(doc);
        foreach (CanvasNodeViewModel node in nodes)
        {
            node.Surfacing = node.IsAuthorable && !node.IsScaffold ? surface.BadgeFor(node.Id) : "";
        }
    }

    private EditOutcome ApplyForInspector(EditRequest request)
    {
        if (SelectedRuleset is not { } selected || !IsEditable)
        {
            return EditOutcome.Refused(null);
        }

        EditOutcome outcome = _session.Apply(request);
        Report(outcome, selected);
        return outcome;
    }

    private void SelectByKey(RulesetDocumentNodeKey key)
    {
        if (Nodes.FirstOrDefault(n => n.Key == key) is { } node)
        {
            SelectedNodes.Clear();
            SelectedNodes.Add(node);
        }
    }

    private bool CreateRuleset(string id)
    {
        id = id.Trim();
        if (!RulesetEdits.IsValidId(id))
        {
            EditStatus = $"'{id}' is not a valid ruleset id: letters, digits and underscores, not starting with a digit.";
            return false;
        }

        if (_corpus?.UserDirectory is not { } directory)
        {
            EditStatus = "There is no user rules directory to write into.";
            return false;
        }

        string path = Path.Combine(directory, id + ".rules.yaml");
        if (File.Exists(path) || Rulesets.Any(r => r.Id == id))
        {
            EditStatus = $"{id} already exists; open it from the picker.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, RulesetEdits.NewRulesetText(id, id.Replace('_', ' ')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EditStatus = "The file could not be written: " + ex.Message;
            return false;
        }

        EditStatus = "";
        _corpus = _loadCorpus();
        Rulesets.Clear();
        foreach (RulesetCorpus.Entry entry in _corpus.Entries)
        {
            Rulesets.Add(entry);
        }

        if (Rulesets.FirstOrDefault(r => r.Id == id) is not { } created)
        {
            EditStatus = $"{id} was written but does not load: " + (_corpus.Failures.FirstOrDefault(f => f.Path == path).Reason ?? "unknown");
            return false;
        }

        SelectedRuleset = created;
        return true;
    }
}
