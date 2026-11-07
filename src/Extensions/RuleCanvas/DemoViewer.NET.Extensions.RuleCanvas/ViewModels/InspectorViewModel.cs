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

    private readonly Func<EditRequest, EditOutcome> _apply;
    private readonly Func<string, bool> _createRuleset;
    private readonly Action<RulesetDocumentNodeKey> _select;
    private CanvasNodeViewModel? _node;
    private string _text = "";
    private ShowSurface _surface = ShowSurface.Empty;
    private IReadOnlyList<string> _ids = [];
    private int _showReferences;

    /// <param name="apply">Applies an edit through the open session and reports it.</param>
    /// <param name="createRuleset">Writes a new ruleset under the id given and opens it; false when it could not.</param>
    /// <param name="select">Selects the node with the key given after a rename, so the Inspector follows it.</param>
    public InspectorViewModel(Func<EditRequest, EditOutcome> apply, Func<string, bool> createRuleset, Action<RulesetDocumentNodeKey> select)
    {
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _createRuleset = createRuleset ?? throw new ArgumentNullException(nameof(createRuleset));
        _select = select ?? throw new ArgumentNullException(nameof(select));

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
        NodeFields = [Id, Label, Title, Per, Gate, Format, Keep, Score, Group, HighlightKind];

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

    /// <summary>The node's fields in display order; each is visible only for the kinds that have it.</summary>
    public IReadOnlyList<InspectorFieldViewModel> NodeFields { get; }

    public InspectorFieldViewModel DocumentTitle { get; }

    public InspectorFieldViewModel Summary { get; }

    public InspectorFieldViewModel For { get; }

    public IReadOnlyList<InspectorFieldViewModel> DocumentFields { get; }

    /// <summary>The <c>use:</c> list as written.</summary>
    public ObservableCollection<string> Uses { get; } = [];

    /// <summary>The surfacing entries shown: the selected node's, or every one of the document.</summary>
    public ObservableCollection<ShowEntryViewModel> Entries { get; } = [];

    /// <summary>The names of the document's <c>show.tables</c>, for the add-to-table picker.</summary>
    public ObservableCollection<string> Tables { get; } = [];

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

    public bool ShowsDocument => HasDocument && !HasNode;

    /// <summary>The Inspector has a node it can write: authorable and the surface editable.</summary>
    public bool CanEditNode => HasNode && IsNodeAuthorable && IsEditable;

    public bool HasNodeNote => NodeNote.Length > 0;

    public bool HasReadOnlyNote => ReadOnlyNote.Length > 0;

    public bool HasEntries => Entries.Count > 0;

    public bool HasTables => Tables.Count > 0;

    public bool HasUses => Uses.Count > 0;

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

    [RelayCommand]
    private void AddToScoreboard()
    {
        if (_node is { } node && CanEditNode)
        {
            string reference = ReferenceOf(node);
            Apply(EditRequest.Of(t => RulesetEdits.AddToScoreboard(t, reference, node.Label ?? node.Id), $"show {node.Id} on the scoreboard"));
        }
    }

    [RelayCommand]
    private void AddToTable(string? table)
    {
        table ??= TableToAdd;
        if (_node is { } node && CanEditNode && !string.IsNullOrEmpty(table))
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

        bool cascade = _showReferences > 0;
        Apply(EditRequest.Of(t =>
        {
            if (cascade)
            {
                t = RulesetEdits.RemoveShowReference(t, node.Id);
            }

            return node.IsHighlight ? RulesetEdits.RemoveHighlight(t, node.Id) : RulesetEdits.RemoveStat(t, node.Id);
        }, "delete " + node.Id));
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
        Heading = $"{node.KindLabel} {node.Id}";
        NodeNote = node.IsScaffold ? "An engine node; nothing on it is written by a ruleset."
            : !node.IsAuthorable ? $"Declared by {node.Ruleset}; open that ruleset to edit it."
            : "";
        bool stat = !node.IsHighlight;
        Id.Load(node.Id, IsNodeAuthorable);
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

    private void ShowDocument(RulesetCorpus.Entry? entry)
    {
        Heading = entry is null ? "No ruleset open" : "RULESET " + entry.Id;
        NodeNote = "";
        Surfacing = "";
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
            "id" => RenameRequest(node, value),
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

        EditOutcome outcome = Apply(request);
        field.Error = outcome.IsRefused ? outcome.Message ?? "" : "";
        if (outcome.IsWritten && field.Key == "id" && value is { } renamed)
        {
            _select(new RulesetDocumentNodeKey(node.Ruleset, renamed.Trim()));
        }
    }

    private EditRequest RenameRequest(CanvasNodeViewModel node, string? value)
    {
        string wanted = (value ?? "").Trim();
        if (!RulesetEdits.IsValidId(wanted))
        {
            return EditRequest.Refuse($"'{wanted}' is not a valid id: letters, digits and underscores, not starting with a digit.");
        }

        if (wanted != node.Id && _ids.Contains(wanted, StringComparer.Ordinal))
        {
            return EditRequest.Refuse($"{wanted} is already declared in this ruleset.");
        }

        return EditRequest.Of(t => node.IsHighlight
                ? RulesetEdits.RenameHighlight(t, node.Id, wanted)
                : RulesetEdits.RenameStat(t, node.Id, wanted),
            $"rename {node.Id} to {wanted}");
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
        if (IsEditable)
        {
            Apply(EditRequest.Of(t => RulesetEdits.SetShowEntryField(t, reference.Table, reference.Index, key, value),
                $"{(value is null ? "remove" : "set")} {reference.Surface} {reference.Stat} {key}"));
        }
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
        OnPropertyChanged(nameof(CanEditNode));
        OnPropertyChanged(nameof(HasNodeNote));
        OnPropertyChanged(nameof(HasReadOnlyNote));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasTables));
        OnPropertyChanged(nameof(HasUses));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(HasDeleteRefusal));
        DeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasNodeChanged(bool value) => OnPropertyChanged(nameof(ShowsDocument));

    partial void OnDeleteRefusalChanged(string value)
    {
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(HasDeleteRefusal));
    }
}
