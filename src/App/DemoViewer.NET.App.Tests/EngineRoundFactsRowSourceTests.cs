#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using TUnit.Assertions.Enums;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The engine row source without a demo: the shipped <c>round_facts</c> ruleset loads and resolves as a
///     per-side ruleset whose every label is a column the projection knows, and a hand-built table in the
///     engine's shape (dimensions, comma-joined lists, 0 for what did not happen) maps onto the seam's rows,
///     renumbered onto the clip rounds, with the victim sides and scores the language cannot express read
///     off the same rows.
/// </summary>
public class EngineRoundFactsRowSourceTests
{
    private static RulesetDoc ShippedDoc()
    {
        string? root = DemoTestHelper.FindRepoRoot();
        if (root is null)
        {
            throw new SkipTestException("repo root not found from the test bin");
        }

        string path = Path.Combine(root, "rules", "round_facts.rules.yaml");
        RuleConfigLoadResult loaded =
            YamlConfigLoader.LoadDocuments([(Path.GetFileName(path), File.ReadAllText(path))]);
        return loaded.Rulesets.Single();
    }

    [Test]
    public async Task TheShippedRuleset_ResolvesClean_AsAPerSideTable_OfKnownColumns()
    {
        RulesetDoc doc = ShippedDoc();
        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets([doc]);
        TableDef table = doc.Show!.Tables.Single();

        using (Assert.Multiple())
        {
            await Assert.That(doc.Id).IsEqualTo(RoundFactsFingerprint.RulesetId);
            await Assert.That(validated.Diagnostics.Count).IsEqualTo(0)
                .Because(string.Join("; ", validated.Diagnostics.Select(d => d.ToString())));
            await Assert.That(table.Name).IsEqualTo(EngineRoundFactsRowSource.TableName);
            await Assert.That(table.Per).IsEqualTo("team_round");
            foreach (TableColumn column in table.Columns)
            {
                await Assert.That(RoundFactsColumns.Known).Contains(column.Label!)
                    .Because($"{column.Label} would land in Extra instead of its typed field");
            }
        }
    }

    [Test]
    public async Task TheShippedParameters_AreTheClassifierDefaults()
    {
        BuyThresholds thresholds =
            RoundFactsProjection.ThresholdsFrom(EngineRoundFactsRowSource.ParametersOf(ShippedDoc()));

        await Assert.That(thresholds).IsEqualTo(BuyThresholds.Default);
    }

    [Test]
    public async Task TheWholeDirectoryHosts_LeaveRoundFactsOut()
    {
        RulesetDoc doc = ShippedDoc();
        Modules.Highlights.MergedRulesBuild build = new(
            () => throw new InvalidOperationException("the filter needs no read"),
            () => [Modules.Highlights.StampedRuleset.Core(RoundFactsFingerprint.RulesetId)]);

        await Assert.That(build.WithoutStampedRulesets([doc])).IsEmpty();
    }

    [Test]
    public async Task NoTable_IsUnavailable_WithNoRows()
    {
        RoundFactsTable table =
            EngineRoundFactsRowSource.FromTable(null, new Dictionary<string, object?>(), [new ClipRound(1, 1000)]);

        using (Assert.Multiple())
        {
            await Assert.That(table.Rows).IsEmpty();
            await Assert.That(table.UnavailableColumns)
                .IsEquivalentTo(RoundFactsColumns.Known, CollectionOrdering.Matching);
        }
    }

    // Team A holds slots 0-4 and starts CT; the sides swap after round 1. Engine round 3 is one the server
    // decided in freeze time: the engine opened it at tick 8000, and the wire has no freeze end there.
    // score, when given, is the captured score_before per (engine round, side); a null leaves the row's
    // capture empty, as a row with nothing to read comes out.
    private static MetricTable EngineTable(Func<int, int, int?>? score = null)
    {
        MetricRow Row(int round, int side, string slots, int freezeEnd, int winner, int plantTick = 0,
            string? site = null,
            int siteEntity = 0, int planter = 0, string kills = "", string victims = "", string teamAlive = "",
            string enemyAlive = "")
        {
            Dictionary<string, object?> dimensions = new()
            {
                ["match_id"] = "m",
                ["map"] = "de_nuke",
                ["round_number"] = round,
                ["side"] = side,
                ["slots"] = slots
            };
            Dictionary<string, object?> values = new()
            {
                [RoundFactsColumns.FreezeEndTick] = freezeEnd,
                [RoundFactsColumns.EndTick] = freezeEnd + 2000,
                [RoundFactsColumns.WinnerSide] = winner,
                [RoundFactsColumns.EndReason] = winner == 3 ? 8 : 9,
                [RoundFactsColumns.PlantTick] = plantTick,
                [RoundFactsColumns.PlantSite] = site,
                [RoundFactsColumns.PlantSiteEntity] = siteEntity,
                [RoundFactsColumns.PlanterSlot] = planter,
                [RoundFactsColumns.DefuseTick] = 0,
                [RoundFactsColumns.Players] = 5,
                [RoundFactsColumns.Equipment] = 4000,
                [RoundFactsColumns.Money] = 800,
                [RoundFactsColumns.MoneyReliable] = true,
                [RoundFactsColumns.KillTicks] = kills,
                [RoundFactsColumns.KillVictimSlot] = victims,
                [RoundFactsColumns.KillTeamAlive] = teamAlive,
                [RoundFactsColumns.KillEnemyAlive] = enemyAlive
            };
            if (score is not null)
            {
                values[RoundFactsColumns.ScoreBefore] = score(round, side);
            }

            return new MetricRow(dimensions, values);
        }

        const string A = "0,1,2,3,4", B = "5,6,7,8,9";
        List<MetricRow> rows =
        [
            Row(1, 2, B, 1000, 3, kills: "1500,2000", victims: "5,1", teamAlive: "4,4", enemyAlive: "5,4"),
            Row(1, 3, A, 1000, 3, kills: "1500,2000", victims: "5,1", teamAlive: "5,4", enemyAlive: "4,4"),
            Row(2, 2, A, 5000, 2, 6000, "A", 173),
            Row(2, 3, B, 5000, 2, 6000, "A", 173),
            Row(3, 2, A, 8000, 3),
            Row(3, 3, B, 8000, 3),
            Row(4, 2, A, 9000, 2),
            Row(4, 3, B, 9000, 2)
        ];
        return new MetricTable(EngineRoundFactsRowSource.TableName,
            ["match_id", "map", "round_number", "side", "slots"],
            [.. rows[0].Values.Keys],
            rows);
    }

    private static readonly IReadOnlyList<ClipRound> _clipRounds =
        [new(1, 1000), new(2, 5000), new(3, 9000)];

    private static IReadOnlyDictionary<string, object?> Cell(RoundFactsTable table, int round, int side) =>
        table.Rows.Single(r => RoundFactsValues.ToInt(r[RoundFactsColumns.RoundNumber]) == round
            && RoundFactsValues.ToInt(r[RoundFactsColumns.Side]) == side);

    [Test]
    public async Task TheEngineTable_MapsOntoTheClipRounds_AndNullsWhatDidNotHappen()
    {
        RoundFactsTable table =
            EngineRoundFactsRowSource.FromTable(EngineTable(), new Dictionary<string, object?>(), _clipRounds);

        using (Assert.Multiple())
        {
            await Assert.That(table.Rows.Count).IsEqualTo(6);
            await Assert.That(table.Diagnostics.Single()).Contains("tick 8000");
            await Assert.That(Cell(table, 3, 3)[RoundFactsColumns.FreezeEndTick]).IsEqualTo(9000)
                .Because("engine round 4 is the clip authority's round 3");
            await Assert.That(table.Rows.Any(r => r.ContainsKey("match_id") || r.ContainsKey("map"))).IsFalse();

            IReadOnlyDictionary<string, object?> noPlant = Cell(table, 1, 3);
            await Assert.That(noPlant[RoundFactsColumns.PlantTick]).IsNull();
            await Assert.That(noPlant[RoundFactsColumns.PlantSite]).IsNull();
            await Assert.That(noPlant[RoundFactsColumns.PlantSiteEntity]).IsNull();
            await Assert.That(noPlant[RoundFactsColumns.PlanterSlot]).IsNull()
                .Because("slot 0 is a real player; the plant tick decides");
            await Assert.That(noPlant[RoundFactsColumns.DefuseTick]).IsNull();
            await Assert.That(noPlant[RoundFactsColumns.EndTick]).IsEqualTo(3000);

            IReadOnlyDictionary<string, object?> planted = Cell(table, 2, 2);
            await Assert.That(planted[RoundFactsColumns.PlanterSlot]).IsEqualTo(0);
            await Assert.That(planted[RoundFactsColumns.PlantSiteEntity]).IsEqualTo(173);

            await Assert.That(table.UnavailableColumns).Contains(RoundFactsColumns.ExplodeTick);
            await Assert.That(table.UnavailableColumns).DoesNotContain(RoundFactsColumns.BuyType);
            await Assert.That(table.UnavailableColumns).DoesNotContain(RoundFactsColumns.ScoreBefore);
            await Assert.That(table.UnavailableColumns).DoesNotContain(RoundFactsColumns.KillVictimSide);
        }
    }

    [Test]
    public async Task ScoresFollowTheTeamsAcrossTheSwap_AndCountADroppedRoundsWinner()
    {
        RoundFactsTable table =
            EngineRoundFactsRowSource.FromTable(EngineTable(), new Dictionary<string, object?>(), _clipRounds);

        int Score(int round, int side) =>
            RoundFactsValues.ToInt(Cell(table, round, side)[RoundFactsColumns.ScoreBefore])!.Value;

        using (Assert.Multiple())
        {
            await Assert.That((Score(1, 3), Score(1, 2))).IsEqualTo((0, 0));
            // A won round 1 on CT and is T from round 2.
            await Assert.That((Score(2, 3), Score(2, 2))).IsEqualTo((0, 1));
            // A won round 2 on T; B won the freeze-time round on CT, which the record drops but the score keeps.
            await Assert.That((Score(3, 3), Score(3, 2))).IsEqualTo((1, 2));
        }
    }

    [Test]
    public async Task TheRows_ProjectOntoTheRecord()
    {
        RoundFactsTable table =
            EngineRoundFactsRowSource.FromTable(EngineTable(), new Dictionary<string, object?>(), _clipRounds);
        RoundFactsRows rows = RoundFactsProjection.Project(_clipRounds, table);
        RoundFacts first = rows.Rounds[0];

        using (Assert.Multiple())
        {
            await Assert.That(rows.Rounds.Select(r => r.Number)).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
            await Assert.That(rows.Rounds.SelectMany(r => r.ProjectionWarnings)).IsEmpty();
            await Assert.That(first.Ct.Slots).IsEquivalentTo([0, 1, 2, 3, 4], CollectionOrdering.Matching);
            await Assert.That(first.PlantTick).IsNull();
            await Assert.That(first.PlantSite).IsEqualTo(BombSite.Unknown);
            await Assert.That(first.Kills.Select(k => k.VictimSide))
                .IsEquivalentTo([2, 3], CollectionOrdering.Matching);
            await Assert.That(first.Kills.Select(k => (k.CtAlive, k.TAlive)))
                .IsEquivalentTo([(5, 4), (4, 4)], CollectionOrdering.Matching);
            await Assert.That(rows.Rounds[1].PlantSite).IsEqualTo(BombSite.A);
            await Assert.That(rows.Rounds[1].PlanterSlot).IsEqualTo(0);
            await Assert.That(rows.Rounds[2].T.ScoreBefore).IsEqualTo(2);
        }
    }

    // The server's score when the recording starts with team A already a round up (a late GOTV start).
    // Team A is CT in engine round 1 and T from round 2; it wins rounds 1 and 2, B the freeze-time round.
    private static int? ServerScore(int round, int side) => (round, side) switch
    {
        (1, 3) => 1, (1, 2) => 0,
        (2, 2) => 2, (2, 3) => 0,
        (3, 2) => 3, (3, 3) => 0,
        (4, 2) => 3, (4, 3) => 1,
        _ => null
    };

    [Test]
    public async Task TheCapturedScore_WinsOverTheTally_AndStaysWithItsSideAcrossTheSwap()
    {
        RoundFactsTable table =
            EngineRoundFactsRowSource.FromTable(EngineTable(ServerScore), new Dictionary<string, object?>(),
                _clipRounds);
        RoundFactsRows rows = RoundFactsProjection.Project(_clipRounds, table);

        using (Assert.Multiple())
        {
            await Assert.That(table.UnavailableColumns).DoesNotContain(RoundFactsColumns.ScoreBefore);
            await Assert.That((rows.Rounds[0].Ct.ScoreBefore, rows.Rounds[0].T.ScoreBefore)).IsEqualTo((1, 0))
                .Because("the tally would start at 0-0");
            await Assert.That((rows.Rounds[1].Ct.ScoreBefore, rows.Rounds[1].T.ScoreBefore)).IsEqualTo((0, 2))
                .Because("team A is T after the swap and carries its score");
            await Assert.That((rows.Rounds[2].Ct.ScoreBefore, rows.Rounds[2].T.ScoreBefore)).IsEqualTo((1, 3));
        }
    }

    [Test]
    public async Task ARowWithoutACapturedScore_FallsBackToTheTally()
    {
        // Engine round 4 (clip round 3) has no capture; every other row keeps its captured score.
        RoundFactsTable table = EngineRoundFactsRowSource.FromTable(
            EngineTable((round, side) => round == 4 ? null : ServerScore(round, side)),
            new Dictionary<string, object?>(), _clipRounds);
        RoundFactsRows rows = RoundFactsProjection.Project(_clipRounds, table);

        using (Assert.Multiple())
        {
            await Assert.That((rows.Rounds[0].Ct.ScoreBefore, rows.Rounds[0].T.ScoreBefore)).IsEqualTo((1, 0));
            await Assert.That((rows.Rounds[1].Ct.ScoreBefore, rows.Rounds[1].T.ScoreBefore)).IsEqualTo((0, 2));
            // The tally from 0-0, following team A onto T: A has 2, B the freeze-time round's 1.
            await Assert.That((rows.Rounds[2].Ct.ScoreBefore, rows.Rounds[2].T.ScoreBefore)).IsEqualTo((1, 2));
        }
    }
}
