#region

using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     The services a layer may read, as the composition root supplied them. Any of the optional ones may be
///     null on a host that does not build it (the browser, a test): a layer that needs it says so as Empty.
/// </summary>
public sealed class ScoutingReportSources
{
    public required TeamIdentityService Teams { get; init; }

    public required IExtensionLibrary Library { get; init; }

    public IRoundFacts? RoundFacts { get; init; }

    public SetupHeatmapService? Heatmaps { get; init; }

    public OpeningTendenciesService? Openings { get; init; }

    public PostPlantService? PostPlant { get; init; }

    public SituationalBehaviourService? Situational { get; init; }

    public GrenadeIndex? Grenades { get; init; }

    public RoundIndexStore? RoundIndex { get; init; }

    /// <summary>
    ///     The fingerprint current positions carry per map, the one the Setup Heatmaps read under; null accepts
    ///     any file (a test host). Layers read positions through <see cref="ScoutingReportData.Positions" />.
    /// </summary>
    public Func<string?, string>? PositionsFingerprint { get; init; }

    /// <summary>
    ///     A map's site regions as Suggested Tags composes them (learned table and the profile's edits); the
    ///     Setups areas use them before the distance rule. Null on a host without Suggested Tags.
    /// </summary>
    public Func<string, SetupSiteRegions?>? SiteRegions { get; init; }

    /// <summary>
    ///     A demo's current fake-then-rotate proposals (demo path, map), or null when Suggested Tags has none current
    ///     for it. Reads a file per demo. Null on a host without Suggested Tags.
    /// </summary>
    public Func<string, string, IReadOnlyList<FakeRound>?>? Fakes { get; init; }

    /// <summary>
    ///     The library-wide Strat Mining patterns, read at build time: null when nothing has been mined yet. The
    ///     source itself is null on a host without a miner (the browser).
    /// </summary>
    public Func<IReadOnlyList<MinedPattern>?>? MinedPatterns { get; init; }

    /// <summary>Seeks 2D playback to a moment; null when the host has none.</summary>
    public Func<ISituationPlayback?>? Playback { get; init; }

    /// <summary>Builds a heatmap renderer for an exported picture.</summary>
    public Func<SetupHeatmapRenderer> Renderer { get; init; } = () => new SetupHeatmapRenderer();
}

/// <summary>
///     One team's shared loads, started once when the team is opened and read by every layer and every scope:
///     the Map Pool Record and the demos (in memory), then the service builds, each on its own worker
///     (<c>runSection</c>) so a slow one does not hold up the rest. A load whose service is absent is null.
/// </summary>
public sealed class ScoutingReportData
{
    private ScoutingReportData(Guid teamId, string teamName, ScoutingReportSources sources)
    {
        TeamId = teamId;
        TeamName = teamName;
        Sources = sources;
        Positions = new PositionsReader(sources.RoundIndex, sources.PositionsFingerprint);
        Captures = new RoundCapturesReader(sources.RoundIndex, sources.PositionsFingerprint);
    }

    public Guid TeamId { get; }

    public string TeamName { get; }

    public ScoutingReportSources Sources { get; }

    /// <summary>
    ///     The one reader every layer takes round positions through: it applies the per-map fingerprint and
    ///     counts the team's demos it could not read, stale ones included, for the partial-positions notice.
    /// </summary>
    public PositionsReader Positions { get; }

    /// <summary>
    ///     The one reader every layer takes round captures through (kills and plants with positions, buys, AWP
    ///     holders, pistol loadouts, first-shot trails, timeouts): the same per-map fingerprint as
    ///     <see cref="Positions" />, a stale part read as absent with the reason, and its own count of demos without.
    /// </summary>
    public RoundCapturesReader Captures { get; }

    public MapPoolRecord Record { get; private init; } = new();

    /// <summary>The team's demos, newest first.</summary>
    public IReadOnlyList<TeamDemoFacts> Demos { get; private init; } = [];

    public Task<SetupHeatmapSet?> Heatmaps { get; private set; } = Task.FromResult<SetupHeatmapSet?>(null);

    public Task<OpeningTendenciesSet?> Openings { get; private set; } = Task.FromResult<OpeningTendenciesSet?>(null);

    public Task<PostPlantSet?> PostPlant { get; private set; } = Task.FromResult<PostPlantSet?>(null);

    public Task<SituationalBehaviourSet?> Situational { get; private set; } =
        Task.FromResult<SituationalBehaviourSet?>(null);

    /// <summary>The team-scoped CT and T round numbers, pistols and plant times per map.</summary>
    public Task<TeamMapFactsSet?> Facts { get; private set; } = Task.FromResult<TeamMapFactsSet?>(null);

    /// <summary>Every load, landed or failed.</summary>
    public Task All { get; private set; } = Task.CompletedTask;

    /// <summary>Why a load failed, by name; read after it landed.</summary>
    public IReadOnlyDictionary<string, string> Problems => _problems;

    private readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);

    /// <summary>Starts a team's loads.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="teamName">Its name, for Review title cards.</param>
    /// <param name="sources">The services.</param>
    /// <param name="runSection">Runs a named load: a processing-queue item in the app, the pool when null.</param>
    public static ScoutingReportData Start(Guid teamId, string teamName, ScoutingReportSources sources,
        Func<string, Func<Action, Task>> runSection)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(runSection);
        ScoutingReportData data = new(teamId, teamName, sources)
        {
            Record = MapPoolRecordService.Build(sources.Teams, sources.Library, teamId),
            Demos = TeamMapFactsService.Demos(sources.Teams, sources.Library, teamId)
        };
        data.Heatmaps = data.Load<SetupHeatmapSet>(runSection, "heatmaps", sources.Heatmaps is { } h ? () => h.Build(teamId) : null);
        data.Openings = data.Load<OpeningTendenciesSet>(runSection, "openings", sources.Openings is { } o ? () => o.Build(teamId) : null);
        data.PostPlant =
            data.Load<PostPlantSet>(runSection, "post-plant", sources.PostPlant is { } p ? () => p.Build(teamId) : null);
        data.Situational =
            data.Load<SituationalBehaviourSet>(runSection, "situational", sources.Situational is { } s ? () => s.Build(teamId) : null);
        data.Facts = data.Load<TeamMapFactsSet>(runSection, "pool",
            sources.RoundFacts is { } rf
                ? () => TeamMapFactsService.Build(sources.Teams, sources.Library, rf, teamId)
                : null);
        data.All = Task.WhenAll(data.Heatmaps, data.Openings, data.PostPlant, data.Situational, data.Facts);
        return data;
    }

    private Task<T?> Load<T>(Func<string, Func<Action, Task>> runSection, string name, Func<T>? build)
        where T : class
    {
        if (build is null)
        {
            return Task.FromResult<T?>(null);
        }

        T? result = null;
        Task run = runSection(name)(() =>
        {
            try
            {
                result = build();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                lock (_problems)
                {
                    _problems[name] = ex.Message;
                }
            }
        });
        // An inline runner (a test, a host without a queue) has finished already: hand back a completed task, so
        // a layer that awaits it lands without a post.
        return run.IsCompleted
            ? Task.FromResult(run.IsCompletedSuccessfully ? result : null)
            : run.ContinueWith(t => t.IsCompletedSuccessfully ? result : null, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The Opening Tendencies block of a map and side, or null.</summary>
    /// <param name="map">The map.</param>
    /// <param name="side">The side.</param>
    public async Task<OpeningTendencies?> OpeningsFor(string map, ScoutingSide side) =>
        (await Openings.ConfigureAwait(false))?.Blocks.FirstOrDefault(b => Same(b.Map, map) && b.Side == (int)side);

    /// <summary>The Post-Plant block of a map and side, or null.</summary>
    /// <param name="map">The map.</param>
    /// <param name="side">The side.</param>
    public async Task<PostPlantBlock?> PostPlantFor(string map, ScoutingSide side) =>
        (await PostPlant.ConfigureAwait(false))?.Blocks.FirstOrDefault(b => Same(b.Map, map) && b.Side == (int)side);

    /// <summary>The Situational Behaviour block of a map and side, or null.</summary>
    /// <param name="map">The map.</param>
    /// <param name="side">The side.</param>
    public async Task<SituationalBehaviourBlock?> SituationalFor(string map, ScoutingSide side) =>
        (await Situational.ConfigureAwait(false))?.Blocks.FirstOrDefault(b =>
            Same(b.Map, map) && b.Side == (int)side);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The scope a layer builds for: the team, map, side and buy, over the team's shared loads.</summary>
public sealed class ScoutingLayerContext
{
    /// <param name="data">The team's loads.</param>
    /// <param name="map">The map.</param>
    /// <param name="side">The side.</param>
    /// <param name="buy">The buy filter.</param>
    /// <param name="window">The AWP trail window; empty elsewhere.</param>
    public ScoutingLayerContext(ScoutingReportData data, string map, ScoutingSide side, ScoutingBuy buy,
        string window = "")
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(map);
        Data = data;
        Map = map;
        Side = side;
        Buy = buy;
        Window = window;
    }

    public ScoutingReportData Data { get; }

    public Guid TeamId => Data.TeamId;

    public string TeamName => Data.TeamName;

    public string Map { get; }

    public ScoutingSide Side { get; }

    public ScoutingBuy Buy { get; }

    public string Window { get; }

    /// <summary>"de_nuke T": how every Review title card of the scope starts.</summary>
    public string Prefix => $"{Map} {Side.Label()}";

    /// <summary>The scope a layer's line is starred under.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="buy">The row's own buy, or null for the layer's (All when it does not split by buy).</param>
    public ScoutingScope Scope(IScoutingLayer layer, ScoutingBuy? buy = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ScoutingBuy filed = buy ?? (layer.UsesBuy ? Buy : ScoutingBuy.All);
        return new ScoutingScope(Map, Side.Label(), layer.Id, filed.ToString(), layer.Id == "awp" ? Window : "");
    }

    /// <summary>Runs a layer's own heavy work on the pool.</summary>
    /// <param name="work">The work.</param>
    public static Task<T> Offload<T>(Func<T> work) => Task.Run(work);
}
