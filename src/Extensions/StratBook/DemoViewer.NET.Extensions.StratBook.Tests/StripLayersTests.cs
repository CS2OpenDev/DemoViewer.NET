#region

using Avalonia.Controls;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Playback2D.Pipeline.Review;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Review;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The strip layers (economy, pace, rotations, impact, by score, timeouts) over one synthetic demo of twelve
///     rounds. Team A (slots 0..4) is T for rounds 1 to 6 and CT for 7 to 12:
///     <list type="number">
///         <item>T pistol, won; A plants A at 0:30; A opens with a kill.</item>
///         <item>T force (CT eco), won; plant B at 0:50.</item>
///         <item>T full, won; plant A at 1:20; A loses the opening and trades it.</item>
///         <item>T full, lost; no plant.</item>
///         <item>T eco (the full save), lost; no plant.</item>
///         <item>T full, won; plant A at exactly 0:35.</item>
///         <item>CT pistol, lost.</item>
///         <item>CT eco, lost; A loses the opening.</item>
///         <item>CT full, won; A wins the opening.</item>
///         <item>CT force, won.</item>
///         <item>CT full, won.</item>
///         <item>CT full, lost (no next round).</item>
///     </list>
/// </summary>
[NotInParallel]
public class StripLayersTests
{
    private const string Demo = "/d/strip.dem";
    private const int Rate = 64;

    internal static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly int[] _a = [0, 1, 2, 3, 4];
    private static readonly int[] _b = [5, 6, 7, 8, 9];

    private static string Id(int n) => $"7657{n:D4}";

    private static int Freeze(int number) => number * 10000;

    private static int At(int number, double seconds) => Freeze(number) + (int)(seconds * Rate);

    // (A's buy, A won, plant site and seconds, first contact seconds, kills as (attacker, victim, seconds)).
    private static readonly (BuyType Buy, bool Won, BombSite Site, int? Plant, int Contact, (int, int, double)[] Kills)[]
        _script =
        [
            (BuyType.Pistol, true, BombSite.A, 30, 15, [(0, 5, 10)]),
            (BuyType.Force, true, BombSite.B, 50, 12, []),
            (BuyType.Full, true, BombSite.A, 80, 20, [(5, 0, 18), (1, 5, 20)]),
            (BuyType.Full, false, default, null, 25, []),
            (BuyType.Eco, false, default, null, 8, []),
            (BuyType.Full, true, BombSite.A, 35, 30, []),
            (BuyType.Pistol, false, default, null, 15, []),
            (BuyType.Eco, false, default, null, 10, [(6, 1, 11)]),
            (BuyType.Full, true, default, null, 22, [(2, 7, 23)]),
            (BuyType.Force, true, default, null, 18, []),
            (BuyType.Full, true, default, null, 20, []),
            (BuyType.Full, false, default, null, 20, [])
        ];

    private static RoundFactsRows Rows()
    {
        List<RoundFacts> rounds = [];
        int ownScore = 0;
        int enemyScore = 0;
        for (int i = 0; i < _script.Length; i++)
        {
            int n = i + 1;
            var s = _script[i];
            bool aIsT = n <= 6;
            int aSide = aIsT ? 2 : 3;
            BuyType enemyBuy = n == 2 ? BuyType.Eco : s.Buy == BuyType.Pistol ? BuyType.Pistol : BuyType.Full;
            SideFacts own = new() { Side = aSide, Slots = _a, PlayersAtFreezeEnd = 5, BuyType = s.Buy, ScoreBefore = ownScore };
            SideFacts enemy = new()
            {
                Side = 5 - aSide, Slots = _b, PlayersAtFreezeEnd = 5, BuyType = enemyBuy, ScoreBefore = enemyScore
            };
            rounds.Add(new RoundFacts
            {
                Number = n,
                IsLive = true,
                FreezeEndTick = Freeze(n),
                EndTick = At(n, 100),
                FirstContactTick = At(n, s.Contact),
                PlantTick = s.Plant is { } p ? At(n, p) : null,
                PlantSite = s.Site,
                WinnerSide = s.Won ? aSide : 5 - aSide,
                Ct = aIsT ? enemy : own,
                T = aIsT ? own : enemy,
                Kills =
                [
                    .. s.Kills.Select(k => new KillStep
                    {
                        Tick = At(n, k.Item3),
                        AttackerSlot = k.Item1,
                        VictimSlot = k.Item2,
                        VictimSide = k.Item2 < 5 == aIsT ? 2 : 3,
                        CtAlive = 4,
                        TAlive = 4
                    })
                ]
            });
            if (s.Won)
            {
                ownScore++;
            }
            else
            {
                enemyScore++;
            }
        }

        return new RoundFactsRows { Schema = RoundFactsRecords.Schema, Clock = new RoundFactsClock { TickRate = Rate }, Rounds = rounds };
    }

    private static IReadOnlyList<TeamRound> Rounds() =>
        TeamRounds.FromRows(Demo, "abc", "de_nuke", Rows(), n => n <= 6 ? 2 : 3);

    private static string Shape(IEnumerable<TendencyBucket> buckets) =>
        string.Join(", ", buckets.Select(b => $"{b.Label}:{b.Count}"));

    [Test]
    public async Task PaceClass_CutsAtThirtyFiveAndSeventyFive_AndNoPlantIsLate()
    {
        using (Assert.Multiple())
        {
            await Assert.That(PaceDerivation.ClassOf(0)).IsEqualTo(PaceClass.Fast);
            await Assert.That(PaceDerivation.ClassOf(35)).IsEqualTo(PaceClass.Fast);
            await Assert.That(PaceDerivation.ClassOf(35.02)).IsEqualTo(PaceClass.Default);
            await Assert.That(PaceDerivation.ClassOf(75)).IsEqualTo(PaceClass.Default);
            await Assert.That(PaceDerivation.ClassOf(75.5)).IsEqualTo(PaceClass.Late);
            await Assert.That(PaceDerivation.ClassOf(null)).IsEqualTo(PaceClass.Late);
        }
    }

    [Test]
    public async Task Pace_SplitsTheTRoundsIntoClasses_PlantsBySite_AndContactByBuy()
    {
        List<TeamRound> t = [.. Rounds().Where(r => r.Side == 2)];
        IReadOnlyList<PlantDot> plants = PaceDerivation.Plants(t);
        IReadOnlyList<ContactByBuy> contact = PaceDerivation.ContactByBuy(t);
        ScoutingStripChart chart = PaceLayer.Chart(plants)!;
        using (Assert.Multiple())
        {
            await Assert.That(Shape(PaceDerivation.Classes(t))).IsEqualTo(
                "Fast execute, plant by 0:35:2, Default into execute, 0:35–1:15:1, Late or no plant:3");
            await Assert.That(string.Join(",", plants.Select(p => $"{p.Site}{p.Seconds}"))).IsEqualTo("A30,A35,B50,A80");
            await Assert.That(PaceDerivation.NoPlant(t).Select(r => r.Number)).IsEquivalentTo([4, 5]);
            await Assert.That(string.Join(", ", contact.Select(c => $"{c.Buy}:{c.MedianSeconds}/{c.Of}")))
                .IsEqualTo("Eco:8/1, Force:12/1, Full:25/3, Pistol:15/1");
            await Assert.That(chart.Lanes.Select(l => l.Label)).IsEquivalentTo(["A", "B"]);
            await Assert.That(chart.Lanes[0].Median).IsEqualTo(35);
            await Assert.That(chart.Lanes[0].Dots.Count(d => d.Filled)).IsEqualTo(3);
            await Assert.That(chart.Lanes[0].Bands.Count).IsEqualTo(2);
            await Assert.That(PaceLayer.Read(PaceDerivation.Classes(t), t.Count))
                .IsEqualTo("Most T rounds end late or with no plant; 2 of 6 rounds is a fast plant by 0:35.");
        }
    }

    [Test]
    public async Task Economy_ReadsTheBuyAfterAPistol_ForceRate_FullSave_AndPistols()
    {
        IReadOnlyList<TeamRound> rounds = Rounds();
        IReadOnlyList<AfterPistolRow> after = EconomyDerivation.AfterPistol(rounds);
        (IReadOnlyList<TeamRound> lostFull, IReadOnlyList<TeamRound> saved) = EconomyDerivation.FullSaves(rounds);
        ScoutingStackChart chart = EconomyLayer.Chart(after)!;
        using (Assert.Multiple())
        {
            await Assert.That(string.Join(" | ", after.Select(r => $"{r.Label}: {Shape(r.Buys.Where(b => b.Count > 0))} won {r.Won.Count}/{r.Rounds.Count}")))
                .IsEqualTo("Won pistol → R2: Force:1 won 1/1 | Won pistol → R3: Full:1 won 1/1 | Lost pistol → R2: Eco:1 won 0/1 | Lost pistol → R3: Full:1 won 1/1");
            await Assert.That(EconomyDerivation.Forces(EconomyDerivation.Buying(rounds.Where(r => r.Side == 2))).Count).IsEqualTo(1);
            await Assert.That(EconomyDerivation.Buying(rounds.Where(r => r.Side == 2)).Count).IsEqualTo(5);
            await Assert.That(EconomyDerivation.Forces(rounds).Count(r => r.Won)).IsEqualTo(2);
            await Assert.That(lostFull.Select(r => r.Number)).IsEquivalentTo([5]);
            await Assert.That(saved.Select(r => r.Number)).IsEquivalentTo([5]);
            // The mini: lost pistol over won pistol, one segment each, the rounds won at the end.
            await Assert.That(chart.Rows.Select(r => r.Label)).IsEquivalentTo(["Lost pistol → R2", "Won pistol → R2"]);
            await Assert.That(chart.Rows[0].Segments.Single().Step).IsEqualTo(0);
            await Assert.That(chart.Rows[1].Segments.Single().Step).IsEqualTo(2);
            await Assert.That(chart.Rows[0].Trailing).IsEqualTo("0%");
            await Assert.That(EconomyLayer.Read(after)).IsEqualTo("After a lost pistol they save 100% of the time in R2 and full-buy 100% of the time in R3.");
        }
    }

    [Test]
    public async Task EcoPosture_IsARushOrAStack_WhenAllButOneStandOnOneSite()
    {
        SetupArea[] rush = [SetupArea.B, SetupArea.B, SetupArea.B, SetupArea.B, SetupArea.Mid];
        SetupArea[] spread = [SetupArea.A, SetupArea.A, SetupArea.Mid, SetupArea.B, SetupArea.B];
        using (Assert.Multiple())
        {
            await Assert.That(EconomyDerivation.Posture(rush, 2)).IsEqualTo(EcoPosture.Rush);
            await Assert.That(EconomyDerivation.Posture(rush, 3)).IsEqualTo(EcoPosture.Stack);
            await Assert.That(EconomyDerivation.Posture(spread, 2)).IsEqualTo(EcoPosture.Spread);
            await Assert.That(EconomyDerivation.Posture([SetupArea.A, SetupArea.A, SetupArea.A], 2)).IsEqualTo(EcoPosture.Rush);
            await Assert.That(EconomyDerivation.Posture([SetupArea.A, SetupArea.A, SetupArea.B], 2)).IsEqualTo(EcoPosture.Spread);
            await Assert.That(EconomyDerivation.Posture([SetupArea.A, SetupArea.A], 2)).IsNull();
        }
    }

    [Test]
    public async Task EcoPosture_ReadsTheTeamsOwnPlayersAtTwentySeconds()
    {
        TeamRound eco = Rounds().Single(r => r.Number == 5); // A on T
        List<RoundPosition> at20 =
        [
            new(0, 10, 10, 0, 1), new(1, 11, 10, 0, 1), new(2, 12, 10, 0, 1), new(3, 13, 10, 0, 1), new(4, 0, 0, 0, 2),
            new(5, 99, 99, 0, 1)
        ];
        RoundPositionsDocument doc = new()
        {
            CadenceTicks = Rate,
            Places = ["", "BombsiteB", "Middle"],
            Rounds =
            [
                new RoundPositionsRound
                {
                    Number = 5,
                    FreezeEndTick = Freeze(5),
                    Ct = _b,
                    Pos = [.. Enumerable.Range(0, 21).Select(i => i == 20 ? at20 : new List<RoundPosition>())]
                }
            ]
        };
        IReadOnlyList<SetupSample> own = EconomyDerivation.OwnAt(doc, eco, EconomyDerivation.PostureSeconds);
        SetupAreaClassifier areas = SetupAreaClassifier.Learn([]);
        using (Assert.Multiple())
        {
            await Assert.That(own.Count).IsEqualTo(5);
            await Assert.That(own.Count(s => s.Place == "BombsiteB")).IsEqualTo(4);
            await Assert.That(EconomyDerivation.Posture([.. own.Select(areas.Of)], eco.Side)).IsEqualTo(EcoPosture.Rush);
        }
    }

    [Test]
    public async Task ScoreState_LeadsAndTrailsByThree_AndMatchPointIsHalfTheRegulation()
    {
        TeamRound r = Rounds()[0];
        r.Own.ScoreBefore = 12;
        r.Enemy.ScoreBefore = 9;
        using (Assert.Multiple())
        {
            await Assert.That(ScoreDerivation.StateOf(3, 0)).IsEqualTo(ScoreState.Leading);
            await Assert.That(ScoreDerivation.StateOf(2, 0)).IsEqualTo(ScoreState.Within);
            await Assert.That(ScoreDerivation.StateOf(5, 7)).IsEqualTo(ScoreState.Within);
            await Assert.That(ScoreDerivation.StateOf(4, 7)).IsEqualTo(ScoreState.Trailing);
            await Assert.That(ScoreDerivation.Label(ScoreState.Within)).IsEqualTo("Within 2");
            await Assert.That(ScoreDerivation.OnMatchPoint(r)).IsTrue();
            await Assert.That(ScoreDerivation.FacingMatchPoint(r)).IsFalse();
            await Assert.That(ScoreDerivation.StateOf(Rounds().Single(x => x.Number == 4))).IsEqualTo(ScoreState.Leading);
            await Assert.That(ScoreDerivation.StateOf(Rounds().Single(x => x.Number == 5))).IsEqualTo(ScoreState.Within);
        }
    }

    [Test]
    public async Task Impact_SplitsRoundsWonByTheOpening_AndCountsTrades()
    {
        TendencyRound Clip(int n) => new(Demo, "abc", n, Freeze(n), At(n, 100), Rate);
        OpeningDuelBlock block = new()
        {
            Map = "de_nuke",
            Side = 3,
            Duels =
            [
                new OpeningDuelRound(Clip(1), Clip(1), "k", "p", true, false, true, null),
                new OpeningDuelRound(Clip(2), Clip(2), "k", "p", true, false, false, null),
                new OpeningDuelRound(Clip(3), Clip(3), "k", "p", false, true, false, null),
                new OpeningDuelRound(Clip(4), Clip(4), "k", "p", false, false, false, null)
            ]
        };
        OpeningImpact impact = OpeningImpact.Of(block);
        ScoutingTableChart chart = ImpactLayer.Chart([impact]);
        using (Assert.Multiple())
        {
            await Assert.That(impact.High).IsEqualTo(50);
            await Assert.That(impact.Low).IsEqualTo(0);
            await Assert.That(impact.Traded.Select(r => r.RoundNumber)).IsEquivalentTo([3]);
            await Assert.That(chart.Rows.Select(r => $"{r.Label}: {r.First} | {r.Second}")).IsEquivalentTo(
                ["Won the opening: 50% (2) | —", "Lost the opening: 0% (2) | —", "Lost opening traded: 50% (2) | —"],
                CollectionOrdering.Matching);
            await Assert.That(ImpactLayer.Read([impact]))
                .IsEqualTo("Losing the opening costs them 50 points of round win % on CT (50% → 0%).");
        }
    }

    [Test]
    public async Task Score_ShowsEachCellsRounds_HidesThinCells_AndReadsTheBiggestSwingAgainstTheOverall()
    {
        // A's scores before each round: T r1-r3 within (won), r4 leading (lost), r5 within (lost), r6 within (won);
        // CT r7-r11 within (lost, lost, won, won, won), r12 leading (lost).
        IReadOnlyList<TeamRound> rounds = Rounds();
        ScoutingTableChart chart = ScoreLayer.Chart(rounds);
        using (Assert.Multiple())
        {
            await Assert.That(chart.Rows.Select(r => $"{r.Label}: {r.First} | {r.Second}")).IsEquivalentTo(
                [
                    "Overall: 50% (6) | 67% (6)", "Leading by 3+: — | —", "Within 2: 60% (5) | 80% (5)",
                    "Trailing by 3+: — | —"
                ],
                CollectionOrdering.Matching);
            await Assert.That(ScoreLayer.Read(rounds))
                .IsEqualTo("With the score within 2, their T win rate rises to 80% (+13 vs their 67% overall).");
            await Assert.That(ScoreLayer.Read([.. rounds.Where(r => r.Number <= 4)])).IsNull()
                .Because($"no state has {ScoreDerivation.MinRounds} rounds on a side");
        }
    }

    [Test]
    public async Task Rotations_DrawALanePerSite_AndNameWhoRotatesFirst()
    {
        SetupHeatmapRound R(int n) => new(Demo, "abc", n, Freeze(n), At(n, 100), Rate, true);
        SetupRotation[] rotations =
        [
            new(R(1), BombSite.A, 6, "p1", true), new(R(2), BombSite.A, 9, "p1", false),
            new(R(3), BombSite.B, 4, "p2", true), new(R(4), BombSite.B, null, null, false)
        ];
        ScoutingStripChart chart = RotationsLayer.Chart(rotations)!;
        (string Name, double Median, IReadOnlyList<SetupRotation> Rounds) fastest =
            RotationsLayer.Fastest([.. rotations.Where(r => r.ArrivalSeconds is not null)])!.Value;
        using (Assert.Multiple())
        {
            await Assert.That(chart.Lanes[0].Median).IsEqualTo(7.5);
            await Assert.That(chart.Lanes[1].Dots.Count).IsEqualTo(1);
            await Assert.That(chart.Lanes[0].Max).IsEqualTo(RotationsLayer.AxisSeconds);
            await Assert.That(fastest.Name).IsEqualTo("p1");
            await Assert.That(fastest.Median).IsEqualTo(7.5);
            await Assert.That(RotationsLayer.Chart([new SetupRotation(R(5), BombSite.A, null, null, false)])).IsNull();
        }
    }

    [Test]
    public async Task Timeouts_AreEmptyWithoutACaptureStore_AndTheSeamBuildsRows()
    {
        using Harness h = await Harness.Create();
        ScoutingReportData data = ScoutingReportData.Start(h.TeamA, "A",
            new ScoutingReportSources { Teams = h.Teams, Library = h.Cache.Library() }, _ => _inline);
        ScoutingLayerContext context = new(data, "de_nuke", ScoutingSide.T, ScoutingBuy.All);
        ScoutingLayerResult waiting = await new TimeoutsLayer().BuildAsync(context, CancellationToken.None);
        TendencyRound Clip(int n) => new(Demo, "abc", n, Freeze(n), At(n, 100), Rate);
        ScoutingLayerResult built = TimeoutsLayer.Build(context,
        [
            new TacticalTimeout(Clip(5), 2, true, "B execute"), new TacticalTimeout(Clip(9), 3, false, "B execute"),
            new TacticalTimeout(Clip(10), 3, true)
        ]);
        List<ScoutingInspectorRow> rows = [.. built.Sections.SelectMany(s => s.Rows)];
        using (Assert.Multiple())
        {
            await Assert.That(waiting.Status.State).IsEqualTo(ScoutingLayerState.Empty);
            await Assert.That(rows[0].Value).IsEqualTo("3 timeouts");
            await Assert.That(rows[0].Note).IsEqualTo("CT 2 · T 1");
            await Assert.That(rows[1].Value).IsEqualTo("2 of 3 rounds");
            await Assert.That(rows[2].Label).IsEqualTo("Usual call after the timeout · B execute");
            await Assert.That(rows.All(r => r.Rounds.Count > 0)).IsTrue();
            await Assert.That(built.Sections[1].Rows.Select(r => $"{r.Label}: {r.Value}")).IsEquivalentTo(
                [
                    "CT · called: 2 timeouts", "CT · round after won: 1 of 2 rounds", "T · called: 1 timeout",
                    "T · round after won: 1 of 1 round"
                ],
                CollectionOrdering.Matching);
            ScoutingTableChart chart = (ScoutingTableChart)built.MiniChart!;
            await Assert.That(chart.Rows.Select(r => $"{r.Label}: {r.First} | {r.Second}"))
                .IsEquivalentTo(["Called: 2 | 1", "Round after won: 1 of 2 | 1 of 1"], CollectionOrdering.Matching);
            await Assert.That(chart.Caption).EndsWith("usual call after one: B execute (2 of 3)");
        }
    }

    [Test]
    public async Task TheStrip_DrawsItsCharts_AndEveryNumberOpensItsRounds()
    {
        using Harness h = await Harness.Create();
        ReviewQueue queue = new(null);
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = new(h.Teams, h.Cache.Library(), new VetoHistoryStore(null), false,
            review: new ExtensionReviewQueue(queue), selectTab: _ => true, post: posted.Post,
            runSection: _ => _inline, roundFacts: h.Cache.RoundFacts());
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == h.TeamA);
        await ScoutingReportTestKit.Settle(vm, posted);

        IReadOnlyList<ScoutingInspectorRowViewModel> econ =
            await ScoutingReportTestKit.Inspect(vm, posted, "de_nuke", ScoutingSide.T, "econ");
        IReadOnlyList<ScoutingInspectorRowViewModel> pace =
            await ScoutingReportTestKit.Inspect(vm, posted, "de_nuke", ScoutingSide.T, "pace");
        IReadOnlyList<ScoutingInspectorRowViewModel> impact =
            await ScoutingReportTestKit.Inspect(vm, posted, "de_nuke", ScoutingSide.T, "impact");
        IReadOnlyList<ScoutingInspectorRowViewModel> score =
            await ScoutingReportTestKit.Inspect(vm, posted, "de_nuke", ScoutingSide.T, "score");
        using (Assert.Multiple())
        {
            await Assert.That(vm.Layers.StripMinis.Where(m => m.Id != "timeouts").All(m => m.HasMiniChart)).IsTrue()
                .Because("economy, pace, impact and score each draw a chart on T; timeouts need round captures");
            await Assert.That(vm.Layers.StripMinis.Select(m => m.Id)).Contains("timeouts");
            await Assert.That(vm.Layers.Find("econ")!.MiniChart).IsTypeOf<ScoutingStackChart>();
            await Assert.That(vm.Layers.Find("pace")!.MiniChart).IsTypeOf<ScoutingStripChart>();
            await Assert.That(vm.Layers.Find("impact")!.MiniChart).IsTypeOf<ScoutingTableChart>();
            await Assert.That(vm.Layers.Find("score")!.MiniChart).IsTypeOf<ScoutingTableChart>();

            await Assert.That(econ.Titled("de_nuke · CT force buys").Value).IsEqualTo("1 of 5 rounds");
            await Assert.That(econ.Titled("de_nuke · forces won").Value).IsEqualTo("2 of 2 rounds");
            await Assert.That(econ.Titled("de_nuke · full save after a lost full buy").Value).IsEqualTo("1 of 1 round");
            await Assert.That(econ.Titled("de_nuke · T pistols won").Value).IsEqualTo("1 of 1 round");
            await Assert.That(econ.Titled("de_nuke · lost pistol → r2").Value).IsEqualTo("won 0 of 1 round");
            await Assert.That(econ.Single(r => r.Label == "On their own eco").Note)
                .Contains("needs round positions");
            await Assert.That(pace.Titled("de_nuke T · fast execute, plant by 0:35").Value).IsEqualTo("2 of 6 rounds");
            await Assert.That(pace.Titled("de_nuke T · plants on A").Value).IsEqualTo("0:35");
            await Assert.That(pace.Titled("de_nuke T · no plant").Value).IsEqualTo("2 rounds, won 0");
            await Assert.That(pace.Titled("de_nuke T · first contact on a full buy").Value).IsEqualTo("median 0:25");
            await Assert.That(impact.Titled("de_nuke · T won after winning the opening").Value).IsEqualTo("1 of 1 round");
            await Assert.That(impact.Titled("de_nuke · CT won after losing the opening").Value).IsEqualTo("0 of 1 round");
            await Assert.That(impact.Titled("de_nuke · T opening deaths traded").Value).IsEqualTo("1 of 1 round");
            await Assert.That(score.Titled("de_nuke · T leading by 3+").Value).IsEqualTo("0 of 1 round");
            await Assert.That(econ.Concat(pace).Concat(impact).Concat(score).EveryNumberOpensItsRounds()).IsTrue();
        }

        score.Titled("de_nuke · T leading by 3+").OpenCommand.Execute(null);
        await Assert.That(queue.ClipCount).IsEqualTo(0).Because("no T round with a 3+ lead was won");
        pace.Titled("de_nuke T · no plant").OpenCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(queue.Clips.Select(c => c.Note))
                .IsEquivalentTo(["de_nuke T · no plant · round 4", "de_nuke T · no plant · round 5"]);
            await Assert.That(vm.ReviewLine).IsEqualTo("2 rounds sent to Review");
        }

        // The buy filter narrows the pace classes to the team's own full buys.
        vm.Buy = ScoutingBuy.Full;
        IReadOnlyList<ScoutingInspectorRowViewModel> full =
            await ScoutingReportTestKit.Inspect(vm, posted, "de_nuke", ScoutingSide.T, "pace");
        await Assert.That(full.Titled("de_nuke T · fast execute, plant by 0:35").Value).IsEqualTo("1 of 3 rounds");
    }

    private static DemoCacheRecord Record(string path, string map, RoundFactsRows? rows)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            Map = map,
            Sha256 = rows is null ? null : "abc"
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

    internal sealed class Harness : IDisposable
    {
        private Harness(DemoCacheStore cache, TeamIdentityService teams, Guid teamA)
        {
            Cache = cache;
            Teams = teams;
            TeamA = teamA;
        }

        public DemoCacheStore Cache { get; }
        public TeamIdentityService Teams { get; }
        public Guid TeamA { get; }

        public void Dispose() => Teams.Dispose();

        public static async Task<Harness> Create()
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: _inline);
            await teams.StartAsync();
            using (cache.BeginBatch())
            {
                cache.Upsert(Record(Demo, "de_nuke", Rows()));
                cache.Upsert(Record("/d/strip-other.dem", "de_mirage", null));
            }

            await teams.Idle;
            Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(1)) == true)).Id;
            return new Harness(cache, teams, teamA);
        }
    }
}

[NotInParallel]
[Category("Render")]
public class StripLayersRenderTests
{
    [Test]
    public async Task TheStrip_RendersItsMinis_OnTAndCt()
    {
        List<string> shown = [];
        await HeadlessSession.RunOnUi(async () =>
        {
            using StripLayersTests.Harness h = await StripLayersTests.Harness.Create();
            using ScoutingReportTabViewModel vm = new(h.Teams, h.Cache.Library(), new VetoHistoryStore(null), false,
                situational: new SituationalBehaviourService(h.Teams, h.Cache.Library(), h.Cache.RoundFacts()),
                runSection: _ => StripLayersTests._inline, roundFacts: h.Cache.RoundFacts(), now: () => new DateTime(2026, 3, 12));
            vm.SelectedTeam = vm.Teams.Single(t => t.Id == h.TeamA);
            Window window = new()
            {
                Width = 1440,
                Height = 1700,
                Content = new DemoViewer.NET.Extensions.StratBook.Views.ScoutingReport.ScoutingReportTabView { DataContext = vm }
            };
            window.Show();
            await vm.LoadTask;
            vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_nuke"));
            foreach (ScoutingSide side in (ScoutingSide[])[ScoutingSide.T, ScoutingSide.Ct])
            {
                vm.Side = side;
                for (int i = 0; i < 4; i++)
                {
                    await vm.LoadTask;
                    await vm.Layers.Idle;
                    UxConsistencyRenderTests.Settle();
                }

                vm.Layers.Focus(vm.Layers.Find(side == ScoutingSide.T ? "pace" : "impact")!);
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Save(window, $"ux-scouting-strip-{side.Label().ToLowerInvariant()}.png");
                shown.AddRange(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
                    .Select(t => t.Text ?? ""));
            }

            window.Close();
        });

        using (Assert.Multiple())
        {
            await Assert.That(shown).Contains("Lost pistol → R2");
            await Assert.That(shown).Contains("Leading by 3+");
            await Assert.That(shown.Any(t => t.StartsWith("plant time 0:00", StringComparison.Ordinal))).IsTrue();
            await Assert.That(shown.Any(t => t.StartsWith("round win % (rounds) after the opening duel", StringComparison.Ordinal))).IsTrue();
        }
    }
}
