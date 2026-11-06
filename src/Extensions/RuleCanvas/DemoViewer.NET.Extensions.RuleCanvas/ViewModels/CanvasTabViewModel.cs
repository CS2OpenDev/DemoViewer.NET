#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     The Canvas section: a picker over the loadable rulesets and the chosen one drawn as nodes. The corpus
///     is read on first activation and composed per selection, on the UI thread for now: a composition is a
///     YAML parse plus a static projection and a first placement, tens of milliseconds at the corpus's sizes.
///     Moving it onto the processing queue needs a demo-less job the SDK does not yet offer.
/// </summary>
public sealed partial class CanvasTabViewModel : ExtensionViewModel, IWorkspaceTabViewModel
{
    /// <summary>The Rule Workbench's tab id on the main strip, as the host declares it.</summary>
    public const string AuthoringTabId = "ruleworkbench.editor";

    private readonly Func<string, bool> _selectTab;
    private readonly Func<RulesetCorpus> _loadCorpus;
    private readonly Func<string?> _layoutFolder;
    private RulesetCorpus? _corpus;

    /// <param name="selectTab">Selects a host tab by id; the Authoring tab's id is <see cref="AuthoringTabId" />.</param>
    public CanvasTabViewModel(Func<string, bool> selectTab)
        : this(selectTab, static () => RulesetCorpus.Load(RulesetCorpus.DefaultDirectories()), static () => null)
    {
    }

    /// <param name="selectTab">Selects a host tab by id.</param>
    /// <param name="loadCorpus">Reads the rulesets the picker offers; called once, on first activation.</param>
    /// <param name="layoutFolder">
    ///     The extension's own config folder, where a shipped ruleset's arrangement is kept since its own
    ///     directory is not ours to write; null means shipped arrangements are not kept.
    /// </param>
    public CanvasTabViewModel(Func<string, bool> selectTab, Func<RulesetCorpus> loadCorpus, Func<string?> layoutFolder)
    {
        _selectTab = selectTab ?? throw new ArgumentNullException(nameof(selectTab));
        _loadCorpus = loadCorpus ?? throw new ArgumentNullException(nameof(loadCorpus));
        _layoutFolder = layoutFolder ?? throw new ArgumentNullException(nameof(layoutFolder));
    }

    /// <summary>
    ///     Where the selected ruleset's arrangement lives: beside a user ruleset, under the extension's own
    ///     folder for a shipped one, or null when there is nowhere to keep it.
    /// </summary>
    public string? SidecarPath => SidecarFor(SelectedRuleset);

    /// <summary>True while a saved arrangement is applied, so it can be reset to the computed one.</summary>
    [ObservableProperty]
    private bool _hasSavedLayout;

    public ObservableCollection<CanvasNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<CanvasConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<RulesetCorpus.Entry> Rulesets { get; } = [];

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

    public bool HasDiagnostics => Diagnostics.Length > 0;

    /// <summary>The detailed style: facet rows on the node face. Off is the compact style: a facet count.</summary>
    [ObservableProperty]
    private bool _showFacets = true;

    /// <summary>True after a click found no Authoring tab to select: it is switched off, or the host has none.</summary>
    [ObservableProperty]
    private bool _authoringUnavailable;

    public bool IsCompact => !ShowFacets;

    /// <summary>Raised on the UI thread after the node and wire collections were refilled, so the view can fit them.</summary>
    public event Action? GraphReplaced;

    public void OnActivated(IModuleContext context)
    {
        if (_corpus is not null)
        {
            return;
        }

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
    }

    public void OnDeactivated()
    {
    }

    partial void OnSelectedRulesetChanged(RulesetCorpus.Entry? value)
    {
        Nodes.Clear();
        Connections.Clear();
        Diagnostics = "";
        if (value is null || _corpus is null)
        {
            return;
        }

        RulesetCorpus.Composed composed = _corpus.Compose(value.Id);
        string? sidecar = SidecarFor(value);
        RulesetNodeLayout layout = sidecar is null ? RulesetNodeLayout.Empty : RulesetLayoutStore.LoadSidecar(sidecar);
        HasSavedLayout = layout.Count > 0;
        OnPropertyChanged(nameof(SidecarPath));
        (IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires) = CanvasGraphBuilder.Build(composed, layout);
        foreach (CanvasNodeViewModel node in nodes)
        {
            node.ShowFacets = ShowFacets;
            Nodes.Add(node);
        }

        foreach (CanvasConnectionViewModel wire in wires)
        {
            Connections.Add(wire);
        }

        Diagnostics = string.Join('\n', composed.Diagnostics);
        int unwired = nodes.Count(n => n.Inputs.Count == 0 && n.Outputs.Count == 0);
        Status = nodes.Count == 0
            ? "Nothing composed; see the diagnostics."
            : $"{nodes.Count} nodes, {wires.Count} wires, {unwired} unwired";
        GraphReplaced?.Invoke();
    }

    partial void OnDiagnosticsChanged(string value) => OnPropertyChanged(nameof(HasDiagnostics));

    partial void OnShowFacetsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCompact));
        foreach (CanvasNodeViewModel node in Nodes)
        {
            node.ShowFacets = value;
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

    /// <summary>Writes every node's position to the sidecar. Bound to the editor's drag-completed command.</summary>
    [RelayCommand]
    private void SaveLayout()
    {
        string? sidecar = SidecarPath;
        if (sidecar is null || Nodes.Count == 0)
        {
            return;
        }

        RulesetNodeLayout layout = RulesetNodeLayout.From(
            Nodes.Select(n => new KeyValuePair<RulesetDocumentNodeKey, Avalonia.Point>(new RulesetDocumentNodeKey(n.Ruleset, n.Id), n.Location)));
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

        RulesetCorpus.Entry? selected = SelectedRuleset;
        OnSelectedRulesetChanged(selected);
    }

    [RelayCommand]
    private void SetStyle(string style) => ShowFacets = style != "compact";

    [RelayCommand]
    private void OpenAuthoring() => AuthoringUnavailable = !_selectTab(AuthoringTabId);
}
