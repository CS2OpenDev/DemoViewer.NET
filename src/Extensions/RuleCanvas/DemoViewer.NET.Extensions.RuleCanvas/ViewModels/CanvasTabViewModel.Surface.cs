#region

using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>What the canvas shows instead of a graph, and why.</summary>
public enum CanvasEmptyState
{
    /// <summary>A graph is drawn.</summary>
    None,

    /// <summary>The corpus has no ruleset at all.</summary>
    NoRulesets,

    /// <summary>The open ruleset declares nothing yet.</summary>
    EmptyRuleset,

    /// <summary>The selected file does not load; <see cref="CanvasTabViewModel.EmptyStateFile" /> and <see cref="CanvasTabViewModel.EmptyStateReason" /> say which and why.</summary>
    LoadFailed
}

/// <summary>A transient message for the surface: a refusal, said near where the edit was asked for.</summary>
/// <param name="Message">The text.</param>
/// <param name="At">Where on the canvas the edit was asked for, in graph coordinates, or null for the strip.</param>
/// <param name="Sequence">Distinguishes a repeated message from the one before it, so the view shows it again.</param>
public sealed record CanvasToast(string Message, Point? At, int Sequence);

/// <summary>One line of the strip's diagnostics list.</summary>
/// <param name="Node">The node the diagnostic is inside, or null for one about the ruleset as a whole.</param>
/// <param name="Message">The text.</param>
/// <param name="Weight">Error or warning, which paints the line's stripe.</param>
/// <param name="Line">The line in the open file a line with no node jumps the YAML tab to; zero for none.</param>
public sealed record CanvasDiagnostic(CanvasNodeViewModel? Node, string Message, DiagnosticWeight Weight = DiagnosticWeight.Error, int Line = 0)
{
    public string Where => Node?.Id ?? (Line > 0 ? "line " + Line.ToString(System.Globalization.CultureInfo.InvariantCulture) : "ruleset");

    /// <summary>The token the weight is painted in.</summary>
    public string Token => RuleDiagnostic.TokenFor(Weight);

    public bool IsError => Weight == DiagnosticWeight.Error;
}

/// <summary>
///     The surface around the graph: the gallery and its search, the diagnostics list, the toast, the panel
///     toggles, the empty and failed states and their quick starts. State the strip, the gallery pane and the
///     overlay bind to; the graph itself is in the main file.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    private const string NewRulesetId = "my_rules";

    private const string NewRulesetText = """
        ruleset: my_rules
        title: My Rules
        summary: Stats of my own, drawn and edited on the canvas.
        for: each_player

        stats:
          enemy_kills:
            count: kill
            match: { enemy: true }
            per: round

        """;

    private int _toastSequence;

    /// <summary>The gallery as built for the open ruleset: the user's own flags are offered as gates.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleGallery))]
    private IReadOnlyList<GalleryGroup> _gallery = CanvasGallery.Build(EmbeddedCatalog.Root);

    /// <summary>What the gallery's search box holds; narrows every group by title, name and hint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleGallery))]
    [NotifyPropertyChangedFor(nameof(IsGallerySearching))]
    private string _gallerySearch = "";

    /// <summary>The last refusal, for the toast. A new record each time, so a repeated message shows again.</summary>
    [ObservableProperty]
    private CanvasToast? _toast;

    /// <summary>The id typed into the strip's New flyout; blank means a fresh <c>my_rules</c>.</summary>
    [ObservableProperty]
    private string _newRulesetName = "";

    /// <summary>The strip's diagnostics list, open or folded.</summary>
    [ObservableProperty]
    private bool _showDiagnosticsList;

    [ObservableProperty]
    private bool _showMinimap = true;

    /// <summary>Why the canvas shows no graph, or <see cref="CanvasEmptyState.None" />.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyState))]
    [NotifyPropertyChangedFor(nameof(IsNoRulesets))]
    [NotifyPropertyChangedFor(nameof(IsEmptyRuleset))]
    [NotifyPropertyChangedFor(nameof(IsLoadFailed))]
    private CanvasEmptyState _emptyState;

    /// <summary>The file a failed load names.</summary>
    [ObservableProperty]
    private string _emptyStateFile = "";

    /// <summary>Why the file did not load, as the loader said it.</summary>
    [ObservableProperty]
    private string _emptyStateReason = "";

    /// <summary>The diagnostics of the drawn composition, one per line, with the node each is inside.</summary>
    public ObservableCollection<CanvasDiagnostic> DiagnosticEntries { get; } = [];

    /// <summary>The gallery narrowed by the search, every shown group opened; the gallery as built when the search is empty.</summary>
    public IReadOnlyList<GalleryGroup> VisibleGallery => CanvasGallery.Filter(Gallery, GallerySearch);

    public bool IsGallerySearching => GallerySearch.Trim().Length > 0;

    public int DiagnosticCount => DiagnosticEntries.Count;

    /// <summary>The strip's count, by weight: <c>2 errors, 1 warning</c>.</summary>
    public string DiagnosticSummary => RuleDiagnostic.Summary(DiagnosticEntries.Select(d => d.Weight));

    /// <summary>The strip badge's token: the error red when any line is an error, the warning amber otherwise.</summary>
    public string DiagnosticsToken => RuleDiagnostic.WorstToken(DiagnosticEntries.Select(d => d.Weight));

    public int ErrorCount => DiagnosticEntries.Count(d => d.IsError);

    public int WarningCount => DiagnosticEntries.Count(d => !d.IsError);

    public bool HasEmptyState => EmptyState != CanvasEmptyState.None;

    public bool IsNoRulesets => EmptyState == CanvasEmptyState.NoRulesets;

    public bool IsEmptyRuleset => EmptyState == CanvasEmptyState.EmptyRuleset;

    public bool IsLoadFailed => EmptyState == CanvasEmptyState.LoadFailed;

    /// <summary>Whether there is a user rules directory to create a ruleset in.</summary>
    public bool CanCreateRuleset => _corpus?.UserDirectory is not null;

    /// <summary>The user rules directory, or null when the corpus has none.</summary>
    public string? RulesFolder => _corpus?.UserDirectory;


    [RelayCommand]
    private void ToggleMinimap() => ShowMinimap = !ShowMinimap;

    [RelayCommand]
    private void ToggleDiagnosticsList() => ShowDiagnosticsList = !ShowDiagnosticsList;

    [RelayCommand]
    private void ClearGallerySearch() => GallerySearch = "";

    /// <summary>Selects and frames the node a diagnostic is inside, which also jumps the source pane to it.</summary>
    [RelayCommand]
    private void SelectDiagnostic(CanvasDiagnostic? diagnostic)
    {
        if (diagnostic?.Node is { } node && Nodes.Contains(node))
        {
            SelectedNodes.Clear();
            SelectedNodes.Add(node);
            FrameRequested?.Invoke();
        }
        else if (diagnostic is { Node: null, Line: > 0 } positioned)
        {
            ShowSource = true;
            DockTab = 1;
            SelectedLine = positioned.Line;
            JumpRequested?.Invoke(positioned.Line);
        }
    }

    /// <summary>
    ///     Adds a gallery item without a drop point: the keyboard path through the + Add flyout and the empty
    ///     ruleset's quick starts. It lands under the lowest node of its kind's column: a define among the
    ///     defines, a table right of the entries, anything else among the entries.
    /// </summary>
    [RelayCommand]
    private void AddItem(GalleryItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!IsEditable)
        {
            Notify(IsShippedRuleset
                ? "A shipped ruleset is read only here. Edit a copy puts one in your rules."
                : "Save or revert the source pane to edit on the canvas.");
            return;
        }

        PlaceItem(item, NextFreeSpot(item));
    }

    /// <summary>A quick start from the empty ruleset's watermark: the Enemy kills prefab, a flag, or a blank highlight.</summary>
    [RelayCommand]
    private void QuickStart(string? which)
    {
        GalleryItem? item = which switch
        {
            "prefab" => Gallery.SelectMany(g => g.Items).FirstOrDefault(i => i.Kind == GalleryItemKind.Prefab && i.Name == "enemy_kills"),
            "flag" => Gallery.SelectMany(g => g.Items).FirstOrDefault(i => i.Kind == GalleryItemKind.Stat && i.Name == "flag"),
            "highlight" => Gallery.SelectMany(g => g.Items).FirstOrDefault(i => i.Kind == GalleryItemKind.Highlight && i.Name == "highlight"),
            _ => null
        };
        AddItem(item);
    }

    /// <summary>
    ///     The strip's New flyout: an empty ruleset (no stats, the watermark's quick starts) under
    ///     <see cref="NewRulesetName" /> or a fresh <c>my_rules</c>, or the one-stat starter. True when a file
    ///     was written and opened; a refusal is on the status and the toast.
    /// </summary>
    public bool CreateNewRuleset(bool starter)
    {
        if (starter)
        {
            CreateStarterRuleset();
            return true;
        }

        string id = NewRulesetName.Trim();
        if (id.Length == 0)
        {
            IEnumerable<string> onDisk = _corpus?.UserDirectory is { } dir && Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.rules.yaml").Select(p => Path.GetFileName(p)[..^".rules.yaml".Length])
                : [];
            id = CanvasEditSemantics.FreshId(NewRulesetId, Rulesets.Select(r => r.Id).Concat(onDisk));
        }

        if (!CreateRuleset(id))
        {
            return false;
        }

        NewRulesetName = "";
        return true;
    }

    // The card's New ruleset: a starter with one stat, under a fresh id, so the canvas opens with something drawn.
    private void CreateStarterRuleset()
    {
        if (_corpus?.UserDirectory is not { } userDir)
        {
            Notify("There is no user rules directory to create a ruleset in.");
            return;
        }

        // A file that does not load, or whose ruleset: key is not its stem, is not in the picker but is on disk.
        IEnumerable<string> onDisk = Directory.Exists(userDir)
            ? Directory.EnumerateFiles(userDir, "*.rules.yaml").Select(p => Path.GetFileName(p)[..^".rules.yaml".Length])
            : [];
        string id = CanvasEditSemantics.FreshId(NewRulesetId, Rulesets.Select(r => r.Id).Concat(onDisk));
        string path = Path.Combine(userDir, id + ".rules.yaml");
        if (File.Exists(path))
        {
            Notify($"{Path.GetFileName(path)} already exists; nothing was written.");
            return;
        }

        try
        {
            Directory.CreateDirectory(userDir);
            File.WriteAllText(path, NewRulesetText.Replace("ruleset: my_rules", "ruleset: " + id, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"Could not write {path}: {ex.Message}");
            return;
        }

        Reload(id);
    }

    /// <summary>Opens the user rules directory in the platform's file browser.</summary>
    [RelayCommand]
    private void OpenRulesFolder()
    {
        if (_corpus?.UserDirectory is not { } userDir)
        {
            Notify("There is no user rules directory.");
            return;
        }

        try
        {
            Directory.CreateDirectory(userDir);
            Process.Start(new ProcessStartInfo(userDir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or PlatformNotSupportedException or InvalidOperationException)
        {
            Notify($"Could not open {userDir}: {ex.Message}");
        }
    }

    /// <summary>Says <paramref name="message" /> on the toast, near <paramref name="at" /> when given.</summary>
    public void Notify(string message, Point? at = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        Toast = new CanvasToast(message, at, ++_toastSequence);
    }

    /// <summary>Where <paramref name="item" /> lands when added without a drop point: the arranger's column rule over the nodes drawn.</summary>
    public Point NextFreeSpot(GalleryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        List<(PlacementColumn? Column, Rect Box)> boxes = [.. Nodes.Select(n => (ColumnOf(n), new Rect(n.Location, n.EstimatedSize)))];
        return CanvasArranger.NextFreeSpot(CanvasArranger.ColumnOf(item.Kind), boxes);
    }

    // The column a drawn node reads in; an engine node and the params block are in none.
    private static PlacementColumn? ColumnOf(CanvasNodeViewModel node) =>
        node.IsScaffold || node.IsParams ? null
        : node.IsDefine ? PlacementColumn.Define
        : node.IsShow ? PlacementColumn.Show
        : node.IsAuthorable ? PlacementColumn.Entry
        : null;

    // The trigger defines of the user's other rulesets, read from disk: small files, and a redraw already
    // follows every change to them. A file that cannot be read offers nothing.
    private IEnumerable<GalleryItem> YourDefines()
    {
        foreach (RulesetCorpus.Entry entry in _corpus?.Entries ?? [])
        {
            if (entry.Source != "user" || entry.Id == SelectedRuleset?.Id)
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(entry.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (GalleryItem item in CanvasGallery.DefinesOf(entry.Id, text))
            {
                yield return item;
            }
        }
    }

    // Called after every redraw: the gallery gets the ruleset's flags, the diagnostics list its lines, and the
    // empty state is decided from what was drawn.
    private void AfterRedraw(RulesetCorpus.Composed? composed)
    {
        Gallery = CanvasGallery.Build(EmbeddedCatalog.Root, Nodes.Where(n => n.IsAuthorable && n.Kind == "flag").Select(n => n.Id), YourDefines());
        DiagnosticEntries.Clear();
        // A line filed on a node is listed under it once; what no node holds is the ruleset's.
        HashSet<RuleDiagnostic> filed = [];
        foreach (CanvasNodeViewModel node in Nodes)
        {
            foreach (RuleDiagnostic diagnostic in node.Diagnostics)
            {
                DiagnosticEntries.Add(new CanvasDiagnostic(node, diagnostic.Message, diagnostic.Weight));
                filed.Add(diagnostic);
            }
        }

        // An entry the loader dropped has no node; its error stands in for the loader's lines inside it.
        if (composed?.Open is { } open && SelectedRuleset?.Id == open.Id && _session.Text.Length > 0)
        {
            foreach (DroppedEntry dropped in DroppedEntries.Of(_session.Text, open, composed.Report))
            {
                DiagnosticEntries.Add(new CanvasDiagnostic(null, dropped.Diagnostic.Message, DiagnosticWeight.Error, dropped.Line));
                filed.UnionWith(dropped.Consumed);
            }
        }

        foreach (RuleDiagnostic diagnostic in composed?.Report ?? [])
        {
            if (!filed.Contains(diagnostic) && !DiagnosticEntries.Any(d => d.Node is not null && diagnostic.Text.Contains(d.Message, StringComparison.Ordinal)))
            {
                DiagnosticEntries.Add(new CanvasDiagnostic(null, diagnostic.Text, diagnostic.Weight));
            }
        }

        OnPropertyChanged(nameof(DiagnosticCount));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(DiagnosticSummary));
        OnPropertyChanged(nameof(DiagnosticsToken));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(CanCreateRuleset));
        OnPropertyChanged(nameof(RulesFolder));
        if (SelectedRuleset is null)
        {
            EmptyState = Rulesets.Count == 0 ? CanvasEmptyState.NoRulesets : CanvasEmptyState.None;
        }
        else if (EmptyState != CanvasEmptyState.LoadFailed)
        {
            EmptyState = Nodes.Any(n => n.IsAuthorable) ? CanvasEmptyState.None : CanvasEmptyState.EmptyRuleset;
        }
    }

    // The selected id no longer loads: the card names the file and the loader's reason.
    private void MarkLoadFailure(string id, string reason)
    {
        (string path, string _) = _corpus?.Failures.FirstOrDefault(f => Path.GetFileName(f.Path) == id + ".rules.yaml") ?? ("", "");
        EmptyStateFile = path.Length > 0 ? Path.GetFileName(path) : id + ".rules.yaml";
        EmptyStateReason = reason;
        EmptyState = CanvasEmptyState.LoadFailed;
    }

    // A ruleset that is about to be drawn is no longer a failed one, and the strip stops saying so.
    private void ClearLoadFailure()
    {
        if (EmptyState == CanvasEmptyState.LoadFailed)
        {
            EmptyState = CanvasEmptyState.None;
            EmptyStateFile = "";
            EmptyStateReason = "";
            EditStatus = "";
        }
    }
}
