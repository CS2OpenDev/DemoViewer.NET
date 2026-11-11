#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;
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
///     The Setups layer's per-player derivations on synthetic rounds (places held, CTs per area, rotations, the
///     area classifier, the capture from a positions file, the isolate fade), then the layer through the tab over
///     one synthetic demo: the Players, Shape and By buy tabs, the read for the buy in view, every number opens its
///     rounds, the player colours match the players table, the CT spot column fills, and "Show CT setup" isolates
///     one player from the T side and "Show all" puts everyone back. The Rotations layer's fake row reads its
///     fakes once per team load.
/// </summary>
[NotInParallel]
public class SetupsLayerTests
{
    private const int Rate = 64;
    private const string Demo = "/d/setups.dem";

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly int[] _teamASlots = [0, 1, 2, 3, 4];
    private static readonly int[] _teamBSlots = [5, 6, 7, 8, 9];

    // A site at x 0, B site at x 2000: mid is anything further than 800 from both.
    private static SetupSample At(int x, string? place) => new(x, 0, 0, place);

    private static SetupRoundPlayer Player(string key, IReadOnlyList<SetupSample> setup,
        params SetupTimedSample[] after) => new(key, key, setup, after);

    private static SetupHeatmapRound Round(int number, SetupRoundDetail? detail) =>
        new("/d/x.dem", null, number, number * 1000, number * 1000 + 500, Rate, detail is not null) { Detail = detail };

    private static SetupRoundDetail Detail(bool won, params SetupRoundPlayer[] players) =>
        new(won, BombSite.Unknown, null, players);

    private static SetupRoundDetail Planted(BombSite site, double plantAfter, params SetupRoundPlayer[] players) =>
        new(false, site, plantAfter, players);

    private static SetupSample[] Repeat(SetupSample sample, int count) => [.. Enumerable.Repeat(sample, count)];

    // One sample a second from contact, as the positions file has them.
    private static SetupTimedSample[] Track(IReadOnlyList<(int X, string? Place)> steps) =>
        [.. steps.Select((s, i) => new SetupTimedSample(i, At(s.X, s.Place)))];

    // ---- Derivations ----

    [Test]
    public async Task PlacesByPlayer_CountsEachPlayersMainPlacePerRound_WithASecondSpotOnlyPastAQuarter()
    {
        // Alice is on A site in 3 of 4 rounds and in Jungle in the fourth (25%, so it shows); Bob passes through
        // Window for one sample every round but holds B site, so Window never counts for him.
        SetupSample[] window = [At(1000, "Window")];
        List<SetupHeatmapRound> rounds =
        [
            Round(1, Detail(true, Player("alice", Repeat(At(0, "BombsiteA"), 5)), Player("bob", [.. window, .. Repeat(At(2000, "BombsiteB"), 4)]))),
            Round(2, Detail(true, Player("alice", Repeat(At(10, "BombsiteA"), 5)), Player("bob", [.. window, .. Repeat(At(2000, "BombsiteB"), 4)]))),
            Round(3, Detail(false, Player("alice", Repeat(At(20, "BombsiteA"), 5)), Player("bob", [.. window, .. Repeat(At(2000, "BombsiteB"), 4)]))),
            Round(4, Detail(false, Player("alice", Repeat(At(300, "Jungle"), 5)), Player("bob", [.. window, .. Repeat(At(2000, "BombsiteB"), 4)]))),
            Round(5, null)
        ];

        IReadOnlyList<SetupPlayerPlace> places = SetupsDerivation.PlacesByPlayer(rounds);
        SetupPlayerPlace aliceA = places.Single(p => p.Key == "alice" && p.IsMain);
        using (Assert.Multiple())
        {
            await Assert.That(places.Select(p => $"{p.Key}:{p.Place}:{p.Rounds.Count}/{p.Of}"))
                .IsEquivalentTo(["alice:BombsiteA:3/4", "alice:Jungle:1/4", "bob:BombsiteB:4/4"],
                    CollectionOrdering.Matching);
            await Assert.That(aliceA.IsFixed).IsTrue().Because("3 of 4 is past the 60% fixed share");
            await Assert.That(aliceA.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
            await Assert.That(aliceA.X).IsEqualTo(10f).Because("the main spot is the mean of the samples there");
            await Assert.That(places.Single(p => p.Place == "Jungle").IsMain).IsFalse();
        }
    }

    [Test]
    public async Task Formations_CountCtsPerArea_WithStacksNamed_AndWins()
    {
        // Two site samples teach the classifier the centres; Jungle (300) is on A, Window (1000) is mid.
        SetupRoundPlayer[] standard =
        [
            Player("a1", [At(0, "BombsiteA")]), Player("a2", [At(300, "Jungle")]), Player("m", [At(1000, "Window")]),
            Player("b1", [At(2000, "BombsiteB")]), Player("b2", [At(1900, "Apartments")])
        ];
        SetupRoundPlayer[] stack =
        [
            Player("a1", [At(0, "BombsiteA")]), Player("a2", [At(300, "Jungle")]), Player("m", [At(1000, "Window")]),
            Player("b1", [At(2000, "BombsiteB")]), Player("b2", [At(100, "Ticket")])
        ];
        List<SetupHeatmapRound> rounds =
        [
            Round(1, Detail(true, standard)), Round(2, Detail(true, standard)), Round(3, Detail(false, standard)),
            Round(4, Detail(false, stack))
        ];

        SetupAreaClassifier areas = SetupAreaClassifier.Learn(rounds);
        IReadOnlyList<SetupFormation> formations = SetupsDerivation.Formations(rounds, areas);
        using (Assert.Multiple())
        {
            await Assert.That(areas.HasSites).IsTrue();
            await Assert.That(formations.Select(f => $"{f.Label}:{f.Rounds.Count}:{f.Won}"))
                .IsEquivalentTo(["2-1-2:3:2", "3-1-1, A stack:1:0"], CollectionOrdering.Matching);
            await Assert.That(formations[1].Rounds.Single().RoundNumber).IsEqualTo(4);
        }
    }

    [Test]
    public async Task TheAreaClassifier_NamesSitesByPlace_ByDistanceOtherwise_AndMidWithoutBothCentres()
    {
        SetupAreaClassifier areas = SetupAreaClassifier.Learn(
            [Round(1, Detail(true, Player("a", [At(0, "BombsiteA")]), Player("b", [At(2000, "BombsiteB")])))]);
        SetupAreaClassifier onlyA = SetupAreaClassifier.Learn([Round(1, Detail(true, Player("a", [At(0, "BombsiteA")])))]);
        using (Assert.Multiple())
        {
            await Assert.That(areas.Of(At(5000, "BombsiteB"))).IsEqualTo(SetupArea.B).Because("the place wins");
            await Assert.That(areas.Of(At(790, "Jungle"))).IsEqualTo(SetupArea.A);
            await Assert.That(areas.Of(At(810, "Jungle"))).IsEqualTo(SetupArea.Mid);
            await Assert.That(areas.Of(At(1300, "Short"))).IsEqualTo(SetupArea.B);
            await Assert.That(areas.Of(new SetupSample(0, 0, 500, null))).IsEqualTo(SetupArea.Mid)
                .Because("height counts double, so a floor above the site is not on it");
            await Assert.That(onlyA.HasSites).IsFalse();
            await Assert.That(onlyA.Of(At(10, "Jungle"))).IsEqualTo(SetupArea.Mid);
        }
    }

    [Test]
    public async Task TheAreaClassifier_ReadsTheSiteRegionsFirst_SharedPlacesAndOthersByDistance()
    {
        SetupSiteRegions regions = new(
            new HashSet<string>(["BombsiteA", "Jungle", "Connector"], StringComparer.Ordinal),
            new HashSet<string>(["BombsiteB", "Short", "Connector"], StringComparer.Ordinal));
        List<SetupHeatmapRound> rounds =
            [Round(1, Detail(true, Player("a", [At(0, "BombsiteA")]), Player("b", [At(2000, "BombsiteB")])))];
        SetupAreaClassifier areas = SetupAreaClassifier.Learn(rounds, regions);
        SetupAreaClassifier distance = SetupAreaClassifier.Learn(rounds);
        using (Assert.Multiple())
        {
            await Assert.That(areas.HasRegions).IsTrue();
            await Assert.That(distance.HasRegions).IsFalse();
            await Assert.That(areas.Of(At(1000, "Jungle"))).IsEqualTo(SetupArea.A)
                .Because("Jungle is in A's region, however far it is from the A centre");
            await Assert.That(distance.Of(At(1000, "Jungle"))).IsEqualTo(SetupArea.Mid);
            await Assert.That(areas.Of(At(1000, "Short"))).IsEqualTo(SetupArea.B);
            await Assert.That(areas.Of(At(1000, "Connector"))).IsEqualTo(SetupArea.Mid)
                .Because("a place both regions hold falls back to distance");
            await Assert.That(areas.Of(At(1300, "Window"))).IsEqualTo(SetupArea.B)
                .Because("a place outside the regions falls back to distance");
            await Assert.That(areas.Of([At(1000, "Jungle"), At(1000, "Jungle"), At(1000, "Window")]))
                .IsEqualTo(SetupArea.A).Because("the dominant place's region decides a player's setup");
        }
    }

    [Test]
    public async Task FakeRotations_CountACtLeavingTheRealSiteBeforeThePlant_OnSuggestedFakeRounds()
    {
        SetupRoundPlayer stays = Player("b", [At(2000, "BombsiteB")], new SetupTimedSample(0, At(2000, "BombsiteB")),
            new SetupTimedSample(8, At(2000, "BombsiteB")));
        SetupRoundPlayer leaves = Player("b2", [At(1950, "BombsiteB")], new SetupTimedSample(0, At(1950, "BombsiteB")),
            new SetupTimedSample(4, At(1000, "Window")));
        SetupRoundPlayer late = Player("b3", [At(1950, "BombsiteB")], new SetupTimedSample(12, At(0, "BombsiteA")));
        SetupRoundPlayer anchorA = Player("a", [At(0, "BombsiteA")]);
        List<SetupHeatmapRound> rounds =
        [
            Round(1, Planted(BombSite.B, 10, anchorA, stays, leaves)), // fake A, real B: b2 left B at 4 s
            Round(2, Planted(BombSite.B, 10, anchorA, stays, late)), // fake A, real B: b3 left only after the plant
            Round(3, Planted(BombSite.A, 10, anchorA, stays)), // the fake's real site was B, the plant A: left out
            Round(4, Planted(BombSite.B, 10, anchorA)), // nobody on B to pull: left out
            Round(5, Planted(BombSite.B, 10, anchorA, leaves)) // no fake proposed
        ];
        FakeRound fake = new(0, BombSite.A, BombSite.B);
        SetupAreaClassifier areas = SetupAreaClassifier.Learn(rounds);

        IReadOnlyList<FakeRotation> fakes = SetupsDerivation.FakeRotations(rounds, areas,
            r => r.RoundNumber <= 4 ? fake with { Round = r.RoundNumber } : null);
        IReadOnlyList<FakeRound> read = FakeRound.From(
        [
            (7, 2, new Dictionary<string, string> { ["fake"] = "BombsiteA", ["real"] = "BombsiteB" }),
            (8, 3, new Dictionary<string, string> { ["fake"] = "BombsiteA", ["real"] = "BombsiteB" }),
            (9, 2, new Dictionary<string, string> { ["fake"] = "Mid", ["real"] = "BombsiteB" })
        ]);
        using (Assert.Multiple())
        {
            await Assert.That(fakes.Select(f => f.Round.RoundNumber)).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
            await Assert.That(fakes.Select(f => f.RotatedOff)).IsEquivalentTo([true, false], CollectionOrdering.Matching);
            await Assert.That(fakes[0].Rotator).IsEqualTo("b2");
            await Assert.That(read).IsEquivalentTo([new FakeRound(7, BombSite.A, BombSite.B)])
                .Because("only T-side proposals naming both sites count");
        }
    }

    [Test]
    public async Task Rotations_TimeTheFirstCtFromElsewhereOntoTheHitSite_AndWhetherItBeatThePlant()
    {
        SetupRoundPlayer anchorA = Player("a", [At(0, "BombsiteA")], new SetupTimedSample(0, At(0, "BombsiteA")));
        SetupRoundPlayer anchorB = Player("b", [At(2000, "BombsiteB")], new SetupTimedSample(0, At(2000, "BombsiteB")));
        List<SetupHeatmapRound> rounds =
        [
            // B hit: mid reaches B 6 s after contact, the A anchor at 9 s; the plant is at 10 s.
            Round(1, Planted(BombSite.B, 10, anchorA, anchorB,
                Player("m", [At(1000, "Window")],
                    Track([.. Enumerable.Repeat((1000, "Window"), 6), (1450, "BombsiteB")])),
                Player("a2", [At(100, "Ticket")],
                    Track([(100, "Ticket"), (500, null), (900, null), .. Enumerable.Repeat((1000, "Window"), 6), (1450, "BombsiteB")])))),
            // A hit: the B anchor gets there at 12 s, after the plant at 4 s.
            Round(2, Planted(BombSite.A, 4, anchorA,
                Player("b", [At(2000, "BombsiteB")],
                    Track([(2000, "BombsiteB"), (1600, null), (1200, null), .. Enumerable.Repeat((1000, "Window"), 9), (550, "BombsiteA")])))),
            // A hit with nobody off A: no rotation to time.
            Round(3, Planted(BombSite.A, 4, anchorA)),
            // B hit where the rotator never arrives inside the followed window.
            Round(4, Planted(BombSite.B, 4, anchorB, Player("m", [At(1000, "Window")], Track([.. Enumerable.Repeat((1000, "Window"), 4)])))),
            // No plant.
            Round(5, Detail(true, anchorA, anchorB))
        ];

        SetupAreaClassifier areas = SetupAreaClassifier.Learn(rounds);
        IReadOnlyList<SetupRotation> rotations = SetupsDerivation.Rotations(rounds, areas);
        using (Assert.Multiple())
        {
            await Assert.That(rotations.Select(r => r.Round.RoundNumber)).IsEquivalentTo([1, 2, 4], CollectionOrdering.Matching);
            await Assert.That(rotations[0].ArrivalSeconds).IsEqualTo(6);
            await Assert.That(rotations[0].Rotator).IsEqualTo("m");
            await Assert.That(rotations[0].BeforePlant).IsTrue();
            await Assert.That(rotations[1].ArrivalSeconds).IsEqualTo(12);
            await Assert.That(rotations[1].BeforePlant).IsFalse();
            await Assert.That(rotations[2].ArrivalSeconds).IsNull();
            await Assert.That(rotations[2].BeforePlant).IsFalse();
            await Assert.That(SetupsDerivation.Median(rotations.Select(r => r.ArrivalSeconds).OfType<double>()))
                .IsEqualTo(9);
            await Assert.That(SetupsDerivation.Median([])).IsNull();
        }
    }

    [Test]
    public async Task TheDots_LandWhereThePlayerStood_NotTheMeanOfAWalkRoundACorner()
    {
        // An L: up the corridor (x = 0), then along the site (y = 1000). The mean, (300, 600), is inside the wall.
        SetupSample[] walk =
        [
            new(0, 0, 0, "Hall"), new(0, 500, 0, "Hall"), new(0, 1000, 0, "BombsiteA"),
            new(500, 1000, 0, "BombsiteA"), new(1000, 1000, 0, "BombsiteA"), new(1000, 1000, 0, "BombsiteA")
        ];
        // A player who drops a floor: most of the window upstairs, a moment below.
        SetupSample[] floors =
        [
            new(4000, 0, 0, "Ramp"), new(4000, 20, 0, "Ramp"), new(4010, 10, 0, "Ramp"), new(4000, 0, -400, "Ramp")
        ];
        List<SetupHeatmapRound> rounds =
            [Round(1, Detail(true, Player("alice", walk), Player("bob", floors)))];
        IReadOnlyList<SetupPlayerPlace> places = SetupsDerivation.PlacesByPlayer(rounds);
        IReadOnlyList<ScoutingMapMark> marks =
            SetupsLayer.Marks(rounds, places, new Dictionary<string, ScoutingColor>(StringComparer.Ordinal), null);

        HashSet<(float, float, float)> stood = [.. walk.Concat(floors).Select(s => ((float)s.X, (float)s.Y, (float)s.Z))];
        using (Assert.Multiple())
        {
            await Assert.That(marks.All(m => stood.Contains((m.X, m.Y, m.Z)))).IsTrue()
                .Because("every dot and main spot is a position a player was actually seen at");
            await Assert.That(marks.Any(m => m.X == 300f && m.Y == 600f)).IsFalse();
            await Assert.That(marks.Where(m => m.X >= 4000f).All(m => m.Z == 0f)).IsTrue()
                .Because("the floor most of the window was spent on");
            await Assert.That(SetupsDerivation.HeldSpot(walk).Place).IsEqualTo("BombsiteA");
        }
    }

    [Test]
    public async Task Isolating_FadesEveryOtherPlayer_AndAKeyNotInTheScopeFadesNobody()
    {
        List<SetupHeatmapRound> rounds =
        [
            Round(1, Detail(true, Player("alice", [At(0, "BombsiteA")]), Player("bob", [At(2000, "BombsiteB")]))),
            Round(2, Detail(true, Player("alice", [At(10, "BombsiteA")]), Player("bob", [At(1990, "BombsiteB")])))
        ];
        IReadOnlyList<SetupPlayerPlace> places = SetupsDerivation.PlacesByPlayer(rounds);
        Dictionary<string, ScoutingColor> colors = new(StringComparer.Ordinal)
        {
            ["alice"] = ScoutingColor.Player1, ["bob"] = ScoutingColor.Player2
        };

        IReadOnlyList<ScoutingMapMark> all = SetupsLayer.Marks(rounds, places, colors, null);
        IReadOnlyList<ScoutingMapMark> alone = SetupsLayer.Marks(rounds, places, colors, "alice");
        IReadOnlyList<ScoutingMapMark> stranger = SetupsLayer.Marks(rounds, places, colors, "carol");
        List<ScoutingMapMark> bobs = [.. alone.Where(m => m.Color == ScoutingColor.Player2)];
        List<ScoutingMapMark> alices = [.. alone.Where(m => m.Color == ScoutingColor.Player1)];
        using (Assert.Multiple())
        {
            await Assert.That(all.Count).IsEqualTo(2 * 2 + 2 * 2).Because("a dot per player per round, a ringed dot per main spot");
            await Assert.That(all.Where(m => m.Color != ScoutingColor.Neutral).All(m => m.Opacity >= 0.8f)).IsTrue();
            await Assert.That(bobs.All(m => m.Opacity <= SetupsLayer.IsolatedFade)).IsTrue();
            await Assert.That(alices.All(m => m.Opacity >= 0.8f)).IsTrue();
            await Assert.That(alone[^1].Color).IsEqualTo(ScoutingColor.Player1).Because("the isolated player draws on top");
            await Assert.That(stranger.Select(m => m.Opacity)).IsEquivalentTo(all.Select(m => m.Opacity), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task TheCapture_KeysPlayersBySteamId_AndTimesTheirSamplesFromContact()
    {
        RoundPositionsDocument positions = new()
        {
            CadenceTicks = Rate,
            Places = ["BombsiteA", "Window"],
            Players = [new RoundPositionsPlayer(5, 76561198000000005, "five"), new RoundPositionsPlayer(6, 0, "bot")],
            Rounds =
            [
                new RoundPositionsRound
                {
                    Number = 1, FreezeEndTick = 1000, Ct = [5, 6],
                    Pos = Steps(70, s => [new RoundPosition(5, s, 0, 0, 0), new RoundPosition(6, 1000, 0, 0, 1), new RoundPosition(0, 9, 9, 9, 1)])
                }
            ]
        };
        RoundFacts round = new()
        {
            Number = 1, IsLive = true, FreezeEndTick = 1000, FirstContactTick = 1000 + 40 * Rate + 10,
            PlantTick = 1000 + 50 * Rate, PlantSite = BombSite.B, EndTick = 1000 + 60 * Rate, WinnerSide = 3
        };
        (int from, int to) = SetupHeatmapService.SetupWindow(round, Rate);

        SetupRoundDetail detail = SetupRoundCapture.Capture(positions, positions.Rounds[0], round, Rate, from, to)!;
        SetupRoundPlayer five = detail.Players[0];
        using (Assert.Multiple())
        {
            await Assert.That(detail.Won).IsTrue();
            await Assert.That(detail.PlantSite).IsEqualTo(BombSite.B);
            await Assert.That(detail.PlantAfterContactSeconds!.Value).IsEqualTo((10 * Rate - 10) / (double)Rate);
            await Assert.That(detail.Players.Select(p => p.Key)).IsEquivalentTo(["76561198000000005", "name:bot"], CollectionOrdering.Matching)
                .Because("a T tuple is not captured, and a bot is keyed by name");
            await Assert.That(five.Setup.Count).IsEqualTo(21).Because("steps 15 to 35 s");
            await Assert.That(five.Setup[0]).IsEqualTo(new SetupSample(15, 0, 0, "BombsiteA"));
            await Assert.That(five.AfterContact[0].Seconds).IsEqualTo((Rate - 10) / (double)Rate)
                .Because("the first step at or past contact is 41 s, a little under a second after it");
            await Assert.That(five.AfterContact[^1].Sample.X).IsEqualTo(60).Because("followed to the round's end");
        }
    }

    [Test]
    public async Task ACutOffRound_KeepsItsPositions_ButIsNotADecidedRound()
    {
        RoundPositionsDocument positions = new()
        {
            CadenceTicks = Rate,
            Places = ["BombsiteA"],
            Players = [new RoundPositionsPlayer(5, 76561198000000005, "five")],
            Rounds =
            [
                new RoundPositionsRound
                {
                    Number = 1, FreezeEndTick = 1000, Ct = [5],
                    Pos = Steps(70, s => [new RoundPosition(5, s, 0, 0, 0)])
                }
            ]
        };
        RoundFacts round = new() { Number = 1, IsLive = true, FreezeEndTick = 1000, WinnerSide = 0 };

        SetupRoundDetail detail = SetupRoundCapture.Capture(positions, positions.Rounds[0], round, Rate, 1000, 1000 + 20 * Rate)!;

        using (Assert.Multiple())
        {
            await Assert.That(detail.Decided).IsFalse();
            await Assert.That(detail.Won).IsFalse();
            await Assert.That(detail.Players[0].Setup.Count).IsGreaterThan(0);
        }
    }

    // ---- Through the tab ----

    [Test]
    public async Task TheRotationsFakeRow_ReadsSuggestedTagsFakes_AndSaysHowManyDemosHadThem()
    {
        using Harness h = await Harness.Create();
        QueuedPost posted = new();
        List<string> asked = [];
        using ScoutingReportTabViewModel vm = new(h.Teams, h.Cache.Library(), new VetoHistoryStore(null), false,
            h.Service, null, _ => true, () => new SetupHeatmapRenderer(_ => null), posted.Post,
            fakes: (path, _) =>
            {
                lock (asked)
                {
                    asked.Add(path);
                }

                return [new FakeRound(1, BombSite.A, BombSite.B), new FakeRound(4, BombSite.A, BombSite.B)];
            });
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == h.TeamB);
        await ScoutingReportTestKit.Settle(vm, posted);
        IReadOnlyList<ScoutingInspectorRowViewModel> rows =
            await ScoutingReportTestKit.Inspect(vm, posted, "de_mirage", ScoutingSide.Ct, "rotations");
        ScoutingInspectorRow fake = rows.Single(r => r.Label == "Rotated off a site on a fake").Row!;
        int reads = asked.Count;
        await ScoutingReportTestKit.Inspect(vm, posted, "de_mirage", ScoutingSide.Ct, "rotations");
        using (Assert.Multiple())
        {
            await Assert.That(fake.IsPlaceholder).IsFalse();
            await Assert.That(fake.Value).EndsWith("of 2 rounds").Because("rounds 1 and 4 are B hits after a fake on A");
            await Assert.That(fake.Note!).Contains("current for 1 of 1 demo");
            await Assert.That(fake.Rounds.All(r => r.RoundNumber is 1 or 4)).IsTrue();
            await Assert.That(asked.Count).IsEqualTo(reads).Because("the fakes are read once per team load");
        }
    }

    [Test]
    public async Task TheLayer_DrawsPlayerColours_InThreeTabs_AndEveryNumberOpensItsRounds_AndShowCtSetupIsolates()
    {
        using Harness h = await Harness.Create();
        ReviewQueue queue = new(null);
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = new(h.Teams, h.Cache.Library(), new VetoHistoryStore(null), false,
            h.Service, new ExtensionReviewQueue(queue), _ => true, () => new SetupHeatmapRenderer(_ => null),
            posted.Post);

        vm.SelectedTeam = vm.Teams.Single(t => t.Id == h.TeamB);
        await ScoutingReportTestKit.Settle(vm, posted);
        IReadOnlyList<ScoutingInspectorRowViewModel> rows =
            await ScoutingReportTestKit.Inspect(vm, posted, "de_mirage", ScoutingSide.Ct, "setups");
        ScoutingInspectorRowViewModel anchor = rows.Single(r => r.Label == "A site · p5");
        ScoutingInspectorRowViewModel standard = rows.Single(r => r.Label == "2-1-2");
        ScoutingInspectorRowViewModel byBuy = rows.Single(r => r.Row?.OpenTitle == "de_mirage · full buy");
        List<string> reads = [.. vm.Layers.Inspector.Reads.Select(r => r.Label)];
        List<string> tabs = [.. vm.Layers.Inspector.Sections.Select(s => $"{s.Tab}:{s.Title}")];
        using (Assert.Multiple())
        {
            await Assert.That(reads).IsEquivalentTo(["Full buy CT: p5 holds A site in 4 of 4 rounds; p6 holds Jungle in 4 of 4."])
                .Because("with every buy in view there is one read, the most sampled buy's; here that is the only buy");
            await Assert.That(tabs).IsEquivalentTo(
            [
                "Players:Places held in the setup window · all buys", "Shape:CTs per area · A · mid · B",
                "By buy:Rounds by buy"
            ], CollectionOrdering.Matching);
            await Assert.That(vm.Layers.Inspector.Tabs.Select(t => t.Name))
                .IsEquivalentTo(["Players", "Shape", "By buy"], CollectionOrdering.Matching);
            await Assert.That(rows.Any(r => r.Row?.Label.Contains("rotat", StringComparison.OrdinalIgnoreCase) == true)).IsFalse()
                .Because("rotations are the Rotations layer's");
            await Assert.That(byBuy.Label).IsEqualTo("Full buy");
            await Assert.That(byBuy.Value).IsEqualTo("4 rounds · 4 sampled");
            await Assert.That(byBuy.Row!.Note).IsEqualTo("p5 A site · p6 Jungle");
            await Assert.That(byBuy.Row.Swatch).IsNull().Because("with every buy in view no buy stands out");
            await Assert.That(anchor.Value).IsEqualTo("4 of 4 rounds");
            await Assert.That(anchor.Color).IsEqualTo(ScoutingColor.Player1);
            await Assert.That(standard.Value).IsEqualTo("3 of 4 rounds");
            await Assert.That(standard.Row!.Note).IsEqualTo("won 67% (2 of 3)");
            await Assert.That(standard.Row.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
            await Assert.That(rows.Single(r => r.Label == "3-1-1, A stack").Row!.Rounds.Single().RoundNumber).IsEqualTo(4);
            await Assert.That(rows.EveryNumberOpensItsRounds()).IsTrue();
            await Assert.That(vm.Players.Select(p => $"{p.Name}:{p.Color}:{p.CtSpot}")).IsEquivalentTo(
                ["p5:Player1:A site", "p6:Player2:Jungle", "p7:Player3:Window", "p8:Player4:B site", "p9:Player5:B site"],
                CollectionOrdering.Matching).Because("the table's swatches are the marks' colours");
            await Assert.That(vm.Players[0].CtSpotNote).IsEqualTo("held in 4 of 4 CT rounds (all buys)");
        }

        standard.OpenCommand.Execute(null);
        await Assert.That(queue.Clips.Select(c => c.FromTick)).IsEquivalentTo(
            [Harness.FreezeEnd(1), Harness.FreezeEnd(2), Harness.FreezeEnd(3)], CollectionOrdering.Matching);

        await Assert.That(vm.Editor.Findings.Any(f => f.Key == "de_mirage|CT|setups|Full||read")).IsTrue()
            .Because("the suggested read is filed under its buy");

        // One buy in view: its read alone, the places follow it, and its By buy row says so.
        vm.Buy = ScoutingBuy.Full;
        await ScoutingReportTestKit.Settle(vm, posted);
        ScoutingInspectorRow fullRow = vm.Layers.Inspector.Sections.SelectMany(s => s.Rows)
            .Single(r => r.Row?.OpenTitle == "de_mirage · full buy").Row!;
        using (Assert.Multiple())
        {
            await Assert.That(vm.Layers.Inspector.Reads.Select(r => r.Label))
                .IsEquivalentTo(["Full buy CT: p5 holds A site in 4 of 4 rounds; p6 holds Jungle in 4 of 4."]);
            await Assert.That(vm.Layers.Inspector.Sections.Select(s => s.Title))
                .Contains("Places held in the setup window · full buy");
            await Assert.That(fullRow.Note).IsEqualTo("in view · p5 A site · p6 Jungle");
            await Assert.That(fullRow.Swatch).IsEqualTo(ScoutingColor.Ct);
            await Assert.That(fullRow.KeyText).IsEqualTo("rounds");
        }

        vm.Buy = ScoutingBuy.All;
        await ScoutingReportTestKit.Settle(vm, posted);

        // From the T side, Show CT setup switches to CT, inspects Setups and fades everyone else.
        ScoutingLayerChipViewModel setups = vm.Layers.Find("setups")!;
        vm.Side = ScoutingSide.T;
        await ScoutingReportTestKit.Settle(vm, posted);
        PlayerRowViewModel p7 = vm.Players.Single(p => p.Name == "p7");
        p7.IsolateCommand!.Execute(p7);
        await ScoutingReportTestKit.Settle(vm, posted);
        List<ScoutingMapMark> mine = [.. setups.Marks.Marks.Where(m => m.Color == ScoutingColor.Player3)];
        List<ScoutingMapMark> others =
            [.. setups.Marks.Marks.Where(m => m.Color is >= ScoutingColor.Player1 and <= ScoutingColor.Player5 && m.Color != ScoutingColor.Player3)];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Side).IsEqualTo(ScoutingSide.Ct);
            await Assert.That(vm.Layers.Focused).IsEqualTo(setups);
            await Assert.That(setups.Marks.Visible).IsTrue();
            await Assert.That(mine.Count).IsGreaterThan(0);
            await Assert.That(mine.All(m => m.Opacity >= 0.8f)).IsTrue();
            await Assert.That(others.All(m => m.Opacity <= SetupsLayer.IsolatedFade)).IsTrue();
            await Assert.That(vm.Players.Single(p => p.Name == "p7").IsolateLabel).IsEqualTo("Show all");
            await Assert.That(vm.Players.Count(p => p.IsIsolated)).IsEqualTo(1);
        }

        // Show all puts everyone back without leaving CT.
        PlayerRowViewModel again = vm.Players.Single(p => p.Name == "p7");
        again.IsolateCommand!.Execute(again);
        await ScoutingReportTestKit.Settle(vm, posted);
        using (Assert.Multiple())
        {
            await Assert.That(setups.Marks.Marks.Where(m => m.Color != ScoutingColor.Neutral).All(m => m.Opacity >= 0.8f)).IsTrue();
            await Assert.That(vm.Players.Any(p => p.IsIsolated)).IsFalse();
            await Assert.That(vm.Players[0].IsolateLabel).IsEqualTo("Show CT setup");
            await Assert.That(vm.Side).IsEqualTo(ScoutingSide.Ct);
        }
    }

    private static List<List<RoundPosition>> Steps(int count, Func<int, IEnumerable<RoundPosition>> at) =>
        [.. Enumerable.Range(0, count).Select(step => at(step).ToList())];

    private sealed class Harness : IDisposable
    {
        private const int Base = 10000;
        private const int RoundLength = 100 * Rate;

        private Harness(DemoCacheStore cache, TeamIdentityService teams, SetupHeatmapService service, Guid teamB)
        {
            Cache = cache;
            Teams = teams;
            Service = service;
            TeamB = teamB;
        }

        public DemoCacheStore Cache { get; }
        public TeamIdentityService Teams { get; }
        public SetupHeatmapService Service { get; }
        public Guid TeamB { get; }

        public static int FreezeEnd(int round) => Base + round * RoundLength;

        public void Dispose() => Teams.Dispose();

        private static string Id(int n) => $"7656{n:D4}";

        // Rounds 1-4, team B (slots 5..9) on CT at full buy. Round 1: B planted 10 s after contact, p7 on B at 6 s.
        // Round 2: A planted 20 s after contact, p8 on A at 12 s. Round 3: no plant. Round 4 (A stack, p9 on A):
        // B planted 5 s after contact, p7 on B at 8 s. Rounds 1 and 2 won.
        private static RoundFacts Round(int number, BombSite site, int? plantAfterContact, int winner) => new()
        {
            Number = number,
            IsLive = true,
            FreezeEndTick = Base + number * RoundLength,
            FirstContactTick = Base + number * RoundLength + 40 * Rate,
            PlantTick = plantAfterContact is { } p ? Base + number * RoundLength + (40 + p) * Rate : null,
            PlantSite = site,
            EndTick = Base + number * RoundLength + 90 * Rate,
            WinnerSide = winner,
            Ct = new SideFacts { Side = 3, Slots = _teamBSlots, BuyType = BuyType.Full },
            T = new SideFacts { Side = 2, Slots = _teamASlots, BuyType = BuyType.Full }
        };

        private static DemoCacheRecord Record(string path, int day, RoundFactsRows? rows)
        {
            DemoCacheRecord record = new()
            {
                Path = path,
                Size = 1000,
                ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
                Map = "de_mirage",
                Sha256 = rows is null ? null : "abc123"
            };
            if (rows is not null)
            {
                record.SetRoundFacts(rows);
            }

            for (int slot = 0; slot < 10; slot++)
            {
                record.Players.Add(new CachedPlayerInfo
                {
                    Slot = slot, Name = $"p{slot}", SteamId64 = Id(slot < 5 ? slot + 1 : slot + 6), Team = slot < 5 ? 2 : 3
                });
            }

            DemoCacheStore.StampParse(record);
            DemoCacheStore.StampAnalysis(record);
            return record;
        }

        // Places: 0 BombsiteA (x 0), 1 BombsiteB (x 2000), 2 Window (x 1000), 3 Jungle (x 300).
        private static RoundPositionsRound Positions(int number, Func<int, int, (int X, int Place)> where) => new()
        {
            Number = number,
            FreezeEndTick = Base + number * RoundLength,
            Ct = _teamBSlots,
            Pos = Steps(90, step => _teamBSlots.Select(slot =>
            {
                (int x, int place) = where(slot, step);
                return new RoundPosition(slot, x, 0, 0, place);
            }).Append(new RoundPosition(0, 5000, 0, 0, 2)))
        };

        // A walk a rotation counts (one step a second, never faster than a player runs): out of Window at
        // `leave`, onto B at `arrive`.
        private static (int, int) WindowToB(int step, int leave, int arrive) =>
            step >= arrive + 1 ? (1950, 1)
            : step == arrive ? (1600, 1)
            : step >= leave ? (1160, 2)
            : Standard(7);

        // p8 off B from 8 s after contact, through Window, onto A at 12 s.
        private static (int, int) BToA(int step) => step switch
        {
            >= 53 => (30, 0),
            52 => (700, 0),
            >= 49 => (1150, 2),
            48 => (1600, 2),
            _ => Standard(8)
        };

        private static (int, int) Standard(int slot) => slot switch
        {
            5 => (0, 0),
            6 => (300, 3),
            7 => (1000, 2),
            _ => (2000, 1)
        };

        public static async Task<Harness> Create()
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: _inline);
            await teams.StartAsync();
            using (cache.BeginBatch())
            {
                cache.Upsert(Record(Demo, 0, new RoundFactsRows
                {
                    Schema = RoundFactsRecords.Schema,
                    Clock = new RoundFactsClock { TickRate = Rate },
                    Rounds =
                    [
                        Round(1, BombSite.B, 10, 3), Round(2, BombSite.A, 20, 3), Round(3, BombSite.Unknown, null, 2),
                        Round(4, BombSite.B, 5, 2)
                    ]
                }));
                cache.Upsert(Record("/d/other.dem", 1, null));
            }

            await teams.Idle;

            RoundIndexStore store = new(cache.Data());
            RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
            store.WritePositions(Demo, new RoundPositionsDocument
            {
                Fingerprint = sources.FingerprintFor("de_mirage"),
                Demo = new RoundPositionsDemo { StableKey = DemoCacheStore.StableKey(Demo), Sha256 = "abc123" },
                CadenceTicks = Rate,
                Places = ["BombsiteA", "BombsiteB", "Window", "Jungle"],
                Players = [.. _teamBSlots.Select(s => new RoundPositionsPlayer(s, ulong.Parse(Id(s + 6), System.Globalization.CultureInfo.InvariantCulture), $"p{s}"))],
                Rounds =
                [
                    Positions(1, (slot, step) => slot == 7 ? WindowToB(step, 45, 46) : Standard(slot)),
                    Positions(2, (slot, step) => slot == 8 ? BToA(step) : slot == 7 && step >= 55 ? (20, 0) : Standard(slot)),
                    Positions(3, (slot, _) => Standard(slot)),
                    Positions(4, (slot, step) => slot == 9 ? (100, 0) : slot == 7 ? WindowToB(step, 45, 48) : Standard(slot))
                ]
            });
            SetupHeatmapService service = new(teams, cache.Library(), cache.RoundFacts(), store, sources.FingerprintFor);
            Guid teamB = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(11)) == true)).Id;
            return new Harness(cache, teams, service, teamB);
        }
    }
}
