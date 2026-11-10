#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Scouting Report captures on the production read: the demo streamed once by <see cref="ForwardDemoPass" />
///     with Round Facts, both round index rulesets and the capture collector on one rules run, as a visit makes it.
///     Two pro GOTV demos and one Valve matchmaking demo: kills and plants carry positions, an AWP holder is found in
///     most rounds a side bought one, purchases outnumber nothing and stay plausible, the timeouts of a demo known
///     to have them are found, and the positions tuples carry their extras. Skips without <c>DEMO_PATH</c>.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class RoundCapturesRealDemoTests
{
    private const string ProDemos = @"D:\Pro Demos";

    // aurora-vs-9z m1 dust2: the engine-asks probe demo (613 pickups with a cash rise, 464 without; T timeouts at
    // 47125 and 69008). legacy-vs-fut m2 dust2: the demo the plan names for timeouts.
    private static readonly string[] _pro =
    [
        @"esl-pro-league-season-24\2026-10-03_esl-pro-league-season-24_aurora-vs-9z_bo3-m1_dust2.dem",
        @"blast-open-porto-2026\2026-08-27_blast-open-porto-2026_legacy-vs-fut_bo3-m2_dust2.dem"
    ];

    private const string MmDemo =
        @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\replays\match730_003852235135051104394_0855789539_392.dem";

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task TheCaptures_CarryPositions_AwpHolders_Purchases_AndTimeouts(int which)
    {
        string path = Resolve(which);
        Captured read = Read(path, withCaptures: true);
        RoundCapturesDocument document = read.Document!;
        DemoCaptures captures = Captures(document);

        int kills = document.Rounds.Sum(r => r.Kills.Count);
        int killsPlaced = document.Rounds.Sum(r => r.Kills.Count(k => k.Attacker is not null && k.Victim is not null));
        int plants = document.Rounds.Count(r => r.Plant is not null);
        int plantsPlaced = document.Rounds.Count(r => r.Plant?.At is not null);
        int openings = document.Rounds.Count(r => r.Kills.Any(k => k.Opening));
        RoundCaptureFrames frames = read.Frames!;
        int bought = frames.Pickups.Count(p => p.Bought);
        int pickedUp = frames.Pickups.Count - bought;
        int awpRounds = 0, awpFound = 0, doubles = 0, saved = 0;
        foreach (RoundCaptureView round in captures.Rounds)
        {
            foreach (int side in new[] { 2, 3 })
            {
                IReadOnlyList<AwpHolder> holders = round.AwpHolders(side);
                doubles += holders.Count >= 2 ? 1 : 0;
                saved += holders.Count(h => h.Saved);
                if (round.AwpBought(side))
                {
                    awpRounds++;
                    awpFound += holders.Count > 0 ? 1 : 0;
                }
            }
        }

        int trails = document.Rounds.Sum(r => r.FirstShots.Count);
        int fullTrails = document.Rounds.Sum(r => r.FirstShots.Count(t => t.Points.Count == RoundCapturesFold.TrailSeconds + 1));
        int pistolLoadouts = captures.Rounds.Where(r => r.IsPistol).Sum(r => r.PistolLoadouts(2).Count + r.PistolLoadouts(3).Count);
        int extended = read.Positions!.Rounds.SelectMany(r => r.Pos).SelectMany(s => s).Count(t => !t.IsBare);
        int tuples = read.Positions.Rounds.SelectMany(r => r.Pos).Sum(s => s.Count);
        Console.WriteLine($"{Path.GetFileName(path)}: rounds {document.Rounds.Count}, kills {kills} ({killsPlaced} placed), "
            + $"openings {openings}, plants {plants} ({plantsPlaced} placed); pickups {frames.Pickups.Count}: "
            + $"bought {bought}, not {pickedUp}; AWP bought side-rounds {awpRounds}, holder found {awpFound}, "
            + $"doubles {doubles}, saved {saved}; trails {trails} ({fullTrails} full); pistol loadouts {pistolLoadouts}; "
            + $"timeouts {string.Join(", ", document.Timeouts.Select(t => $"{(t.Technical ? "tech" : t.Side == 3 ? "CT" : "T")}@{t.StartTick} {t.CtLeftBefore}/{t.TLeftBefore}->{t.CtLeftAfter}/{t.TLeftAfter} before r{t.BeforeRound}"))}; "
            + $"tuples {tuples}, extended {extended}; captures {document.SerializeUtf8().Length / 1024} KB json; "
            + $"positions {read.Positions.SerializeUtf8().Length / 1024} KB json");

        using (Assert.Multiple())
        {
            await Assert.That(document.Collected).IsTrue();
            await Assert.That(kills).IsGreaterThan(50);
            await Assert.That(killsPlaced).IsGreaterThanOrEqualTo(kills * 95 / 100);
            await Assert.That(openings).IsGreaterThanOrEqualTo(document.Rounds.Count * 9 / 10);
            await Assert.That(plants).IsGreaterThan(0);
            await Assert.That(plantsPlaced).IsEqualTo(plants);
            await Assert.That(bought).IsGreaterThan(document.Rounds.Count * 5).Because("every player buys most rounds");
            await Assert.That(pickedUp).IsGreaterThan(0).Because("the spawn kit is picked up, not bought");
            await Assert.That(awpRounds).IsGreaterThan(0);
            await Assert.That(awpFound).IsGreaterThanOrEqualTo(awpRounds * 9 / 10)
                .Because("a side that bought an AWP has a holder in most rounds");
            await Assert.That(trails).IsGreaterThan(0);
            await Assert.That(extended).IsGreaterThanOrEqualTo(tuples * 95 / 100);
            await Assert.That(read.Positions.Weapons!).Contains("CWeaponAWP");
            if (which < 2)
            {
                await Assert.That(document.Timeouts.Count(t => !t.Technical)).IsGreaterThan(0)
                    .Because("both pro dust2 demos have tactical timeouts");
                await Assert.That(pistolLoadouts).IsGreaterThanOrEqualTo(10);
            }
        }
    }

    /// <summary>
    ///     The Timeouts layer's derivation over the legacy-vs-fut m2 dust2 captures: each team's rounds scoped by
    ///     its slots, and between the two teams at least three tactical timeouts are found.
    /// </summary>
    [Test]
    public async Task TheTimeoutsLayer_FindsTheTacticalTimeouts_OfLegacyVsFut()
    {
        string path = Resolve(1);
        Console.WriteLine($"demo read: {path}");
        Captured read = Read(path, withCaptures: true);
        RoundFactsRows rows = read.Rows!;
        RoundCapturesDocument document = read.Document!;
        RoundFacts first = rows.Rounds.First(r => r.IsLive);
        int total = 0;
        int technical = 0;
        foreach (int[] slots in new[] { first.Ct.Slots, first.T.Slots })
        {
            IReadOnlyList<TeamRound> team = TeamRounds.FromRows(path, null, "de_dust2", rows, n =>
            {
                RoundFacts round = rows.Rounds.First(r => r.Number == n);
                return round.Ct.Slots.Intersect(slots).Any() ? 3 : round.T.Slots.Intersect(slots).Any() ? 2 : null;
            });
            TeamTimeouts found = TimeoutDerivation.Build(team, _ => document.Timeouts);
            total += found.Tactical.Count;
            technical += found.Technical;
            Console.WriteLine($"team of slots {string.Join(",", slots)}: {found.Tactical.Count} tactical, "
                + $"calls {string.Join("/", found.Tactical.Select(t => $"r{t.Round.Number}:{t.Call ?? "-"}"))}");
        }

        Console.WriteLine($"stored {document.Timeouts.Count}, tactical found {total}, technical {technical}");
        await Assert.That(total).IsGreaterThanOrEqualTo(3);
    }

    /// <summary>
    ///     The re-index cost: one forward read with Round Facts alone, with the schema 5 positions ruleset, with the
    ///     extended one, with the event table and with the collector, interleaved and printed. Not asserted: the
    ///     run-to-run spread is about 15 percent.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(2)]
    public async Task TheCost_OfEachAddition_IsPrinted(int which)
    {
        string path = Resolve(which);
        (string Name, Func<Captured> Run)[] arms =
        [
            ("facts", () => Read(path, false, positions: null)),
            ("positions v5", () => Read(path, false, positions: OldPositions())),
            ("positions v6", () => Read(path, false, positions: RoundIndexTable.LoadRuleset())),
            ("+ events", () => Read(path, false, positions: RoundIndexTable.LoadRuleset(), events: true)),
            ("+ collector", () => Read(path, true))
        ];
        Dictionary<string, List<long>> times = arms.ToDictionary(a => a.Name, _ => new List<long>());
        arms[0].Run(); // warm
        for (int pass = 0; pass < 2; pass++)
        {
            foreach ((string name, Func<Captured> run) in arms)
            {
                Stopwatch watch = Stopwatch.StartNew();
                run();
                times[name].Add(watch.ElapsedMilliseconds);
            }
        }

        Console.WriteLine($"{Path.GetFileName(path)}: "
            + string.Join("; ", times.Select(t => $"{t.Key} {string.Join("/", t.Value)} ms")));
        await Assert.That(times.Count).IsEqualTo(5);
    }

    private static string Resolve(int which)
    {
        if (Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar) is not { Length: > 0 })
        {
            throw new SkipTestException("DEMO_PATH is not set");
        }

        string path = which < _pro.Length ? Path.Combine(ProDemos, _pro[which]) : MmDemo;
        return File.Exists(path) ? path : throw new SkipTestException($"missing {path}");
    }

    // The positions ruleset as schema 5 had it: the three extra columns cut out of the shipped YAML.
    private static RulesetDoc OldPositions()
    {
        using StreamReader reader = new(RoundIndexTable.OpenYaml());
        string yaml = string.Join("\n", reader.ReadToEnd().Split('\n')
            .Where(l => !l.Contains("weapon_class") && !l.Contains("armor") && !l.Contains("money")));
        RuleConfigLoadResult read = YamlConfigLoader.LoadDocuments([("roundindex_positions.rules.yaml", yaml)]);
        return read.Rulesets.Single();
    }

    private sealed record Captured(RoundCapturesDocument? Document, RoundCaptureFrames? Frames,
        RoundPositionsDocument? Positions, RoundFactsRows? Rows = null);

    private static Captured Read(string path, bool withCaptures) =>
        Read(path, withCaptures, RoundIndexTable.LoadRuleset(), withCaptures);

    private static Captured Read(string path, bool withCaptures, RulesetDoc? positions, bool events = false)
    {
        MergedRulesBuild rules = new(() =>
            YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null));
        RulesetDoc factsDoc = rules.EnabledDoc(RoundFactsFingerprint.RulesetId)!;
        List<RulesetDoc> docs = [factsDoc];
        HashSet<string> outputs = [ForwardDemoPass.RoundFactsTable];
        if (positions is not null)
        {
            docs.Add(positions);
            outputs.Add(RoundIndexTable.TableName);
        }

        if (events)
        {
            docs.Add(RoundEventsTable.LoadRuleset());
            outputs.UnionWith(RoundEventsTable.Tables);
        }

        CollectorSet set = withCaptures
            ? CollectorSet.Of([
                new RegisteredCollector(RoundCapturesCollector.Contribution, null),
                new RegisteredCollector(RoundIndexRosterCollector.Contribution, null)
            ])
            : CollectorSet.Empty;
        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        ForwardDemoResult forward = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, docs, null, outputs, set);
        if (!withCaptures)
        {
            return new Captured(null, null, null);
        }

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
            forward.Demo.MapName);
        RoundIndexBuild build = RoundIndexBuilder.BuildWithPositions(values, rows, RoundIndexOptions.Default,
            PawnPlaceSource.Instance, RoundIndexTable.Samples(positionRows), RoundIndexTable.Extras(positionRows),
            frames.Flags);
        RoundCapturesDocument document = RoundCapturesBuilder.Build(rows, forward.LastFrameTick, forward.Demo.TickRate,
            RoundEventsTable.Read(tables), frames, build.Positions.Fingerprint);
        return new Captured(document, frames, build.Positions, rows);
    }

    private static DemoCaptures Captures(RoundCapturesDocument document)
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(RoundIndexTestData.ParsedRecord("/d/real.dem"));
        RoundIndexStore store = new(cache.Data());
        store.Write("/d/real.dem", RoundIndexTestData.Document("de_dust2", document.Fingerprint),
            new RoundPositionsDocument { Fingerprint = document.Fingerprint }, document.Fingerprint, document);
        return new RoundCapturesReader(store, _ => document.Fingerprint).Read("/d/real.dem", "de_dust2", null)!;
    }
}
