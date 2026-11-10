#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Scouting Report's opening duels: the trade window, the positions sample at or before the kill, the
///     round won after the opening, who opens, and the layer's rows each opening their rounds.
/// </summary>
[NotInParallel]
public class OpeningDuelsTests
{
    private const string Demo = "/d/openings.dem";
    private const int Rate = 64;

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly int[] _a = [0, 1, 2, 3, 4];
    private static readonly int[] _b = [5, 6, 7, 8, 9];

    private static string Id(int n) => $"7656{n:D4}";

    private static int Freeze(int number) => number * 10000;

    private static KillStep Kill(int round, double seconds, int attacker, int victim) => new()
    {
        Tick = Freeze(round) + (int)(seconds * Rate),
        AttackerSlot = attacker,
        VictimSlot = victim,
        VictimSide = 0
    };

    private static RoundFacts Round(int number, bool aOnT, int winner, params KillStep[] kills) => new()
    {
        Number = number,
        IsLive = true,
        FreezeEndTick = Freeze(number),
        EndTick = Freeze(number) + 90 * Rate,
        WinnerSide = winner,
        Ct = new SideFacts { Side = 3, Slots = aOnT ? _b : _a, PlayersAtFreezeEnd = 5, BuyType = BuyType.Full },
        T = new SideFacts { Side = 2, Slots = aOnT ? _a : _b, PlayersAtFreezeEnd = 5, BuyType = BuyType.Full },
        Kills = [.. kills]
    };

    // Team A (slots 0-4) is T for rounds 1 to 3 and CT for 4 to 6.
    // T: r1 p0 wins the opening, round won; r2 p0 loses it, traded at 3 s, round lost; r3 p0 loses it, the
    // trade comes at 11 s (too late), round won anyway.
    // CT: r4 p1 dies first (the opening), traded at 2 s, round lost; r5 p3 wins the opening and p4 is the first
    // CT death later; r6 has no kill.
    private static RoundFactsRows Rows() => new()
    {
        Schema = RoundFactsRecords.Schema,
        Clock = new RoundFactsClock { TickRate = Rate },
        Rounds =
        [
            Round(1, true, 2, Kill(1, 10.5, 0, 5), Kill(1, 12, 6, 1)),
            Round(2, true, 3, Kill(2, 8, 5, 0), Kill(2, 11, 1, 5)),
            Round(3, true, 2, Kill(3, 9, 6, 0), Kill(3, 20, 2, 6)),
            Round(4, false, 2, Kill(4, 15, 5, 1), Kill(4, 17, 2, 5)),
            Round(5, false, 3, Kill(5, 6, 3, 7), Kill(5, 30, 8, 4)),
            Round(6, false, 2)
        ]
    };

    // Every slot alive at every step, at x = slot * 100, y = round; even slots in Ramp, odd ones in Hut.
    private static RoundPositionsDocument Positions() => new()
    {
        Fingerprint = "any",
        Demo = new RoundPositionsDemo { StableKey = DemoCacheStore.StableKey(Demo), Sha256 = "abc123" },
        CadenceTicks = Rate,
        Places = ["Ramp", "Hut"],
        Rounds =
        [
            .. Enumerable.Range(1, 6).Select(n => new RoundPositionsRound
            {
                Number = n,
                FreezeEndTick = Freeze(n),
                Ct = n <= 3 ? _b : _a,
                Pos =
                [
                    .. Enumerable.Range(0, 40).Select(step =>
                        Enumerable.Range(0, 10).Select(slot => new RoundPosition(slot, slot * 100 + step, n, 0, slot % 2))
                            .ToList())
                ]
            })
        ]
    };

    private static DemoCacheRecord Record(string path, string map, int day, RoundFactsRows? rows)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = map,
            Sha256 = rows is null ? null : "abc123",
            CtScore = 7,
            TScore = 13,
            CtSideWins = 7,
            TSideWins = 13
        };
        if (rows is not null)
        {
            record.SetRoundFacts(rows);
        }

        for (int slot = 0; slot < 10; slot++)
        {
            record.Players.Add(new CachedPlayerInfo
            {
                Slot = slot,
                Name = $"p{slot}",
                SteamId64 = Id(slot < 5 ? slot + 1 : slot + 6),
                Team = slot < 5 ? 2 : 3
            });
        }

        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    private static async Task<(DemoCacheStore Cache, TeamIdentityService Teams, RoundIndexStore Store, Guid TeamA)>
        Library()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            cache.Upsert(Record(Demo, "de_nuke", 0, Rows()));
            cache.Upsert(Record("/d/other.dem", "de_mirage", 1, null));
        }

        await teams.Idle;
        RoundIndexStore store = new(cache.Data());
        store.WritePositions(Demo, Positions());
        Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(1)) == true)).Id;
        return (cache, teams, store, teamA);
    }

    [Test]
    public async Task ATrade_IsTheKillerDyingToTheVictimsTeammate_WithinTheWindow()
    {
        RoundFacts round = Round(1, true, 2,
            Kill(1, 10, 5, 0), // the death
            Kill(1, 12, 9, 1), // another CT kills a teammate: not the killer dying
            Kill(1, 14.9, 1, 5)); // the killer dies to the victim's teammate inside 5 s
        RoundFacts late = Round(1, true, 2, Kill(1, 10, 5, 0), Kill(1, 15.1, 1, 5));
        RoundFacts byEnemy = Round(1, true, 2, Kill(1, 10, 5, 0), Kill(1, 11, 6, 5)); // a teamkill is no trade
        RoundFacts world = Round(1, true, 2, Kill(1, 10, -1, 0), Kill(1, 11, 1, 5));

        using (Assert.Multiple())
        {
            await Assert.That(OpeningDuelsService.IsTraded(round, round.Kills[0], 2, Rate)).IsTrue();
            await Assert.That(OpeningDuelsService.IsTraded(late, late.Kills[0], 2, Rate)).IsFalse()
                .Because("5.1 s is past the window");
            await Assert.That(OpeningDuelsService.IsTraded(byEnemy, byEnemy.Kills[0], 2, Rate)).IsFalse();
            await Assert.That(OpeningDuelsService.IsTraded(world, world.Kills[0], 2, Rate)).IsFalse()
                .Because("a death with no killer cannot be traded");
        }
    }

    [Test]
    public async Task ThePosition_IsTheSampleAtOrBeforeTheKill_FallingBackOneStep()
    {
        RoundPositionsDocument doc = Positions();
        RoundPositionsRound one = doc.Round(1)!;
        one.Pos[5] = [.. one.Pos[5].Where(p => p.Slot != 3)];
        one.Pos[4] = [.. one.Pos[4].Where(p => p.Slot != 4)];
        one.Pos[5] = [.. one.Pos[5].Where(p => p.Slot != 4)];
        int at = Freeze(1) + (int)(5.9 * Rate);

        OpeningSpot? exact = OpeningDuelsService.SpotAt(doc, 1, 2, at);
        OpeningSpot? onTheStep = OpeningDuelsService.SpotAt(doc, 1, 2, Freeze(1) + 5 * Rate);
        OpeningSpot? fallback = OpeningDuelsService.SpotAt(doc, 1, 3, at);
        OpeningSpot? missing = OpeningDuelsService.SpotAt(doc, 1, 4, at);
        OpeningSpot? past = OpeningDuelsService.SpotAt(doc, 1, 2, Freeze(1) + 500 * Rate);
        using (Assert.Multiple())
        {
            await Assert.That(exact).IsEqualTo(new OpeningSpot(205, 1, 0, "Ramp")).Because("step 5, never step 6");
            await Assert.That(onTheStep!.X).IsEqualTo(205);
            await Assert.That(fallback).IsEqualTo(new OpeningSpot(304, 1, 0, "Hut"));
            await Assert.That(missing).IsNull().Because("only one earlier step is tried");
            await Assert.That(past!.X).IsEqualTo(239).Because("past the last sample reads the last one");
            await Assert.That(OpeningDuelsService.SpotAt(doc, 1, 2, Freeze(1) - 1)).IsNull();
            await Assert.That(OpeningDuelsService.SpotAt(doc, 9, 2, at)).IsNull();
            await Assert.That(OpeningDuelsService.SpotAt(null, 1, 2, at)).IsNull();
        }
    }

    [Test]
    public async Task TheBuild_CountsDuels_RoundWonAfter_Trades_FirstDeaths_AndWhoOpens()
    {
        (DemoCacheStore cache, TeamIdentityService teams, RoundIndexStore store, Guid teamA) = await Library();

        OpeningDuelSet set =
            OpeningDuelsService.Build(teams, cache.Library(), cache.RoundFacts(), new PositionsReader(store, null), teamA);
        OpeningDuelBlock t = set.For("de_nuke", 2)!;
        OpeningDuelBlock ct = set.For("de_nuke", 3)!;
        using (Assert.Multiple())
        {
            await Assert.That(t.Rounds.Count).IsEqualTo(3);
            await Assert.That(t.PositionRounds).IsEqualTo(3);
            await Assert.That(t.Duels.Select(d => d.Won)).IsEquivalentTo([true, false, false]);
            await Assert.That(t.Duels.Select(d => d.RoundWon)).IsEquivalentTo([true, false, true]);
            await Assert.That(t.Duels.Select(d => d.Traded)).IsEquivalentTo([false, true, false]);
            await Assert.That(t.Duels.Select(d => d.Player).Distinct()).IsEquivalentTo(["p0"]);
            await Assert.That(t.Duels[0].Spot).IsEqualTo(new OpeningSpot(10, 1, 0, "Ramp"))
                .Because("the kill at 10.5 s reads the 10 s sample");
            await Assert.That(t.Duels[0].Clip.FromTick).IsEqualTo(Freeze(1) + (int)(10.5 * Rate) - 5 * Rate);
            await Assert.That(t.Duels[2].Rest.ToTick).IsEqualTo(Freeze(3) + 90 * Rate)
                .Because("round won after runs to the round's end");

            await Assert.That(ct.Rounds.Count).IsEqualTo(3);
            await Assert.That(ct.Duels.Select(d => d.Player)).IsEquivalentTo(["p1", "p3"]);
            await Assert.That(ct.Duels.Select(d => d.Won)).IsEquivalentTo([false, true]);
            await Assert.That(ct.FirstDeaths.Select(d => d.Player)).IsEquivalentTo(["p1", "p4"]);
            await Assert.That(ct.FirstDeaths.Select(d => d.IsOpening)).IsEquivalentTo([true, false]);
            await Assert.That(ct.FirstDeaths.Select(d => d.Traded)).IsEquivalentTo([true, false]);
            await Assert.That(ct.FirstDeaths[1].Spot).IsEqualTo(new OpeningSpot(430, 5, 0, "Ramp"));
        }
    }

    [Test]
    public async Task StalePositions_ReadAsAbsent_AndCountTowardTheNotice()
    {
        (DemoCacheStore cache, TeamIdentityService teams, RoundIndexStore store, Guid teamA) = await Library();
        PositionsReader current = new(store, _ => "any");
        PositionsReader stale = new(store, _ => "rebuilt-since");
        PositionsReader none = new(null, _ => "any");

        OpeningDuelSet read = OpeningDuelsService.Build(teams, cache.Library(), cache.RoundFacts(), current, teamA);
        OpeningDuelSet skipped = OpeningDuelsService.Build(teams, cache.Library(), cache.RoundFacts(), stale, teamA);
        RoundPositionsDocument? again = stale.Read(Demo, "de_nuke", "abc123", out PositionsMiss miss);
        none.Read(Demo, "de_nuke", "abc123", out PositionsMiss noIndex);
        using (Assert.Multiple())
        {
            await Assert.That(read.DemosWithoutPositions).IsEqualTo(0);
            await Assert.That(current.WithoutCount).IsEqualTo(0);
            await Assert.That(skipped.DemosWithoutPositions).IsEqualTo(1)
                .Because("a file built under another fingerprint is not the current positions");
            await Assert.That(skipped.For("de_nuke", 2)!.Duels.All(d => d.Spot is null)).IsTrue()
                .Because("no mark is drawn from a stale file");
            await Assert.That(again).IsNull();
            await Assert.That(miss).IsEqualTo(PositionsMiss.Stale);
            await Assert.That(stale.StaleCount).IsEqualTo(1);
            await Assert.That(stale.WithoutCount).IsEqualTo(1).Because("one demo, counted once");
            await Assert.That(noIndex).IsEqualTo(PositionsMiss.NoIndex);
        }
    }

    [Test]
    public async Task TheLayer_DrawsKillsAndDeaths_AndEveryNumberOpensItsRounds()
    {
        (DemoCacheStore cache, TeamIdentityService teams, RoundIndexStore store, Guid teamA) = await Library();
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), false,
            post: posted.Post, runSection: _ => _inline, roundIndex: store, roundFacts: new RoundFactsSource(cache));
        vm.SelectedTeam = vm.Teams.Single(x => x.Id == teamA);
        await ScoutingReportTestKit.Settle(vm, posted);

        IReadOnlyList<ScoutingInspectorRowViewModel> tRows =
            await ScoutingReportTestKit.Inspect(vm, posted, "de_nuke", ScoutingSide.T, "opening");
        ScoutingLayerResult tResult = vm.Layers.Find("opening")!.Result;
        string tRead = vm.Layers.Inspector.Reads.Single().Label;
        bool tOpens = tRows.EveryNumberOpensItsRounds();
        ScoutingInspectorRow traded = tRows.Titled("de_nuke T · opening deaths traded").Row!;
        ScoutingInspectorRow after = tRows.Titled("de_nuke T · round won after winning the opening").Row!;
        ScoutingInspectorRow opener = tRows.Titled("de_nuke T · opening duels p0").Row!;

        IReadOnlyList<ScoutingInspectorRowViewModel> ctRows =
            await ScoutingReportTestKit.Inspect(vm, posted, "de_nuke", ScoutingSide.Ct, "opening");
        ScoutingLayerResult ctResult = vm.Layers.Find("opening")!.Result;
        bool ctOpens = ctRows.EveryNumberOpensItsRounds();
        ScoutingInspectorRow ramp = ctRows.Titled("de_nuke CT · first death Ramp").Row!;
        ScoutingInspectorRow hut = ctRows.Titled("de_nuke CT · first death Hut").Row!;
        ScoutingInspectorRow winPct = ctRows.Titled("de_nuke CT · opening duels won").Row!;

        using (Assert.Multiple())
        {
            await Assert.That(tResult.Marks.Count(m => m is { Shape: ScoutingMarkShape.Dot, Color: ScoutingColor.Neutral }))
                .IsEqualTo(1);
            await Assert.That(tResult.Marks.Count(m => m.Shape == ScoutingMarkShape.Ring)).IsEqualTo(2);
            await Assert.That(tResult.Legend.Select(l => l.Text)).Contains("white dot = opening kill (position ±1 s)");
            await Assert.That(tRead).IsEqualTo("p0 takes 100% of T opening duels; a won opening converts 100% of the time.");
            await Assert.That(tOpens).IsTrue();
            await Assert.That(traded.Value).IsEqualTo("50% · 1 of 2 opening deaths");
            await Assert.That(traded.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([2]);
            await Assert.That(after.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1]);
            await Assert.That(opener.Rounds.Count).IsEqualTo(3);
            await Assert.That(opener.Note).IsEqualTo("won 1 · lost 2");

            await Assert.That(ctResult.Marks.Count(m => m.Shape == ScoutingMarkShape.Ring)).IsEqualTo(2);
            await Assert.That(ctResult.Marks.All(m => m.Color == ScoutingColor.Bad)).IsTrue()
                .Because("CT draws only first deaths: a ring over a faint disc");
            await Assert.That(ctOpens).IsTrue();
            await Assert.That(hut.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([4]);
            await Assert.That(ramp.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([5]);
            await Assert.That(winPct.Value).IsEqualTo("50% · 1 of 2 duels");
            await Assert.That(winPct.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([5]);
        }
    }
}
