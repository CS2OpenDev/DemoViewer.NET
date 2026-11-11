#region

using System.Globalization;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Layers;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Extensions.Testing;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using SkiaSharp;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The AWP layer on one team's real pro demos, read the way a visit indexes them (Round Facts, the positions
///     ruleset and the capture collector on one forward pass): the holder from the captures against the holder from
///     the weapon samples alone, the route clusters at several cuts and windows, and the stat rows, printed for
///     tuning; then the CT and T routes to first contact rendered on the map's radar in both palettes. Skips without
///     <c>DEMO_PATH</c>; the demos read are printed.
/// </summary>
[NotInParallel(TestLanes.HeadlessUi)]
[Category("RealDemo")]
public class AwpLayerRealDemoTests
{
    private const string ProDemos = @"D:\Pro Demos";

    // FURIA on Mirage, five maps across three events.
    private static readonly string[] _demos =
    [
        @"blast-open-porto-2026\2026-08-26_blast-open-porto-2026_furia-vs-pain_bo3-m1_mirage.dem",
        @"esl-pro-league-season-24\2026-10-03_esl-pro-league-season-24_furia-vs-betboom_bo3-m1_mirage.dem",
        @"esl-pro-league-season-24\2026-10-04_esl-pro-league-season-24_furia-vs-mouz_bo3-m2_mirage.dem",
        @"esports-world-cup-2026\2026-08-15_esports-world-cup-2026_furia-vs-9z_bo3-m2_mirage.dem",
        @"esports-world-cup-2026\2026-08-19_esports-world-cup-2026_furia-vs-aurora_bo3-m3_mirage.dem"
    ];

    private static readonly string[] _roster = ["fallen", "yuurih", "kscerato", "yekindar", "molodoy"];

    [Test]
    public async Task OneTeamsMirage_HoldersRoutesAndStats_ArePrinted_AndRendered()
    {
        if (Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar) is not { Length: > 0 })
        {
            throw new SkipTestException("DEMO_PATH is not set");
        }

        List<AwpRound> all = [];
        List<AwpRound> samplesOnly = [];
        Dictionary<string, ScoutingColor> colors = new(StringComparer.Ordinal);
        foreach (string relative in _demos)
        {
            string path = Path.Combine(ProDemos, relative);
            if (!File.Exists(path))
            {
                Console.WriteLine($"[awp] missing {path}");
                continue;
            }

            (RoundFactsRows rows, RoundPositionsDocument positions, RoundCapturesDocument captures) = Read(path);
            HashSet<int> team =
            [
                .. positions.Players.Where(p => _roster.Any(n => p.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Slot)
            ];
            foreach (RoundPositionsPlayer p in positions.Players.Where(p => team.Contains(p.Slot)))
            {
                string key = p.SteamId64.ToString(CultureInfo.InvariantCulture);
                if (!colors.ContainsKey(key) && colors.Count < 5)
                {
                    colors[key] = ScoutingColor.Player1 + colors.Count;
                }
            }

            DemoCaptures demoCaptures = Captures(captures);
            IReadOnlyList<TeamRound> rounds = TeamRounds.FromRows(path, null, "de_mirage", rows, n =>
                positions.Round(n) is { } r && r.Pos.SelectMany(s => s).Any(t => team.Contains(t.Slot))
                    ? r.Ct.Any(team.Contains) ? 3 : 2
                    : null);
            Console.WriteLine($"[awp] read {path}: {rounds.Count} rounds, team slots {string.Join(",", team.Order())} "
                + $"({string.Join(", ", positions.Players.Where(p => team.Contains(p.Slot)).Select(p => p.Name))})");
            foreach (TeamRound round in rounds)
            {

                all.Add(AwpRules.Read(round, positions, demoCaptures.Round(round.Number)));
                samplesOnly.Add(AwpRules.Read(round, positions, null));
            }
        }

        if (all.Count == 0)
        {
            throw new SkipTestException("none of the demos is on this machine");
        }

        foreach (ScoutingSide side in new[] { ScoutingSide.Ct, ScoutingSide.T })
        {
            List<AwpRound> rounds = [.. all.Where(r => r.Round.Side == (int)side)];
            List<AwpRound> samples = [.. samplesOnly.Where(r => r.Round.Side == (int)side)];
            List<AwpRound> awp = [.. rounds.Where(r => r.HasAwp)];
            int agree = rounds.Zip(samples).Count(x => x.First.HolderSlot == x.Second.HolderSlot);
            int capturesOnly = rounds.Zip(samples).Count(x => x.First.HasAwp && !x.Second.HasAwp);
            int samplesOnlyCount = rounds.Zip(samples).Count(x => !x.First.HasAwp && x.Second.HasAwp);
            Console.WriteLine($"[awp] {side.Label()}: {rounds.Count} rounds, AWP in {awp.Count} (bought {rounds.Count(r => r.Bought == true)}), "
                + $"holder by captures vs by samples agree {agree}/{rounds.Count}, captures only {capturesOnly}, samples only {samplesOnlyCount}; "
                + $"holders {string.Join(", ", awp.GroupBy(r => r.HolderName).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}; "
                + $"two AWPs {awp.Count(r => r.Holders >= 2)}; opening W-L {awp.Count(r => r.OpeningWon == true)}-{awp.Count(r => r.OpeningWon == false)}; "
                + $"moved after first shot {awp.Count(r => r.MovedAfterShot == true)}/{awp.Count(r => r.MovedAfterShot is not null)}; "
                + $"saved on loss {awp.Count(r => r.SavedOnLoss == true)}/{awp.Count(r => r.SavedOnLoss is not null)}; "
                + $"peeks mid {awp.Count(r => r.PeeksMid == true)}/{awp.Count(r => r.PeeksMid is not null)}; "
                + $"holds start {awp.Count(r => r.HoldsStartSpot == true)}/{awp.Count(r => r.HoldsStartSpot is not null)}");
            foreach (AwpWindow window in Enum.GetValues<AwpWindow>())
            {
                foreach (double cut in new double[] { 300, 400, 550, 700, 900 })
                {
                    AwpRouteSet set = AwpRoutes.Build(awp, window, cut);
                    Console.WriteLine($"[awp]   {side.Label()} {window} cut {cut}: {set.Routes.Count} routes "
                        + $"[{string.Join("; ", set.Routes.Select(r => $"{r.Members.Count} {r.Name}"))}], other {set.Other.Count} of {set.Clustered}");
                }
            }

            ScoutingLayerResult result = AwpLayer.Compose($"de_mirage {side.Label()}", side, ScoutingBuy.All,
                "de_mirage", rounds, AwpWindow.Contact, "first contact", colors);
            Console.WriteLine($"[awp]   read: {string.Join(" | ", result.Reads.Select(r => r.Text))}");
            foreach (ScoutingInspectorSection section in result.Sections)
            {
                Console.WriteLine($"[awp]   {section.Title}: {string.Join("; ", section.Rows.Select(r => $"{r.Badge}{(r.Badge is null ? "" : " ")}{r.Label} = {r.Value} ({r.Rounds.Count} rds{(r.FollowSteamId is { } f ? $", follow {f}" : "")})"))}");
            }

            foreach (string theme in new[] { "light", "dark" })
            {
                string png = await Render(result.Marks, theme, $"p3-awp-de_mirage-{side.Label().ToLowerInvariant()}-{theme}.png");
                Console.WriteLine($"[awp-render] {png}");
            }

            await Assert.That(awp.Count).IsGreaterThan(0);
        }
    }

    private static async Task<string> Render(IReadOnlyList<ScoutingMapMark> marks, string theme, string name)
    {
        SKSizeI size = new(1100, 1100);
        HostedMapAssets.Install();
        using IMapAssetLease? lease = MapAssets.Acquire("de_mirage");
        IMapAsset asset = lease!;
        WorldBounds bounds = asset.RadarBounds;
        ViewportTransform camera =
            ViewportTransform.Fit(size.Width, size.Height, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
        Scene2DFrame frame = new()
        {
            Time = new SceneTime(0, 0, 0, 1.0 / 64, true),
            Markers = [],
            Map = new SceneMapInfo
            {
                MapName = "de_mirage", NetworkedBounds = bounds, ObservedBounds = bounds, Radars = asset.DescribeRadars()
            }
        };
        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack([SceneLayerIds.Radar]);
        ScoutingMarksDocument document = new();
        document.Set(marks, true, 1f);
        compositor.Add(new ScoutingMarksLayer("scouting.awp", document));
        using HeadlessSceneRenderer renderer = new(provider, compositor);
        renderer.Palette = theme == "light" ? ScenePalette.Light : ScenePalette.Dark;
        renderer.Camera = camera;
        renderer.Levels.SetAuthoritativeFloors(asset.Floors);
        renderer.Levels.RadarBinder = asset.CreateRadarBinder();
        SceneTime time = frame.Time;
        byte[] png = renderer.RenderPng(frame, in time, size);
        string path = Path.Combine(HeadlessSession.ArtifactDir, name);
        await File.WriteAllBytesAsync(path, png);
        return path;
    }

    // The production read, as RoundCapturesRealDemoTests makes it: one forward pass, then the builders.
    private static (RoundFactsRows Rows, RoundPositionsDocument Positions, RoundCapturesDocument Captures) Read(
        string path)
    {
        MergedRulesBuild rules = new(() =>
            YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null));
        RulesetDoc factsDoc = rules.EnabledDoc(RoundFactsFingerprint.RulesetId)!;
        List<RulesetDoc> docs = [factsDoc, RoundIndexTable.LoadRuleset(), RoundEventsTable.LoadRuleset()];
        HashSet<string> outputs = [ForwardDemoPass.RoundFactsTable, RoundIndexTable.TableName];
        outputs.UnionWith(RoundEventsTable.Tables);
        CollectorSet set = CollectorSet.Of([
            new RegisteredCollector(RoundCapturesCollector.Contribution, null),
            new RegisteredCollector(RoundIndexRosterCollector.Contribution, null)
        ]);
        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        ForwardDemoResult forward = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, docs, null, outputs, set);
        IReadOnlyList<MetricTable> tables = forward.Run!.ProjectConfiguredOutputs();
        RoundFactsTable factsTable = EngineRoundFactsRowSource.FromTable(
            tables.FirstOrDefault(t => t.Name == EngineRoundFactsRowSource.TableName),
            EngineRoundFactsRowSource.ParametersOf(factsDoc), forward.Rounds);
        RoundFactsRows rows = RoundFactsProjection.Project(forward.Rounds, factsTable);
        RoundCaptureFrames frames = forward.Collected.Get<RoundCaptureFrames>(RoundCapturesCollector.CollectorId)
            ?? throw new InvalidOperationException("the capture collector put nothing");
        MetricTable positionsTable = tables.Single(t => t.Name == RoundIndexTable.TableName);
        List<(IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>)> positionRows =
            [.. positionsTable.Rows.Select(r => (r.Dimensions, r.Values))];
        RoundIndexDemoValues values = new(forward.Demo.TickRate, forward.LastFrameTick, new RoundFactsClock(),
            forward.Demo.MapName)
        {
            Players = forward.Demo.Players,
            Departed = forward.Collected.Get<RoundIndexRosterCollector.Collected>(RoundIndexRosterCollector.CollectorId)
                    ?.Slots
                ?? new Dictionary<int, RosterEntry>()
        };
        RoundIndexBuild build = RoundIndexBuilder.BuildWithPositions(values, rows, RoundIndexOptions.Default,
            PawnPlaceSource.Instance, RoundIndexTable.Samples(positionRows), RoundIndexTable.Extras(positionRows),
            frames.Flags);
        RoundCapturesDocument document = RoundCapturesBuilder.Build(rows, forward.LastFrameTick, forward.Demo.TickRate,
            RoundEventsTable.Read(tables), frames, build.Positions.Fingerprint);
        return (rows, build.Positions, document);
    }

    private static DemoCaptures Captures(RoundCapturesDocument document)
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(RoundIndexTestData.ParsedRecord("/d/real.dem"));
        RoundIndexStore store = new(cache.Data());
        store.Write("/d/real.dem", RoundIndexTestData.Document("de_mirage", document.Fingerprint),
            new RoundPositionsDocument { Fingerprint = document.Fingerprint }, document.Fingerprint, document);
        return new RoundCapturesReader(store, _ => document.Fingerprint).Read("/d/real.dem", "de_mirage", null)!;
    }
}
