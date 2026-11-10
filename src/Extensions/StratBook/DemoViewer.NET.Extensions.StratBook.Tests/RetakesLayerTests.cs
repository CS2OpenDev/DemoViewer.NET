#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Retakes and saves layer over synthetic rounds: the Q6 save rule per CT (alive at the plant, alive
///     at the end, never within the radius of the bomb, in a lost round), the round read as a retake or a
///     save, the man-count rows, the per-site split and grouping, the marks, and every row opening its rounds.
/// </summary>
public class RetakesLayerTests
{
    private const int Rate = 64;
    private const string Demo = "/d/retakes.dem";
    private const string Prefix = "de_nuke CT";

    private static readonly int[] _ct = [0, 1, 2, 3, 4];

    private static RoundPosition P(int slot, int x, int y, int z = 0) => new(slot, x, y, z, 0);

    // One step a second from freeze end; the bomb goes down at 40 s by slot 9 at (0, 0, 0); the round ends at 80 s.
    private static RoundFacts Facts(int winner, params KillStep[] kills) => new()
    {
        Number = 1,
        IsLive = true,
        FreezeEndTick = 0,
        PlantTick = 40 * Rate,
        PlantSite = BombSite.A,
        PlanterSlot = 9,
        EndTick = 80 * Rate,
        WinnerSide = winner,
        Ct = new SideFacts { Side = 3, Slots = _ct, PlayersAtFreezeEnd = 5, BuyType = BuyType.Full },
        T = new SideFacts { Side = 2, Slots = [5, 6, 7, 8, 9], PlayersAtFreezeEnd = 5, BuyType = BuyType.Full },
        Kills = [.. kills]
    };

    private static KillStep Kill(int seconds, int victim, int ctAlive, int tAlive) =>
        new() { Tick = seconds * Rate, VictimSlot = victim, VictimSide = 3, CtAlive = ctAlive, TAlive = tAlive };

    private static RoundPositionsDocument Positions(Func<int, IEnumerable<RoundPosition>> ctAt) => new()
    {
        CadenceTicks = Rate,
        Rounds =
        [
            new RoundPositionsRound
            {
                Number = 1,
                FreezeEndTick = 0,
                Ct = _ct,
                Pos = [.. Enumerable.Range(0, 90).Select(s => ctAt(s).Append(P(9, 0, 0)).ToList())]
            }
        ]
    };

    [Test]
    public async Task TheSaveRule_IsAliveAtThePlant_AliveAtTheEnd_NeverNearTheBomb_InALostRound()
    {
        // p0 walks onto the bomb at 50 s and lives; p1 stays 2000 away and lives; p2 stays away and is killed at
        // 60 s; p3 is 500 off in XY but a floor below; p4 died before the plant.
        IEnumerable<RoundPosition> Ct(int s)
        {
            yield return P(0, s >= 50 ? 300 : 3000, 0);
            yield return P(1, 2000, 0);
            if (s < 60)
            {
                yield return P(2, -2000, 0);
            }

            yield return P(3, 500, 0, -400);
            if (s < 30)
            {
                yield return P(4, 0, 3000);
            }
        }

        KillStep early = Kill(30, 4, 4, 5);
        KillStep p2 = Kill(60, 2, 3, 5);
        RoundPositionsDocument positions = Positions(Ct);
        (RetakeKind lostKind, IReadOnlyList<RoundPosition> lostReached, IReadOnlyList<RoundPosition> lostSaves) =
            RetakeRules.Read(positions, Facts(2, early, p2));
        (RetakeKind wonKind, _, IReadOnlyList<RoundPosition> wonSaves) = RetakeRules.Read(positions, Facts(3, early, p2));

        // Nobody goes near the bomb, two live through the loss: the round is a save.
        RoundPositionsDocument hide = Positions(s => [P(0, 3000, 0), P(1, 2000, 0)]);
        (RetakeKind hideKind, _, IReadOnlyList<RoundPosition> hideSaves) = RetakeRules.Read(hide, Facts(2));

        // Nobody goes near the bomb and both die on the way: a failed retake, not a save.
        RoundPositionsDocument die = Positions(s => s < 55 ? [P(0, 3000, 0), P(1, 2000, 0)] : []);
        (RetakeKind dieKind, _, IReadOnlyList<RoundPosition> dieSaves) =
            RetakeRules.Read(die, Facts(2, Kill(55, 0, 1, 5), Kill(55, 1, 0, 5)));

        using (Assert.Multiple())
        {
            await Assert.That(lostReached.Select(p => p.Slot)).IsEquivalentTo([0]);
            await Assert.That(lostReached[0].X).IsEqualTo(300).Because("the mark is where it first reached the bomb");
            await Assert.That(lostSaves.Select(p => p.Slot)).IsEquivalentTo([1, 3])
                .Because("p0 reached the bomb, p2 was killed, p4 was dead at the plant, p3 was on another floor");
            await Assert.That(lostKind).IsEqualTo(RetakeKind.Retook).Because("a CT reached the bomb");
            await Assert.That(wonSaves).IsEmpty().Because("staying away in a won round is not a save");
            await Assert.That(wonKind).IsEqualTo(RetakeKind.Retook);
            await Assert.That(hideKind).IsEqualTo(RetakeKind.Saved);
            await Assert.That(hideSaves.Count).IsEqualTo(2);
            await Assert.That(dieKind).IsEqualTo(RetakeKind.Retook);
            await Assert.That(dieSaves).IsEmpty();
            await Assert.That(RetakeRules.Read(null, Facts(2)).Kind).IsEqualTo(RetakeKind.Unread);
            await Assert.That(RetakeRules.Read(positions, new RoundFacts { Number = 1 }).Kind)
                .IsEqualTo(RetakeKind.NoPlant);
        }
    }

    [Test]
    public async Task TheBombPosition_IsThePlantersNearestSample_AtOrJustBeforeThePlant()
    {
        RoundPositionsDocument positions = Positions(s => [P(0, 3000, 0)]);
        positions.Rounds[0].Pos[40].RemoveAll(p => p.Slot == 9); // the planter is missing at the plant's own step
        positions.Rounds[0].Pos[39][^1] = P(9, 111, 222);
        await Assert.That(RetakeRules.PlantPosition(positions, Facts(2))).IsEqualTo(P(9, 111, 222));
    }

    [Test]
    public async Task TheBombPosition_IsTheCapturedPlantSpot_WhenTheRoundHasOne_ElseTheSample()
    {
        RoundPositionsDocument positions = Positions(s => [P(0, 3000, 0)]);
        RoundCaptureView exact = new(new CapturedRound
        {
            Number = 1,
            Plant = new CapturedPlant(40 * Rate, 9, 0, BombSite.A, new CaptureSpot(-1500, 700, 12, "BombsiteA"))
        }, true);
        RoundCaptureView unspotted = new(new CapturedRound
        {
            Number = 1, Plant = new CapturedPlant(40 * Rate, 9, 0, BombSite.A, null)
        }, true);
        RoundFacts facts = Facts(2);

        RoundPosition? fromCapture = RetakeRules.PlantPosition(positions, facts, exact);
        RoundPosition? withoutSpot = RetakeRules.PlantPosition(positions, facts, unspotted);
        RoundPosition? none = RetakeRules.PlantPosition(positions, facts, null);
        using (Assert.Multiple())
        {
            await Assert.That(fromCapture).IsNotNull();
            await Assert.That((fromCapture!.Value.X, fromCapture.Value.Y, fromCapture.Value.Z)).IsEqualTo((-1500, 700, 12));
            await Assert.That(withoutSpot).IsEqualTo(none).Because("a plant with no spot falls back to the sample");
            await Assert.That(none).IsNotNull();
            await Assert.That(PostPlantService.PlantSpot(positions, facts, exact)).IsEqualTo(fromCapture)
                .Because("the plants layer reads the same source");
        }
    }

    [Test]
    public async Task ManCountRows_SplitRetookAndSaved_AndCountTheRetakesWon()
    {
        List<RetakeRound> rounds =
        [
            .. Many(3, n => R(n, 4, 4, RetakeKind.Retook, true)),
            .. Many(2, n => R(10 + n, 4, 4, RetakeKind.Retook, false)),
            R(20, 4, 4, RetakeKind.Saved, false),
            R(21, 4, 4, RetakeKind.Unread, false),
            .. Many(4, n => R(30 + n, 2, 4, RetakeKind.Saved, false)),
            R(40, 2, 4, RetakeKind.Retook, false),
            R(50, 0, 3, RetakeKind.NoCtAlive, false),
            R(60, 5, 5, RetakeKind.NoPlant, true)
        ];

        ScoutingLayerResult result = RetakesLayer.Compose(Prefix, rounds);
        IReadOnlyList<ScoutingInspectorRow> man = Section(result, "CTs alive at the plant");
        IReadOnlyList<ScoutingInspectorRow> saves = Section(result, "Saves");
        IReadOnlyList<ScoutingInspectorRow> top = Section(result, "");

        using (Assert.Multiple())
        {
            await Assert.That(man.Select(r => $"{r.Label} | {r.Value}")).IsEquivalentTo(
            [
                "4v4 · retook 5 · saved 1 | won 3 of 5",
                "2v4 · retook 1 · saved 4 | won 0 of 1",
                "0v3 · no CT alive | 1 round"
            ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(man[0].Rounds.Count).IsEqualTo(7).Because("the row opens every round at its count");
            await Assert.That(man[0].Note).IsEqualTo("1 without positions");
            await Assert.That(man[0].OpenTitle).IsEqualTo("de_nuke CT · retakes at 4v4");
            await Assert.That(saves.Select(r => $"{r.Label} | {r.Value}")).IsEquivalentTo(
                ["4v4 · saved | 1 of 7 rounds", "2v4 · saved | 4 of 5 rounds"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(saves[1].Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([30, 31, 32, 33]);
            await Assert.That(top[1].Value).IsEqualTo("13 of 14 rounds").Because("13 of the 14 CT rounds had a plant");
            await Assert.That(result.CountLabel).IsEqualTo("13 plants");
            await Assert.That(result.Reads.Single().Text)
                .IsEqualTo("Retakes collapse at 2 CTs alive and below: they save in 4 of 5 rounds.");
        }
    }

    [Test]
    public async Task BySite_SplitsTheRetakesWon_AndTheGrouping_AndEveryRowOpensItsRounds()
    {
        List<RetakeRound> rounds =
        [
            R(1, 4, 4, RetakeKind.Retook, true, BombSite.A, PostPlantService.Together),
            R(2, 4, 4, RetakeKind.Retook, true, BombSite.A, PostPlantService.Together),
            R(3, 4, 4, RetakeKind.Retook, false, BombSite.A, PostPlantService.Trickled),
            R(4, 3, 4, RetakeKind.Retook, false, BombSite.B, PostPlantService.Trickled),
            R(5, 3, 4, RetakeKind.Retook, true, BombSite.B, PostPlantService.Trickled),
            R(6, 3, 4, RetakeKind.Saved, false, BombSite.B, "nobody reached the bomb")
        ];

        ScoutingLayerResult result = RetakesLayer.Compose(Prefix, rounds);
        IReadOnlyList<ScoutingInspectorRow> site = Section(result, "By site");

        using (Assert.Multiple())
        {
            await Assert.That(site.Select(r => $"{r.Label} | {r.Value}")).IsEquivalentTo(
            [
                "A · retake won | 2 of 3 rounds",
                "A · together | 2 of 3 rounds",
                "A · trickled | 1 of 3 rounds",
                "B · retake won | 1 of 2 rounds",
                "B · trickled | 2 of 2 rounds"
            ], TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("a save is not a retake on its site");
            await Assert.That(site[0].Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 2]);
            await Assert.That(site[1].Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 2]);
            await Assert.That(site[4].Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([4, 5]);
            await Assert.That(site[3].OpenTitle).IsEqualTo("de_nuke CT · B retakes won");
            await Assert.That(Section(result, "Retake grouping").Select(r => $"{r.KeyText} | {r.Value}"))
                .IsEquivalentTo(["retake trickled | 3 of 5 rounds", "retake together | 2 of 5 rounds"],
                    TUnit.Assertions.Enums.CollectionOrdering.Matching)
                .Because("a schema-1 star on \"retake together\" still lands on its row");
            await Assert.That(result.Sections.SelectMany(s => s.Rows).Where(r => r.Value.Length > 0)
                    .All(r => r.Rounds.Count > 0 || r.Value.StartsWith("0 ", StringComparison.Ordinal)))
                .IsTrue().Because("every number opens its rounds");
        }
    }

    [Test]
    public async Task Marks_AreBlueDotsWhereCtsReachedTheBomb_AndGreyRingsWhereTheySaved()
    {
        List<RetakeRound> rounds =
        [
            R(1, 4, 4, RetakeKind.Retook, true) with { Reached = [P(0, 10, 20, 30), P(1, 11, 21, 31)] },
            R(2, 2, 4, RetakeKind.Saved, false) with { Saves = [P(2, -500, 900, -40)] }
        ];

        ScoutingLayerResult result = RetakesLayer.Compose(Prefix, rounds);

        using (Assert.Multiple())
        {
            await Assert.That(result.Marks.Count(m => m is { Shape: ScoutingMarkShape.Dot, Color: ScoutingColor.Ct }))
                .IsEqualTo(2);
            await Assert.That(result.Marks.Single(m => m.Shape == ScoutingMarkShape.Ring))
                .IsEqualTo(new ScoutingMapMark(-500, 900, -40, ScoutingMarkShape.Ring, ScoutingColor.Neutral, 5f));
            await Assert.That(result.Legend.Select(l => l.Text))
                .IsEquivalentTo(["blue dot = a CT who reached the bomb", "grey ring = a CT who saved"]);
        }
    }

    [Test]
    public async Task ACutOffRound_IsUnread_NotALostRetake()
    {
        RoundPositionsDocument positions = Positions(s => [P(0, 3000, 0), P(1, 3000, 0)]);

        RetakeRound round = RetakeService.Round("de_nuke", Demo, "abc", Rate, positions, Facts(0));

        using (Assert.Multiple())
        {
            await Assert.That(round.Kind).IsEqualTo(RetakeKind.Unread);
            await Assert.That(round.Saves).IsEmpty();
        }
    }

    [Test]
    public async Task TheRound_ReadsTheManCountAndClip_FromTheFactsAndThePositions()
    {
        RoundPositionsDocument positions = Positions(s => [P(0, s >= 45 ? 100 : 3000, 0), P(1, s >= 46 ? 120 : 3000, 0)]);
        RoundFacts facts = Facts(3, Kill(20, 2, 4, 5), Kill(25, 3, 3, 5), Kill(30, 4, 2, 5));

        RetakeRound round = RetakeService.Round("de_nuke", Demo, "abc", Rate, positions, facts);

        using (Assert.Multiple())
        {
            await Assert.That(round.ManCount).IsEqualTo("2v5");
            await Assert.That(round.Kind).IsEqualTo(RetakeKind.Retook);
            await Assert.That(round.Grouping).IsEqualTo(PostPlantService.Together);
            await Assert.That(round.Outcome).IsEqualTo("won: elimination");
            await Assert.That(round.Clip).IsEqualTo(new TendencyRound(Demo, "abc", 1, 35 * Rate, 80 * Rate, Rate));
        }
    }

    private static IEnumerable<RetakeRound> Many(int count, Func<int, RetakeRound> make) =>
        Enumerable.Range(0, count).Select(make);

    private static RetakeRound R(int number, int ct, int t, RetakeKind kind, bool won, BombSite site = BombSite.A,
        string? grouping = null) =>
        new("de_nuke", BuyType.Full, site, ct, t, kind, won, won ? "won: defused" : "lost: exploded", grouping, [],
            [], new TendencyRound(Demo, null, number, number * 100, number * 100 + 50, Rate));

    private static IReadOnlyList<ScoutingInspectorRow> Section(ScoutingLayerResult result, string title) =>
        result.Sections.Single(s => s.Title == title).Rows;
}
