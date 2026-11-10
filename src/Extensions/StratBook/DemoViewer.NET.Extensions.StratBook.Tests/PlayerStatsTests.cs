#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Playback2D.Pipeline.Review;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Review;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The players table: HLTV pooled as a rounds-weighted mean, ADR and KAST from summed components, opening duels
///     and clutch attempts from Round Facts alive counts, the "n of m demos" and empty states while the facts are
///     missing, the role suggestions and their evidence, the IGL kept in the notes store, and the player link
///     opening the Player Overview narrowed to the team's demos.
/// </summary>
[NotInParallel]
public class PlayerStatsTests
{
    private const int Rate = 64;
    private const int Base = 10000;
    private const int RoundLength = 100 * Rate;
    internal const string DemoOne = "/d/ps1.dem";
    internal const string DemoTwo = "/d/ps2.dem";

    internal static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly int[] _teamASlots = [0, 1, 2, 3, 4];
    private static readonly int[] _teamBSlots = [5, 6, 7, 8, 9];

    internal static string Id(int n) => $"7656{n:D4}";

    // Slot s of team B (5..9) is account Id(s + 6): p5 is 76560011.
    private static string IdOfSlot(int slot) => Id(slot < 5 ? slot + 1 : slot + 6);

    private static int FreezeEnd(int round) => Base + round * RoundLength;

    private static KillStep Kill(int round, int seconds, int attacker, int victim, int ct, int t) => new()
    {
        Tick = FreezeEnd(round) + seconds * Rate,
        AttackerSlot = attacker,
        VictimSlot = victim,
        VictimSide = victim < 5 ? 2 : 3,
        CtAlive = ct,
        TAlive = t
    };

    private static RoundFacts Round(int number, int winner, params KillStep[] kills) => new()
    {
        Number = number,
        IsLive = true,
        FreezeEndTick = FreezeEnd(number),
        EndTick = FreezeEnd(number) + 90 * Rate,
        WinnerSide = winner,
        Ct = new SideFacts { Side = 3, Slots = _teamBSlots, BuyType = BuyType.Full },
        T = new SideFacts { Side = 2, Slots = _teamASlots, BuyType = BuyType.Full },
        Kills = [.. kills]
    };

    // Demo one, round 1: p5 (CT) opens on p0, then the T side kills p6..p9 and leaves p5 alone against four: a
    // 1v4 the CT side loses. Round 2: p0 kills p6 first, so p6 loses the CT opening; CT wins. Demo two, round 1:
    // p5 opens on p1 and CT wins.
    internal static RoundFactsRows RowsOne() => new()
    {
        Schema = RoundFactsRecords.Schema,
        Clock = new RoundFactsClock { TickRate = Rate },
        Rounds =
        [
            Round(1, 2, Kill(1, 10, 5, 0, 5, 4), Kill(1, 20, 1, 6, 4, 4), Kill(1, 21, 1, 7, 3, 4),
                Kill(1, 22, 2, 8, 2, 4), Kill(1, 23, 2, 9, 1, 4)),
            Round(2, 3, Kill(2, 10, 0, 6, 4, 5))
        ]
    };

    internal static RoundFactsRows RowsTwo() => new()
    {
        Schema = RoundFactsRecords.Schema,
        Clock = new RoundFactsClock { TickRate = Rate },
        Rounds = [Round(1, 3, Kill(1, 10, 5, 1, 5, 4))]
    };

    internal static DemoCacheRecord Record(string path, int day, RoundFactsRows rows, Func<int, string>? name = null)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = "de_mirage",
            CtScore = 13,
            TScore = 5,
            CtSideWins = 13,
            TSideWins = 5
        };
        record.SetRoundFacts(rows);
        for (int slot = 0; slot < 10; slot++)
        {
            record.Players.Add(new CachedPlayerInfo
            {
                Slot = slot, Name = name?.Invoke(slot) ?? $"p{slot}", SteamId64 = IdOfSlot(slot), Team = slot < 5 ? 2 : 3
            });
        }

        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    internal static FactRow Row(string steamId, double rounds, double hltv, double damage, double kast, int kills,
        int deaths, int awp, int team = 3) =>
        new(new Dictionary<string, FactValue>
            {
                ["steam_id"] = FactValue.Of(steamId),
                ["team"] = FactValue.Of((long)team)
            },
            new Dictionary<string, FactValue>
            {
                ["rounds"] = FactValue.Of(rounds),
                ["HLTV"] = FactValue.Of(hltv),
                ["EnemyDmg"] = FactValue.Of(damage),
                ["KAST%"] = FactValue.Of(kast),
                ["TotalK"] = FactValue.Of((long)kills),
                ["TotalD"] = FactValue.Of((long)deaths),
                ["AWP"] = FactValue.Of((long)awp),
                ["Shots"] = FactValue.Of(100.0)
            });

    internal static FactTable Table(params FactRow[] rows) => new(PlayerStatsService.PlayerMatch, "fp", 1,
        "player_match", ["steam_id", "team"], ["rounds", "HLTV", "EnemyDmg", "KAST%", "TotalK", "TotalD", "AWP"],
        new Dictionary<string, string>(), rows);

    private static DemoCacheStore Cache()
    {
        DemoCacheStore cache = new(null);
        using (cache.BeginBatch())
        {
            cache.Upsert(Record(DemoOne, 0, RowsOne()));
            cache.Upsert(Record(DemoTwo, 1, RowsTwo()));
        }

        return cache;
    }

    private static TeamDemoFacts[] Demos() =>
        [new TeamDemoFacts(DemoOne, "de_mirage", 2, true, null), new TeamDemoFacts(DemoTwo, "de_mirage", 1, true, null)];

    // ---- The numbers ----

    [Test]
    public async Task Hltv_PoolsAsARoundsWeightedMean_AndAdrAndKast_FromSummedComponents()
    {
        DemoCacheStore cache = Cache();
        FakeFacts facts = new(cache.RoundFacts());
        facts.Set(DemoOne, FactStatus.Current, Table(Row(Id(11), 20, 1.20, 1600, 75, 18, 12, 10)));
        facts.Set(DemoTwo, FactStatus.Stale, Table(Row(Id(11), 30, 0.90, 2100, 60, 20, 25, 4)));

        PlayerStatsSet set = PlayerStatsService.Build(cache.Library(), facts, cache.RoundFacts(), Demos(), [Id(11)]);
        PlayerStatLine line = set.For("de_mirage", Id(11))!;

        using (Assert.Multiple())
        {
            await Assert.That(line.Rating!.Value).IsEqualTo(1.02).Within(1e-9)
                .Because("(1.20 x 20 + 0.90 x 30) / 50, never the plain mean 1.05");
            await Assert.That(line.Adr!.Value).IsEqualTo(74).Within(1e-9).Because("3700 damage over 50 rounds");
            await Assert.That(line.Kast!.Value).IsEqualTo(0.66).Within(1e-9).Because("(15 + 18) KAST rounds of 50");
            await Assert.That(line.KillDeath!.Value).IsEqualTo(38 / 37.0).Within(1e-9);
            await Assert.That(line.WithFacts).IsEqualTo(2).Because("a stale table is still the last one written");
            await Assert.That(line.Pending).IsEqualTo(0);
            await Assert.That(set.TeamAwpKills("de_mirage")).IsEqualTo(14);
            await Assert.That(set.For(PlayerStatsSet.AllMaps, Id(11))!.Rating!.Value).IsEqualTo(1.02).Within(1e-9);
        }
    }

    [Test]
    public async Task TwoSlotsOfOneAccount_MergeLessTheRepeatedConstant_AndAGhostRowIsLeftOut()
    {
        FactRow ghost = new(
            new Dictionary<string, FactValue> { ["steam_id"] = FactValue.Of(Id(11)), ["team"] = FactValue.Of(0L) },
            new Dictionary<string, FactValue> { ["rounds"] = FactValue.Of(20.0), ["HLTV"] = FactValue.Of(0.5) });
        IReadOnlyDictionary<string, double> one = PlayerStatsService.Merge([Row(Id(11), 20, 1.2, 1600, 75, 18, 12, 0), ghost], Id(11))!;
        IReadOnlyDictionary<string, double> two = PlayerStatsService.Merge(
            [Row(Id(11), 20, 0.6, 800, 40, 9, 6, 0), Row(Id(11), 20, 0.6, 800, 40, 9, 6, 0)], Id(11))!;

        using (Assert.Multiple())
        {
            await Assert.That(one["HLTV"]).IsEqualTo(1.2).Because("a row on no side with no activity is a reconnect ghost");
            await Assert.That(two["rounds"]).IsEqualTo(20).Because("rounds is the match's on every slot");
            await Assert.That(two["EnemyDmg"]).IsEqualTo(1600);
            await Assert.That(two["KAST%"]).IsEqualTo(80).Within(1e-9);
            await Assert.That(two["HLTV"]).IsEqualTo(1.2 - (0.1587 - 0.2372 * 0.41)).Within(1e-9);
        }
    }

    [Test]
    public async Task OpeningsAndClutchAttempts_ComeFromRoundFacts_WithTheirRounds()
    {
        DemoCacheStore cache = Cache();
        PlayerStatsSet set = PlayerStatsService.Build(cache.Library(), null, cache.RoundFacts(), Demos(), [Id(11), Id(12)]);
        PlayerStatLine p5 = set.For("de_mirage", Id(11))!;
        PlayerStatLine p6 = set.For("de_mirage", Id(12))!;

        using (Assert.Multiple())
        {
            await Assert.That(p5.Openings.Select(o => (o.Side, o.Won))).IsEquivalentTo([(3, true), (3, true)]);
            await Assert.That(p6.Openings.Select(o => (o.Side, o.Won))).IsEquivalentTo([(3, false)]);
            await Assert.That(p5.Clutches.Count).IsEqualTo(1).Because("left alone against four in demo one, round 1");
            await Assert.That(p5.Clutches[0].Versus).IsEqualTo(4);
            await Assert.That(p5.Clutches[0].Won).IsFalse();
            await Assert.That(p5.Clutches[0].Round.FromTick).IsEqualTo(FreezeEnd(1) + 18 * Rate)
                .Because("from five seconds before the kill that left him alone");
            await Assert.That(p5.Clutches[0].Round.ToTick).IsEqualTo(FreezeEnd(1) + 90 * Rate);
            await Assert.That(p6.Clutches).IsEmpty();
            await Assert.That(p5.Rating).IsNull().Because("no facts source: the rates stay empty");
            await Assert.That(p5.KillDeath).IsEqualTo(2.0).Because("Round Facts: two kills, and p5 never dies");
            await Assert.That(p5.KdFromRoundFacts).IsEqualTo(2);
            await Assert.That(set.FactsAvailable).IsFalse();
        }
    }

    [Test]
    public async Task ARecordingCutOffMidRound_KeepsItsOpening_ButNotItsClutchResult()
    {
        // Demo one's round 1 again as round 3, with no winner: the opening stands, the 1v4 is not a lost clutch.
        RoundFactsRows one = RowsOne();
        RoundFactsRows rows = new()
        {
            Schema = one.Schema,
            Clock = one.Clock,
            Rounds =
            [
                .. one.Rounds,
                Round(3, 0, Kill(3, 10, 5, 0, 5, 4), Kill(3, 20, 1, 6, 4, 4), Kill(3, 21, 1, 7, 3, 4),
                    Kill(3, 22, 2, 8, 2, 4), Kill(3, 23, 2, 9, 1, 4))
            ]
        };
        DemoCacheStore cache = new(null);
        using (cache.BeginBatch())
        {
            cache.Upsert(Record(DemoOne, 0, rows));
            cache.Upsert(Record(DemoTwo, 1, RowsTwo()));
        }

        PlayerStatLine p5 = PlayerStatsService.Build(cache.Library(), null, cache.RoundFacts(), Demos(), [Id(11)])
            .For("de_mirage", Id(11))!;

        using (Assert.Multiple())
        {
            await Assert.That(p5.Openings.Count).IsEqualTo(3);
            await Assert.That(p5.Clutches.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AClutch_IsTheLastAliveWithAnEnemyStanding_NotATradeToZero()
    {
        RoundFacts traded = Round(1, 3, Kill(1, 5, 0, 6, 4, 5), Kill(1, 6, 0, 7, 3, 5), Kill(1, 7, 0, 8, 2, 5),
            Kill(1, 8, 9, 0, 2, 4), Kill(1, 9, 1, 9, 1, 4), Kill(1, 30, 5, 1, 1, 3), Kill(1, 31, 5, 2, 1, 2),
            Kill(1, 32, 5, 3, 1, 1), Kill(1, 33, 5, 4, 1, 0));
        RoundFacts wiped = Round(2, 2, Kill(2, 5, 0, 5, 4, 5), Kill(2, 6, 0, 6, 3, 5), Kill(2, 7, 0, 7, 2, 5),
            Kill(2, 8, 0, 8, 1, 0));

        using (Assert.Multiple())
        {
            await Assert.That(PlayerStatsService.Clutch(traded, 3)).IsEqualTo((5, FreezeEnd(1) + 9 * Rate, 4));
            await Assert.That(PlayerStatsService.Clutch(traded, 2)).IsEqualTo((4, FreezeEnd(1) + 32 * Rate, 1))
                .Because("p4 is left alone against p5: a 1v1 the T side loses");
            await Assert.That(PlayerStatsService.Clutch(wiped, 3)).IsNull()
                .Because("the last CT was left alone only as the T side hit zero");
        }
    }

    [Test]
    public async Task MissingFacts_CountTheDemosStillToCompute_AndAFailedRulesetSaysSo()
    {
        DemoCacheStore cache = Cache();
        FakeFacts facts = new(cache.RoundFacts());
        facts.Set(DemoOne, FactStatus.Current, Table(Row(Id(11), 20, 1.20, 1600, 75, 18, 12, 10)));
        facts.Set(DemoTwo, FactStatus.Absent, null);
        PlayerStatLine partial = PlayerStatsService.Build(cache.Library(), facts, cache.RoundFacts(), Demos(), [Id(11)])
            .For("de_mirage", Id(11))!;

        facts.Set(DemoOne, FactStatus.Failed, null);
        PlayerStatLine none = PlayerStatsService.Build(cache.Library(), facts, cache.RoundFacts(), Demos(), [Id(11)])
            .For("de_mirage", Id(11))!;

        using (Assert.Multiple())
        {
            await Assert.That((partial.Demos, partial.WithFacts, partial.Pending)).IsEqualTo((2, 1, 1));
            await Assert.That(partial.Rating!.Value).IsEqualTo(1.2).Within(1e-9).Because("the one demo that has them");
            await Assert.That(partial.KillDeath!.Value).IsEqualTo(19 / 12.0).Within(1e-9)
                .Because("the facts' 18-12 plus the Round Facts kill on the demo without them");
            await Assert.That(partial.KdFromRoundFacts).IsEqualTo(1);
            await Assert.That((none.WithFacts, none.Pending, none.Failed)).IsEqualTo((0, 1, 1));
            await Assert.That(none.Rating).IsNull();
            await Assert.That(none.Adr).IsNull();
        }
    }

    // ---- Roles ----

    [Test]
    public async Task Roles_AreSuggestedOncePerRole_WithTheirEvidence_AndTheIglIsAppended()
    {
        PlayerRoleEvidence[] players =
        [
            new("a", 41, 2, 0, "", 0, "Mid", 5, 10),
            new("b", 10, 15, 7, "A", 1, "BombsiteA", 2, 10),
            new("c", 0, 4, 0, "", 9, "Connector", 4, 10),
            new("d", 2, 1, 0, "", 0, "BombsiteB", 9, 11),
            new("e", 3, 2, 0, "", 0, "Mid", 4, 10)
        ];
        IReadOnlyDictionary<string, PlayerRoleSuggestion> roles =
            PlayerStatsRoles.Suggest(players, new PlayerRoleTotals(100, 44, 19), igl: "e");
        IReadOnlyDictionary<string, PlayerRoleSuggestion> nothing = PlayerStatsRoles.Suggest(
            [new PlayerRoleEvidence("a", 0, 0, 0, "", 0, null, 0, 0)], new PlayerRoleTotals(0, 0, 0), null);

        using (Assert.Multiple())
        {
            await Assert.That(roles["a"]).IsEqualTo(new PlayerRoleSuggestion("AWP", "41% of AWP kills", "support"));
            await Assert.That(roles["b"]).IsEqualTo(new PlayerRoleSuggestion("Entry", "34% of T openings", "entry A"));
            await Assert.That(roles["c"]).IsEqualTo(new PlayerRoleSuggestion("Lurk", "lurked 47% of T rounds", "lurk"));
            await Assert.That(roles["d"]).IsEqualTo(new PlayerRoleSuggestion("Anchor", "B anchor in 82% of setups", "support"));
            await Assert.That(roles["e"]).IsEqualTo(new PlayerRoleSuggestion("Support · IGL", "IGL set by you", "support"));
            await Assert.That(nothing["a"].Role).IsEqualTo("—").Because("no evidence at all suggests nothing");
        }
    }

    [Test]
    public async Task TheIgl_PersistsInTheNotesStore_AndClears()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-scouting-igl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, ScoutingReportNotesStore.FileName);
            Guid team = Guid.NewGuid();
            ScoutingReportNotesStore store = new(StoredFile.At(file));
            int changes = 0;
            store.Changed += () => changes++;
            store.SetIgl(team, " 76560012 ");
            store.SetIgl(team, "76560012");
            string? reloaded = new ScoutingReportNotesStore(StoredFile.At(file)).For(team).Igl;
            store.SetIgl(team, null);
            string? cleared = new ScoutingReportNotesStore(StoredFile.At(file)).For(team).Igl;

            using (Assert.Multiple())
            {
                await Assert.That(reloaded).IsEqualTo("76560012");
                await Assert.That(cleared).IsNull();
                await Assert.That(changes).IsEqualTo(2).Because("setting the same IGL again changes nothing");
                await Assert.That(await File.ReadAllTextAsync(file)).Contains("\"schemaVersion\": 2")
                    .Because("the field is additive: no schema bump");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ---- Through the tab ----

    [Test]
    public async Task TheTable_ShowsTheNumbers_CountsPartialFacts_AndEveryLinkOpensItsScope()
    {
        DemoCacheStore cache = Cache();
        TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: _inline);
        await teams.StartAsync();
        cache.Upsert(Record(DemoOne, 0, RowsOne()));
        await teams.Idle;
        Guid teamB = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(11)) == true)).Id;

        FakeFacts facts = new(cache.RoundFacts());
        facts.Set(DemoOne, FactStatus.Current, Table(Row(Id(11), 20, 1.20, 1600, 75, 18, 12, 10),
            Row(Id(12), 20, 0.90, 1200, 60, 10, 15, 0)));
        facts.Set(DemoTwo, FactStatus.Absent, null);
        List<(ulong Id, PlayerOverviewScope? Scope)> opened = [];
        ReviewQueue queue = new(null);
        ScoutingReportNotesStore notes = new(null);
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), false,
            null, new ExtensionReviewQueue(queue), _ => true, () => new SetupHeatmapRenderer(_ => null), posted.Post,
            notes: notes, runSection: _ => _inline, roundFacts: cache.RoundFacts(), analysisFacts: facts,
            openPlayerOverview: (id, scope) =>
            {
                opened.Add((id, scope));
                return true;
            });
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamB);
        await ScoutingReportTestKit.Settle(vm, posted);

        PlayerRowViewModel p5 = vm.Players.Single(p => p.Name == "p5");
        PlayerRowViewModel p6 = vm.Players.Single(p => p.Name == "p6");
        using (Assert.Multiple())
        {
            await Assert.That(p5.Rating.Label).IsEqualTo("1.20");
            await Assert.That(p5.Rating.Detail).IsEqualTo("1 of 2 demos");
            await Assert.That(p5.KillDeath.Label).IsEqualTo("1.58").Because("18-12 from the facts plus 1-0 from Round Facts");
            await Assert.That(p5.KillDeath.ToolTip).Contains("AWP: 10 kills, 100% of the team's");
            await Assert.That(p5.Adr.Label).IsEqualTo("80.0");
            await Assert.That(p5.Kast.Label).IsEqualTo("75%");
            await Assert.That(p5.Opening.Label).IsEqualTo("2–0");
            await Assert.That(p5.Opening.ToolTip).Contains("CT 2–0, T 0–0");
            await Assert.That(p5.Clutch.Label).IsEqualTo("0 of 1");
            await Assert.That(p5.Clutch.ToolTip).Contains("1v4 0 of 1");
            await Assert.That(p5.Role).IsEqualTo("AWP");
            await Assert.That(p5.RoleNote).IsEqualTo("100% of AWP kills");
            await Assert.That(p6.Opening.Label).IsEqualTo("0–1");
            await Assert.That(vm.PlayersStatusLine).StartsWith("Player stats cover 1 of 2 demos on de_mirage.");
            await Assert.That(vm.PlayersStatusLine).Contains("still to compute");
        }

        p5.OpenOverviewCommand!.Execute(null);
        p5.Rating.Command!.Execute(null);
        p5.Opening.Command!.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(opened.Select(o => o.Id)).IsEquivalentTo([76560011UL, 76560011UL]);
            await Assert.That(opened[0].Scope!.Label).IsEqualTo($"with {vm.SelectedTeam!.Name}");
            await Assert.That(opened[0].Scope!.Demos).IsEquivalentTo([DemoOne, DemoTwo], CollectionOrdering.Any);
            await Assert.That(opened[1].Scope!.Label).IsEqualTo($"with {vm.SelectedTeam!.Name} on de_mirage");
            await Assert.That(queue.Clips.Count).IsEqualTo(2).Because("p5's two opening duels");
            await Assert.That(vm.ReviewLine).IsEqualTo("2 rounds sent to Review");
        }

        // The IGL pick is saved with the team's notes and marks the row.
        vm.SelectedIgl = vm.IglOptions.Single(o => o.SteamId == Id(12));
        PlayerRowViewModel igl = vm.Players.Single(p => p.Name == "p6");
        using (Assert.Multiple())
        {
            await Assert.That(notes.For(teamB).Igl).IsEqualTo(Id(12));
            await Assert.That(igl.Role).IsEqualTo("Support · IGL");
            await Assert.That(igl.RoleNote).IsEqualTo("IGL set by you");
        }

        // All maps pools the same two demos here; the title says so.
        vm.TogglePlayersScopeCommand.Execute(null);
        await Assert.That(vm.PlayersTitle).IsEqualTo("Players · all maps");
        await Assert.That(vm.Players.Single(p => p.Name == "p5").Rating.Label).IsEqualTo("1.20");

        // The second demo's facts land: the numbers reload, and the partial count is gone.
        facts.Set(DemoTwo, FactStatus.Current, Table(Row(Id(11), 30, 0.90, 2100, 60, 20, 25, 4)));
        cache.Upsert(Record(DemoTwo, 1, RowsTwo()));
        await ScoutingReportTestKit.Settle(vm, posted);
        PlayerRowViewModel landed = vm.Players.Single(p => p.Name == "p5");
        using (Assert.Multiple())
        {
            await Assert.That(landed.Rating.Label).IsEqualTo("1.02");
            await Assert.That(landed.Rating.HasDetail).IsFalse();
            await Assert.That(vm.PlayersStatusLine).IsEqualTo("");
        }

        teams.Dispose();
    }

    [Test]
    public async Task NoFactsAtAll_ShowsADash_WithWhatWouldFillIt()
    {
        DemoCacheStore cache = Cache();
        TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: _inline);
        await teams.StartAsync();
        cache.Upsert(Record(DemoOne, 0, RowsOne()));
        await teams.Idle;
        Guid teamB = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(11)) == true)).Id;
        FakeFacts facts = new(cache.RoundFacts());
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), false,
            null, null, _ => true, () => new SetupHeatmapRenderer(_ => null), posted.Post,
            runSection: _ => _inline, roundFacts: cache.RoundFacts(), analysisFacts: facts);
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamB);
        await ScoutingReportTestKit.Settle(vm, posted);
        PlayerRowViewModel p5 = vm.Players.Single(p => p.Name == "p5");

        using (Assert.Multiple())
        {
            await Assert.That(p5.Rating.Label).IsEqualTo("—");
            await Assert.That(p5.Rating.ToolTip).Contains("not computed yet for 2 demos");
            await Assert.That(p5.Rating.CanOpen).IsFalse();
            await Assert.That(p5.Rating.Detail).IsEqualTo("0 of 2 demos");
            await Assert.That(p5.KillDeath.Label).IsEqualTo("2.00").Because("Round Facts fill K/D meanwhile");
            await Assert.That(p5.OpenOverviewCommand).IsNull().Because("this host has no Player Overview");
            await Assert.That(vm.PlayersStatusLine).StartsWith("Player stats cover 0 of 2 demos");
        }

        teams.Dispose();
    }

    internal sealed class FakeFacts(IRoundFacts roundFacts) : IAnalysisFacts
    {
        private readonly Dictionary<string, (FactStatus Status, FactTable? Table)> _demos =
            new(StringComparer.OrdinalIgnoreCase);

        public void Set(string path, FactStatus status, FactTable? table)
        {
            lock (_demos)
            {
                _demos[path] = (status, table);
            }
        }

        public IReadOnlyList<FactKey> Declared => [PlayerStatsService.PlayerMatch];

        public FactStatus Status(string demoPath, FactKey key)
        {
            lock (_demos)
            {
                return _demos.TryGetValue(demoPath, out (FactStatus Status, FactTable? Table) d) ? d.Status : FactStatus.Absent;
            }
        }

        public bool IsCurrent(string demoPath, FactKey key) => Status(demoPath, key) == FactStatus.Current;

        public FactTable? TryGet(string demoPath, FactKey key)
        {
            lock (_demos)
            {
                return _demos.TryGetValue(demoPath, out (FactStatus Status, FactTable? Table) d) ? d.Table : null;
            }
        }

        public IReadOnlyList<LibraryHighlight> Highlights(string demoPath) => [];

        public IRoundFacts RoundFacts => roundFacts;
    }
}

[NotInParallel]
[Category("Render")]
public class PlayerStatsRenderTests
{
    [Test]
    [Arguments(1440)]
    [Arguments(1280)]
    public async Task TheTable_Renders_WithNoNumberOrNameClipped(int width)
    {
        List<string> clipped = [];
        int rows = 0;
        await HeadlessSession.RunOnUi(async () =>
        {
            string[] names = ["", "", "", "", "", "ropz-the-anchor", "broky", "rain", "frozen", "karrigan"];
            DemoCacheStore cache = new(null);
            TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: PlayerStatsTests._inline);
            await teams.StartAsync();
            using (cache.BeginBatch())
            {
                cache.Upsert(PlayerStatsTests.Record(PlayerStatsTests.DemoOne, 0, PlayerStatsTests.RowsOne(), s => s < 5 ? $"p{s}" : names[s]));
                cache.Upsert(PlayerStatsTests.Record(PlayerStatsTests.DemoTwo, 1, PlayerStatsTests.RowsTwo(), s => s < 5 ? $"p{s}" : names[s]));
            }

            await teams.Idle;
            Guid teamB = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(PlayerStatsTests.Id(11)) == true)).Id;
            PlayerStatsTests.FakeFacts facts = new(cache.RoundFacts());
            facts.Set(PlayerStatsTests.DemoOne, FactStatus.Current, PlayerStatsTests.Table(PlayerStatsTests.Row(PlayerStatsTests.Id(11), 20, 1.20, 1600, 75, 18, 12, 10),
                PlayerStatsTests.Row(PlayerStatsTests.Id(12), 20, 0.90, 1200, 60, 10, 15, 0), PlayerStatsTests.Row(PlayerStatsTests.Id(13), 20, 1.31, 2050.5, 85, 22, 9, 0),
                PlayerStatsTests.Row(PlayerStatsTests.Id(14), 20, 0.70, 1100, 55, 8, 17, 0), PlayerStatsTests.Row(PlayerStatsTests.Id(15), 20, 1.05, 1500, 70, 14, 14, 1)));
            using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), false,
                null, null, _ => true, () => new SetupHeatmapRenderer(_ => null), a => a(),
                notes: new ScoutingReportNotesStore(null), runSection: _ => PlayerStatsTests._inline, roundFacts: cache.RoundFacts(),
                analysisFacts: facts, openPlayerOverview: (_, _) => true);
            vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamB);
            await vm.LoadTask;
            vm.SelectedIgl = vm.IglOptions.Single(o => o.SteamId == PlayerStatsTests.Id(15));
            Window window = new()
            {
                Width = width,
                Height = 900,
                Content = new DemoViewer.NET.Extensions.StratBook.Views.ScoutingReport.ScoutingReportTabView { DataContext = vm }
            };
            window.Show();
            UxConsistencyRenderTests.Settle();
            ScrollViewer page = window.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.IsEffectivelyVisible)
                .MaxBy(v => v.Extent.Height)!;
            TextBlock title = window.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == vm.PlayersTitle && t.IsEffectivelyVisible);
            page.Offset = new Vector(0, page.Offset.Y + title.TranslatePoint(default, page)!.Value.Y - 20);
            UxConsistencyRenderTests.Settle();
            if (window.CaptureRenderedFrame() is { } frame)
            {
                frame.Save(Path.Combine(HeadlessSession.ArtifactDir, $"scouting-players-table-{width}.png"),
                    new PngBitmapEncoderOptions());
            }

            ItemsControl table = window.GetVisualDescendants().OfType<ItemsControl>()
                .First(i => ReferenceEquals(i.ItemsSource, vm.Players));
            rows = table.GetVisualDescendants().OfType<Grid>().Count(g => g.DataContext is PlayerRowViewModel
                                                                          && g.ColumnDefinitions.Count == 12);
            foreach (TextBlock text in table.GetVisualDescendants().OfType<TextBlock>()
                         .Where(t => t.IsEffectivelyVisible && t.TextWrapping == Avalonia.Media.TextWrapping.NoWrap
                                                            && !string.IsNullOrEmpty(t.Text)))
            {
                TextBlock probe = new() { Text = text.Text, FontSize = text.FontSize, FontWeight = text.FontWeight };
                probe.Measure(Size.Infinity);
                if (probe.DesiredSize.Width > text.Bounds.Width + 1)
                {
                    clipped.Add($"{text.Text} ({probe.DesiredSize.Width:0} > {text.Bounds.Width:0})");
                }
            }

            window.Close();
            teams.Dispose();
        });

        using (Assert.Multiple())
        {
            await Assert.That(rows).IsEqualTo(5);
            await Assert.That(clipped).IsEmpty();
        }
    }
}
