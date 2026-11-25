#region

using CS2DemoKit.Analysis.Output;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.ViewModels.MatchOverview;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Match Overview's cached scoreboard is the demo's <c>player_match</c> facet: a demo the facts pass reached
///     renders its scoreboard and reads FULL with no <see cref="DemoCacheRecord.Scoreboard" />, the facet wins over
///     a scoreboard an earlier version stored, and the record's rows are still read for a demo with no facet.
/// </summary>
[NotInParallel]
public class MatchOverviewFacetScoreboardTests
{
    private const string Demo = "/demos/facet_de_nuke.dem";

    private static readonly string[] CtNames = ["s1mple", "b1t", "electroNic", "Perfecto", "jL"];
    private static readonly string[] TNames = ["ZywOo", "apEX", "flameZ", "mezii", "Spinx"];

    // Parsed and scanned, 13:9 over 22 rounds, with no stored scoreboard.
    private static DemoCacheRecord Scanned()
    {
        DemoCacheRecord r = new()
        {
            Path = Demo,
            Size = 10,
            ModifiedTicks = 20,
            Map = "de_nuke",
            Parse = new TierStamp { Schema = DemoCacheRecord.ParseSchema, ComputedAtTicks = 1 },
            Analysis = new TierStamp { Schema = DemoCacheRecord.AnalysisSchema, ComputedAtTicks = 1 },
            AnalysisState = DemoAnalysisState.Indexed,
            CtScore = 13,
            TScore = 9
        };
        for (int i = 0; i < 22; i++)
        {
            r.Rounds.Add(new CachedRound { Number = i + 1, StartTickFrameClock = 1000 + i * 5000 });
        }

        for (int slot = 0; slot < 10; slot++)
        {
            r.Players.Add(new CachedPlayerInfo
            {
                Slot = slot,
                Name = slot < 5 ? CtNames[slot] : TNames[slot - 5],
                SteamId64 = "7656119800000000" + slot,
                Team = slot < 5 ? 3 : 2
            });
        }

        return r;
    }

    private static FactRow Row(int slot, int team, long kills, long enemyDmg, double hltv, long ctw, long tw,
        long rounds = 22) => new(
        new Dictionary<string, FactValue>
        {
            ["player_slot"] = FactValue.Of(slot),
            ["steam_id"] = FactValue.Of("7656119800000000" + slot),
            ["team"] = FactValue.Of(team)
        },
        new Dictionary<string, FactValue>
        {
            ["rounds"] = FactValue.Of(rounds),
            ["TotalK"] = FactValue.Of(kills),
            ["TotalD"] = FactValue.Of(15L),
            ["TotalA"] = FactValue.Of(4L),
            ["EnemyDmg"] = FactValue.Of(enemyDmg),
            ["HLTV"] = FactValue.Of(hltv),
            ["CTW"] = FactValue.Of(ctw),
            ["TW"] = FactValue.Of(tw)
        });

    private static FactTable Facet(params FactRow[] rows) =>
        new(PlayerMatchFacet.Key, "fp", StampedFacts.Schema, "player_match", ["player_slot", "steam_id", "team"],
            ["rounds", "TotalK", "TotalD", "TotalA", "EnemyDmg", "HLTV", "CTW", "TW"], new Dictionary<string, string>(),
            rows);

    // Ten players: the CT side won 7 rounds on CT and 6 on T, the T side 5 and 4, so the sides split 12:10.
    private static FactTable Match() => Facet(
    [
        .. Enumerable.Range(0, 5).Select(i => Row(i, 3, 20 - i, 2000 - i * 100, 1.234 - i * 0.1, 7, 6)),
        .. Enumerable.Range(5, 5).Select(i => Row(i, 2, 20 - i, 2000 - i * 100, 1.234 - i * 0.1, 5, 4))
    ]);

    [Test]
    public async Task AFacetWithNoStoredScoreboard_RendersTheScoreboard_AndReadsFull()
    {
        MatchOverviewTabViewModel vm = new(computeFullStats: _ => { });
        vm.SetCachedRecord(Scanned(), Match());

        OverviewStatRow top = vm.PlayerStats.First();
        using (Assert.Multiple())
        {
            await Assert.That(vm.PlayerStats.Count).IsEqualTo(10);
            await Assert.That(vm.HasAnalysis).IsTrue();
            await Assert.That(vm.PlayerStatsMessage).IsEqualTo(string.Empty);
            await Assert.That(vm.Completeness).IsEqualTo(OverviewCompleteness.Full);
            await Assert.That(top.Name).IsEqualTo("s1mple").Because("names join the roster by slot");
            await Assert.That(top.Kills).IsEqualTo("20");
            await Assert.That(top.Adr).IsEqualTo("90.9").Because("2000 damage over 22 rounds, at the scoreboard's F1");
            await Assert.That(top.Rating).IsEqualTo("1.23").Because("HLTV at the scoreboard's F2");
            await Assert.That(vm.CtSideWinsDisplay).IsEqualTo("12");
            await Assert.That(vm.TSideWinsDisplay).IsEqualTo("10");
            await Assert.That(vm.RoundCountDisplay).IsEqualTo("22");
        }
    }

    // The facet has a row for every slot: the ghost a reconnect leaves on the old slot (team 0, nothing done)
    // and a spectator (team 1). Neither is a T player; a slot left with real stats on it still counts.
    [Test]
    public async Task GhostAndSpectatorRows_AreNotDrawnAsTPlayers()
    {
        DemoCacheRecord record = Scanned();
        record.Players.Add(new CachedPlayerInfo { Slot = 10, Name = "s1mple", SteamId64 = "76561198000000000" });
        record.Players.Add(new CachedPlayerInfo { Slot = 11, Name = "caster", Team = 1 });
        record.Players.Add(new CachedPlayerInfo { Slot = 12, Name = "leaver", SteamId64 = "76561198000000012" });

        static FactRow Idle(int slot, int team) => new(
            new Dictionary<string, FactValue>
            {
                ["player_slot"] = FactValue.Of(slot),
                ["steam_id"] = FactValue.Of("7656119800000000" + slot),
                ["team"] = FactValue.Of(team)
            },
            new Dictionary<string, FactValue>
            {
                ["rounds"] = FactValue.Of(22L),
                ["TotalK"] = FactValue.Of(0L),
                ["TotalD"] = FactValue.Of(0L),
                ["TotalA"] = FactValue.Of(0L),
                ["EnemyDmg"] = FactValue.Of(0L),
                ["HLTV"] = FactValue.Of(0.5),
                ["CTW"] = FactValue.Of(0L),
                ["TW"] = FactValue.Of(0L)
            });

        FactTable facet = Facet(
        [
            .. Match().Rows,
            Idle(10, 0),
            Idle(11, 1),
            Row(12, 0, 3, 300, 0.4, 0, 0)
        ]);
        MatchOverviewTabViewModel vm = new();
        vm.SetCachedRecord(record, facet);

        using (Assert.Multiple())
        {
            await Assert.That(vm.PlayerStats.Count).IsEqualTo(11);
            await Assert.That(vm.PlayerStats.Count(r => r.Name == "s1mple")).IsEqualTo(1);
            await Assert.That(vm.PlayerStats.Any(r => r.Name == "caster")).IsFalse();
            await Assert.That(vm.PlayerStats.Any(r => r.Name == "leaver")).IsTrue();
        }
    }

    // The live game table formats a missing value as the placeholder and a NaN as "NaN", and a slot that played
    // no round has no ADR to give. Neither is a number above zero, so the row is as idle as one that says 0.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ASpectatorRowWithNoAdr_FromTheLiveTable_IsNotDrawnAsATPlayer(bool nan)
    {
        static MetricRow Row(string name, int team, int kills, object? adr) => new(
            new Dictionary<string, object?> { ["player_name"] = name, ["team"] = team },
            new Dictionary<string, object?>
            {
                ["TotalK"] = kills, ["TotalD"] = 0, ["TotalA"] = 0, ["ADR"] = adr, ["HLTV"] = 1.0
            });

        MetricTable table = new("player_game", ["player_name", "team"], ["TotalK", "TotalD", "TotalA", "ADR", "HLTV"],
        [
            Row("ct", 3, 10, 80.0),
            Row("t", 2, 9, 70.0),
            Row("caster", 1, 0, nan ? double.NaN : null)
        ]);
        MatchOverviewTabViewModel vm = new();
        vm.BeginOpening("a.dem", "Mirage", "Server", "/demos/a.dem");
        vm.SetAnalysis("/demos/a.dem", table, 22);

        await Assert.That(vm.PlayerStats.Select(r => r.Name)).IsEquivalentTo(["ct", "t"]);
    }

    [Test]
    public async Task TheFacet_WinsOverAScoreboardAnEarlierVersionStored()
    {
        DemoCacheRecord record = Scanned();
        record.Scoreboard = [new CachedStatRow { Slot = 0, Team = 3, Kills = 99 }];
        record.CtSideWins = 1;
        record.TSideWins = 1;
        MatchOverviewTabViewModel vm = new();
        vm.SetCachedRecord(record, Match());

        using (Assert.Multiple())
        {
            await Assert.That(vm.PlayerStats.Count).IsEqualTo(10);
            await Assert.That(vm.PlayerStats.First().Kills).IsEqualTo("20");
            await Assert.That(vm.CtSideWinsDisplay).IsEqualTo("12")
                .Because("the side split comes from the same source as the rows");
        }
    }

    [Test]
    public async Task WithNoFacet_TheStoredScoreboardIsRead_AndWithNeitherTheStatsNeedAPass()
    {
        DemoCacheRecord stored = Scanned();
        stored.Scoreboard =
        [
            new CachedStatRow
            {
                Slot = 0,
                Team = 3,
                Kills = 21,
                Adr = 80,
                Rating = 1.1
            }
        ];
        MatchOverviewTabViewModel vm = new(computeFullStats: _ => { });
        vm.SetCachedRecord(stored);
        int storedRows = vm.PlayerStats.Count;
        OverviewCompleteness storedCompleteness = vm.Completeness;

        vm.SetCachedRecord(Scanned(), Facet());

        using (Assert.Multiple())
        {
            await Assert.That(storedRows).IsEqualTo(1);
            await Assert.That(storedCompleteness).IsEqualTo(OverviewCompleteness.Full);
            await Assert.That(vm.PlayerStats.Count).IsEqualTo(0).Because("an empty facet is no scoreboard");
            await Assert.That(vm.Completeness).IsEqualTo(OverviewCompleteness.Indexed);
            await Assert.That(vm.PlayerStatsMessage).IsEqualTo("Player stats need a full analysis pass.");
        }
    }

    [Test]
    public async Task SeedingAnOpen_FillsTheScoreboardFromTheFacet()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MatchOverviewTabViewModel vm = new();
            vm.BeginOpening("facet_de_nuke.dem", "Nuke", "Server", Demo);
            vm.SeedFromCache(Demo, Scanned(), Match());

            using (Assert.Multiple())
            {
                await Assert.That(vm.Mode).IsEqualTo(OverviewMode.Live);
                await Assert.That(vm.PlayerStats.Count).IsEqualTo(10);
            }
        });
    }

    [Test]
    public async Task TheShell_RendersTheFacetWhenTheFactsPassLandsOnTheShownDemo()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = new(null);
            store.Upsert(Scanned());
            OneFacet facts = new(Demo);
            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.Empty(), demoCache: store, facts: facts);
            try
            {
                vm.MatchOverviewTab.SetCachedRecord(store.TryLoadRecord(Demo)!);
                int before = vm.MatchOverviewTab.PlayerStats.Count;

                // The facts pass writes the sidecar, then stamps the record.
                facts.Table = Match();
                store.UpdateExisting(Demo, r => r.CtSideWins = 12);

                using (Assert.Multiple())
                {
                    await Assert.That(before).IsEqualTo(0);
                    await Assert.That(vm.MatchOverviewTab.PlayerStats.Count).IsEqualTo(10);
                    await Assert.That(vm.MatchOverviewTab.Completeness).IsEqualTo(OverviewCompleteness.Full);
                }
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Test]
    public async Task TheProjection_DropsSlotlessRows_AndRefusesASplitTheTeamDisagreesOn()
    {
        FactRow slotless = new(new Dictionary<string, FactValue> { ["team"] = FactValue.Of(3L) },
            new Dictionary<string, FactValue> { ["TotalK"] = FactValue.Of(100L) });
        List<CachedStatRow> rows =
            DemoCacheAnalysisProjector.ProjectScoreboard(Facet(Row(1, 3, 24, 0, 1.005, 7, 6, 0), slotless));
        (int? ct, int? t) = DemoCacheAnalysisProjector.ComputeSideWins(rows);
        (int? splitCt, int? splitT) = DemoCacheAnalysisProjector.ComputeSideWins(
            DemoCacheAnalysisProjector.ProjectScoreboard(Facet(Row(1, 3, 1, 0, 1, 7, 6), Row(2, 3, 1, 0, 1, 5, 6))));

        using (Assert.Multiple())
        {
            await Assert.That(rows.Count).IsEqualTo(1);
            await Assert.That(rows[0].Kills).IsEqualTo(24);
            await Assert.That(rows[0].Adr).IsEqualTo(0.0).Because("no rounds is no average, not a division by zero");
            await Assert.That(ct).IsEqualTo(7);
            await Assert.That(t).IsEqualTo(6);
            await Assert.That(splitCt).IsNull();
            await Assert.That(splitT).IsNull();
            await Assert.That(DemoCacheAnalysisProjector.Rounds(Match())).IsEqualTo(22);
            await Assert.That(DemoCacheAnalysisProjector.Rounds(Facet())).IsEqualTo(0);
        }
    }

    private sealed class OneFacet(string path) : IAnalysisFacts
    {
        public FactTable? Table { get; set; }

        public IReadOnlyList<FactKey> Declared => [PlayerMatchFacet.Key];

        public IRoundFacts RoundFacts => throw new NotSupportedException();

        public FactStatus Status(string demoPath, FactKey key) =>
            TryGet(demoPath, key) is null ? FactStatus.Absent : FactStatus.Current;

        public bool IsCurrent(string demoPath, FactKey key) => Status(demoPath, key) == FactStatus.Current;

        public FactTable? TryGet(string demoPath, FactKey key) =>
            key == PlayerMatchFacet.Key && string.Equals(demoPath, path, StringComparison.Ordinal) ? Table : null;

        public IReadOnlyList<LibraryHighlight> Highlights(string demoPath) => [];
    }
}
