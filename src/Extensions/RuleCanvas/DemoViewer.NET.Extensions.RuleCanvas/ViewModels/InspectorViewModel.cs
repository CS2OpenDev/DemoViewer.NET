#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     The right dock's Inspector: with a node selected, the long tail of its schema keys the face does not
///     carry (the id, the label, the display and retention keys, a highlight's metadata) and where
///     <c>show:</c> surfaces it; with nothing selected, the document itself (title, summary, scope, uses)
///     and its whole surfacing block. Every write is an <see cref="EditRequest" /> the tab applies through
///     the session, and the tab shows the Inspector again after each redraw, so what is shown is the file.
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    /// <summary>The scopes a ruleset materializes for.</summary>
    public static IReadOnlyList<string> ForOptions { get; } = ["match", "each_player", "each_team", "each_projectile"];

    /// <summary>The retention modes a capture or a sum takes; the empty one is the default.</summary>
    public static IReadOnlyList<string> KeepOptions { get; } = ["", "first", "last", "list", "min", "max"];

    /// <summary>The kinds a highlight can be; the empty one is the default reel.</summary>
    public static IReadOnlyList<string> HighlightKindOptions { get; } = ["", "highlight", "funny", "lowlight", "hidden"];

    /// <summary>Every grain the table registry knows; the one picked has to match the ruleset's <c>for:</c>.</summary>
    public static IReadOnlyList<string> TableGrainOptions { get; } =
        ["player_round", "player_match", "match", "team_round", "team_match", "player_event", "team_event", "match_event", "projectile"];

    private readonly Func<EditRequest, EditOutcome> _apply;
    private readonly Func<string, bool> _createRuleset;
    private readonly Action<RulesetDocumentNodeKey> _select;
    private readonly Action<RulesetDocumentNodeKey, RulesetDocumentNodeKey>? _renaming;
    private CanvasNodeViewModel? _node;
    private string _text = "";
    private ShowSurface _surface = ShowSurface.Empty;
    private IReadOnlyList<string> _ids = [];
    private int _showReferences;

    /// <param name="apply">Applies an edit through the open session and reports it.</param>
    /// <param name="createRuleset">Writes a new ruleset under the id given and opens it; false when it could not.</param>
    /// <param name="select">Selects the node with the key given after a rename, so the Inspector follows it.</param>
    /// <param name="renaming">
    ///     Told the old and the new key just before a rename is written, so the arrangement under the old key
    ///     can follow the node; null when nothing keeps one.
    /// </param>
    public InspectorViewModel(Func<EditRequest, EditOutcome> apply, Func<string, bool> createRuleset, Action<RulesetDocumentNodeKey> select,
        Action<RulesetDocumentNodeKey, RulesetDocumentNodeKey>? renaming = null)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _createRuleset = createRuleset ?? throw new ArgumentNullException(nameof(createRuleset));
        _select = select ?? throw new ArgumentNullException(nameof(select));
        _renaming = renaming;

        Id = new InspectorFieldViewModel("id", "id", WriteNodeField, hint: "Enter renames the entry and every reference to it");
        Label = new InspectorFieldViewModel("label", "label", WriteNodeField);
        Per = new InspectorFieldViewModel("per", "per", WriteNodeField, CanvasNodeViewModel.PerOptions);
        Gate = new InspectorFieldViewModel("while", "while", WriteNodeField, hint: "a gate path or a sibling flag; empty for none");
        Format = new InspectorFieldViewModel("format", "format", WriteNodeField, hint: "a .NET numeric format such as F1 or F0");
        Keep = new InspectorFieldViewModel("keep", "keep", WriteNodeField, KeepOptions);
        Title = new InspectorFieldViewModel("title", "title", WriteNodeField, hint: "the reel title; {player.name} and stat ids expand");
        Score = new InspectorFieldViewModel("score", "score", WriteNodeField);
        Group = new InspectorFieldViewModel("group", "group", WriteNodeField, hint: "highlights of one group supersede each other");
        HighlightKind = new InspectorFieldViewModel("kind", "kind", WriteNodeField, HighlightKindOptions);
        ForEach = new InspectorFieldViewModel("for_each", "for each", WriteNodeField,
            hint: "axes as key: value, value, separated by ;  the id must hold {key}; empty removes for_each");
        TablePer = new InspectorFieldViewModel("per", "per", WriteNodeField, TableGrainOptions, "the table's grain, which must match the ruleset's for:");
        NodeFields = [Id, Label, Title, Per, TablePer, Gate, Format, Keep, Score, Group, HighlightKind, ForEach];

        DocumentTitle = new InspectorFieldViewModel("title", "title", WriteDocumentField);
        Summary = new InspectorFieldViewModel("summary", "summary", WriteDocumentField);
        For = new InspectorFieldViewModel("for", "for", WriteDocumentField, ForOptions);
        DocumentFields = [DocumentTitle, Summary, For];
    }

    public InspectorFieldViewModel Id { get; }

    public InspectorFieldViewModel Label { get; }

    public InspectorFieldViewModel Per { get; }

    public InspectorFieldViewModel Gate { get; }

    public InspectorFieldViewModel Format { get; }

    public InspectorFieldViewModel Keep { get; }

    public InspectorFieldViewModel Title { get; }

    public InspectorFieldViewModel Score { get; }

    public InspectorFieldViewModel Group { get; }

    public InspectorFieldViewModel HighlightKind { get; }

    /// <summary>The <c>for_each:</c> axes of a stat or a highlight, as <c>key: value, value</c> lines; empty for none.</summary>
    public InspectorFieldViewModel ForEach { get; }

    /// <summary>A table's grain; the scoreboard and every other node hide it.</summary>
    public InspectorFieldViewModel TablePer { get; }

    /// <summary>The node's fields in display order; each is visible only for the kinds that have it.</summary>
    public IReadOnlyList<InspectorFieldViewModel> NodeFields { get; }

    public InspectorFieldViewModel DocumentTitle { get; }

    public InspectorFieldViewModel Summary { get; }

    public InspectorFieldViewModel For { get; }

    public IReadOnlyList<InspectorFieldViewModel> DocumentFields { get; }

    /// <summary>The <c>use:</c> list as written.</summary>
    public ObservableCollection<string> Uses { get; } = [];

    /// <summary>The <c>exports:</c> list as written; empty when the key is absent, which exports every entry.</summary>
    public ObservableCollection<string> Exports { get; } = [];

    /// <summary>The <c>use:</c> list in a line: <c>uses 2 rulesets: kast, player_stats</c>, or <c>uses no other ruleset</c>.</summary>
    [ObservableProperty]
    private string _usesSummary = "";

    /// <summary>What the exports section says under its list: everything is exported, or only the listed ids.</summary>
    [ObservableProperty]
    private string _exportsNote = "";

    [ObservableProperty]
    private string _exportToAdd = "";

    /// <summary>The heading over the entries list: the node's columns, or where an entry is surfaced.</summary>
    [ObservableProperty]
    private string _surfacingHeading = "SURFACING";

    /// <summary>The surfacing entries shown: the selected node's, or every one of the document.</summary>
    public ObservableCollection<ShowEntryViewModel> Entries { get; } = [];

    /// <summary>The names of the document's <c>show.tables</c>, for the add-to-table picker.</summary>
    public ObservableCollection<string> Tables { get; } = [];

    /// <summary>The <c>params:</c> entries with their type and bounds, shown while the params node is selected.</summary>
    public ObservableCollection<ParamEntryViewModel> Params { get; } = [];

    public bool HasParams => Params.Count > 0;

    /// <summary>Whether a ruleset is open at all.</summary>
    [ObservableProperty]
    private bool _hasDocument;

    /// <summary>Whether a node is selected, which puts the Inspector in node mode.</summary>
    [ObservableProperty]
    private bool _hasNode;

    /// <summary>The selected node is one the open document declares, so its fields can be edited.</summary>
    [ObservableProperty]
    private bool _isNodeAuthorable;

    [ObservableProperty]
    private bool _isEditable;

    /// <summary>Why nothing can be written, or empty.</summary>
    [ObservableProperty]
    private string _readOnlyNote = "";

    /// <summary>The section heading: the kind and id of the node, or the ruleset's id.</summary>
    [ObservableProperty]
    private string _heading = "";

    /// <summary>What a node that cannot be edited is: an engine node, or another ruleset's.</summary>
    [ObservableProperty]
    private string _nodeNote = "";

    [ObservableProperty]
    private string _rulesetId = "";

    [ObservableProperty]
    private string _rulesetSource = "";

    /// <summary>Where the selected node shows, in words.</summary>
    [ObservableProperty]
    private string _surfacing = "";

    [ObservableProperty]
    private string? _tableToAdd;

    [ObservableProperty]
    private string _useToAdd = "";

    [ObservableProperty]
    private string _newRulesetId = "";

    /// <summary>Whether a new ruleset can be written: a user rules directory exists.</summary>
    [ObservableProperty]
    private bool _canCreate;

    /// <summary>What the Delete button says: plain, or with the cascade it would do.</summary>
    [ObservableProperty]
    private string _deleteLabel = "Delete";

    /// <summary>Why the entry cannot be deleted, naming the siblings that read it, or empty.</summary>
    [ObservableProperty]
    private string _deleteRefusal = "";

    // The for_each write refused for want of a hole, kept so the fix button can rename the id and write the axes as one edit.
    private sealed record ForEachFix(CanvasNodeViewModel Node, EntrySection Section, string Id, string FixedId,
        IReadOnlyList<(string Key, IReadOnlyList<string> Values)> Axes);

    private ForEachFix? _forEachFix;

    /// <summary>The fix offered under a refused <c>for_each:</c>: <c>Rename id to kills_{weapon}</c>; empty when none is pending.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForEachFix))]
    private string _forEachFixLabel = "";

    public bool HasForEachFix => ForEachFixLabel.Length > 0;

    /// <summary>The frozen fixture above the selected entry, one line each as the comment holds them; empty when it has none.</summary>
    public ObservableCollection<string> Fixture { get; } = [];

    public bool HasFixture => Fixture.Count > 0;

    public bool CanClearFixture => HasFixture && CanEditNode;

    public bool ShowsDocument => HasDocument && !HasNode && Wire is null;

    /// <summary>The condition editor over the selected wire, with no node selected; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsWire))]
    [NotifyPropertyChangedFor(nameof(ShowsDocument))]
    private CanvasWireEditorViewModel? _wire;

    /// <summary>What the selected wire says, for the section's first line.</summary>
    [ObservableProperty]
    private string _wireText = "";

    public bool ShowsWire => HasDocument && !HasNode && Wire is not null;

    /// <summary>
    ///     Shows the editor over <paramref name="wire" /> under the document, or drops it on null. Called by the
    ///     tab after <see cref="Show" /> with the first selected wire when no node is selected. Escape in the
    ///     editor rebuilds it from the file through <paramref name="build" />, which is the revert.
    /// </summary>
    public void ShowWire(CanvasConnectionViewModel? wire, Func<CanvasConnectionViewModel, Action, CanvasWireEditorViewModel?> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (wire is null || HasNode)
        {
            Wire = null;
            WireText = "";
            if (!HasNode)
            {
                Heading = HasDocument ? "RULESET " + RulesetId : "No ruleset open";
            }

            return;
        }

        Wire = build(wire, () => ShowWire(wire, build));
        WireText = wire.Text;
        Heading = $"WIRE {wire.Source.Owner?.Title} to {wire.Target.Owner?.Title}";
    }

    /// <summary>The Inspector has a node it can write: authorable and the surface editable.</summary>
    public bool CanEditNode => HasNode && IsNodeAuthorable && IsEditable;

    public bool HasNodeNote => NodeNote.Length > 0;

    public bool HasReadOnlyNote => ReadOnlyNote.Length > 0;

    public bool HasEntries => Entries.Count > 0;

    public bool HasTables => Tables.Count > 0;

    public bool HasUses => Uses.Count > 0;

    public bool HasExports => Exports.Count > 0;

    public bool CanDelete => CanEditNode && DeleteRefusal.Length == 0;

    public bool HasDeleteRefusal => DeleteRefusal.Length > 0;

    public static string RulesetHint => "The id is the file's name; rename the file to change it.";

    /// <summary>
    ///     Shows <paramref name="node" />, or the document when it is null. Called by the tab after every redraw
    ///     and selection change with the file's text as the session holds it.
    /// </summary>
    /// <param name="node">The first selected node, or null.</param>
    /// <param name="text">The open file's text.</param>
    /// <param name="doc">The open ruleset as loaded, for its <c>show:</c> block; null when it did not load.</param>
    /// <param name="entry">The open ruleset's entry, or null when none is open.</param>
    /// <param name="editable">Whether the surface may write.</param>
    /// <param name="sourceDirty">Whether the YAML tab holds unsaved text, which is the one reason a user file is not editable.</param>
    /// <param name="canCreate">Whether a user rules directory exists to write a new ruleset into.</param>
    public void Show(CanvasNodeViewModel? node, string text, RulesetDoc? doc, RulesetCorpus.Entry? entry, bool editable, bool sourceDirty, bool canCreate)
    {
        ArgumentNullException.ThrowIfNull(text);
        _node = node;
        _text = text;
        _forEachFix = null;
        ForEachFixLabel = "";
        _surface = ShowSurface.Of(doc);
        _ids = Try(() => RulesetEdits.EntryIds(text)) ?? [];
        HasDocument = entry is not null;
        HasNode = node is not null;
        IsNodeAuthorable = node is { IsAuthorable: true, IsScaffold: false };
        IsEditable = editable;
        CanCreate = canCreate;
        RulesetId = entry?.Id ?? "";
        RulesetSource = entry?.Source ?? "";
        ReadOnlyNote = entry is null ? ""
            : entry.Source != "user" ? "A shipped ruleset is read only here. Edit a copy puts one in your rules."
            : sourceDirty ? "Save or revert the YAML tab to edit here."
            : "";

        Tables.Clear();
        foreach (ShowTable table in _surface.Tables)
        {
            Tables.Add(table.Name);
        }

        Params.Clear();
        if (node is { IsParams: true, Face: ParamsFace parameters })
        {
            foreach (ParamDef param in parameters.Params)
            {
                Params.Add(new ParamEntryViewModel(param, IsNodeAuthorable && editable, WriteParamField));
            }
        }

        Fixture.Clear();
        foreach (string line in node is null ? [] : CanvasEditSemantics.FixtureOf(node, text))
        {
            Fixture.Add(line);
        }

        TableToAdd = Tables.FirstOrDefault();
        if (node is not null)
        {
            ShowNode(node);
        }
        else
        {
            ShowDocument(entry);
        }

        NotifyShape();
    }

    /// <summary>A define is a trigger, not a value, so <c>show:</c> has nothing to surface for it; a show node is the surface.</summary>
    public bool CanSurfaceNode => CanEditNode && _node is { IsDefine: false, IsShow: false, IsParams: false };

    /// <summary>The surfacing block shows for an authorable stat or highlight, and a show node's columns in its place.</summary>
    public bool ShowsSurfacing => IsNodeAuthorable && _node is { IsDefine: false, IsParams: false };

    [RelayCommand]
    private void AddToScoreboard()
    {
        if (_node is { } node && CanSurfaceNode)
        {
            string reference = ReferenceOf(node);
            Apply(EditRequest.Of(t => RulesetEdits.AddToScoreboard(t, reference, node.Label ?? node.Id), $"show {node.Id} on the scoreboard"));
        }
    }

    [RelayCommand]
    private void AddToTable(string? table)
    {
        table ??= TableToAdd;
        if (_node is { } node && CanSurfaceNode && !string.IsNullOrEmpty(table))
        {
            string reference = ReferenceOf(node);
            Apply(EditRequest.Of(t => RulesetEdits.AddToTable(t, table, reference, node.Label ?? node.Id), $"show {node.Id} in {table}"));
        }
    }

    /// <summary>Removes the entry, with its <c>show:</c> references when the label offered that.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (_node is not { } node || !CanDelete)
        {
            return;
        }

        bool cascade = _showReferences > 0 && !node.IsShow;
        Apply(EditRequest.Of(t =>
        {
            if (cascade)
            {
                t = RulesetEdits.RemoveShowReference(t, node.Id);
            }

            return node.IsShow ? CanvasEditSemantics.RemoveShow(t, node)
                : node.IsDefine ? RulesetEdits.RemoveDefine(t, node.Title)
                : node.IsHighlight ? RulesetEdits.RemoveHighlight(t, node.Id)
                : RulesetEdits.RemoveStat(t, node.Id);
        }, "delete " + node.Title));
    }

    /// <summary>Removes the selected entry's fixture comment; Undo puts it back.</summary>
    [RelayCommand]
    private void ClearFixture()
    {
        if (_node is { } node && CanClearFixture)
        {
            Apply(CanvasEditSemantics.ClearFixture(node));
        }
    }

    [RelayCommand]
    private void AddExport()
    {
        string id = ExportToAdd.Trim();
        if (!IsEditable || id.Length == 0)
        {
            return;
        }

        if (Apply(EditRequest.Of(t => RulesetEdits.AddExport(t, id), "export " + id)).IsWritten)
        {
            ExportToAdd = "";
        }
    }

    [RelayCommand]
    private void RemoveExport(string? id)
    {
        if (IsEditable && !string.IsNullOrEmpty(id))
        {
            Apply(EditRequest.Of(t => RulesetEdits.RemoveExport(t, id), "stop exporting " + id));
        }
    }

    [RelayCommand]
    private void AddUse()
    {
        string ruleset = UseToAdd.Trim();
        if (!IsEditable || ruleset.Length == 0)
        {
            return;
        }

        if (Apply(EditRequest.Of(t => RulesetEdits.AddUse(t, ruleset), "use " + ruleset)).IsWritten)
        {
            UseToAdd = "";
        }
    }

    [RelayCommand]
    private void RemoveUse(string? ruleset)
    {
        if (IsEditable && !string.IsNullOrEmpty(ruleset))
        {
            Apply(EditRequest.Of(t => RulesetEdits.RemoveUse(t, ruleset), "stop using " + ruleset));
        }
    }

    /// <summary>Writes a new ruleset under <see cref="NewRulesetId" /> and opens it.</summary>
    [RelayCommand]
    private void CreateRuleset()
    {
        if (CanCreate && _createRuleset(NewRulesetId.Trim()))
        {
            NewRulesetId = "";
        }
    }

    private void ShowNode(CanvasNodeViewModel node)
    {
        Heading = node.IsShow && node.Key.IsScoreboard ? "SCOREBOARD" : node.IsShow ? $"TABLE {node.Title}" : $"{node.KindLabel} {node.Title}";
        NodeNote = node.IsScaffold ? "An engine node; nothing on it is written by a ruleset."
            : node.IsDependency ? $"use: [ {node.Id} ] folded to one node; Expand draws its entries, Open {node.Id} edits them."
            : !node.IsAuthorable ? $"Declared by {node.Ruleset}; open that ruleset to edit it."
            : node.IsDefine ? "A named trigger; the stats that fire on it are wired from it."
            : node.IsShow ? "A surface; wire a stat into it to add a column, remove a column's wire to drop it."
            : node.IsParams ? "The params: block; each default is edited on the node, its type and bounds below, and the entries reading it are wired from it."
            : "";
        SurfacingHeading = node.IsShow ? "COLUMNS" : "SURFACING";
        if (node.IsDefine)
        {
            ShowDefine(node);
            return;
        }

        if (node.IsShow || node.IsParams)
        {
            ShowSurfaceNode(node);
            return;
        }

        bool stat = !node.IsHighlight;
        // An expanded entry is edited through its template, which is where its keys are.
        EntrySection section = RulesetEntryText.SectionOf(node.Key, node.IsHighlight);
        string entryId = Try(() => RulesetEntryText.TemplateOf(_text, section, node.Id)) ?? node.Id;
        Id.Load(node.Id, IsNodeAuthorable);
        TablePer.Load(null, false);
        ForEach.Load(RulesetEdits.SpellForEach(Try(() => RulesetEdits.ForEachOf(_text, section, entryId)) ?? []), IsNodeAuthorable);
        Label.Load(stat ? Field(node.Id, "label") : null, stat && IsNodeAuthorable);
        Per.Load(stat ? Field(node.Id, "per") : HighlightField(node.Id, "per"), IsNodeAuthorable);
        Gate.Load(stat ? Field(node.Id, "while") : null, stat && IsNodeAuthorable);
        Format.Load(stat ? Field(node.Id, "format") : null, stat && IsNodeAuthorable && node.Kind == "compute");
        Keep.Load(stat ? Field(node.Id, "keep") : null, stat && IsNodeAuthorable && node.Kind is "capture" or "sum");
        Title.Load(stat ? null : HighlightField(node.Id, "title"), !stat && IsNodeAuthorable);
        Score.Load(stat ? null : HighlightField(node.Id, "score"), !stat && IsNodeAuthorable);
        Group.Load(stat ? null : HighlightField(node.Id, "group"), !stat && IsNodeAuthorable);
        HighlightKind.Load(stat ? null : HighlightField(node.Id, "kind"), !stat && IsNodeAuthorable);
        foreach (InspectorFieldViewModel field in NodeFields)
        {
            field.IsEditable = CanEditNode;
        }

        IReadOnlyList<ShowReference> places = IsNodeAuthorable ? _surface.ReferencesTo(node.Id) : [];
        Surfacing = IsNodeAuthorable ? _surface.BadgeFor(node.Id) : "";
        _showReferences = places.Count;
        FillEntries(places);

        IReadOnlyList<string> readers = IsNodeAuthorable ? Try(() => RulesetEdits.SiblingReaders(_text, node.Id)) ?? [] : [];
        DeleteRefusal = readers.Count == 0 ? "" : $"{node.Id} is read by {string.Join(", ", readers)}; change them first.";
        DeleteLabel = places.Count switch
        {
            0 => "Delete",
            1 => "Delete and remove its reference",
            var n => $"Delete and remove its {n} references"
        };
    }

    // A define has a name and a gate the Inspector can edit; its trigger, facets and where are the node's own
    // rows. show: cannot surface it, and it cannot go while a stat fires on it.
    private void ShowDefine(CanvasNodeViewModel node)
    {
        string name = node.Title;
        Id.Load(name, IsNodeAuthorable);
        Label.Load(null, false);
        Per.Load(null, false);
        TablePer.Load(null, false);
        ForEach.Load(null, false);
        Gate.Load(Try(() => RulesetEdits.DefineField(_text, name, "while")), IsNodeAuthorable);
        Format.Load(null, false);
        Keep.Load(null, false);
        Title.Load(null, false);
        Score.Load(null, false);
        Group.Load(null, false);
        HighlightKind.Load(null, false);
        foreach (InspectorFieldViewModel field in NodeFields)
        {
            field.IsEditable = CanEditNode;
        }

        Surfacing = "";
        _showReferences = 0;
        FillEntries([]);
        IReadOnlyList<string> firing = Try(() => RulesetEdits.StatsFiringOn(_text, name)) ?? [];
        DeleteRefusal = firing.Count == 0 ? "" : $"{name} is fired on by {string.Join(", ", firing)}; retarget them first.";
        DeleteLabel = "Delete";
    }

    // A table has its name, its grain and its columns; the scoreboard its entries; the params node nothing
    // the Inspector edits, since each default is a row on the node. Only the table has an id to rename.
    private void ShowSurfaceNode(CanvasNodeViewModel node)
    {
        bool table = node.IsShow && !node.Key.IsScoreboard;
        Id.Load(node.Title, table && IsNodeAuthorable);
        Label.Load(null, false);
        Per.Load(null, false);
        TablePer.Load(table ? node.Per : null, table && IsNodeAuthorable);
        ForEach.Load(null, false);
        Gate.Load(null, false);
        Format.Load(null, false);
        Keep.Load(null, false);
        Title.Load(null, false);
        Score.Load(null, false);
        Group.Load(null, false);
        HighlightKind.Load(null, false);
        foreach (InspectorFieldViewModel field in NodeFields)
        {
            field.IsEditable = CanEditNode;
        }

        IReadOnlyList<ShowReference> columns = node.IsShow ? [.. node.Columns.Select(c => c.Reference)] : [];
        Surfacing = node.IsShow ? columns.Count switch { 0 => "no columns yet; wire a stat in", 1 => "1 column", var n => $"{n} columns" } : "";
        _showReferences = 0;
        FillEntries(columns);
        DeleteRefusal = node.IsParams ? "params: is removed in the YAML tab, with the reads that name it." : "";
        DeleteLabel = node.IsShow && !node.Key.IsScoreboard ? "Delete table" : node.IsShow ? "Delete scoreboard" : "Delete";
    }

    private void ShowDocument(RulesetCorpus.Entry? entry)
    {
        Heading = entry is null ? "No ruleset open" : "RULESET " + entry.Id;
        NodeNote = "";
        Surfacing = "";
        SurfacingHeading = "SURFACING";
        _showReferences = 0;
        DeleteRefusal = "";
        DeleteLabel = "Delete";
        DocumentTitle.Load(entry is null ? null : DocumentField("title"), entry is not null);
        Summary.Load(entry is null ? null : DocumentField("summary"), entry is not null);
        For.Load(entry is null ? null : DocumentField("for"), entry is not null);
        foreach (InspectorFieldViewModel field in DocumentFields)
        {
            field.IsEditable = IsEditable;
        }

        Uses.Clear();
        IReadOnlyList<string> uses = entry is null ? [] : Try(() => RulesetEdits.Uses(_text)) ?? [];
        foreach (string use in uses)
        {
            Uses.Add(use);
        }

        UsesSummary = entry is null ? ""
            : uses.Count == 0 ? "uses no other ruleset"
            : $"uses {uses.Count} {(uses.Count == 1 ? "ruleset" : "rulesets")}: {string.Join(", ", uses)}";

        Exports.Clear();
        IReadOnlyList<string> exports = entry is null ? [] : Try(() => RulesetEdits.Exports(_text)) ?? [];
        foreach (string export in exports)
        {
            Exports.Add(export);
        }

        ExportsNote = entry is null ? ""
            : exports.Count == 0 ? "Every stat and highlight is exported; list ids here to make the rest private."
            : "Only these can be read as " + entry.Id + ".<id> from other rulesets.";

        FillEntries([.. _surface.All]);
    }

    private void FillEntries(IReadOnlyList<ShowReference> places)
    {
        Entries.Clear();
        foreach (ShowReference place in places)
        {
            Entries.Add(new ShowEntryViewModel(place, IsEditable, WriteShowEntry, RemoveShowEntry));
        }
    }

    // The field writes name the node shown when the field was loaded; the redraw that follows reloads them.
    private void WriteNodeField(InspectorFieldViewModel field, string? value)
    {
        if (_node is not { } node || !CanEditNode)
        {
            return;
        }

        EditRequest request = field.Key switch
        {
            "id" when node.IsShow && !node.Key.IsScoreboard => RenameTableRequest(node, value),
            _ when node.IsShow || node.IsParams => CanvasEditSemantics.WriteField(node, field.Key, value),
            "id" => RenameRequest(node, value),
            "for_each" => ForEachRequest(node, value),
            _ when node.IsDefine => CanvasEditSemantics.WriteDefineField(node, field.Key, value),
            "per" when value is null => EditRequest.Refuse("per cannot be empty; a stat is kept per round or per match."),
            _ when node.IsHighlight => EditRequest.Of(t => value is null
                    ? RulesetEdits.RemoveHighlightFieldIfPresent(t, node.Id, field.Key)
                    : RulesetEdits.SetHighlightField(t, node.Id, field.Key, value),
                $"{(value is null ? "remove" : "set")} {node.Id}.{field.Key}"),
            _ => EditRequest.Of(t => value is null
                    ? RulesetEdits.RemoveFieldIfPresent(t, node.Id, field.Key)
                    : RulesetEdits.SetField(t, node.Id, field.Key, value),
                $"{(value is null ? "remove" : "set")} {node.Id}.{field.Key}")
        };

        // The arrangement follows the node before the write redraws under the new key.
        RulesetDocumentNodeKey? renamedKey = null;
        if (field.Key == "id" && !request.IsRefused && value is { } wanted)
        {
            string id = wanted.Trim();
            renamedKey = node.IsDefine ? RulesetDocumentNodeKey.Define(node.Ruleset, id)
                : node.IsShow ? RulesetDocumentNodeKey.Show(node.Ruleset, id)
                : new RulesetDocumentNodeKey(node.Ruleset, id);
            _renaming?.Invoke(node.Key, renamedKey.Value);
        }

        EditOutcome outcome = Apply(request);
        field.Error = outcome.IsRefused ? outcome.Message ?? "" : "";
        if (outcome.IsWritten && renamedKey is { } key)
        {
            _select(key);
        }
    }

    // The axes go on the entry the text declares: the node's own, or the template it was expanded from.
    private EditRequest ForEachRequest(CanvasNodeViewModel node, string? value)
    {
        EntrySection section = RulesetEntryText.SectionOf(node.Key, node.IsHighlight);
        string id = Try(() => RulesetEntryText.TemplateOf(_text, section, node.Id)) ?? node.Id;
        if (value is null)
        {
            return EditRequest.Of(t => RulesetEdits.RemoveForEach(t, section, id), $"remove {id}.for_each");
        }

        IReadOnlyList<(string Key, IReadOnlyList<string> Values)> axes;
        string? fixedId;
        try
        {
            axes = RulesetEdits.ParseForEach(value);
            fixedId = RulesetEdits.ForEachIdFix(id, axes.Select(a => a.Key));
        }
        catch (InvalidOperationException ex)
        {
            return EditRequest.Refuse(ex.Message);
        }

        if (fixedId is null)
        {
            return EditRequest.Of(t => RulesetEdits.SetForEach(t, section, id, axes), $"set {id}.for_each");
        }

        // An id with no hole would declare the same id once per value; the rename that gives it one is offered as the fix.
        _forEachFix = new ForEachFix(node, section, id, fixedId, axes);
        ForEachFixLabel = $"Rename id to {fixedId}";
        IReadOnlyList<string> holes = RulesetEdits.HolesOf(id);
        string missing = string.Join(" and ", axes.Select(a => a.Key).Where(k => !holes.Contains(k, StringComparer.Ordinal)).Select(k => "{" + k + "}"));
        return EditRequest.Refuse($"{id} has no {missing} hole, so every value would declare the same id; rename it to {fixedId} to expand it.");
    }

    /// <summary>
    ///     Renames the entry to the id with the hole and writes the refused <c>for_each:</c>, as one edit and
    ///     one Undo. Refused while a sibling or <c>show:</c> names the entry: a template has no name to read.
    /// </summary>
    [RelayCommand]
    private void ApplyForEachFix()
    {
        if (_forEachFix is not { } fix || _node is not { } node || !ReferenceEquals(node, fix.Node) || !CanEditNode)
        {
            return;
        }

        IReadOnlyList<string> readers = (fix.Section == EntrySection.Define
            ? Try(() => RulesetEdits.StatsFiringOn(_text, fix.Id))
            : Try(() => RulesetEdits.SiblingReaders(_text, fix.Id))) ?? [];
        IReadOnlyList<ShowReference> shown = _surface.ReferencesTo(fix.Id);
        if (readers.Count > 0 || shown.Count > 0)
        {
            List<string> by = [];
            if (readers.Count > 0)
            {
                by.Add("read by " + string.Join(", ", readers));
            }

            if (shown.Count > 0)
            {
                by.Add("shown on " + string.Join(", ", shown.Select(s => s.Surface).Distinct()));
            }

            ForEach.Error = $"{fix.Id} is {string.Join(" and ", by)}; a template has no name to read. Change them first.";
            return;
        }

        // The arrangement follows the first expansion, which is where the eye looks for the entry next.
        string first = fix.Axes.Aggregate(fix.FixedId, (id, axis) => id.Replace("{" + axis.Key + "}", axis.Values[0], StringComparison.Ordinal));
        RulesetDocumentNodeKey carried = fix.Section == EntrySection.Define ? RulesetDocumentNodeKey.Define(node.Ruleset, first) : new RulesetDocumentNodeKey(node.Ruleset, first);
        _renaming?.Invoke(node.Key, carried);
        EditOutcome outcome = Apply(EditRequest.Of(
            t => RulesetEdits.SetForEach(RulesetEdits.RenameEntry(t, fix.Section, fix.Id, fix.FixedId), fix.Section, fix.FixedId, fix.Axes),
            $"expand {fix.Id} as {fix.FixedId}"));
        if (outcome.IsRefused)
        {
            ForEach.Error = outcome.Message ?? "";
        }
        else if (outcome.IsWritten)
        {
            _select(carried);
        }
    }

    // A table's name is its show: key alone; the sidecar position follows through the same carry an entry's does.
    private EditRequest RenameTableRequest(CanvasNodeViewModel node, string? value)
    {
        string wanted = (value ?? "").Trim();
        if (!RulesetEdits.IsValidId(wanted))
        {
            return EditRequest.Refuse($"'{wanted}' is not a valid table name: letters, digits and underscores, not starting with a digit.");
        }

        if (wanted != node.Title && (Try(() => RulesetEdits.TableNames(_text)) ?? []).Contains(wanted, StringComparer.Ordinal))
        {
            return EditRequest.Refuse($"show: already has a table named {wanted}.");
        }

        return EditRequest.Of(t => RulesetEdits.RenameTable(t, node.Title, wanted), $"rename table {node.Title} to {wanted}");
    }

    private EditRequest RenameRequest(CanvasNodeViewModel node, string? value)
    {
        string wanted = (value ?? "").Trim();
        if (!RulesetEdits.IsValidId(wanted))
        {
            return EditRequest.Refuse($"'{wanted}' is not a valid id: letters, digits and underscores, not starting with a digit.");
        }

        if (wanted != node.Title && _ids.Contains(wanted, StringComparer.Ordinal))
        {
            return EditRequest.Refuse($"{wanted} is already declared in this ruleset.");
        }

        return EditRequest.Of(t => node.IsDefine ? RulesetEdits.RenameDefine(t, node.Title, wanted)
                : node.IsHighlight ? RulesetEdits.RenameHighlight(t, node.Id, wanted)
                : RulesetEdits.RenameStat(t, node.Id, wanted),
            $"rename {node.Title} to {wanted}");
    }

    // A param's type or bound, under the key the semantics address it by; the refusal lands on the field.
    private void WriteParamField(InspectorFieldViewModel field, string key, string? value)
    {
        if (_node is not { IsParams: true } node || !CanEditNode)
        {
            return;
        }

        EditOutcome outcome = Apply(CanvasEditSemantics.WriteParamField(node, key, value));
        field.Error = outcome.IsRefused ? outcome.Message ?? "" : "";
    }

    private void WriteDocumentField(InspectorFieldViewModel field, string? value)
    {
        if (!IsEditable || !HasDocument)
        {
            return;
        }

        EditRequest request = field.Key switch
        {
            "for" when value is null => EditRequest.Refuse("for cannot be empty; pick the scope the ruleset runs for."),
            _ => EditRequest.Of(t => value is null
                    ? RulesetEdits.RemoveDocumentField(t, field.Key)
                    : RulesetEdits.SetDocumentField(t, field.Key, value),
                $"{(value is null ? "remove" : "set")} {field.Key}")
        };
        EditOutcome outcome = Apply(request);
        field.Error = outcome.IsRefused ? outcome.Message ?? "" : "";
    }

    private void WriteShowEntry(ShowReference reference, string key, string? value)
    {
        if (!IsEditable)
        {
            return;
        }

        string what = $"{(value is null ? "remove" : "set")} {reference.Surface} {reference.Stat} {key}";
        Apply(key == "boards"
            ? EditRequest.Of(t => RulesetEdits.SetShowEntryBoards(t, reference.Index, ShowEntryViewModel.ParseBoards(value)), what)
            : EditRequest.Of(t => RulesetEdits.SetShowEntryField(t, reference.Table, reference.Index, key, value), what));
    }

    private void RemoveShowEntry(ShowReference reference)
    {
        if (IsEditable)
        {
            Apply(EditRequest.Of(t => RulesetEdits.RemoveShowItem(t, reference.Table, reference.Index),
                $"stop showing {reference.Stat} on {reference.Surface}"));
        }
    }

    private EditOutcome Apply(EditRequest request) => _apply(request);

    // A highlight is surfaced by its count, as the corpus writes it; a stat by its id.
    private static string ReferenceOf(CanvasNodeViewModel node) => node.IsHighlight ? node.Id + ".count" : node.Id;

    private string? Field(string id, string key) => Try(() => RulesetEdits.Field(_text, id, key));

    private string? HighlightField(string id, string key) => Try(() => RulesetEdits.HighlightField(_text, id, key));

    private string? DocumentField(string key) => Try(() => RulesetEdits.DocumentField(_text, key));

    // The text the session holds parsed once already; a read that fails anyway answers nothing rather than throwing.
    private static T? Try<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            return null;
        }
    }

    private void NotifyShape()
    {
        OnPropertyChanged(nameof(ShowsDocument));
        OnPropertyChanged(nameof(ShowsWire));
        OnPropertyChanged(nameof(CanEditNode));
        OnPropertyChanged(nameof(CanSurfaceNode));
        OnPropertyChanged(nameof(ShowsSurfacing));
        OnPropertyChanged(nameof(HasNodeNote));
        OnPropertyChanged(nameof(HasReadOnlyNote));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasParams));
        OnPropertyChanged(nameof(HasTables));
        OnPropertyChanged(nameof(HasUses));
        OnPropertyChanged(nameof(HasExports));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(HasDeleteRefusal));
        OnPropertyChanged(nameof(HasFixture));
        OnPropertyChanged(nameof(CanClearFixture));
        DeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasNodeChanged(bool value) => OnPropertyChanged(nameof(ShowsDocument));

    partial void OnDeleteRefusalChanged(string value)
    {
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(HasDeleteRefusal));
    }
}
