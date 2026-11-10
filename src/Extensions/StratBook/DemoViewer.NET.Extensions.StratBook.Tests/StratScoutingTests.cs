#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Review;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Scouting Report's strats layers over synthetic mined patterns: the library-wide mining narrowed to the
///     team's T rounds and to the T rounds run against it, the "lost to" counter and when it stays silent, the
///     rankings, and the rows' rounds and 2D play.
/// </summary>
public class StratScoutingTests
{
    private const int Rate = 64;

    private static readonly Guid _team = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _opp1 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _opp2 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    // The member's demo names the scouted team's side: "/t/..." it was T, "/ct/..." it was CT, "/x/..." absent.
    private static int? SideOf(MinedMember m) =>
        m.DemoPath.StartsWith("/t/", StringComparison.Ordinal) ? 2
        : m.DemoPath.StartsWith("/ct/", StringComparison.Ordinal) ? 3
        : null;

    private static MinedMember M(string demo, int round, bool? won, Guid? team = null, double distance = 0)
    {
        int freeze = round * 10000;
        return new MinedMember(demo, null, round, team, won, BuyType.Full, freeze, freeze + 40 * Rate, Rate,
            distance);
    }

    private static MinedPattern P(string key, IReadOnlyList<MinedMember> members, string map = "de_nuke",
        int side = 2, string site = "B") => new()
    {
        Key = key,
        Kind = PatternKind.Execute,
        Map = map,
        Side = side,
        Site = site,
        Members = members,
        Medoid = new RoundSignature
        {
            DemoPath = members.Count > 0 ? members[0].DemoPath : "/x/none.dem",
            Round = members.Count > 0 ? members[0].Round : 1,
            Map = map,
            Side = side,
            Kind = PatternKind.Execute,
            Site = site,
            Anchors =
            [
                [new MinedPawn(0, 0, 0, 0, "Outside"), new MinedPawn(1, 100, 0, 0, "Outside")],
                [new MinedPawn(0, 1000, 1000, 0, "BombsiteB"), new MinedPawn(1, 1100, 1000, 0, "BombsiteB")]
            ]
        }
    };

    [Test]
    public async Task Own_KeepsOnlyTheTeamsTRounds_RunTwice_MostRunFirst()
    {
        MinedPattern big = P("big",
        [
            M("/t/a.dem", 1, true), M("/t/a.dem", 2, false), M("/t/b.dem", 3, true),
            M("/ct/c.dem", 4, true, _opp1) // run against them: not theirs
        ]);
        MinedPattern small = P("small", [M("/t/a.dem", 5, true), M("/t/b.dem", 6, true, distance: 0.2)]);
        MinedPattern once = P("once", [M("/t/a.dem", 7, true), M("/ct/c.dem", 8, true, _opp1)]);
        MinedPattern otherMap = P("mirage", [M("/t/a.dem", 9, true), M("/t/a.dem", 10, true)], "de_mirage");
        MinedPattern ctSide = P("ct", [M("/t/a.dem", 11, true), M("/t/a.dem", 12, true)], side: 3);
        // Without a side for the round, the miner's own team stamp decides.
        MinedPattern stamped = P("stamped", [M("/x/d.dem", 13, true, _team), M("/x/d.dem", 14, false, _opp2)]);

        IReadOnlyList<ScoutedStrat> own =
            StratScouting.Own([small, big, once, otherMap, ctSide, stamped], "DE_NUKE", _team, SideOf);

        using (Assert.Multiple())
        {
            await Assert.That(own.Select(s => s.Pattern.Key)).IsEquivalentTo(["big", "small"],
                CollectionOrdering.Matching);
            await Assert.That(own[0].Support).IsEqualTo(3).Because("the round against them is not theirs");
            await Assert.That(own[0].Wins).IsEqualTo(2);
            await Assert.That(own[0].Known).IsEqualTo(3);
        }
    }

    [Test]
    public async Task Against_KeepsTheTRoundsWhereTheyWereCt_BestWinRateFirst_CountingOpponents()
    {
        MinedPattern often = P("often",
            [M("/ct/a.dem", 1, true, _opp1), M("/ct/b.dem", 2, false, _opp2), M("/ct/b.dem", 3, true, _opp2),
             M("/t/x.dem", 4, true, _team)]);
        MinedPattern perfect = P("perfect", [M("/ct/a.dem", 5, true, _opp1), M("/ct/a.dem", 6, true, _opp1)]);
        MinedPattern single = P("single", [M("/ct/a.dem", 7, true, _opp1), M("/t/a.dem", 8, true, _team)]);
        MinedPattern unknown = P("unknown", [M("/x/a.dem", 9, true, _opp1), M("/x/a.dem", 10, true, _opp1)]);

        IReadOnlyList<ScoutedStrat> against = StratScouting.Against([often, perfect, single, unknown], "de_nuke",
            SideOf);

        using (Assert.Multiple())
        {
            await Assert.That(against.Select(s => s.Pattern.Key)).IsEquivalentTo(["perfect", "often"],
                CollectionOrdering.Matching).Because("ranked by win rate, run at least twice against them");
            await Assert.That(against[1].Support).IsEqualTo(3);
            await Assert.That(against[1].Wins).IsEqualTo(2);
            await Assert.That(against[1].Opponents).IsEqualTo(2);
            await Assert.That(against[0].Opponents).IsEqualTo(1);
        }
    }

    [Test]
    public async Task LostTo_NamesTheCommonestCtOpenerOfTheLosses_AndSaysNothingOnAWeakSample()
    {
        ScoutedStrat strat = new(P("s", []),
        [
            M("/t/a.dem", 1, false), M("/t/a.dem", 2, false), M("/t/a.dem", 3, false), M("/t/a.dem", 4, false),
            M("/t/a.dem", 5, true), M("/t/a.dem", 6, true), M("/t/a.dem", 7, true)
        ]);
        Dictionary<int, string?> features = new()
        {
            [1] = "early Mid smoke", [2] = "early Mid smoke", [3] = "early Mid smoke", [4] = "Outside flash",
            [5] = "early Mid smoke", [6] = "Outside flash", [7] = null
        };
        LostToFinding? found = StratScouting.LostTo(strat, m => features[m.Round]);

        // One loss is no sample; a feature as common in the wins blames nothing.
        ScoutedStrat oneLoss = new(P("o", []), [M("/t/a.dem", 1, false), M("/t/a.dem", 2, true)]);
        ScoutedStrat everyRound = new(P("e", []),
            [M("/t/a.dem", 1, false), M("/t/a.dem", 2, false), M("/t/a.dem", 3, true), M("/t/a.dem", 4, true)]);
        ScoutedStrat scattered = new(P("x", []),
            [M("/t/a.dem", 1, false), M("/t/a.dem", 2, false), M("/t/a.dem", 3, false)]);

        using (Assert.Multiple())
        {
            await Assert.That(found).IsNotNull();
            await Assert.That(found!.Sentence).IsEqualTo("Lost to: early Mid smoke (3 of 4 losses)");
            await Assert.That(found.Rounds.Select(r => r.Round)).IsEquivalentTo([1, 2, 3],
                CollectionOrdering.Matching);
            await Assert.That(found.WinsWith).IsEqualTo(1);
            await Assert.That(found.Wins).IsEqualTo(2).Because("a round whose opener is unread is not counted");
            await Assert.That(StratScouting.LostTo(oneLoss, _ => "early Mid smoke")).IsNull();
            await Assert.That(StratScouting.LostTo(everyRound, _ => "early Mid smoke")).IsNull();
            await Assert.That(StratScouting.LostTo(scattered, m => $"place {m.Round} smoke")).IsNull()
                .Because("no feature is in two losses");
            await Assert.That(StratScouting.LostTo(strat, _ => null)).IsNull();
        }
    }

    [Test]
    public async Task Opener_IsTheFirstCtGrenade_EarlyInsideTwentySeconds()
    {
        using (Assert.Multiple())
        {
            await Assert.That(StratScouting.Opener(
                    [(GrenadeKind.Flash, "Outside", 30.0), (GrenadeKind.Smoke, "Mid", 8.0), (GrenadeKind.He, "Pre", -3.0)]))
                .IsEqualTo("early Mid smoke");
            await Assert.That(StratScouting.Opener([(GrenadeKind.Incendiary, "Banana", 45.0)]))
                .IsEqualTo("Banana molotov");
            await Assert.That(StratScouting.Opener([(GrenadeKind.Smoke, null, 5.0)])).IsNull();
            await Assert.That(StratScouting.Opener([])).IsNull();
        }
    }

    private static async Task<ScoutingReportData> Data(IReadOnlyList<MinedPattern>? patterns, bool withSource = true)
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: _inline);
        await teams.StartAsync();
        return ScoutingReportData.Start(_team, "Scouted",
            new ScoutingReportSources
            {
                Teams = teams, Library = cache.Library(), MinedPatterns = withSource ? () => patterns : null
            }, _ => _inline);
    }

    [Test]
    public async Task TheStratsLayer_ListsTheirStrats_LettersTheTrails_PlaysAndOpensEveryNumbersRounds()
    {
        MinedPattern pattern = P("big",
        [
            M("/t/a.dem", 1, false), M("/t/a.dem", 2, false), M("/t/b.dem", 3, false, distance: 0.1),
            M("/t/b.dem", 4, true), M("/ct/c.dem", 5, true, _opp1)
        ]);
        ScoutingReportData data = await Data([pattern]);
        ScoutingLayerContext context = new(data, "de_nuke", ScoutingSide.T, ScoutingBuy.All);
        StratsLayer layer = new((_, m) => SideOf(m), (_, m) => m.Round <= 2 ? "early Mid smoke" : "Outside flash");

        ScoutingLayerResult result = await layer.BuildAsync(context, CancellationToken.None);
        IReadOnlyList<ScoutingInspectorRow> rows = result.Sections.Single().Rows;
        ScoutingInspectorRow strat = rows[0];
        ScoutingInspectorRow lostTo = rows[1];

        ReviewQueue queue = new(null);
        ScoutingReviewOpener opener = new(new ExtensionReviewQueue(queue), null);
        string line = opener.Open(data.TeamName, _team, lostTo.Rounds, lostTo.OpenTitle);

        using (Assert.Multiple())
        {
            await Assert.That(result.Status.State).IsEqualTo(ScoutingLayerState.Ready);
            await Assert.That(result.CountLabel).IsEqualTo("1 strat");
            await Assert.That(strat.Label).IsEqualTo("B execute: 2 Outside");
            await Assert.That(strat.Value).IsEqualTo("won 1 of 4");
            await Assert.That(strat.Badge).IsEqualTo("A");
            await Assert.That(strat.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 2, 3, 4],
                CollectionOrdering.Matching);
            await Assert.That(strat.Play).IsEqualTo(("/t/a.dem", 10000 + 30 * Rate))
                .Because("2D play lands ten seconds before the take of the most typical round");
            await Assert.That(lostTo.Label).IsEqualTo("Lost to: early Mid smoke");
            await Assert.That(lostTo.Value).IsEqualTo("2 of 3 losses");
            await Assert.That(lostTo.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 2],
                CollectionOrdering.Matching);
            await Assert.That(rows.All(r => r.Rounds.Count > 0 && r.OpenTitle.Length > 0)).IsTrue();
            await Assert.That(result.Marks.Single(m => m.Shape == ScoutingMarkShape.Badge).Text).IsEqualTo("A");
            await Assert.That(result.Marks.Count(m => m.Shape == ScoutingMarkShape.Dot)).IsEqualTo(4)
                .Because("without a positions file the medoid's anchors draw the trail");
            await Assert.That(result.Reads[0].Text).IsEqualTo(
                "Their most run T strat on de_nuke is B execute: 2 Outside: won 1 of 4.");
            await Assert.That(line).IsEqualTo("2 rounds sent to Review");
            await Assert.That(queue.Clips.Select(c => c.Note)).IsEquivalentTo(
                ["de_nuke T · B execute: 2 Outside lost to early Mid smoke · round 1",
                 "de_nuke T · B execute: 2 Outside lost to early Mid smoke · round 2"],
                CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task TheAgainstLayer_RanksTheOpponentsStrats_SaysHowManyRanThem_AndOpensTheirRounds()
    {
        MinedPattern often = P("often",
            [M("/ct/a.dem", 1, true, _opp1), M("/ct/b.dem", 2, false, _opp2), M("/ct/b.dem", 3, true, _opp2)]);
        MinedPattern perfect = P("perfect", [M("/ct/a.dem", 5, true, _opp1), M("/ct/a.dem", 6, true, _opp1)],
            site: "A");
        ScoutingReportData data = await Data([often, perfect]);
        ScoutingLayerContext context = new(data, "de_nuke", ScoutingSide.Ct, ScoutingBuy.All);

        ScoutingLayerResult result = await new AgainstLayer((_, m) => SideOf(m)).BuildAsync(context,
            CancellationToken.None);
        IReadOnlyList<ScoutingInspectorRow> rows = result.Sections.Single().Rows;
        ReviewQueue queue = new(null);
        new ScoutingReviewOpener(new ExtensionReviewQueue(queue), null).Open(data.TeamName, _team, rows[1].Rounds,
            rows[1].OpenTitle);

        using (Assert.Multiple())
        {
            await Assert.That(rows.Select(r => r.Value)).IsEquivalentTo(["won 2 of 2", "won 2 of 3"],
                CollectionOrdering.Matching);
            await Assert.That(rows.Select(r => r.Badge ?? "")).IsEquivalentTo(["A", "B"], CollectionOrdering.Matching);
            await Assert.That(rows.Select(r => r.Note ?? "")).IsEquivalentTo(["seen from 1 opponent", "seen from 2 opponents"],
                CollectionOrdering.Matching);
            await Assert.That(rows.All(r => r.Play is not null)).IsTrue();
            await Assert.That(result.Reads[0].Text).IsEqualTo(
                "What worked best against their CT on de_nuke: A execute: 2 Outside, won 2 of 2, seen from 1 opponent.");
            await Assert.That(queue.ClipCount).IsEqualTo(3);
        }
    }

    [Test]
    public async Task TheStratsLayers_SayWhyTheyAreEmpty()
    {
        ScoutingLayerResult noSource = await new StratsLayer().BuildAsync(
            new ScoutingLayerContext(await Data(null, withSource: false), "de_nuke", ScoutingSide.T, ScoutingBuy.All),
            CancellationToken.None);
        ScoutingLayerResult notMined = await new AgainstLayer().BuildAsync(
            new ScoutingLayerContext(await Data(null), "de_nuke", ScoutingSide.Ct, ScoutingBuy.All),
            CancellationToken.None);
        ScoutingLayerResult none = await new StratsLayer((_, m) => SideOf(m)).BuildAsync(
            new ScoutingLayerContext(await Data([P("p", [M("/ct/a.dem", 1, true), M("/ct/a.dem", 2, true)])]),
                "de_nuke", ScoutingSide.T, ScoutingBuy.All), CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(noSource.Status.Message).IsEqualTo("not built on this host");
            await Assert.That(notMined.Status.Message)
                .IsEqualTo("strats not mined yet: Find strats in the Strat Book's Detected inbox");
            await Assert.That(none.Status.State).IsEqualTo(ScoutingLayerState.Empty);
            await Assert.That(none.Status.Message)
                .IsEqualTo("no T strat of theirs was run twice on de_nuke in the mined library");
        }
    }
}
