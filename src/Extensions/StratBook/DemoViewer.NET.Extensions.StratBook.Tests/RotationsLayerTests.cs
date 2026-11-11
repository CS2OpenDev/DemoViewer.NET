#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Services.Facts;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Rotations layer on synthetic rounds: which movements the rule counts as a rotation and which it rejects,
///     the trail being the player's own samples, the marks landing only on those samples and the straight lines
///     between them, and the inspector grouping first rotators by SteamID under the roster's name.
/// </summary>
public class RotationsLayerTests
{
    private const int Rate = 64;

    // A site at x 0, B site at x 4000; "Mid" and unplaced samples go by distance from the two centres.
    private static SetupSample At(int x, string? place) => new(x, 0, 0, place);

    private static SetupRoundPlayer Player(string key, IReadOnlyList<SetupSample> setup,
        IReadOnlyList<SetupTimedSample> after, string? name = null) => new(key, name ?? key, setup, after);

    // One sample a second from contact.
    private static SetupTimedSample[] Track(IReadOnlyList<(int X, string? Place)> steps, int from = 0) =>
        [.. steps.Select((s, i) => new SetupTimedSample(from + i, At(s.X, s.Place)))];

    private static SetupHeatmapRound Round(int number, SetupRoundDetail detail) =>
        new("/d/r.dem", null, number, number * 1000, number * 1000 + 500, Rate, true) { Detail = detail };

    private static readonly SetupRoundPlayer _anchorA =
        Player("a", [.. Enumerable.Repeat(At(0, "BombsiteA"), 20)], Track([(0, "BombsiteA")]));

    private static readonly SetupRoundPlayer _anchorB =
        Player("b", [.. Enumerable.Repeat(At(4000, "BombsiteB"), 20)], Track([(4000, "BombsiteB")]));

    private static readonly SetupSample[] _mid = [At(2000, "Mid"), At(2000, "Mid")];

    // From mid onto B in 3 s: the one real rotation.
    private static SetupRoundPlayer Rotator(string key = "rot", string? name = null, int arriveX = 2650) =>
        Player(key, _mid, Track([(2000, "Mid"), (2000, "Mid"), (2200, "Mid"), (arriveX, "BombsiteB")]), name);

    [Test]
    public async Task TheRule_CountsOnlyAContinuousWalkFromOffTheSiteOntoIt_StartedBeforeThePlant()
    {
        SetupRoundPlayer rotator = Rotator();
        SetupRoundPlayer near = Player("near", _mid, Track([(2000, "Mid"), (2300, "BombsiteB")]));
        SetupRoundPlayer gap = Player("gap", _mid,
        [
            new SetupTimedSample(0, At(2000, "Mid")), new SetupTimedSample(1, At(2000, "Mid")),
            new SetupTimedSample(3, At(2200, "Mid")), new SetupTimedSample(4, At(2650, "BombsiteB"))
        ]);
        SetupRoundPlayer teleport = Player("teleport", _mid, Track([(2000, "Mid"), (3900, "BombsiteB")]));
        SetupRoundPlayer late = Player("late", _mid,
            Track([.. Enumerable.Repeat((2000, "Mid"), 12), (2200, "Mid"), (2650, "BombsiteB")]));
        SetupRoundPlayer untracked = Player("untracked", _mid, Track([(2000, "Mid"), (2650, "BombsiteB")], from: 3));
        SetupRoundPlayer onSite = Player("onsite", [At(3900, "BombsiteB")], Track([(3900, "BombsiteB")]));
        SetupRoundPlayer wasThere = Player("wasthere", [.. _mid, At(3900, "BombsiteB")],
            Track([(2000, "Mid"), (2200, "Mid"), (2650, "BombsiteB")]));
        SetupRoundPlayer wrongWay = Player("wrongway", _mid,
            Track([(2000, "Mid"), (1600, null), (1200, null), (800, "BombsiteA")]));
        SetupRoundPlayer[] players = [_anchorA, _anchorB, rotator, near, gap, teleport, late, untracked, onSite, wasThere, wrongWay];
        List<SetupHeatmapRound> rounds = [Round(1, new SetupRoundDetail(false, BombSite.B, 10, players))];
        SetupAreaClassifier areas = SetupAreaClassifier.Learn(rounds);

        SetupRotation rotation = SetupsDerivation.Rotations(rounds, areas).Single();
        using (Assert.Multiple())
        {
            await Assert.That(players.Where(p => RotationRule.IsCandidate(p, SetupArea.B, areas)).Select(p => p.Key))
                .IsEquivalentTo(["a", "rot", "near", "gap", "teleport", "late", "wrongway"], CollectionOrdering.Matching)
                .Because("on the site, not seen at contact, or on the site just before contact is no rotator");
            await Assert.That(rotation.Trails.Select(t => t.Key)).IsEquivalentTo(["rot"])
                .Because("too close, a gap, a teleport, a walk begun after the plant and the wrong site do not count");
            await Assert.That(rotation.ArrivalSeconds).IsEqualTo(3);
            await Assert.That(rotation.Rotator).IsEqualTo("rot");
            await Assert.That(rotation.RotatorKey).IsEqualTo("rot");
            await Assert.That(rotation.BeforePlant).IsTrue();
            await Assert.That(rotation.Trails[0].Samples).IsEquivalentTo(rotator.AfterContact, CollectionOrdering.Matching)
                .Because("the trail is the player's own samples, contact to arrival, untouched");
        }
    }

    [Test]
    public async Task TheTrail_StopsAtTheFirstSampleOnTheSite_AndALateArrivalStillCountsWhenTheWalkBeganBeforeThePlant()
    {
        // Leaves at 1 s, the plant is at 2 s, on B at 4 s; then off the site and back on, which never shows.
        SetupRoundPlayer slow = Player("slow", _mid,
            Track([(2000, "Mid"), (2200, "Mid"), (2200, "Mid"), (2200, "Mid"), (2650, "BombsiteB"), (2200, "Mid"), (2700, "BombsiteB")]));
        List<SetupHeatmapRound> rounds = [Round(1, new SetupRoundDetail(false, BombSite.B, 2, [_anchorA, _anchorB, slow]))];

        SetupRotation rotation = SetupsDerivation.Rotations(rounds, SetupAreaClassifier.Learn(rounds)).Single();
        using (Assert.Multiple())
        {
            await Assert.That(rotation.Trails.Single().Samples.Count).IsEqualTo(5);
            await Assert.That(rotation.ArrivalSeconds).IsEqualTo(4);
            await Assert.That(rotation.BeforePlant).IsFalse();
        }
    }

    [Test]
    public async Task TheMarks_LandOnlyOnTheSamples_AndTheStraightLinesBetweenThem()
    {
        SetupRoundPlayer rotator = Rotator();
        List<SetupHeatmapRound> rounds =
            [Round(1, new SetupRoundDetail(false, BombSite.B, 10, [_anchorA, _anchorB, rotator]))];
        IReadOnlyList<SetupRotation> rotations = SetupsDerivation.Rotations(rounds, SetupAreaClassifier.Learn(rounds));
        Dictionary<string, ScoutingColor> colors = new(StringComparer.Ordinal) { ["rot"] = ScoutingColor.Player3 };

        IReadOnlyList<ScoutingMapMark> marks = RotationsLayer.Marks(rotations, colors);
        HashSet<float> sampled = [.. rotator.AfterContact.Select(t => (float)t.Sample.X)];
        List<ScoutingMapMark> samples = [.. marks.Where(m => m.Shape == ScoutingMarkShape.Dot && m.Size == RotationsLayer.SampleSize)];
        using (Assert.Multiple())
        {
            await Assert.That(samples.Select(m => m.X)).IsEquivalentTo(rotator.AfterContact.Select(t => (float)t.Sample.X),
                CollectionOrdering.Matching).Because("a sample dot for each sample, in order");
            await Assert.That(marks.All(m => m.Y == 0 && m.X >= 2000 && m.X <= 2650)).IsTrue()
                .Because("the joining dots lie on the line between two consecutive samples");
            await Assert.That(marks.All(m => m.Color == ScoutingColor.Player3)).IsTrue();
            await Assert.That(marks.Single(m => m.Shape == ScoutingMarkShape.Ring).X).IsEqualTo(2000f);
            await Assert.That(marks[^1].X).IsEqualTo(2650f).Because("the arrival dot draws last, on top");
            await Assert.That(marks[^1].Size).IsEqualTo(RotationsLayer.ArrivalSize);
            await Assert.That(sampled.Contains(marks[^1].X)).IsTrue();
            await Assert.That(samples.All(m => m.Opacity < 1f)).IsTrue().Because("one round's trail is faint");
        }
    }

    [Test]
    public async Task TheInspector_LeadsWithTheRead_PerSite_AndOneRowPerPlayerUnderTheRosterName()
    {
        // One player under two in-game names, first onto B in rounds 1 and 2; nobody reaches A in round 3.
        List<SetupHeatmapRound> rounds =
        [
            Round(1, new SetupRoundDetail(false, BombSite.B, 10, [_anchorA, _anchorB, Rotator("765", "sandman")])),
            Round(2, new SetupRoundDetail(false, BombSite.B, 2, [_anchorA, _anchorB, Rotator("765", "1Sandman", 2700)])),
            Round(3, new SetupRoundDetail(false, BombSite.A, 10, [_anchorA, _anchorB, Player("m", _mid, Track([(2000, "Mid")]))]))
        ];
        IReadOnlyList<SetupRotation> rotations = SetupsDerivation.Rotations(rounds, SetupAreaClassifier.Learn(rounds));
        Dictionary<string, string> names = new(StringComparer.Ordinal) { ["765"] = "Sandman" };

        ScoutingLayerResult result = RotationsLayer.Compose("de_x CT", rotations,
            new Dictionary<string, ScoutingColor>(StringComparer.Ordinal) { ["765"] = ScoutingColor.Player1 }, names);
        IReadOnlyList<ScoutingInspectorRow> sites = result.Sections[0].Rows;
        ScoutingInspectorRow who = result.Sections[1].Rows.Single();
        using (Assert.Multiple())
        {
            await Assert.That(new RotationsLayer().Placement).IsEqualTo(ScoutingLayerPlacement.Map);
            await Assert.That(result.MiniChart).IsNull();
            await Assert.That(result.Reads.Single().Text).IsEqualTo(
                "The first rotator reaches the hit site a median 3s after T contact; before the plant in 1 of 3 planted rounds.");
            await Assert.That(sites.Select(r => $"{r.Label}: {r.Value}"))
                .IsEquivalentTo(["A site: no rotation", "B site: median 3s", "Rotated before the plant: 1 of 3 rounds"],
                    CollectionOrdering.Matching);
            await Assert.That(sites[1].Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
            await Assert.That(sites[1].Note).Contains("before the plant in 1");
            await Assert.That(who.Label).IsEqualTo("Sandman");
            await Assert.That(who.Value).IsEqualTo("2 of 2 rounds");
            await Assert.That(who.Swatch).IsEqualTo(ScoutingColor.Player1);
            await Assert.That(who.Rounds.Count).IsEqualTo(2);
            await Assert.That(result.Marks.Count(m => m.Shape == ScoutingMarkShape.Ring)).IsEqualTo(2);
            await Assert.That(result.Legend.Count).IsEqualTo(2);
        }
    }
}
