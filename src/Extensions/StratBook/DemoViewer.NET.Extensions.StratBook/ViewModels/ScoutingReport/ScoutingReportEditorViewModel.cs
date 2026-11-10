#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.StratBook.Services.Generated;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

/// <summary>
///     One generated Scouting Report line before the user's edits: its scope key, its section, the generated
///     text, and, for a setup read, where to read its rendered picture at export time.
/// </summary>
/// <param name="Key">The scope key (<see cref="ScoutingReportStarKeys" />); never an index.</param>
/// <param name="Section">The section heading it prints under: "de_nuke CT · Setups", "Map pool".</param>
/// <param name="Generated">The generated text.</param>
/// <param name="Image">Reads the line's PNG when it is exported; null for a text line.</param>
/// <param name="Map">The map a map-specific line is about; null for the team as a whole.</param>
/// <param name="Suggested">Starts starred the first time it is built (once ever per team).</param>
public sealed record ScoutingReportFindingSource(
    string Key,
    string Section,
    string Generated,
    Func<byte[]?>? Image = null,
    string? Map = null,
    bool Suggested = false);

/// <summary>
///     Scouting Report Editing And Export: every layer's reads and rows over every map and side as findings,
///     the long form being the working document and the starred findings assembling the one-pager. Every line
///     is editable (rewritten, reset, left out), the user adds notes and a summary beside them, and the export
///     writes either form as self-contained HTML or Markdown, the one-pager by default.
///     <para>
///         <b>No verdicts.</b> A finding is a count over its stated sample; the export adds no rating, no
///         grade and no win probability.
///     </para>
///     <para>
///         <b>Stars, edits and dismissals live in <see cref="ScoutingReportNotesStore" /> by scope key.</b> The
///         inspector stars a row through <see cref="SetStarred" />, the long form through
///         <see cref="ToggleStarCommand" />; both land on the same key, so the one-pager preview, the long form and
///         the inspector agree. This VM is the store's only writer.
///     </para>
/// </summary>
public sealed partial class ScoutingReportEditorViewModel : ObservableObject
{
    public const string MapPoolSection = "Map pool";
    public const string PeriodDiffSection = "Period Diff";
    public const string VetoSection = "Veto history";
    public const string NotesSection = "Notes";

    private readonly Func<string, string, string, string?> _export;
    private readonly List<ScoutingReportFindingViewModel> _rows = [];
    private readonly ScoutingReportNotesStore _store;
    private bool _loading;
    private string _sampleLine = "";
    private IReadOnlyList<ScoutingReportFindingSource> _sources = [];
    private Guid? _teamId;
    private string _teamName = "";

    /// <summary>"one-pager written: falcons-one-pager.html", or why not; empty until an export.</summary>
    [ObservableProperty]
    private string _exportLine = "";

    /// <summary>The form Export writes. The one-pager is the default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportLabel), nameof(SelectedFormOption), nameof(CanExport))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private ScoutingReportForm _exportForm = ScoutingReportForm.OnePager;

    /// <summary>The file Export writes: HTML by default, the LAN Print route.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFormatOption))]
    private ScoutingReportFormat _exportFormat = ScoutingReportFormat.Html;

    [ObservableProperty]
    private string _newNoteText = "";

    /// <summary>Also list the dismissed lines, each with Restore. The forms never include them.</summary>
    [ObservableProperty]
    private bool _showSettled;

    /// <summary>Shows only the starred lines: the one-pager as it will print.</summary>
    [ObservableProperty]
    private bool _showStarredOnly;

    /// <summary>The user's paragraph at the top of both forms.</summary>
    [ObservableProperty]
    private string _summary = "";

    /// <param name="store">Where stars, edits, notes and the summary persist.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; Export has no file to write there.</param>
    /// <param name="export">Writes (text, stem, extension) and opens it, returning the path or null; the temp-file writer when null.</param>
    public ScoutingReportEditorViewModel(ScoutingReportNotesStore store, bool isBrowser = false,
        Func<string, string, string, string?>? export = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        IsBrowser = isBrowser;
        _export = export ?? ScoutingReportExportWriter.WriteAndOpen;
    }

    public bool IsBrowser { get; }

    /// <summary>The long form's rows shown: every line not left out, or only the starred ones.</summary>
    public ObservableCollection<ScoutingReportFindingViewModel> Findings { get; } = [];

    /// <summary>The one-pager as it stands: every starred line not left out, in print order.</summary>
    public ObservableCollection<ScoutingReportFindingViewModel> Starred { get; } = [];

    /// <summary>Raised after a star changed, from either the long form or the inspector.</summary>
    public event Action? StarsChanged;

    /// <summary>The two forms, worded for the picker; the one-pager first, as the default.</summary>
    public static IReadOnlyList<ScoutingReportExportOption<ScoutingReportForm>> FormOptions { get; } =
    [
        new(ScoutingReportForm.OnePager, "One-pager"),
        new(ScoutingReportForm.LongForm, "Long form")
    ];

    /// <summary>The two files, worded for the picker; HTML first, as the default.</summary>
    public static IReadOnlyList<ScoutingReportExportOption<ScoutingReportFormat>> FormatOptions { get; } =
    [
        new(ScoutingReportFormat.Html, "HTML"),
        new(ScoutingReportFormat.Markdown, "Markdown")
    ];

    /// <summary>The picker's row for <see cref="ExportForm" />.</summary>
    public ScoutingReportExportOption<ScoutingReportForm>? SelectedFormOption
    {
        get => FormOptions.First(o => o.Value == ExportForm);
        set => ExportForm = value?.Value ?? ScoutingReportForm.OnePager;
    }

    /// <summary>The picker's row for <see cref="ExportFormat" />.</summary>
    public ScoutingReportExportOption<ScoutingReportFormat>? SelectedFormatOption
    {
        get => FormatOptions.First(o => o.Value == ExportFormat);
        set => ExportFormat = value?.Value ?? ScoutingReportFormat.Html;
    }

    public bool HasTeam => _teamId is not null;

    public bool HasFindings => Findings.Count > 0;

    public int StarredCount => _rows.Count(r => r is { IsStarred: true, IsHidden: false });

    public bool HasStarred => StarredCount > 0;

    public int HiddenCount => _rows.Count(r => r.IsHidden);

    public bool HasHidden => HiddenCount > 0;

    public string SettledLabel => GeneratedInbox.SettledLabel(HiddenCount);

    /// <summary>"42 findings · 5 starred · 2 dismissed".</summary>
    public string CountsLine
    {
        get
        {
            int shown = _rows.Count(r => !r.IsHidden);
            List<string> parts = [Plural(shown, "finding"), $"{StarredCount} starred"];
            if (HiddenCount > 0)
            {
                parts.Add($"{HiddenCount} dismissed");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>"One-pager · 5 starred", the preview's heading.</summary>
    public string OnePagerHeading => $"One-pager · {StarredCount} starred";

    /// <summary>What the Findings list says when it is empty.</summary>
    public string EmptyLine => ShowStarredOnly
        ? ScoutingReportExporter.NothingStarredLine
        : "no findings yet: the layers are still building, or have nothing to count";

    /// <summary>
    ///     A team, a file to write, and for the one-pager at least one star: an empty one-pager is not exported,
    ///     the long form always can be.
    /// </summary>
    public bool CanExport => HasTeam && !IsBrowser && (ExportForm == ScoutingReportForm.LongForm || HasStarred);

    /// <summary>What the Export slot says when there is no file to write.</summary>
    public string ExportUnavailableNote =>
        IsBrowser ? "Export scouting report: unavailable in the browser; copy the markdown instead" : "";

    public bool HasExportUnavailableNote => ExportUnavailableNote.Length > 0;

    public bool NotesAreSessionOnly => _store.IsSessionOnly;

    /// <summary>"Export one-pager · 5 starred", or "Export long form".</summary>
    public string ExportLabel => ExportForm == ScoutingReportForm.OnePager
        ? $"Export one-pager · {StarredCount} starred"
        : "Export long form";

    /// <summary>Whether a scope key is starred for the current team.</summary>
    /// <param name="key">The key.</param>
    public bool IsStarred(string key) =>
        _teamId is { } id && _store.For(id).Starred.Contains(key, StringComparer.Ordinal);

    /// <summary>Stars or unstars a line by key, from the inspector; the long form's row follows.</summary>
    /// <param name="key">The key.</param>
    /// <param name="starred">Starred.</param>
    public void SetStarred(string key, bool starred)
    {
        if (_teamId is not { } id)
        {
            return;
        }

        _store.SetStarred(id, key, starred);
        foreach (ScoutingReportFindingViewModel row in _rows.Where(r => r.Key == key))
        {
            row.IsStarred = starred;
        }

        AfterStar();
    }

    /// <summary>Stars suggested lines the first time each is built; a line unstarred after that stays unstarred.</summary>
    /// <param name="keys">The suggested keys.</param>
    public void Seed(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (_teamId is not { } id)
        {
            return;
        }

        bool any = false;
        foreach (string key in keys)
        {
            if (_store.Seed(id, key))
            {
                any = true;
                foreach (ScoutingReportFindingViewModel row in _rows.Where(r => r.Key == key))
                {
                    row.IsStarred = true;
                }
            }
        }

        if (any)
        {
            AfterStar();
        }
    }

    /// <summary>
    ///     Rebuilds the rows for a team from the current lines and the stored edits, or clears them when
    ///     <paramref name="teamId" /> is null.
    /// </summary>
    /// <param name="teamId">The team, or null.</param>
    /// <param name="teamName">Its display name, the export's title.</param>
    /// <param name="sampleLine">The sample line printed under the title.</param>
    /// <param name="sources">Every generated line, in print order.</param>
    public void Load(Guid? teamId, string teamName, string sampleLine, IReadOnlyList<ScoutingReportFindingSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        bool teamChanged = teamId != _teamId;
        _teamId = teamId;
        _teamName = teamName;
        _sampleLine = sampleLine;
        _sources = sources;
        if (teamChanged)
        {
            ExportLine = "";
            NewNoteText = "";
        }

        Rebuild();
        OnPropertyChanged(nameof(HasTeam));
        RaiseCounts();
        StarsChanged?.Invoke();
    }

    /// <summary>The document as it will print, with the user's edits applied and left-out lines dropped.</summary>
    public ScoutingReportDocument Document() => new()
    {
        TeamName = _teamName,
        SampleLine = _sampleLine,
        Summary = Summary.Trim(),
        Findings =
        [
            .. _rows.Where(r => !r.IsHidden).Select(r => new ScoutingReportFinding
            {
                Key = r.Key,
                Section = r.Section,
                Text = r.Text,
                IsStarred = r.IsStarred,
                ImagePng = r.IsStarred || ExportForm == ScoutingReportForm.LongForm ? r.Image?.Invoke() : null
            })
        ]
    };

    /// <summary>The chosen form as Markdown, for the Copy button.</summary>
    public string MarkdownText() => ScoutingReportExporter.Markdown(Document(), ExportForm);

    /// <summary>The line the Copy button leaves once the clipboard took the text.</summary>
    public void ReportCopied() => ExportLine = ExportForm == ScoutingReportForm.OnePager
        ? "one-pager copied as markdown"
        : "long form copied as markdown";

    /// <summary>The Copy button's fallback when the host refused the clipboard.</summary>
    public void ReportCopyFailed() => ExportLine = "could not reach the clipboard";

    partial void OnSummaryChanged(string value)
    {
        if (!_loading && _teamId is { } id)
        {
            _store.SetSummary(id, value);
        }
    }

    partial void OnShowStarredOnlyChanged(bool value) => Project();

    partial void OnShowSettledChanged(bool value) => Project();

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export()
    {
        if (!CanExport)
        {
            return;
        }

        ScoutingReportDocument doc = Document();
        (string text, string extension) = ExportFormat == ScoutingReportFormat.Html
            ? (ScoutingReportExporter.Html(doc, ExportForm), ".html")
            : (ScoutingReportExporter.Markdown(doc, ExportForm), ".md");
        string stem = StratBookTabViewModel.ExportFileStem(_teamName)
            + (ExportForm == ScoutingReportForm.OnePager ? "-one-pager" : "-scouting-report");
        string? path;
        try
        {
            path = _export(text, stem, extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportLine = $"could not write the export: {ex.Message}";
            return;
        }

        ExportLine = path is null
            ? "could not open the export"
            : $"{(ExportForm == ScoutingReportForm.OnePager ? "one-pager" : "long form")} written: {Path.GetFileName(path)}";
    }

    [RelayCommand]
    private void ToggleStar(ScoutingReportFindingViewModel? row)
    {
        if (row is null || _teamId is not { } id)
        {
            return;
        }

        row.IsStarred = !row.IsStarred;
        _store.SetStarred(id, row.Key, row.IsStarred);
        if (ShowStarredOnly)
        {
            Project();
        }

        AfterStar();
    }

    [RelayCommand]
    private void CommitEdit(ScoutingReportFindingViewModel? row)
    {
        if (row is null || _teamId is not { } id)
        {
            return;
        }

        if (row.IsNote)
        {
            _store.EditNote(id, row.Key, row.EditText);
            row.Text = _store.For(id).Notes.FirstOrDefault(n => ScoutingReportNotesStore.NoteKey(n.Id) == row.Key)?.Text
                ?? row.Text;
        }
        else
        {
            _store.SetEdit(id, row.Key, row.EditText, row.Generated);
            row.Text = _store.For(id).Edits.TryGetValue(row.Key, out string? edit) ? edit : row.Generated;
        }

        row.IsEditing = false;
    }

    /// <summary>Puts a rewritten line back to its generated text.</summary>
    [RelayCommand]
    private void ResetEdit(ScoutingReportFindingViewModel? row)
    {
        if (row is null || row.IsNote || _teamId is not { } id)
        {
            return;
        }

        _store.SetEdit(id, row.Key, null, row.Generated);
        row.Text = row.Generated;
        row.IsEditing = false;
    }

    /// <summary>Dismisses a generated line from both forms, or restores a dismissed one; removes a note.</summary>
    [RelayCommand]
    private void Remove(ScoutingReportFindingViewModel? row)
    {
        if (row is null || _teamId is not { } id)
        {
            return;
        }

        if (row.IsNote)
        {
            _store.RemoveNote(id, row.Key);
            _rows.Remove(row);
        }
        else
        {
            _store.SetHidden(id, row.Key, !row.IsHidden);
            row.IsHidden = !row.IsHidden;
        }

        Project();
        StarsChanged?.Invoke();
    }

    [RelayCommand]
    private void AddNote()
    {
        if (_teamId is not { } id || _store.AddNote(id, NewNoteText) is null)
        {
            return;
        }

        NewNoteText = "";
        Rebuild();
    }

    private void AfterStar()
    {
        ProjectStarred();
        RaiseCounts();
        StarsChanged?.Invoke();
    }

    private void Rebuild()
    {
        _rows.Clear();
        _loading = true;
        try
        {
            if (_teamId is not { } id)
            {
                Summary = "";
            }
            else
            {
                ScoutingReportTeamNotes notes = _store.For(id);
                HashSet<string> starred = new(notes.Starred, StringComparer.Ordinal);
                HashSet<string> hidden = new(notes.Hidden, StringComparer.Ordinal);
                HashSet<string> seen = new(StringComparer.Ordinal);
                foreach (ScoutingReportFindingSource source in _sources)
                {
                    if (!seen.Add(source.Key))
                    {
                        continue;
                    }

                    string text = notes.Edits.TryGetValue(source.Key, out string? edit) ? edit : source.Generated;
                    _rows.Add(
                        new ScoutingReportFindingViewModel(source.Key, source.Section, source.Generated, text, false,
                            source.Image)
                        {
                            Map = source.Map,
                            IsStarred = starred.Contains(source.Key),
                            IsHidden = hidden.Contains(source.Key)
                        });
                }

                foreach (ScoutingReportNote note in notes.Notes)
                {
                    string key = ScoutingReportNotesStore.NoteKey(note.Id);
                    _rows.Add(new ScoutingReportFindingViewModel(key, NotesSection, note.Text, note.Text, true, null)
                    {
                        IsStarred = starred.Contains(key)
                    });
                }

                Summary = notes.Summary;
            }
        }
        finally
        {
            _loading = false;
        }

        Project();
    }

    // The shown rows from the full set, with a section heading on the first row of each section.
    private void Project()
    {
        Findings.Clear();
        string? section = null;
        foreach (ScoutingReportFindingViewModel row in _rows)
        {
            if ((row.IsHidden && !ShowSettled) || (ShowStarredOnly && !row.IsStarred))
            {
                continue;
            }

            row.ShowsSectionHeader = !string.Equals(section, row.Section, StringComparison.Ordinal);
            section = row.Section;
            Findings.Add(row);
        }

        ProjectStarred();
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(EmptyLine));
        RaiseCounts();
    }

    private void ProjectStarred()
    {
        List<ScoutingReportFindingViewModel> starred = [.. _rows.Where(r => r is { IsStarred: true, IsHidden: false })];
        if (starred.SequenceEqual(Starred))
        {
            return;
        }

        Starred.Clear();
        foreach (ScoutingReportFindingViewModel row in starred)
        {
            Starred.Add(row);
        }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(StarredCount));
        OnPropertyChanged(nameof(HasStarred));
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(SettledLabel));
        OnPropertyChanged(nameof(CountsLine));
        OnPropertyChanged(nameof(OnePagerHeading));
        OnPropertyChanged(nameof(ExportLabel));
        OnPropertyChanged(nameof(CanExport));
        ExportCommand.NotifyCanExecuteChanged();
    }

    private static string Plural(int count, string noun) => ScoutingLayerBase.Plural(count, noun);

    // ---- The team-wide lines. Each builder is pure over display rows. ----

    /// <summary>One line per map played, and the decider line when there is one.</summary>
    /// <param name="maps">The map pool's rows.</param>
    /// <param name="deciderLine">The decider line, or empty.</param>
    public static IEnumerable<ScoutingReportFindingSource> FromMapPool(IEnumerable<MapPoolRowViewModel> maps,
        string deciderLine)
    {
        ArgumentNullException.ThrowIfNull(maps);
        foreach (MapPoolRowViewModel map in maps)
        {
            yield return new ScoutingReportFindingSource(
                ScoutingReportStarKeys.Key(new ScoutingScope(map.Map, "", "pool"), "record"), MapPoolSection,
                map.FindingText, Map: map.Map);
        }

        if (deciderLine.Length > 0)
        {
            yield return new ScoutingReportFindingSource(
                ScoutingReportStarKeys.Key(new ScoutingScope("", "", "pool"), "deciders"), MapPoolSection,
                deciderLine);
        }
    }

    /// <summary>The note, then one line per period.</summary>
    /// <param name="recent">The last period, or null with no diff.</param>
    /// <param name="previous">The period before it, or null.</param>
    /// <param name="note">The section's note.</param>
    public static IEnumerable<ScoutingReportFindingSource> FromPeriodDiff(PeriodDiffRowViewModel? recent,
        PeriodDiffRowViewModel? previous, string note)
    {
        if (recent is null)
        {
            yield break;
        }

        if (note.Length > 0)
        {
            yield return new ScoutingReportFindingSource(Key("period", "note"), PeriodDiffSection, note);
        }

        PeriodDiffRowViewModel[] periods = previous is null ? [recent] : [recent, previous];
        foreach (PeriodDiffRowViewModel p in periods)
        {
            List<string> parts =
                [p.CountLabel, p.RecordLabel, $"win rate {p.WinRateLabel}", p.SideLabel, p.RosterLabel];
            if (p.HasStandIn)
            {
                parts.Add(p.StandInLabel);
            }

            yield return new ScoutingReportFindingSource(Key("period", p.Label), PeriodDiffSection,
                $"{p.Label}: {string.Join(", ", parts)}");
        }
    }

    /// <summary>One line per veto step the user entered.</summary>
    /// <param name="vetoes">The veto rows.</param>
    public static IEnumerable<ScoutingReportFindingSource> FromVetoes(IEnumerable<VetoRowViewModel> vetoes) =>
        vetoes.Select(v => new ScoutingReportFindingSource(Key("veto", v.Id.ToString("N")), VetoSection,
            $"{v.Order}. {v.ActionLabel} {v.Map} by {v.ByLabel}" + (v.Note.Length > 0 ? $" ({v.Note})" : "")));

    /// <summary>
    ///     A layer build's lines for the long form: each read, then each row with a number, under the section
    ///     "de_nuke CT · Setups". Placeholder rows are left out: they count nothing.
    /// </summary>
    /// <param name="layer">The layer.</param>
    /// <param name="context">The scope it was built for.</param>
    /// <param name="result">The build.</param>
    public static IEnumerable<ScoutingReportFindingSource> FromLayer(IScoutingLayer layer, ScoutingLayerContext context,
        ScoutingLayerResult result)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status.State != ScoutingLayerState.Ready)
        {
            yield break;
        }

        string section = $"{context.Prefix} · {layer.NameFor(context.Side)}";
        foreach (ScoutingRead read in result.Reads)
        {
            yield return new ScoutingReportFindingSource(
                ScoutingReportStarKeys.Key(context.Scope(layer, read.Buy), read.StarText), section, read.Text,
                read.Image, context.Map, read.Suggested);
        }

        foreach (ScoutingInspectorRow row in result.Sections.SelectMany(s => s.Rows))
        {
            if (row.IsPlaceholder || row.Value.Length == 0)
            {
                continue;
            }

            yield return new ScoutingReportFindingSource(
                ScoutingReportStarKeys.Key(context.Scope(layer, row.Buy), row.KeyText), section,
                $"{row.KeyText}: {row.Value}", Map: context.Map);
        }
    }

    private static string Key(string group, string text) => ScoutingReportStarKeys.Key(new ScoutingScope("", "", group), text);
}

/// <summary>One choice in an export picker: the value and the words shown for it.</summary>
/// <param name="Value">The value.</param>
/// <param name="Label">The words.</param>
public sealed record ScoutingReportExportOption<T>(T Value, string Label)
    where T : struct, Enum
{
    public override string ToString() => Label;
}

/// <summary>One line of the long form: the star, the text (the user's rewrite when there is one) and its edit state.</summary>
public sealed partial class ScoutingReportFindingViewModel : ObservableObject
{
    [ObservableProperty]
    private string _editText = "";

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoveLabel))]
    private bool _isHidden;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarToolTip))]
    private bool _isStarred;

    [ObservableProperty]
    private bool _showsSectionHeader;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    private string _text;

    /// <param name="key">The finding's key.</param>
    /// <param name="section">Its section heading.</param>
    /// <param name="generated">The generated text (a note's own text).</param>
    /// <param name="text">The text shown.</param>
    /// <param name="isNote">True for a line the user wrote.</param>
    /// <param name="image">Reads the line's PNG at export time, or null.</param>
    public ScoutingReportFindingViewModel(string key, string section, string generated, string text, bool isNote,
        Func<byte[]?>? image)
    {
        Key = key;
        Section = section;
        Generated = generated;
        _text = text;
        IsNote = isNote;
        Image = image;
        Where = isNote
            ? "note"
            : ScoutingReportStarKeys.Parse(key) is { } parsed && ScoutingReportStarKeys.Where(parsed.Scope) is
                { Length: > 0 } where
                ? where
                : section;
    }

    public string Key { get; }

    /// <summary>The map a map-specific line sits under; null for the team as a whole and for notes.</summary>
    public string? Map { get; init; }

    public string Section { get; }

    /// <summary>"de_nuke CT · Full buy": the scope the one-pager preview prints before the line.</summary>
    public string Where { get; }

    public string Generated { get; }

    public bool IsNote { get; }

    public Func<byte[]?>? Image { get; }

    /// <summary>True when a generated line has been rewritten.</summary>
    public bool IsEdited => !IsNote && !string.Equals(Text, Generated, StringComparison.Ordinal);

    /// <summary>"Remove" on a note, "Dismiss" or "Restore" on a generated line.</summary>
    public string RemoveLabel => IsNote ? "Remove" : IsHidden ? "Restore" : "Dismiss";

    public string StarToolTip => IsStarred ? "On the one-pager: click to take it off" : "Star for the one-pager";

    /// <summary>Opens the line for editing, starting from the text shown.</summary>
    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Text;
        IsEditing = true;
    }

    /// <summary>Closes the editor without keeping what was typed.</summary>
    [RelayCommand]
    private void CancelEdit() => IsEditing = false;
}
