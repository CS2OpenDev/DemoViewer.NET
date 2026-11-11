#region

using System.Globalization;
using TUnit.Assertions.Enums;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
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
///     The AWP layer over synthetic rounds: the holder from a window of weapon samples or from the captures, never
///     one sample; the holder's path and its window trails; route clustering by complete linkage with its cut, the
///     "other" bucket and the route cap; the stat rows; the marks; the 2D play following the AWPer; and, through the
///     tab, the window pills filing stars under their window, every number opening its rounds, and the pool's AWP
///     bought column.
/// </summary>
[NotInParallel]
public class AwpLayerTests
{
    private const string Demo = "/d/awp.dem";
    private const int Rate = 64;
    private const int Knife = 0;
    private const int Awp = 1;

    private static readonly int[] _a = [0, 1, 2, 3, 4];
    private static readonly int[] _b = [5, 6, 7, 8, 9];

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string Id(int n) => $"7656{n:D4}";

    private static ulong Steam(int n) => ulong.Parse(Id(n), CultureInfo.InvariantCulture);

    private static int Freeze(int number) => number * 10000;

    private static RoundFacts Facts(int number, int winner, BuyType buy = BuyType.Full, params KillStep[] kills) => new()
    {
        Number = number,
        IsLive = true,
        FreezeEndTick = Freeze(number),
        EndTick = Freeze(number) + 90 * Rate,
        WinnerSide = winner,
        Ct = new SideFacts { Side = 3, Slots = _a, PlayersAtFreezeEnd = 5, BuyType = buy },
        T = new SideFacts { Side = 2, Slots = _b, PlayersAtFreezeEnd = 5, BuyType = buy },
        Kills = [.. kills]
    };

    private static KillStep Kill(int round, int seconds, int attacker, int victim) => new()
    {
        Tick = Freeze(round) + seconds * Rate, AttackerSlot = attacker, VictimSlot = victim
    };

    private static TeamRound Team(RoundFacts facts, int side = 3) => new()
    {
        DemoPath = Demo, TickRate = Rate, Map = "de_mirage", Facts = facts, Side = side
    };

    // One step a second for 41 s; every slot at (slot * 10, -500) unless the round's own rule moves it.
    private static RoundPositionsRound Stored(int number, Func<int, int, RoundPosition?> at) => new()
    {
        Number = number,
        FreezeEndTick = Freeze(number),
        Ct = _a,
        Pos =
        [
            .. Enumerable.Range(0, 41).Select(step => Enumerable.Range(0, 10)
                .Select(slot => at(step, slot) ?? new RoundPosition(slot, slot * 10, -500, 0, 0, Knife))
                .Where(p => p.Slot >= 0).ToList())
        ]
    };

    private static RoundPositionsDocument Positions(params RoundPositionsRound[] rounds) => new()
    {
        Fingerprint = "any",
        Demo = new RoundPositionsDemo { StableKey = DemoCacheStore.StableKey(Demo), Sha256 = "abc123" },
        CadenceTicks = Rate,
        Places = ["CTSpawn", "Jungle", "TopofMid", "Connector"],
        Weapons = ["CKnife", "CWeaponAWP"],
        Players = [.. Enumerable.Range(0, 10).Select(s => new RoundPositionsPlayer(s, Steam(s + 1), $"p{s}"))],
        Rounds = [.. rounds]
    };

    // Route A runs +X 100 a second to (1000, 0) by 10 s and holds it in Jungle; route B runs +Y to (0, 1000) in
    // Connector. The holder's weapon flickers: AWP on every third step.
    private static RoundPosition? RouteA(int step, int slot, int holder) => slot == holder
        ? new RoundPosition(slot, Math.Min(step, 10) * 100, 0, 0, step == 0 ? 0 : 1, step % 3 == 0 ? Awp : Knife)
        : null;

    private static RoundPosition? RouteB(int step, int slot, int holder) => slot == holder
        ? new RoundPosition(slot, 0, Math.Min(step, 10) * 100, 0, step == 0 ? 0 : 3, step % 3 == 0 ? Awp : Knife)
        : null;

    private static AwpHolding Holding(int slot, bool bought = false, bool atFreezeEnd = true, int inHand = 500,
        bool savedAlive = false) =>
        new(slot, 3, atFreezeEnd, bought, false, inHand, inHand > 0 ? 1 : null, 1, 0, savedAlive);

    private static CapturedKill CKill(int round, int seconds, int attacker, int victim, int side, string weapon) =>
        new(Freeze(round) + seconds * Rate, attacker, victim, side, weapon, null, false, false, -1, true,
            new CaptureSpot(0, 0, 0, null), new CaptureSpot(0, 0, 0, null));

    private static FirstShotTrail Shot(int round, int slot, int seconds, params (int X, int Y)[] points) =>
        new(slot, Freeze(round) + seconds * Rate,
        [
            .. points.Select((p, i) => new FirstShotPoint(Freeze(round) + (seconds + i) * Rate,
                new CaptureSpot(p.X, p.Y, 0, null), true))
        ]);

    // Team A on CT in all five: r1 pistol, no AWP; r2 and r3 route A by p1 (bought in r2, kept into r3); r4 route
    // B by p1, who dies in the opening, round lost; r5 route B by p2 with p1's second AWP, lost, p2 saves it.
    private static RoundFactsRows Rows() => new()
    {
        Schema = RoundFactsRecords.Schema,
        Clock = new RoundFactsClock { TickRate = Rate },
        Rounds =
        [
            Facts(1, 3, BuyType.Pistol, Kill(1, 20, 0, 5)),
            Facts(2, 3, BuyType.Full, Kill(2, 20, 1, 5)),
            Facts(3, 3, BuyType.Full, Kill(3, 20, 0, 6)),
            Facts(4, 2, BuyType.Full, Kill(4, 20, 5, 1)),
            Facts(5, 2, BuyType.Full, Kill(5, 20, 6, 3))
        ]
    };

    private static RoundPositionsDocument FivePositions() => Positions(
        Stored(1, (_, _) => null),
        Stored(2, (s, slot) => RouteA(s, slot, 1)),
        Stored(3, (s, slot) => RouteA(s, slot, 1)),
        Stored(4, (s, slot) => s > 20 && slot == 1 ? new RoundPosition(-1, 0, 0, 0, 0) : RouteB(s, slot, 1)),
        Stored(5, (s, slot) => RouteB(s, slot, 2)));

    private static RoundCapturesDocument FiveCaptures() => new()
    {
        Fingerprint = "any",
        Demo = new RoundPositionsDemo { StableKey = DemoCacheStore.StableKey(Demo), Sha256 = "abc123" },
        TickRate = Rate,
        Collected = true,
        Rounds =
        [
            new CapturedRound { Number = 1, FreezeEndTick = Freeze(1), Pistol = true },
            new CapturedRound
            {
                Number = 2, FreezeEndTick = Freeze(2), Awp = [Holding(1, bought: true)],
                Kills = [CKill(2, 20, 1, 5, 3, "awp")],
                Purchases = [new CapturedPurchase(Freeze(2) - 10, 1, "awp", 4750, true)],
                FirstShots = [Shot(2, 1, 20, (1000, 0), (1000, 150), (1000, 300), (1000, 300))]
            },
            new CapturedRound { Number = 3, FreezeEndTick = Freeze(3), Awp = [Holding(1)] },
            new CapturedRound
            {
                Number = 4, FreezeEndTick = Freeze(4), Awp = [Holding(1)],
                Kills = [CKill(4, 20, 5, 1, 2, "ak47")],
                FirstShots = [Shot(4, 1, 15, (0, 1000), (0, 1050), (0, 1100), (0, 1100))]
            },
            new CapturedRound
            {
                Number = 5, FreezeEndTick = Freeze(5),
                Awp = [Holding(2, savedAlive: true), Holding(1, inHand: 40)]
            }
        ]
    };

    private static DemoCaptures Read(RoundCapturesDocument captures)
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(RoundIndexTestData.ParsedRecord(Demo, "de_mirage"));
        RoundIndexStore store = new(cache.Data());
        store.Write(Demo, RoundIndexTestData.Document("de_mirage", "any"), FivePositions(), "any", captures);
        return new RoundCapturesReader(store, null).Read(Demo, "de_mirage", null)!;
    }

    private static List<AwpRound> FiveRounds()
    {
        RoundPositionsDocument positions = FivePositions();
        DemoCaptures captures = Read(FiveCaptures());
        return [.. Rows().Rounds.Select(f => AwpRules.Read(Team(f), positions, captures.Round(f.Number)))];
    }

    // ── Holder ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheHolder_FromSamples_NeedsAWindowOfThem_NeverOne()
    {
        // p1 has it in hand at 3 s and 9 s; p2 at 5 s only (a flick, or a pickup for a teammate).
        RoundPositionsDocument flicker = Positions(Stored(1, (s, slot) => slot switch
        {
            1 => new RoundPosition(1, 0, 0, 0, 0, s is 3 or 9 ? Awp : Knife),
            2 => new RoundPosition(2, 0, 0, 0, 0, s == 5 ? Awp : Knife),
            _ => null
        }));
        RoundPositionsDocument one = Positions(Stored(1, (s, slot) =>
            slot == 2 ? new RoundPosition(2, 0, 0, 0, 0, s == 5 ? Awp : Knife) : null));
        RoundPositionsDocument late = Positions(Stored(1, (s, slot) =>
            slot == 2 ? new RoundPosition(2, 0, 0, 0, 0, s > AwpRules.HolderWindowSeconds ? Awp : Knife) : null));
        RoundPositionsDocument enemy = Positions(Stored(1, (_, slot) =>
            slot == 7 ? new RoundPosition(7, 0, 0, 0, 0, Awp) : null));

        AwpRound read = AwpRules.Read(Team(Facts(1, 3)), flicker, null);
        using (Assert.Multiple())
        {
            await Assert.That(read.HolderSlot).IsEqualTo(1);
            await Assert.That(read.Holders).IsEqualTo(1).Because("one sample does not make p2 a second AWP");
            await Assert.That(read.FromSamples).IsTrue();
            await Assert.That(read.HolderName).IsEqualTo("p1");
            await Assert.That(read.HolderSteamId).IsEqualTo(Steam(2));
            await Assert.That(read.Bought).IsNull().Because("no captures, no purchases");
            await Assert.That(AwpRules.Read(Team(Facts(1, 3)), one, null).HasAwp).IsFalse();
            await Assert.That(AwpRules.Read(Team(Facts(1, 3)), late, null).HasAwp).IsFalse()
                .Because("only the first 20 s name a holder");
            await Assert.That(AwpRules.Read(Team(Facts(1, 3)), enemy, null).HasAwp).IsFalse()
                .Because("the other side's AWP is not the team's");
        }
    }

    [Test]
    public async Task TheHolder_FromCaptures_IsTheSurestUser_AndTwoUsersAreTwoAwps()
    {
        List<AwpRound> rounds = FiveRounds();
        using (Assert.Multiple())
        {
            await Assert.That(rounds[0].HasAwp).IsFalse();
            await Assert.That(rounds[1].HolderSlot).IsEqualTo(1);
            await Assert.That(rounds[1].FromSamples).IsFalse();
            await Assert.That(rounds[1].Bought).IsTrue();
            await Assert.That(rounds[2].Bought).IsFalse().Because("kept from round 2, not bought");
            await Assert.That(rounds[4].HolderSlot).IsEqualTo(2).Because("p2 had it in hand longest");
            await Assert.That(rounds[4].Holders).IsEqualTo(2);
            await Assert.That(rounds.All(r => r.CapturesRead && r.PositionsRead)).IsTrue();
        }
    }

    // ── Path and windows ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ThePath_IsOneASecond_GapsFilled_EndingAtDeath_AndTheWindowsCutIt()
    {
        // p1 runs route A, is not sampled at 5 s, and is dead from 13 s; the first kill is at 8 s.
        RoundPositionsRound stored = Stored(1, (s, slot) => slot != 1 ? null
            : s == 5 || s > 12 ? new RoundPosition(-1, 0, 0, 0, 0) : RouteA(s, slot, 1));
        AwpRound read = AwpRules.Read(Team(Facts(1, 3, BuyType.Full, Kill(1, 8, 7, 3))), Positions(stored), null);
        AwpRound noKill = AwpRules.Read(Team(Facts(1, 3)), Positions(Stored(1, (s, slot) => RouteA(s, slot, 1))), null);

        using (Assert.Multiple())
        {
            await Assert.That(read.Path.Select(p => p.Second)).IsEquivalentTo(Enumerable.Range(0, 13));
            await Assert.That(read.Path[5].X).IsEqualTo(400).Because("the gap takes the last point");
            await Assert.That(read.Path[0].Place).IsEqualTo("CTSpawn");
            await Assert.That(read.ContactSeconds).IsEqualTo(8);
            await Assert.That(read.Trail(AwpWindow.Contact).Count).IsEqualTo(9);
            await Assert.That(read.Trail(AwpWindow.To15).Count).IsEqualTo(13).Because("dead before 0:15");
            await Assert.That(noKill.Trail(AwpWindow.To15).Count).IsEqualTo(16);
            await Assert.That(noKill.Trail(AwpWindow.To30).Count).IsEqualTo(31);
            await Assert.That(noKill.EndOf(AwpWindow.Contact)).IsEqualTo(AwpRules.NoContactSeconds);
        }
    }

    // ── Clustering ────────────────────────────────────────────────────────────────────────────────

    private static AwpRound Trail(int number, int dx, int dy, string place = "Jungle", int holder = 1,
        int length = 15, string? name = null) => new()
    {
        Round = Team(Facts(number, 3)),
        PositionsRead = true,
        HolderSlot = holder,
        HolderName = name ?? $"p{holder}",
        HolderSteamId = Steam(holder + 1),
        Holders = 1,
        Path = [.. Enumerable.Range(0, length + 1).Select(s => new AwpPoint(s, dx * s, dy * s, 0, s == 0 ? "CTSpawn" : place))]
    };

    [Test]
    public async Task TheDistance_IsTheMeanSecondBySecond_TheShorterHeldAtItsLastPoint()
    {
        AwpPoint[] a = [new(0, 0, 0, 0, null), new(1, 100, 0, 0, null), new(2, 200, 0, 0, null)];
        AwpPoint[] b = [new(0, 0, 0, 0, null), new(1, 100, 0, 0, null)];
        using (Assert.Multiple())
        {
            await Assert.That(AwpRoutes.Distance(a, a)).IsEqualTo(0);
            await Assert.That(AwpRoutes.Distance(a, b)).IsEqualTo(100.0 / 3).Within(1e-9);
            await Assert.That(AwpRoutes.Distance(a, [])).IsEqualTo(double.PositiveInfinity);
        }
    }

    [Test]
    public async Task CompleteLinkage_CutsAtTheThreshold_AndNeverChainsFarEnds()
    {
        // 0-1 and 2-3 are close pairs; the chain 4-5-6 has 600 links but 1200 ends.
        double[] pairs = Matrix(4, (i, j) => i / 2 == j / 2 ? 100 : 2000);
        double[] chain = Matrix(3, (i, j) => Math.Abs(i - j) * 600);
        using (Assert.Multiple())
        {
            await Assert.That(AwpRoutes.CompleteLinkage(pairs, 4, 700).Count).IsEqualTo(2);
            await Assert.That(AwpRoutes.CompleteLinkage(pairs, 4, 50).Count).IsEqualTo(4);
            await Assert.That(AwpRoutes.CompleteLinkage(pairs, 4, 5000).Count).IsEqualTo(1);
            await Assert.That(AwpRoutes.CompleteLinkage(chain, 3, 700).Count).IsEqualTo(2)
                .Because("a cluster's distance is its farthest pair");
        }
    }

    private static double[] Matrix(int n, Func<int, int, double> d)
    {
        double[] m = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                m[i * n + j] = i == j ? 0 : d(i, j);
            }
        }

        return m;
    }

    [Test]
    public async Task Routes_AreTheClusters_BiggestFirst_NamedByPlaces_WithSmallOnesInOther()
    {
        // 5 rounds east to Jungle (one slower), 4 north to Connector, 1 alone south.
        List<AwpRound> rounds =
        [
            .. Enumerable.Range(1, 4).Select(n => Trail(n, 100, 0)), Trail(5, 90, 0),
            .. Enumerable.Range(6, 4).Select(n => Trail(n, 0, 100, "Connector")),
            Trail(10, 0, -100, "TopofMid")
        ];
        AwpRouteSet set = AwpRoutes.Build(rounds, AwpWindow.To15);
        AwpRouteSet tight = AwpRoutes.Build(rounds, AwpWindow.To15, 10);
        using (Assert.Multiple())
        {
            await Assert.That(set.Clustered).IsEqualTo(10);
            await Assert.That(set.Routes.Select(r => r.Members.Count)).IsEquivalentTo([5, 4]);
            await Assert.That(set.Routes[0].Name).IsEqualTo("CTSpawn → Jungle");
            await Assert.That(set.Routes[1].Name).IsEqualTo("CTSpawn → Connector");
            await Assert.That(set.Routes[0].Medoid.Round.Number).IsNotEqualTo(5).Because("the slow one is the outlier");
            await Assert.That(set.Other.Select(r => r.Round.Number)).IsEquivalentTo([10]);
            await Assert.That(tight.Routes.Select(r => r.Members.Count)).IsEquivalentTo([4, 4])
                .Because("at a 10 unit cut only identical runs stay together");
            await Assert.That(tight.Other.Select(r => r.Round.Number)).IsEquivalentTo([5, 10]);
        }
    }

    [Test]
    public async Task Routes_AreCapped_TheRestGoToOther_AndAHeldSpotIsHolds()
    {
        List<AwpRound> rounds =
        [
            .. Enumerable.Range(0, 6).SelectMany(k => new[]
            {
                Trail(k * 2 + 1, 300 * (k - 3), 300 * (k % 2 == 0 ? 1 : -1)),
                Trail(k * 2 + 2, 300 * (k - 3), 300 * (k % 2 == 0 ? 1 : -1))
            })
        ];
        AwpRound still = Trail(20, 0, 0, "CTSpawn");
        AwpRouteSet set = AwpRoutes.Build(rounds, AwpWindow.To15);
        AwpRouteSet holds = AwpRoutes.Build([still, Trail(21, 0, 0, "CTSpawn")], AwpWindow.To15);
        using (Assert.Multiple())
        {
            await Assert.That(set.Routes.Count).IsEqualTo(AwpRoutes.MaxRoutes);
            await Assert.That(set.Other.Count).IsEqualTo(4);
            await Assert.That(holds.Routes.Single().Name).IsEqualTo("holds CTSpawn");
        }
    }

    // ── Stats ─────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheStats_OpeningDuels_MovesAfterTheShot_SavedOnLoss_HoldsAndPeeks()
    {
        List<AwpRound> rounds = FiveRounds();
        AwpRound mid = AwpRules.Read(Team(Facts(1, 2), side: 2),
            Positions(Stored(1, (s, slot) => slot == 6
                ? new RoundPosition(6, 0, s * 100, 0, s >= 8 ? 2 : 0, s % 2 == 0 ? Awp : Knife)
                : null)), null);
        using (Assert.Multiple())
        {
            await Assert.That(rounds[1].OpeningWon).IsTrue().Because("the round's first kill was p1's AWP kill");
            await Assert.That(rounds[3].OpeningWon).IsFalse().Because("p1 died in the opening");
            await Assert.That(rounds[2].OpeningWon).IsNull().Because("the AWP took no part");
            await Assert.That(rounds[1].MovedAfterShot).IsTrue().Because("300 units by 2 s");
            await Assert.That(rounds[3].MovedAfterShot).IsFalse().Because("100 units by 2 s");
            await Assert.That(rounds[2].MovedAfterShot).IsNull();
            await Assert.That(rounds[3].SavedOnLoss).IsFalse();
            await Assert.That(rounds[4].SavedOnLoss).IsTrue();
            await Assert.That(rounds[1].SavedOnLoss).IsNull().Because("a won round is no save");
            await Assert.That(rounds[1].HoldsStartSpot).IsTrue().Because("at (1000, 0) from 0:10 to 0:20");
            await Assert.That(mid.PeeksMid).IsTrue().Because("TopofMid at 0:08");
            await Assert.That(mid.HoldsStartSpot).IsFalse();
        }
    }

    // ── The layer ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheLayer_DrawsRoutes_RowsOpenTheirRounds_AndPlayFollowsTheAwper()
    {
        List<AwpRound> rounds = FiveRounds();
        Dictionary<string, ScoutingColor> colors = new(StringComparer.Ordinal) { [Id(2)] = ScoutingColor.Player2 };
        ScoutingLayerResult contact = AwpLayer.Compose("de_mirage CT", ScoutingSide.Ct, ScoutingBuy.All, "de_mirage",
            rounds, AwpWindow.Contact, "first contact", colors);
        ScoutingLayerResult early = AwpLayer.Compose("de_mirage CT", ScoutingSide.Ct, ScoutingBuy.All, "de_mirage",
            rounds, AwpWindow.To15, "0:15", colors);
        List<ScoutingInspectorRow> rows = [.. contact.Sections.SelectMany(s => s.Rows)];
        ScoutingInspectorRow route1 = rows.Single(r => r.Badge == "1");
        ScoutingInspectorRow Row(string label) => rows.Single(r => r.Label == label);
        ScoutingMapMark badge1 = contact.Marks.Single(m => m is { Shape: ScoutingMarkShape.Badge, Text: "1" });

        using (Assert.Multiple())
        {
            await Assert.That(contact.Status.State).IsEqualTo(ScoutingLayerState.Ready);
            await Assert.That(contact.CountLabel).IsEqualTo("2 routes");
            await Assert.That(contact.Sections[0].Title).IsEqualTo("Routes · trail to first contact");
            await Assert.That(route1.Label).IsEqualTo("CTSpawn → Jungle");
            await Assert.That(route1.Value).IsEqualTo("50%");
            await Assert.That(route1.Play).IsEqualTo((Demo, Freeze(2)));
            await Assert.That(route1.FollowSteamId).IsEqualTo(Steam(2)).Because("p1, the route's AWPer");
            await Assert.That(route1.StarText).IsEqualTo("route CTSpawn → Jungle");
            await Assert.That(rows.Where(r => r.Rounds.Count > 0).All(r => r.OpenTitle.StartsWith("de_mirage CT · ", StringComparison.Ordinal)))
                .IsTrue();
            await Assert.That(Row("p1").Value).IsEqualTo("3 of 4 rounds");
            await Assert.That(Row("p1").Swatch).IsEqualTo(ScoutingColor.Player2);
            await Assert.That(Row("p2").Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([5]);
            await Assert.That(Row("rounds with an AWP").Value).IsEqualTo("4 of 5 rounds");
            await Assert.That(Row("AWP bought").Value).IsEqualTo("1 of 4 rounds").Because("the pistol round is left out");
            await Assert.That(Row("AWP bought").Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([2]);
            await Assert.That(Row("AWP opening duels").Value).IsEqualTo("1–1");
            await Assert.That(Row("AWP opening duels").Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([2, 4]);
            await Assert.That(Row("Moves after the first shot").Value).IsEqualTo("50%");
            await Assert.That(Row("Moves after the first shot").Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([2]);
            await Assert.That(Row("Saved when the round is lost").Value).IsEqualTo("1 of 2 rounds");
            await Assert.That(Row("Two AWPs").Value).IsEqualTo("1 round");
            await Assert.That(Row("Holds the start spot").Value).IsEqualTo("100%");
            await Assert.That(contact.Reads.Single().Text)
                .IsEqualTo("CT AWP: p1 holds it in 75% of AWP rounds; most often CTSpawn → Jungle (50% to first contact); moves after the first shot in 50%.");
            await Assert.That(contact.Reads.Single().Suggested).IsTrue();
            await Assert.That(contact.Marks.Count(m => m.Shape == ScoutingMarkShape.Ring)).IsEqualTo(2);
            await Assert.That(contact.Marks.Where(m => m.Shape == ScoutingMarkShape.Dot).All(m => m.Color == ScoutingColor.Neutral))
                .IsTrue();
            await Assert.That((badge1.X, badge1.Y)).IsEqualTo((1000f, 0f)).Because("at 0:20, held at (1000, 0)");
            await Assert.That(early.Marks.Single(m => m is { Shape: ScoutingMarkShape.Badge, Text: "1" }).X).IsEqualTo(1000f);
            await Assert.That(contact.Legend.Single().Text)
                .IsEqualTo("ring = AWP start · white trail · numbered = position at first contact");
        }
    }

    [Test]
    public async Task OneHolder_UnderTwoInGameNames_IsOneRow_UnderTheRostersName()
    {
        // The same account as "SLIGHT" in two rounds and "slight" in a third; another player once.
        AwpRound Named(int number, string name, int holder) => Trail(number, 100, 0, holder: holder, name: name);
        AwpRound[] rounds = [Named(1, "SLIGHT", 1), Named(2, "SLIGHT", 1), Named(3, "slight", 1), Named(4, "morty", 2)];
        Dictionary<string, string> names = new(StringComparer.Ordinal) { [Steam(2).ToString(CultureInfo.InvariantCulture)] = "SLIGHT" };

        ScoutingLayerResult result = AwpLayer.Compose("de_mirage CT", ScoutingSide.Ct, ScoutingBuy.All, "de_mirage",
            rounds, AwpWindow.Contact, "first contact", new Dictionary<string, ScoutingColor>(), names);
        List<ScoutingInspectorRow> holders = [.. result.Sections.Single(s => s.Title == "Who holds it").Rows];

        using (Assert.Multiple())
        {
            await Assert.That(holders.Select(h => h.Label)).IsEquivalentTo(["SLIGHT", "morty"], CollectionOrdering.Matching)
                .Because("one player is one row, keyed by SteamID, under the roster's name");
            await Assert.That(holders[0].Rounds.Count).IsEqualTo(3);
            await Assert.That(result.Reads.Single().Text).StartsWith("CT AWP: SLIGHT holds it in 75% of AWP rounds");
        }
    }

    [Test]
    public async Task TheLayer_SaysWhyItIsEmpty()
    {
        AwpRound unread = new() { Round = Team(Facts(1, 3)) };
        AwpRound noAwp = new() { Round = Team(Facts(1, 3)), PositionsRead = true };
        ScoutingLayerResult Compose(params AwpRound[] rounds) => AwpLayer.Compose("de_mirage CT", ScoutingSide.Ct,
            ScoutingBuy.Full, "de_mirage", rounds, AwpWindow.Contact, "first contact", new Dictionary<string, ScoutingColor>());
        using (Assert.Multiple())
        {
            await Assert.That(Compose().Status.Message).IsEqualTo("no full CT rounds on de_mirage");
            await Assert.That(Compose(unread).Status.Message)
                .IsEqualTo("no positions or round captures for these rounds; rebuild the round index");
            await Assert.That(Compose(noAwp).Status.Message).IsEqualTo("no CT AWP seen in 1 round");
        }
    }

    // ── Through the tab ───────────────────────────────────────────────────────────────────────────

    private sealed class Playback : ISituationPlayback
    {
        public List<(string Demo, int Tick, ulong? Follow)> Seeks { get; } = [];

        public Task<bool> SeekAsync(string demoPath, int tick, ulong? followSteamId = null)
        {
            Seeks.Add((demoPath, tick, followSteamId));
            return Task.FromResult(true);
        }
    }

    private static DemoCacheRecord Record(string path, string map, bool facts)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            Map = map,
            Sha256 = facts ? "abc123" : null,
            CtScore = 13,
            TScore = 7,
            CtSideWins = 13,
            TSideWins = 7
        };
        if (facts)
        {
            record.SetRoundFacts(Rows());
        }

        for (int slot = 0; slot < 10; slot++)
        {
            record.Players.Add(new CachedPlayerInfo
            {
                Slot = slot, Name = $"p{slot}", SteamId64 = Id(slot + 1), Team = slot < 5 ? 3 : 2
            });
        }

        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    [Test]
    public async Task ThroughTheTab_WindowPillsRebuild_StarsKeepTheWindow_PlayFollows_AndThePoolFillsAwpBought()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            cache.Upsert(Record(Demo, "de_mirage", true));
            cache.Upsert(Record("/d/other.dem", "de_nuke", false));
        }

        await teams.Idle;
        RoundIndexStore store = new(cache.Data());
        store.Write(Demo, RoundIndexTestData.Document("de_mirage", "any"), FivePositions(), "any", FiveCaptures());
        Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(1)) == true)).Id;
        Playback playback = new();
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), false,
            post: posted.Post, runSection: _ => _inline, roundIndex: store, roundFacts: new RoundFactsSource(cache),
            playback: () => playback);
        vm.SelectedTeam = vm.Teams.Single(x => x.Id == teamA);
        await ScoutingReportTestKit.Settle(vm, posted);

        IReadOnlyList<ScoutingInspectorRowViewModel> rows =
            await ScoutingReportTestKit.Inspect(vm, posted, "de_mirage", ScoutingSide.Ct, "awp");
        ScoutingInspectorViewModel inspector = vm.Layers.Inspector;
        string[] pills = [.. inspector.Windows.Select(w => w.Label)];
        string onBefore = inspector.Windows.Single(w => w.IsOn).Window.Key;
        bool opens = rows.EveryNumberOpensItsRounds();
        string contactKey = rows.Single(r => r.Row?.Badge == "1").Key;
        rows.Single(r => r.Row?.Badge == "1").PlayCommand.Execute(null);

        inspector.Windows.Single(w => w.Window.Key == "0:15").PickCommand.Execute(null);
        await ScoutingReportTestKit.Settle(vm, posted);
        IReadOnlyList<ScoutingInspectorRowViewModel> early = [.. inspector.Sections.SelectMany(s => s.Rows)];
        string earlyKey = early.Single(r => r.Row?.Badge == "1").Key;

        vm.Layers.Focus(vm.Layers.Find("setups")!);
        bool pillsOnSetups = inspector.HasWindows;
        MapPoolRowViewModel pool = vm.Maps.Single(m => m.Map == "de_mirage");

        using (Assert.Multiple())
        {
            await Assert.That(pills).IsEquivalentTo(["0:15", "0:30", "first contact"]);
            await Assert.That(onBefore).IsEqualTo("contact");
            await Assert.That(opens).IsTrue();
            await Assert.That(contactKey).Contains("|awp|All|contact|route ");
            await Assert.That(earlyKey).Contains("|awp|All|0:15|route ");
            await Assert.That(early.Count).IsGreaterThan(0);
            await Assert.That(early[0].Row!.Note).IsNotNull();
            await Assert.That(playback.Seeks).IsEquivalentTo([(Demo, Freeze(2), (ulong?)Steam(2))]);
            await Assert.That(pillsOnSetups).IsFalse().Because("only the AWP layer has a trail window");
            await Assert.That(pool.AwpLabel).IsEqualTo("25%").Because("1 of 4 non-pistol rounds bought one");
        }
    }
}
