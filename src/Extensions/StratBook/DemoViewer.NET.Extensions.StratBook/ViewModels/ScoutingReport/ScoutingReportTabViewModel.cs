#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.Sdk.Review;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

/// <summary>
///     The Scouting Report tab: one team, one map, one side at a time on a map stage with layers.
///     <list type="bullet">
///         <item>The header: the team, its record, ranks (manual entry later), head-to-head against us, the
///         sample by date, and the export.</item>
///         <item>Map tabs, the side (CT or T, never both), the buy filter and the scope line, with notices for a
///         thin sample and for demos without positions.</item>
///         <item>The map pool table, with the team's own CT and T round win % (not the match-wide side split).</item>
///         <item>The stage (<see cref="Layers" />): the map with the drawn layers, the strip minis under it, and the
///         inspector with the focused layer's reads and starrable rows, whose numbers open their rounds in Review.</item>
///         <item>The players, then Period Diff, the veto history and the long form, collapsed.</item>
///     </list>
///     <para>
///         <b>Loads.</b> Opening a team starts its loads (<see cref="ScoutingReportData" />): the record and the
///         demos at once, every service build on its own worker. They come from <see cref="ScoutingReportLoads" />,
///         so another tab on the same team joins them instead of loading again. The stage builds the scope in
///         view from them; the long form builds every map and side once they have all landed, and is what the
///         export prints.
///     </para>
///     <para>
///         Delegate-injected (the Teams tab's own precedent): the VM owns no clustering and no aggregation of
///         its own, and re-projects on every Team Identity <c>Changed</c> that moves what the team is built from.
///     </para>
/// </summary>
public sealed partial class ScoutingReportTabViewModel : ExtensionViewModel, IWorkspaceTabViewModel, IScoutingRowHost,
    IDisposable
{
    private readonly GrenadeIndex? _grenades;
    private readonly IExtensionLibrary _library;
    private readonly ScoutingReportLoads _loads;
    private readonly Func<DateTime> _now;
    private readonly ScoutingReviewOpener _opener;
    private readonly Action<Action> _post;
    private readonly RoundIndexStore? _roundIndex;
    private readonly ScoutingReportSources _sources;
    private readonly TeamIdentityService _teams;
    private readonly VetoHistoryStore _vetoes;
    private readonly TeamRanksStore _ranks;
    private readonly ScoutingReportNotesStore _notes;
    private readonly Func<ulong, PlayerOverviewScope?, bool>? _openPlayerOverview;
    private readonly Func<string, bool> _openUri;

    // The long form's layer lines for the current loads, by map; empty until every load has landed.
    private IReadOnlyList<ScoutingReportFindingSource> _layerFindings = [];
    private ScoutingReportData? _data;
    private bool _disposed;
    private readonly IDisposable? _minedWatch;

    // The layers that read the Strat Mining patterns.
    private static readonly string[] _stratLayerIds = ["strats", "against"];
    private int _generation;
    private string? _projectedKey;

    // The player whose CT setup the map shows alone (SteamID64), or null.
    private string? _isolated;
    private bool _shown = true;

    [ObservableProperty]
    private ScoutingBuy _buy = ScoutingBuy.All;

    [ObservableProperty]
    private bool _isNotesOpen;

    [ObservableProperty]
    private bool _isPeriodOpen;

    [ObservableProperty]
    private bool _isPoolOpen = true;

    [ObservableProperty]
    private bool _isVetoOpen;

    [ObservableProperty]
    private VetoAction _newVetoAction = VetoAction.Ban;

    [ObservableProperty]
    private bool _newVetoByOpponent;

    [ObservableProperty]
    private string _newVetoMap = "";

    [ObservableProperty]
    private PeriodDiffRowViewModel? _periodDiffPrevious;

    [ObservableProperty]
    private PeriodDiffRowViewModel? _periodDiffRecent;

    [ObservableProperty]
    private string _periodDiffNote = "";

    /// <summary>"12 rounds sent to Review", or why none were; empty until a number is opened.</summary>
    [ObservableProperty]
    private string _reviewLine = "";

    [ObservableProperty]
    private MapPoolRowViewModel? _selectedMap;

    [ObservableProperty]
    private ScoutingReportTeamRow? _selectedTeam;

    /// <summary>Shows each layer's plan code (E, D, D-rs, R, K) beside the inspector title.</summary>
    [ObservableProperty]
    private bool _showBuildStatus;

    [ObservableProperty]
    private ScoutingSide _side = ScoutingSide.Ct;

    /// <summary>Demos per Period Diff period; one of <see cref="WindowSizeOptions" />.</summary>
    [ObservableProperty]
    private int _windowSize = PeriodDiffService.DefaultWindowSize;

    /// <param name="teams">Team Identity, for the team picker and the sides everything is built from.</param>
    /// <param name="library">The library a demo's map, score and side-round totals come from.</param>
    /// <param name="vetoes">The user's manually entered veto steps, filed per opponent.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; null reads the runtime.</param>
    /// <param name="heatmaps">Builds the setups; null leaves the Setups layer empty.</param>
    /// <param name="review">The Review Queue a number's rounds are sent to; null says so on open.</param>
    /// <param name="selectTab">Shows a tab by id, for the Review tab after a send; null stays here.</param>
    /// <param name="renderer">Builds the heatmap renderer for an exported picture; the bundle loader when null.</param>
    /// <param name="post">UI-thread marshal for the workers' results; the dispatcher when null.</param>
    /// <param name="openings">Builds the Opening Tendencies; null leaves their layers empty.</param>
    /// <param name="postPlant">Builds the Post-Plant And Retake; null leaves their layers empty.</param>
    /// <param name="situational">Builds the Situational Behaviour; null leaves the Economy strip empty.</param>
    /// <param name="notes">The user's stars, edits and notes; a session-only store when null.</param>
    /// <param name="export">Writes an export (text, stem, extension) and opens it; the temp-file writer when null.</param>
    /// <param name="grenades">The grenade index the openings read; its changes re-project the team.</param>
    /// <param name="runSection">Runs a named load; the pool when null. Unused when <paramref name="loads" /> is given.</param>
    /// <param name="roundIndex">The round index stamps the projection key reads; none when null.</param>
    /// <param name="roundFacts">Round Facts, for the team-scoped CT and T %, pistols and plant times; none when null.</param>
    /// <param name="playback">2D playback for a row's play button; none when null.</param>
    /// <param name="now">Today, for "5d ago"; the clock when null.</param>
    /// <param name="layers">The layers; <see cref="ScoutingLayerCatalog.Create" /> when null.</param>
    /// <param name="minedPatterns">The Strat Mining patterns, null until mined; the strats layers are empty when null.</param>
    /// <param name="positionsFingerprint">The fingerprint current positions carry per map; null accepts any file.</param>
    /// <param name="siteRegions">A map's site regions for the Setups areas; distance alone when null.</param>
    /// <param name="fakes">A demo's current fake proposals for the Setups fake row; a placeholder when null.</param>
    /// <param name="ranks">The manual VRS and HLTV ranks per team; a session-only store when null.</param>
    /// <param name="analysisFacts">The library's analysis facts, for the players table's rates; none when null.</param>
    /// <param name="openPlayerOverview">Opens the Player Overview on a SteamID64 with a demo scope; none when null.</param>
    /// <param name="requestDemos">Queues demos at user priority, off the UI thread; the team is never pushed ahead when null.</param>
    /// <param name="loads">The loads every report tab shares; this tab's own over <paramref name="runSection" /> when null.</param>
    /// <param name="openUri">Opens a web address in the browser; the OS handler when null.</param>
    /// <param name="watchMinedPatterns">
    ///     Subscribes a handler to Strat Mining's change event (raised on the UI thread) and returns the
    ///     unsubscribe; the strat layers rebuild on it. None when null.
    /// </param>
    public ScoutingReportTabViewModel(TeamIdentityService teams, IExtensionLibrary library, VetoHistoryStore vetoes,
        bool? isBrowser = null,
        SetupHeatmapService? heatmaps = null,
        IExtensionReviewQueue? review = null,
        Func<string, bool>? selectTab = null,
        Func<SetupHeatmapRenderer>? renderer = null,
        Action<Action>? post = null,
        OpeningTendenciesService? openings = null,
        PostPlantService? postPlant = null,
        SituationalBehaviourService? situational = null,
        ScoutingReportNotesStore? notes = null,
        Func<string, string, string, string?>? export = null,
        GrenadeIndex? grenades = null,
        Func<string, Func<Action, Task>>? runSection = null,
        RoundIndexStore? roundIndex = null,
        IRoundFacts? roundFacts = null,
        Func<ISituationPlayback?>? playback = null,
        Func<DateTime>? now = null,
        IReadOnlyList<IScoutingLayer>? layers = null,
        Func<IReadOnlyList<MinedPattern>?>? minedPatterns = null,
        Func<string?, string>? positionsFingerprint = null,
        Func<Action, IDisposable>? watchMinedPatterns = null,
        Func<string, SetupSiteRegions?>? siteRegions = null,
        Func<string, string, IReadOnlyList<FakeRound>?>? fakes = null,
        TeamRanksStore? ranks = null,
        IAnalysisFacts? analysisFacts = null,
        Func<ulong, PlayerOverviewScope?, bool>? openPlayerOverview = null,
        Action<IReadOnlyList<string>>? requestDemos = null,
        ScoutingReportLoads? loads = null,
        Func<string, bool>? openUri = null)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(vetoes);
        _teams = teams;
        _library = library;
        _vetoes = vetoes;
        _ranks = ranks ?? new TeamRanksStore(null);
        _grenades = grenades;
        _roundIndex = roundIndex;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        Func<string, Func<Action, Task>> run = runSection ?? (_ => Task.Run);
        _loads = loads ?? new ScoutingReportLoads((_, section) => run(section));
        _openUri = openUri ?? OpenExternal.OpenUri;
        _now = now ?? (() => DateTime.Now);
        VrsRank = new RankFieldViewModel(_ranks, RankSystem.Vrs, _now);
        HltvRank = new RankFieldViewModel(_ranks, RankSystem.Hltv, _now);
        _opener = new ScoutingReviewOpener(review, selectTab);
        _openPlayerOverview = openPlayerOverview;
        _requestDemos = requestDemos;
        _notes = notes ?? new ScoutingReportNotesStore(null);
        IsBrowser = isBrowser ?? OperatingSystem.IsBrowser();
        _sources = new ScoutingReportSources
        {
            Teams = teams,
            Library = library,
            RoundFacts = roundFacts,
            AnalysisFacts = analysisFacts,
            Heatmaps = heatmaps,
            Openings = openings,
            PostPlant = postPlant,
            Situational = situational,
            Grenades = grenades,
            RoundIndex = roundIndex,
            PositionsFingerprint = positionsFingerprint,
            SiteRegions = siteRegions,
            Fakes = fakes,
            Playback = playback,
            MinedPatterns = minedPatterns,
            Renderer = renderer ?? (() => new SetupHeatmapRenderer())
        };
        Editor = new ScoutingReportEditorViewModel(_notes, IsBrowser, export, ExportHeader);
        Editor.StarsChanged += OnStarsChanged;
        Layers = new ScoutingLayerRegistry(layers ?? ScoutingLayerCatalog.Create(), _post, this);
        Editor.LayerOrder = [.. Layers.All.Select(c => c.Id)];
        Layers.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScoutingLayerRegistry.Focused))
            {
                OnPropertyChanged(nameof(ScopeLine));
            }
        };

        Players.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPlayers));
        _teams.Changed += Refresh;
        _library.Changed += OnLibraryChangedForPlayers;
        _library.Changed += OnLibraryChangedForProcessing;
        _vetoes.Changed += ProjectVetoes;
        _ranks.Changed += OnRanksChanged;
        if (_grenades is not null)
        {
            _grenades.Changed += Refresh;
        }

        _minedWatch = watchMinedPatterns?.Invoke(OnMinedPatternsChanged);
        Refresh();
    }

    /// <summary>Where both report tabs send feedback on the two layouts.</summary>
    public const string FeedbackUrl = "https://github.com/CS2OpenDev/DemoViewer.NET/issues/78";

    public bool IsBrowser { get; }

    public ObservableCollection<ScoutingReportTeamRow> Teams { get; } = [];

    /// <summary>The pool, most played first: the map tabs and the pool table.</summary>
    public ObservableCollection<MapPoolRowViewModel> Maps { get; } = [];

    public ObservableCollection<VetoRowViewModel> Vetoes { get; } = [];

    /// <summary>One dot per demo, by date.</summary>
    public ObservableCollection<TimelineDotViewModel> Timeline { get; } = [];

    /// <summary>The thin-sample and partial-positions notices for the scope in view.</summary>
    public ObservableCollection<string> Notices { get; } = [];

    public ObservableCollection<PlayerRowViewModel> Players { get; } = [];

    /// <summary>The map stage: the layers, the strip and the inspector.</summary>
    public ScoutingLayerRegistry Layers { get; }

    /// <summary>The findings, the long form, the one-pager and the export.</summary>
    public ScoutingReportEditorViewModel Editor { get; }

    /// <summary>The selected team's loads, set before the header is raised; null with no team.</summary>
    internal ScoutingReportData? Data => _data;

    /// <summary>Every load and the long form of the selected team; tests await it, then drain the post.</summary>
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The two veto step kinds, for the add-row picker.</summary>
    public static IReadOnlyList<VetoAction> VetoActionOptions { get; } = Enum.GetValues<VetoAction>();

    /// <summary>The window sizes the Period Diff picker offers.</summary>
    public static IReadOnlyList<int> WindowSizeOptions { get; } = [3, 5, 10, 20];

    /// <summary>The buy filter's choices.</summary>
    public static IReadOnlyList<ScoutingBuy> BuyOptions { get; } = Enum.GetValues<ScoutingBuy>();

    public bool HasTeams => Teams.Count > 0;

    public bool HasSelection => SelectedTeam is not null;

    public bool HasMaps => Maps.Count > 0;

    /// <summary>Teams exist and none is open: the page teaches what picking one shows.</summary>
    public bool IsAwaitingTeam => HasTeams && !HasSelection;

    /// <summary>A team is open with no map to show: the page says why and what fills it.</summary>
    public bool HasSelectionWithoutMaps => HasSelection && !HasMaps;

    public bool HasVetoes => Vetoes.Count > 0;

    public bool HasNotices => Notices.Count > 0;

    public bool HasPlayers => Players.Count > 0;

    public bool HasPeriodDiff => PeriodDiffRecent is not null;

    public bool VetoesAreSessionOnly => _vetoes.IsSessionOnly;

    public bool IsCt
    {
        get => Side == ScoutingSide.Ct;
        set
        {
            if (value)
            {
                Side = ScoutingSide.Ct;
            }
        }
    }

    public bool IsT
    {
        get => Side == ScoutingSide.T;
        set
        {
            if (value)
            {
                Side = ScoutingSide.T;
            }
        }
    }

    /// <summary>The map in view, as MapView names it; null with none.</summary>
    public string? MapName => SelectedMap?.Map;

    // ---- Header ----

    public string TeamName => SelectedTeam?.Name ?? "";

    public int Wins { get; private set; }

    public int Losses { get; private set; }

    /// <summary>"61% of 23 maps".</summary>
    public string RecordLine { get; private set; } = "";

    /// <summary>The team's VRS rank, edited inline in the header.</summary>
    public RankFieldViewModel VrsRank { get; }

    /// <summary>The team's HLTV rank, edited inline in the header.</summary>
    public RankFieldViewModel HltvRank { get; }

    /// <summary>Set when the ranks file could not be read, so the header says why edits do not stick.</summary>
    public string RanksProblem => _ranks.FileProblem ?? "";

    public bool HasRanksProblem => _ranks.FileProblem is not null;

    /// <summary>"vs Us 1–2", or empty when Team Identity knows no us or no meeting.</summary>
    public string HeadToHeadLine { get; private set; } = "";

    public bool HasHeadToHead => HeadToHeadLine.Length > 0;

    /// <summary>"23 demos · Aug 1 to Nov 8 · newest 3d ago".</summary>
    public string DemosLine { get; private set; } = "";

    /// <summary>"11 vs top-30 · 10 other · 2 unknown", or what to do when no opponent has a rank.</summary>
    public string OpponentMixLine { get; private set; } = "";

    /// <summary>"filled = won · de_nuke bright".</summary>
    public string TimelineCaption => SelectedMap is { } map ? $"filled = won, ring = lost, large = top-30 opponent · {map.MapLabel} bright" : "";

    /// <summary>"18 demos", the sample the export prints under its title.</summary>
    public string SampleSizeLine { get; private set; } = "";

    /// <summary>"Deciders: 3-1 (4 inferable)", or empty.</summary>
    public string DeciderLine { get; private set; } = "";

    public bool HasDeciderData => DeciderLine.Length > 0;

    /// <summary>"de_mirage · CT · Full buy · 6 maps".</summary>
    public string ScopeLine => SelectedMap is not { } map
        ? ""
        : $"{map.MapLabel} · {Side.Label()} · {(Buy == ScoutingBuy.All ? "all buys" : Buy + " buy")} · {ScoutingLayerBase.Plural(map.Played, "map")}";

    /// <summary>"de_mirage open below · a row opens its map · CT and T are the team's own round win %".</summary>
    public string PoolCaption => SelectedMap is { } map
        ? $"{map.MapLabel} open below · a row opens its map · CT and T are the team's own round win %"
        : "a row opens its map · CT and T are the team's own round win %";

    public string PlayersTitle => SelectedMap is { } map ? $"Players · {(PlayersAllMaps ? "all maps" : map.MapLabel)}" : "Players";

    // ---- Lifecycle ----

    /// <inheritdoc />
    public void OnActivated(IModuleContext context)
    {
        _shown = true;
        using (JobScope.UserAction())
        {
            Refresh();
        }
    }

    /// <inheritdoc />
    public void OnDeactivated() => _shown = false;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _generation);
        _loads.Release(this);
        Layers.Dispose();
        Editor.StarsChanged -= OnStarsChanged;
        Editor.Detach();
        _teams.Changed -= Refresh;
        _library.Changed -= OnLibraryChangedForPlayers;
        _library.Changed -= OnLibraryChangedForProcessing;
        _vetoes.Changed -= ProjectVetoes;
        _ranks.Changed -= OnRanksChanged;
        if (_grenades is not null)
        {
            _grenades.Changed -= Refresh;
        }

        _minedWatch?.Dispose();
    }

    // Strat Mining published or dropped its patterns: only the two layers that read them rebuild, in place.
    private void OnMinedPatternsChanged()
    {
        if (!_disposed)
        {
            Layers.Rebuild(_stratLayerIds);
        }
    }

    partial void OnSelectedTeamChanged(ScoutingReportTeamRow? value)
    {
        VrsRank.SetTeam(value?.Id);
        HltvRank.SetTeam(value?.Id);
        NewVetoMap = "";
        Layers.ResetToggles();
        using (JobScope.UserAction())
        {
            Project();
        }

        if (value is not null)
        {
            RequestTeam(value.Id, force: false);
        }

        OnPropertyChanged(nameof(CanProcessTeamFirst));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsAwaitingTeam));
        OnPropertyChanged(nameof(HasSelectionWithoutMaps));
        OnPropertyChanged(nameof(TeamName));
    }

    partial void OnSelectedMapChanged(MapPoolRowViewModel? value)
    {
        foreach (MapPoolRowViewModel map in Maps)
        {
            map.IsSelected = ReferenceEquals(map, value);
        }

        OnPropertyChanged(nameof(MapName));
        OnPropertyChanged(nameof(TimelineCaption));
        OnPropertyChanged(nameof(PoolCaption));
        OnPropertyChanged(nameof(PlayersTitle));
        OnPropertyChanged(nameof(PlayersScopeLabel));
        ProjectTimeline();
        ProjectScope();
        SeedSuggested();
    }

    partial void OnSideChanged(ScoutingSide value)
    {
        OnPropertyChanged(nameof(IsCt));
        OnPropertyChanged(nameof(IsT));
        ProjectScope();
    }

    partial void OnBuyChanged(ScoutingBuy value) => ProjectScope();

    partial void OnWindowSizeChanged(int value)
    {
        ProjectPeriodDiff();
        CollectFindings();
    }

    /// <summary>Shows a map's tab.</summary>
    /// <param name="map">The map.</param>
    [RelayCommand]
    private void SelectMap(MapPoolRowViewModel? map)
    {
        if (map is not null)
        {
            SelectedMap = map;
        }
    }

    [RelayCommand]
    private void TogglePool() => IsPoolOpen = !IsPoolOpen;

    /// <summary>Opens the feedback issue for the two report layouts in the browser.</summary>
    [RelayCommand]
    private void GiveFeedback() => _openUri(FeedbackUrl);

    [RelayCommand]
    private void TogglePeriod() => IsPeriodOpen = !IsPeriodOpen;

    [RelayCommand]
    private void ToggleVeto() => IsVetoOpen = !IsVetoOpen;

    [RelayCommand]
    private void ToggleNotes() => IsNotesOpen = !IsNotesOpen;

    [RelayCommand]
    private void AddVeto()
    {
        if (SelectedTeam is not { } team || string.IsNullOrWhiteSpace(NewVetoMap))
        {
            return;
        }

        _vetoes.Add(new VetoEntry
        {
            OpponentTeamId = team.Id,
            Order = Vetoes.Count + 1,
            Map = NewVetoMap.Trim(),
            Action = NewVetoAction,
            ByOpponent = NewVetoByOpponent
        });
        NewVetoMap = "";
    }

    [RelayCommand]
    private void RemoveVeto(VetoRowViewModel? row)
    {
        if (row is not null)
        {
            _vetoes.Remove(row.Id);
        }
    }

    /// <summary>Takes a line off the one-pager from its preview.</summary>
    /// <param name="row">The starred line.</param>
    [RelayCommand]
    private void Unstar(ScoutingReportFindingViewModel? row)
    {
        if (row is not null)
        {
            Editor.SetStarred(row.Key, false);
        }
    }

    // ---- IScoutingRowHost: the inspector's stars, Review and 2D ----

    /// <inheritdoc />
    public bool IsStarred(string key) => Editor.IsStarred(key);

    /// <inheritdoc />
    public void SetStarred(string key, bool starred) => Editor.SetStarred(key, starred);

    /// <inheritdoc />
    public void Open(ScoutingInspectorRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Row is not { Rounds.Count: > 0 } r)
        {
            return;
        }

        ReviewLine = _opener.Open(SelectedTeam?.Name ?? "", SelectedTeam?.Id, r.Rounds, r.OpenTitle);
        Layers.Inspector.ReviewLine = ReviewLine;
    }

    /// <inheritdoc />
    public void Play(ScoutingInspectorRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Row?.Play is { } at && _sources.Playback?.Invoke() is { } playback)
        {
            _ = playback.SeekAsync(at.DemoPath, at.Tick, row.Row.FollowSteamId);
        }
    }

    private void OnStarsChanged() => Layers.RefreshStars();

    // ---- Projection ----

    private void Refresh()
    {
        if (!_shown)
        {
            return;
        }

        List<ScoutingReportTeamRow> rows = [.. _teams.Teams.Select(t => new ScoutingReportTeamRow(t))];
        if (rows.Count == Teams.Count && rows.Zip(Teams).All(p => p.First.SameAs(p.Second)))
        {
            // Same rows: the selection stays put, and only a change in what the selected team is built from
            // re-projects. Every Team Identity recompute raises Changed, whichever team it touched.
            if (SelectedTeam is { } selected && ProjectionKey(selected) != _projectedKey)
            {
                Project();
            }

            return;
        }

        Guid? keep = SelectedTeam?.Id;
        Teams.Clear();
        foreach (ScoutingReportTeamRow row in rows)
        {
            Teams.Add(row);
        }

        OnPropertyChanged(nameof(HasTeams));
        OnPropertyChanged(nameof(IsAwaitingTeam));
        SelectedTeam = keep is { } id ? Teams.FirstOrDefault(t => t.Id == id) : null;
        if (keep is not null && SelectedTeam is null)
        {
            Project();
        }
    }

    // The team, its sides with their assignments, and the cache stamps of those demos: everything the
    // projection reads that a Team Identity change can move.
    private string ProjectionKey(ScoutingReportTeamRow row)
    {
        StringBuilder key = new();
        key.Append(JsonSerializer.Serialize(_teams.AllTeams.FirstOrDefault(t => t.Id == row.Id)));
        key.Append(CultureInfo.InvariantCulture, $"|grenades:{_grenades?.IsReady}|us:{_teams.Us?.Id}");
        foreach ((DemoRef demo, int side, TeamAssignment assignment) in _teams.SidesOf(row.Id))
        {
            key.Append('|').Append(side).Append(JsonSerializer.Serialize(demo))
                .Append(JsonSerializer.Serialize(assignment));
            if (_library.Find(demo.Path) is { } entry)
            {
                DemoDataStamp? index = _roundIndex?.Stamp(demo.Path);
                DemoDataStamp? grenades = _grenades?.Store.Stamp(demo.Path);
                key.Append(CultureInfo.InvariantCulture,
                    $"{entry.Modified.Ticks}/{entry.FileSizeBytes}/{entry.State}/{entry.CtSideWins}/{entry.TSideWins}/{entry.Fact(RoundFactsRows.FacetId)?.Fingerprint}/{index?.Fingerprint}/{index?.WrittenAtTicks}/{grenades?.State ?? DemoDataState.Pending}/{grenades?.Count ?? 0}/{grenades?.Fingerprint}/{_grenades?.IsLoaded(demo.Path)}");
            }
        }

        return key.ToString();
    }

    private void Project()
    {
        int generation = Interlocked.Increment(ref _generation);
        _projectedKey = SelectedTeam is { } selected ? ProjectionKey(selected) : null;
        _layerFindings = [];
        ResetPlayerStats();
        string? keepMap = SelectedMap?.Map;
        Maps.Clear();
        ReviewLine = "";
        if (SelectedTeam is not { } row)
        {
            _loads.Release(this);
            _data = null;
            Wins = Losses = 0;
            RecordLine = HeadToHeadLine = DemosLine = SampleSizeLine = DeciderLine = "";
            Timeline.Clear();
            OpponentMixLine = "";
            Players.Clear();
            ResetPlayerStats();
            SelectedMap = null;
            RaiseHeader();
            ProjectVetoes(collect: false);
            ProjectPeriodDiff();
            ProjectScope();
            CollectFindings();
            LoadTask = Task.CompletedTask;
            return;
        }

        ScoutingReportData data = _loads.Acquire(this, row.Id, row.Name, _projectedKey!, _sources);
        _data = data;
        DateTime now = _now();
        foreach (MapPoolMapRow map in data.Record.Maps)
        {
            long last = data.Demos.Where(d => d.Map == map.Map).Select(d => d.Ticks).DefaultIfEmpty(0).Max();
            Maps.Add(new MapPoolRowViewModel(map, last > 0 ? new DateTime(last) : null, now));
        }

        ProjectHeader(data, now);
        ProjectVetoes(collect: false);
        ProjectPeriodDiff();
        SelectedMap = Maps.FirstOrDefault(m => m.Map == keepMap) ?? Maps.FirstOrDefault();
        if (SelectedMap is null)
        {
            ProjectScope();
        }

        CollectFindings();
        LoadTask = Task.WhenAll(Land(data, generation), StartPlayerStats(data));
    }

    // Once every load is in: the pool's team-scoped columns, the notices, the players and the long form. The long
    // form is built once per load and layer set, whichever tab asks first.
    private Task Land(ScoutingReportData data, int generation)
    {
        Task<IReadOnlyList<ScoutingReportFindingSource>> longForm = _loads.Share(data, "long-form",
            string.Join(',', Layers.All.Select(c => c.Id)),
            () => data.All.IsCompleted
                ? BuildLongForm(data)
                : data.All.ContinueWith(_ => BuildLongForm(data), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap());
        if (longForm.IsCompleted)
        {
            Landed(data, generation, longForm);
            return Task.CompletedTask;
        }

        return longForm.ContinueWith(t => _post(() => Landed(data, generation, t)), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Landed(ScoutingReportData data, int generation, Task<IReadOnlyList<ScoutingReportFindingSource>> longForm)
    {
        if (_disposed || generation != Volatile.Read(ref _generation) || !ReferenceEquals(data, _data))
        {
            return;
        }

        TeamMapFactsSet? facts = data.Facts.IsCompletedSuccessfully ? data.Facts.Result : null;
        foreach (MapPoolRowViewModel map in Maps)
        {
            map.SetFacts(facts?.For(map.Map), _sources.RoundFacts is not null);
        }

        _layerFindings = longForm.IsCompletedSuccessfully ? longForm.Result : [];
        ProjectNotices();
        ProjectPlayers();
        CollectFindings();
        SeedSuggested();
    }

    // Every layer over every map and side at the "All" buy (a layer that splits by buy files each buy's lines
    // under its own), in map, side and layer order: the long form, and the export's source.
    private async Task<IReadOnlyList<ScoutingReportFindingSource>> BuildLongForm(ScoutingReportData data)
    {
        List<ScoutingReportFindingSource> sources = [];
        foreach (string map in data.Record.Maps.Select(m => m.Map))
        {
            foreach (ScoutingSide side in (ScoutingSide[])[ScoutingSide.Ct, ScoutingSide.T])
            {
                ScoutingLayerContext context = new(data, map, side, ScoutingBuy.All);
                foreach (ScoutingLayerChipViewModel chip in Layers.All.Where(c => c.Layer.AppliesTo(side)))
                {
                    ScoutingLayerResult result;
                    try
                    {
                        result = await ScoutingLayerRegistry.Build(chip.Layer, context, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        continue;
                    }

                    sources.AddRange(ScoutingReportEditorViewModel.FromLayer(chip.Layer, context, result));
                }
            }
        }

        return sources;
    }

    // The suggested reads of the map in view start starred, once ever per line.
    private void SeedSuggested()
    {
        if (SelectedMap is not { } map)
        {
            return;
        }

        Editor.Seed(_layerFindings.Where(f => f.Suggested && f.Map == map.Map).Select(f => f.Key));
    }

    private void ProjectHeader(ScoutingReportData data, DateTime now)
    {
        MapPoolRecord record = data.Record;
        Wins = record.Maps.Sum(m => m.Wins);
        Losses = record.Maps.Sum(m => m.Losses);
        int maps = record.TotalDemos;
        RecordLine = Wins + Losses == 0
            ? $"no resolved score in {ScoutingLayerBase.Plural(maps, "map")}"
            : $"{ScoutingLayerBase.Percent((double)Wins / (Wins + Losses))} of {ScoutingLayerBase.Plural(maps, "map")}";
        SampleSizeLine = record.TotalDemos == 0
            ? "no demos yet: this team has no side assigned in any indexed demo"
            : $"{ScoutingLayerBase.Plural(record.TotalDemos, "demo")} across {ScoutingLayerBase.Plural(record.Maps.Count, "map")}";
        DeciderLine = record.Deciders.Played == 0
            ? ""
            : $"Deciders: {record.Deciders.Wins}-{record.Deciders.Losses} ({record.Deciders.Played} inferable)";

        HeadToHeadLine = "";
        if (_teams.Us is { } us && us.Id != data.TeamId)
        {
            List<TeamDemoFacts> met = [.. data.Demos.Where(d => d.OpponentId == us.Id)];
            if (met.Count > 0)
            {
                HeadToHeadLine =
                    $"vs {DisplayText.Sanitize(us.Name)} {met.Count(d => d.Won == true)}–{met.Count(d => d.Won == false)}";
            }
        }

        if (data.Demos.Count == 0)
        {
            DemosLine = "no demos";
        }
        else
        {
            DateTime first = new(data.Demos.Min(d => d.Ticks));
            DateTime last = new(data.Demos.Max(d => d.Ticks));
            DemosLine =
                $"{ScoutingLayerBase.Plural(data.Demos.Count, "demo")} · {first.ToString("MMM d", CultureInfo.InvariantCulture)} to {last.ToString("MMM d", CultureInfo.InvariantCulture)} · newest {MapPoolRowViewModel.Ago(last, now)}";
        }

        ProjectOpponentMix(data);
        RaiseHeader();
    }

    private void RaiseHeader()
    {
        OnPropertyChanged(nameof(Wins));
        OnPropertyChanged(nameof(Losses));
        OnPropertyChanged(nameof(RecordLine));
        OnPropertyChanged(nameof(HeadToHeadLine));
        OnPropertyChanged(nameof(HasHeadToHead));
        OnPropertyChanged(nameof(DemosLine));
        OnPropertyChanged(nameof(SampleSizeLine));
        OnPropertyChanged(nameof(DeciderLine));
        OnPropertyChanged(nameof(HasDeciderData));
        OnPropertyChanged(nameof(HasMaps));
        OnPropertyChanged(nameof(HasSelectionWithoutMaps));
        OnPropertyChanged(nameof(OpponentMixLine));
    }

    // A rank was set or cleared: the box refills, and the mix and the dot sizes follow.
    private void OnRanksChanged()
    {
        VrsRank.Refresh();
        HltvRank.Refresh();
        OnPropertyChanged(nameof(RanksProblem));
        OnPropertyChanged(nameof(HasRanksProblem));
        ProjectTimeline();
        RaiseHeader();
    }

    private static string TierNote(OpponentTier tier) => tier switch
    {
        OpponentTier.Top30 => " · vs top-30",
        OpponentTier.Other => " · vs ranked below 30",
        _ => " · opponent rank unknown"
    };

    private OpponentTier TierOf(TeamDemoFacts demo) =>
        _ranks.TierAt(demo.OpponentId, DateOnly.FromDateTime(new DateTime(demo.Ticks)));

    private void ProjectOpponentMix(ScoutingReportData? data)
    {
        if (data is not { Demos.Count: > 0 })
        {
            OpponentMixLine = "";
            return;
        }

        int top = 0, other = 0, unknown = 0;
        foreach (TeamDemoFacts demo in data.Demos)
        {
            switch (TierOf(demo))
            {
                case OpponentTier.Top30: top++; break;
                case OpponentTier.Other: other++; break;
                default: unknown++; break;
            }
        }

        OpponentMixLine = top + other == 0
            ? "no opponent ranks yet: open an opponent and add a rank to see opponent tiers"
            : $"{top} vs top-30 · {other} other · {unknown} unknown";
    }

    private void ProjectTimeline()
    {
        Timeline.Clear();
        ProjectOpponentMix(_data);
        if (_data is not { Demos.Count: > 0 } data)
        {
            return;
        }

        long min = data.Demos.Min(d => d.Ticks);
        long max = data.Demos.Max(d => d.Ticks);
        double span = Math.Max(1, max - min);
        int i = 0;
        foreach (TeamDemoFacts demo in data.Demos.OrderBy(d => d.Ticks))
        {
            double x = 4 + (demo.Ticks - min) / span * (TimelineDotViewModel.Width - 15);
            double y = 1 + i++ % 3 * 5;
            OpponentTier tier = TierOf(demo);
            string result = demo.Won switch { true => "won", false => "lost", _ => "no result" };
            Timeline.Add(new TimelineDotViewModel(x, y, demo.Won == true,
                SelectedMap is { } map && map.Map == demo.Map,
                $"{ScoutingNames.Map(demo.Map)} · {new DateTime(demo.Ticks).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} · {result}{TierNote(tier)}",
                tier == OpponentTier.Top30));
        }
    }

    // The stage, the notices and the players for the map, side and buy in view.
    private void ProjectScope()
    {
        Layers.ScopeSuffix = SelectedMap is { } m ? ScoutingLayerBase.Plural(m.Played, "map") : "";
        Layers.SetScope(_data, SelectedMap?.Map ?? "", Side, Buy);
        OnPropertyChanged(nameof(ScopeLine));
        ProjectNotices();
        ProjectPlayers();
    }

    private void ProjectNotices()
    {
        Notices.Clear();
        _processingNotice = ProcessingNotice();
        if (_processingNotice.Length > 0)
        {
            Notices.Add(_processingNotice);
        }

        if (SelectedMap is { } map && _data is { } data)
        {
            if (map.IsThin)
            {
                Notices.Add(
                    $"{map.MapLabel} has {ScoutingLayerBase.Plural(map.Played, "map")} in the sample. Treat every percentage here as a hint, not a tendency.");
            }

            // The services count their own misses; the layers that read positions themselves (retakes, opening
            // duels, strat trails) count theirs on the shared reader, stale files included.
            int without = Max(Landed(data.Heatmaps)?.DemosWithoutPositions, Landed(data.Openings)?.DemosWithoutPositions,
                Landed(data.PostPlant)?.DemosWithoutPositions, data.Positions.WithoutCount);
            // A stale positions file counts as missing: it was written for older place data.
            if (without > 0 && without >= data.Demos.Count)
            {
                Notices.Add(
                    $"No positions yet for {(data.Demos.Count == 1 ? "this team's demo" : $"any of this team's {data.Demos.Count} demos")} (missing or out of date). Setups, lurks, AWP routes and plant spots need them; records and economy do not. Rebuild the round index from the Situations tab to read them.");
            }
            else if (without > 0)
            {
                Notices.Add(
                    $"Positions were read for {Math.Max(0, data.Demos.Count - without)} of {ScoutingLayerBase.Plural(data.Demos.Count, "demo")}. The rest are missing or out of date: setups, lurks and plant spots use the read ones, records and economy use all. Rebuild the round index from the Situations tab to fill the rest.");
            }
        }

        OnPropertyChanged(nameof(HasNotices));
    }

    private static int Max(params int?[] values) => values.Select(v => v ?? 0).DefaultIfEmpty(0).Max();

    private static T? Landed<T>(Task<T?> load) where T : class => load.IsCompletedSuccessfully ? load.Result : null;

    /// <summary>
    ///     Shows one player's CT setup alone (everyone else fades), switching to CT and inspecting Setups; on the
    ///     isolated player it puts everyone back.
    /// </summary>
    /// <param name="row">The player.</param>
    [RelayCommand]
    private void IsolatePlayer(PlayerRowViewModel? row)
    {
        if (row is null || row.SteamId.Length == 0)
        {
            return;
        }

        string? next = row.IsIsolated ? null : row.SteamId;
        SetIsolated(next);
        if (next is not null && Side != ScoutingSide.Ct)
        {
            Side = ScoutingSide.Ct; // rebuilds every CT layer, Setups with the isolation in it
        }
        else
        {
            RebuildSetups();
        }

        if (next is not null && Layers.Find("setups") is { } chip)
        {
            Layers.Focus(chip);
        }

        ProjectPlayers();
    }

    private void SetIsolated(string? key)
    {
        _isolated = key;
        if (Layers.Find("setups")?.Layer is SetupsLayer setups)
        {
            setups.Isolated = key;
        }
    }

    // Rebuilds only the Setups layer for the scope in view, so an isolate does not rebuild the whole stage.
    private void RebuildSetups()
    {
        if (Side != ScoutingSide.Ct || Layers.Find("setups") is not { Context: { } context } chip)
        {
            return;
        }

        Task<ScoutingLayerResult> build = ScoutingLayerRegistry.Build(chip.Layer, context, CancellationToken.None);
        if (build.IsCompleted)
        {
            Take(build);
            return;
        }

        build.ContinueWith(t => _post(() => Take(t)), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        void Take(Task<ScoutingLayerResult> landed)
        {
            if (_disposed || !ReferenceEquals(chip.Context, context) || !landed.IsCompletedSuccessfully)
            {
                return;
            }

            chip.Apply(landed.Result, context);
            if (chip.IsOn)
            {
                Layers.Focus(chip);
            }
        }
    }

    // Synchronous like the record: SidesOf is already in memory, so a window-size change (or a team change)
    // re-derives without a worker.
    private void ProjectPeriodDiff()
    {
        if (SelectedTeam is not { } row)
        {
            PeriodDiffRecent = null;
            PeriodDiffPrevious = null;
            PeriodDiffNote = "";
            OnPropertyChanged(nameof(HasPeriodDiff));
            return;
        }

        PeriodDiffSet set = PeriodDiffService.Build(_teams, _library, row.Id, WindowSize);
        PeriodDiffRecent = new PeriodDiffRowViewModel(set.Recent);
        PeriodDiffPrevious = new PeriodDiffRowViewModel(set.Previous);
        PeriodDiffNote = ScoutingReportLines.PeriodDiffNoteFor(set);
        OnPropertyChanged(nameof(HasPeriodDiff));
    }

    private void ProjectVetoes() => ProjectVetoes(collect: true);

    private void ProjectVetoes(bool collect)
    {
        Vetoes.Clear();
        if (SelectedTeam is { } row)
        {
            foreach (VetoEntry entry in _vetoes.For(row.Id))
            {
                Vetoes.Add(new VetoRowViewModel(entry));
            }
        }

        OnPropertyChanged(nameof(VetoesAreSessionOnly));
        OnPropertyChanged(nameof(HasVetoes));
        if (collect)
        {
            CollectFindings();
        }
    }

    /// <summary>
    ///     Re-collects every line into the editor in print order: the map pool, every layer over every map and
    ///     side (once the loads are in), the Period Diff and the veto history.
    /// </summary>
    private void CollectFindings()
    {
        if (_disposed)
        {
            return;
        }

        if (SelectedTeam is not { } row)
        {
            Editor.Load(null, "", "", []);
            return;
        }

        List<ScoutingReportFindingSource> sources =
        [
            .. ScoutingReportEditorViewModel.FromMapPool(Maps, DeciderLine),
            .. _layerFindings,
            .. ScoutingReportEditorViewModel.FromPeriodDiff(PeriodDiffRecent, PeriodDiffPrevious, PeriodDiffNote),
            .. ScoutingReportEditorViewModel.FromVetoes(Vetoes)
        ];
        Editor.Load(row.Id, row.Name, SampleSizeLine, sources);
    }
}

/// <summary>One period (last or previous) of a Period Diff, worded for display.</summary>
public sealed class PeriodDiffRowViewModel
{
    public PeriodDiffRowViewModel(PeriodDiffPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        Label = period.Label;
        CountLabel = period.Count == 1 ? "1 demo" : $"{period.Count} demos";
        RecordLabel = period.Wins + period.Losses == 0
            ? "no resolved score"
            : $"{period.Wins}-{period.Losses}"
              + (period.Undetermined > 0 ? $" ({period.Undetermined} undetermined)" : "");
        WinRateLabel = period.WinRate is { } wr
            ? wr.ToString("P0", CultureInfo.InvariantCulture)
            : "—";
        SideLabel = period.HasRoundData
            ? $"CT {period.CtRoundsWon}-{period.TRoundsWon} T ({period.CtRoundShare!.Value:P0} CT)"
            : "no round data";
        RosterLabel = period.DominantRoster is not { } roster
            ? "no roster resolved"
            : period.Rosters.Count > 1
                ? $"{roster.Label} (+{period.Rosters.Count - 1} more)"
                : roster.Label;
        StandInLabel = period.StandInCount == 0 ? "" : $"{period.StandInCount} with a stand-in";
    }

    /// <summary>"last" or "previous".</summary>
    public string Label { get; }

    public string CountLabel { get; }

    public string RecordLabel { get; }

    public string WinRateLabel { get; }

    public string SideLabel { get; }

    /// <summary>The period's dominant roster, or the count of rosters it spans when more than one played it.</summary>
    public string RosterLabel { get; }

    public string StandInLabel { get; }

    public bool HasStandIn => StandInLabel.Length > 0;
}

/// <summary>One user-entered veto step, worded for display.</summary>
public sealed class VetoRowViewModel(VetoEntry entry)
{
    public Guid Id { get; } = entry.Id;

    public int Order { get; } = entry.Order;

    public string Map { get; } = entry.Map;

    public string ActionLabel { get; } = entry.Action == VetoAction.Ban ? "Ban" : "Pick";

    public string ByLabel { get; } = entry.ByOpponent ? "them" : "us";

    public string Note { get; } = entry.Note;
}
