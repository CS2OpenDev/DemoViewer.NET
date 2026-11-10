#region

using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Plants layer over hand-built blocks: a plant draws a dot when the round was won and a ring when it was
///     lost, plants cluster into named spots with a badge each, the T buy filter narrows every number and mark,
///     the site split and the most common plant per site read as n of m, and every row with a number opens its rounds.
/// </summary>
public class PlantsLayerTests
{
    private const string Demo = "/d/plants.dem";

    // Rounds 1 to 6 on T. Plants: 1 A spot (won, full), 2 A spot (lost, full), 3 A spot (won, eco), 4 B spot (lost,
    // eco), 5 B with no spot read (won, full); round 6 no plant (full).
    private static readonly (int Round, string Site, int X, int Y, bool Won, BuyType Buy)[] _plants =
    [
        (1, "A", 1000, 1000, true, BuyType.Full),
        (2, "A", 1100, 1000, false, BuyType.Full),
        (3, "A", 1050, 1100, true, BuyType.Eco),
        (4, "B", -2000, 0, false, BuyType.Eco)
    ];

    private static TendencyRound Clip(int round, int from = 100, int to = 900) =>
        new(Demo, "abc", round, from, to, 64);

    private static PostPlantBlock Block()
    {
        Dictionary<(string, int), BuyType> buys = new()
        {
            [(Demo, 1)] = BuyType.Full, [(Demo, 2)] = BuyType.Full, [(Demo, 3)] = BuyType.Eco,
            [(Demo, 4)] = BuyType.Eco, [(Demo, 5)] = BuyType.Full, [(Demo, 6)] = BuyType.Full
        };
        List<PlantSpotPoint> spots = [.. _plants.Select(p => new PlantSpotPoint(Clip(p.Round), p.X, p.Y, 0, p.Site, p.Won, p.Buy))];
        TendencyRound[] side = [.. Enumerable.Range(1, 6).Select(n => Clip(n, 0, 5000))];
        return new PostPlantBlock
        {
            Map = "de_nuke",
            Side = 2,
            SideRounds = new TendencyBucket("rounds", side),
            Planted = new TendencyBucket("planted", [.. Enumerable.Range(1, 5).Select(n => Clip(n))]),
            PlantClusters =
            [
                new TendencyBucket("A spot 1 (1050, 1033)", [Clip(1), Clip(2), Clip(3)]),
                new TendencyBucket("B spot 1 (-2000, 0)", [Clip(4)]),
                new TendencyBucket("B spot unread", [Clip(5)])
            ],
            Outcomes =
            [
                new TendencyBucket("won: exploded", [Clip(1), Clip(3), Clip(5)]),
                new TendencyBucket("lost: defused", [Clip(2), Clip(4)])
            ],
            HoldShapes = [new TendencyBucket("3 near the bomb, 2 away", [Clip(1), Clip(2), Clip(4)])],
            PlantSpots = spots,
            RoundBuys = buys
        };
    }

    private static OpeningTendencies Openings() => new()
    {
        Map = "de_nuke",
        Side = 2,
        Rounds = new TendencyBucket("rounds", [.. Enumerable.Range(1, 6).Select(n => Clip(n, 0, 5000))]),
        SiteSplit =
        [
            new TendencyBucket("A", [Clip(1, 0, 5000), Clip(2, 0, 5000), Clip(3, 0, 5000)]),
            new TendencyBucket("B", [Clip(4, 0, 5000), Clip(5, 0, 5000)]),
            new TendencyBucket("no plant", [Clip(6, 0, 5000)])
        ]
    };

    private static ScoutingLayerContext Context(ScoutingBuy buy = ScoutingBuy.All)
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache));
        ScoutingReportData data = ScoutingReportData.Start(Guid.NewGuid(), "Team A",
            new ScoutingReportSources { Teams = teams, Library = cache.Library() }, _ => a =>
            {
                a();
                return Task.CompletedTask;
            });
        return new ScoutingLayerContext(data, "de_nuke", ScoutingSide.T, buy);
    }

    [Test]
    public async Task APlant_DrawsADotWhenWon_AndARingWhenLost_WithABadgePerNamedSpot()
    {
        PlantReading reading = PlantReading.Read(Block(), Openings(), null);

        IReadOnlyList<ScoutingMapMark> marks = PlantsLayer.Marks(reading);

        using (Assert.Multiple())
        {
            await Assert.That(marks.Count(m => m.Shape == ScoutingMarkShape.Dot)).IsEqualTo(2);
            await Assert.That(marks.Count(m => m.Shape == ScoutingMarkShape.Ring)).IsEqualTo(2);
            await Assert.That(marks.Where(m => m.Shape == ScoutingMarkShape.Dot).Select(m => (m.X, m.Y)))
                .IsEquivalentTo([(1000f, 1000f), (1050f, 1100f)]);
            await Assert.That(marks.Where(m => m.Shape == ScoutingMarkShape.Ring).Select(m => (m.X, m.Y)))
                .IsEquivalentTo([(1100f, 1000f), (-2000f, 0f)]);
            await Assert.That(marks.Where(m => m.Shape == ScoutingMarkShape.Badge).Select(m => m.Text!))
                .IsEquivalentTo(["A1", "B1"]).Because("the unread spot has no centre to put a badge on");
            await Assert.That(marks.Where(m => m.Shape != ScoutingMarkShape.Badge).All(m => m.Size >= 7f)).IsTrue()
                .Because("marks smaller than that vanish into the radar art");
        }
    }

    [Test]
    public async Task PlantsCluster_IntoNamedSpots_WhoseCentreIsTheMeanOfTheirPoints()
    {
        PlantReading reading = PlantReading.Read(Block(), Openings(), null);

        PlantSpot a = reading.Spots.Single(s => s.Badge == "A1");
        using (Assert.Multiple())
        {
            await Assert.That(reading.Spots.Select(s => s.Label))
                .IsEquivalentTo(["A spot 1 (1050, 1033)", "B spot 1 (-2000, 0)", "B spot unread"]);
            await Assert.That(a.Bucket.Count).IsEqualTo(3);
            await Assert.That(a.X).IsEqualTo(1050f);
            await Assert.That(Math.Round(a.Y)).IsEqualTo(1033);
            await Assert.That(reading.Spots.Single(s => s.Label == "B spot unread").HasCentre).IsFalse();
        }
    }

    [Test]
    public async Task TheBuyFilter_NarrowsEveryNumberAndMark_ToThatBuy()
    {
        PlantReading eco = PlantReading.Read(Block(), Openings(), BuyType.Eco);

        using (Assert.Multiple())
        {
            await Assert.That(eco.SideRounds.Count).IsEqualTo(2);
            await Assert.That(eco.Planted.Count).IsEqualTo(2);
            await Assert.That(eco.Points.Select(p => p.Round.RoundNumber)).IsEquivalentTo([3, 4]);
            await Assert.That(eco.SiteSplit.Select(b => $"{b.Label}:{b.Count}")).IsEquivalentTo(["A:1", "B:1"]);
            await Assert.That(eco.SiteRounds).IsEqualTo(2);
            await Assert.That(eco.PlantedWon.Count).IsEqualTo(1);
            await Assert.That(PlantsLayer.Marks(eco).Count(m => m.Shape != ScoutingMarkShape.Badge)).IsEqualTo(2);
            await Assert.That(PlantReading.Read(Block(), Openings(), BuyType.Force).Planted.Count).IsEqualTo(0);
            await Assert.That(PlantReading.Read(null, Openings(), BuyType.Eco).SiteSplit).IsEmpty()
                .Because("without the block there is no way to say which round was which buy");
        }
    }

    [Test]
    public async Task TheInspector_ReadsSiteTargetingAndTheMostCommonPlantPerSite_AsNOfM()
    {
        PlantReading reading = PlantReading.Read(Block(), Openings(), null);

        ScoutingLayerResult result = PlantsLayer.Compose(Context(), reading);

        ScoutingInspectorSection site = result.Sections.Single(s => s.Title == "Site targeting");
        ScoutingInspectorSection top = result.Sections.Single(s => s.Title == "Most common plant per site");
        ScoutingInspectorSection won = result.Sections.Single(s => s.Title == "Post-plant");
        using (Assert.Multiple())
        {
            await Assert.That(site.Rows.Select(r => $"{r.Label}={r.Value}"))
                .IsEquivalentTo(["A site=3 of 6 rounds", "B site=2 of 6 rounds", "no plant=1 of 6 rounds"]);
            await Assert.That(top.Rows.Select(r => $"{r.Label}={r.Value}")).IsEquivalentTo(
                ["A site · A spot 1 (1050, 1033)=3 of 3 rounds", "B site · B spot 1 (-2000, 0)=1 of 2 rounds"]);
            await Assert.That(top.Rows.Select(r => r.Badge!)).IsEquivalentTo(["A1", "B1"]);
            await Assert.That(won.Rows.Single().Value).IsEqualTo("3 of 5 rounds");
            await Assert.That(result.CountLabel).IsEqualTo("5 plants");
            await Assert.That(result.Legend.Select(l => l.Swatch.Shape))
                .IsEquivalentTo([ScoutingMarkShape.Dot, ScoutingMarkShape.Ring, ScoutingMarkShape.Badge]);
        }
    }

    [Test]
    public async Task EveryRowWithANumber_OpensItsRounds_AndTheWonRowOpensOnlyTheWonOnes()
    {
        ScoutingLayerResult result = PlantsLayer.Compose(Context(), PlantReading.Read(Block(), Openings(), null));

        IReadOnlyList<ScoutingInspectorRow> rows = [.. result.Sections.SelectMany(s => s.Rows)];
        ScoutingInspectorRow wonRow = rows.Single(r => r.OpenTitle == "de_nuke T · plants won");
        using (Assert.Multiple())
        {
            await Assert.That(rows.All(r => r.Rounds.Count > 0 && r.OpenTitle.Length > 0)).IsTrue();
            await Assert.That(wonRow.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 3, 5]);
            await Assert.That(rows.Single(r => r.OpenTitle == "de_nuke T · site A").Rounds.Count).IsEqualTo(3);
            await Assert.That(rows.Single(r => r.OpenTitle == "de_nuke T · plant A spot 1 (1050, 1033)").Rounds.Count)
                .IsEqualTo(3);
        }
    }
}
