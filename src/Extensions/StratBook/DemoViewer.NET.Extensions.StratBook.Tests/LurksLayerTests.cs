#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Lurks layer over one synthetic demo: the lurker's share of the rounds with positions, the rounds with a
///     lurk, the split-off median and clock bins, the places the lurk goes via, the lurker's position at 0:30 as
///     a map mark, the buy filter re-counting all of it, and every number opening its rounds.
/// </summary>
[NotInParallel]
public class LurksLayerTests
{
    private const string Demo = "/d/lurks.dem";
    private const int Rate = 64;

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly int[] _a = [0, 1, 2, 3, 4];
    private static readonly int[] _b = [5, 6, 7, 8, 9];

    private static string Id(int n) => $"7656{n:D4}";

    private static RoundFacts Round(int number, BuyType buy) => new()
    {
        Number = number,
        IsLive = true,
        FreezeEndTick = number * 10000,
        EndTick = number * 10000 + 90 * Rate,
        Ct = new SideFacts { Side = 3, Slots = _b, BuyType = BuyType.Full },
        T = new SideFacts { Side = 2, Slots = _a, BuyType = buy }
    };

    // Team A is T all four rounds: 1 full, 2 eco, 3 full, 4 full.
    private static RoundFactsRows Rows() => new()
    {
        Schema = RoundFactsRecords.Schema,
        Clock = new RoundFactsClock { TickRate = Rate },
        Rounds = [Round(1, BuyType.Full), Round(2, BuyType.Eco), Round(3, BuyType.Full), Round(4, BuyType.Full)]
    };

    private static DemoCacheRecord Record(string path = Demo, string map = "de_nuke", bool facts = true)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            Map = map,
            Sha256 = facts ? "abc123" : null
        };
        if (facts)
        {
            record.SetRoundFacts(Rows());
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

    // One step per second. The group p0..p2 (and the lurker's replacement mate) sits together at place Ramp.
    // Round 1: p3 leaves at 15 s into Palace, goes on to Mid at 25 s, stands at (3000, 111, 7) at 30 s.
    // Round 2 (eco): p4 leaves at 12 s, stays in Palace. Round 3: p3 leaves at 20 s and is dead by 28 s.
    // Round 4: nobody leaves.
    private static RoundPositionsDocument Positions(string fingerprint)
    {
        const int ramp = 0, palace = 1, mid = 2;

        IEnumerable<RoundPosition> Step(int round, int step)
        {
            yield return new RoundPosition(0, 0, 0, 0, ramp);
            yield return new RoundPosition(1, 100, 0, 0, ramp);
            yield return new RoundPosition(2, 0, 100, 0, ramp);
            yield return new RoundPosition(5, -5000, 0, 0, ramp);
            (int split, int slot) = round switch { 1 => (15, 3), 2 => (12, 4), 3 => (20, 3), _ => (999, 3) };
            int mate = slot == 3 ? 4 : 3;
            yield return new RoundPosition(mate, 200, 0, 0, ramp);
            bool dead = round == 3 && step >= 28;
            if (dead)
            {
                yield break;
            }

            bool away = step >= split;
            int place = !away ? ramp : round == 1 && step >= 25 ? mid : palace;
            yield return new RoundPosition(slot, away ? 3000 : 250, away ? 111 : 0, away ? 7 : 0, place);
        }

        List<RoundPositionsRound> rounds =
        [
            .. Enumerable.Range(1, 4).Select(n => new RoundPositionsRound
            {
                Number = n,
                FreezeEndTick = n * 10000,
                Ct = _b,
                Pos = [.. Enumerable.Range(0, 60).Select(s => Step(n, s).ToList())]
            })
        ];
        return new RoundPositionsDocument
        {
            Fingerprint = fingerprint,
            Demo = new RoundPositionsDemo { StableKey = DemoCacheStore.StableKey(Demo), Sha256 = "abc123" },
            CadenceTicks = Rate,
            Places = ["Ramp", "Palace", "Mid"],
            Rounds = rounds
        };
    }

    private static async Task<(ScoutingLayerResult Result, Harness H)> Build(ScoutingBuy buy)
    {
        Harness h = await Harness.Create();
        ScoutingLayerContext context = new(h.Data, "de_nuke", ScoutingSide.T, buy);
        return (await new LurksLayer().BuildAsync(context, CancellationToken.None), h);
    }

    private static IEnumerable<ScoutingInspectorRow> Rows(ScoutingLayerResult r) => r.Sections.SelectMany(s => s.Rows);

    private static ScoutingInspectorRow Titled(ScoutingLayerResult r, string title) =>
        Rows(r).Single(x => x.OpenTitle == title);

    [Test]
    public async Task ALurkersColour_IsTheirPlayersTableColour()
    {
        (ScoutingLayerResult r, Harness h) = await Build(ScoutingBuy.All);
        using (h)
        {
            IReadOnlyList<ScoutingRosterPlayer> roster = ScoutingPlayerColors.Roster(h.Teams, h.Data.TeamId);
            ScoutingRosterPlayer p3 = roster.Single(p => p.Member.LastName == "p3");
            ScoutingRosterPlayer p4 = roster.Single(p => p.Member.LastName == "p4");
            using (Assert.Multiple())
            {
                await Assert.That(roster.Count).IsEqualTo(5);
                await Assert.That(Titled(r, "de_nuke T · lurker p3").Swatch).IsEqualTo(p3.Color);
                await Assert.That(Titled(r, "de_nuke T · lurker p4").Swatch).IsEqualTo(p4.Color);
                await Assert.That(ScoutingPlayerColors.Lookup(h.Teams, h.Data.TeamId)[p3.SteamId]).IsEqualTo(p3.Color)
                    .Because("rows keyed by SteamID and rows keyed by name read the same colour");
            }
        }
    }

    [Test]
    public async Task WhoLurks_IsEachPlayersShareOfTheRoundsWithPositions_AndEveryNumberOpensItsRounds()
    {
        (ScoutingLayerResult r, Harness h) = await Build(ScoutingBuy.All);
        using (h)
        {
            ScoutingInspectorRow p3 = Titled(r, "de_nuke T · lurker p3");
            ScoutingInspectorRow p4 = Titled(r, "de_nuke T · lurker p4");
            using (Assert.Multiple())
            {
                await Assert.That(r.Status.State).IsEqualTo(ScoutingLayerState.Ready);
                await Assert.That(p3.Value).IsEqualTo("2 of 4 rounds");
                await Assert.That(p3.Rounds.Select(x => x.RoundNumber)).IsEquivalentTo([1, 3]);
                await Assert.That(p4.Value).IsEqualTo("1 of 4 rounds");
                await Assert.That(p3.Swatch).IsNotNull();
                await Assert.That(Titled(r, "de_nuke T lurk rounds").Value).IsEqualTo("3 of 4 rounds");
                await Assert.That(Titled(r, "de_nuke T · lurk no lurk").Rounds.Single().RoundNumber).IsEqualTo(4);
                await Assert.That(Rows(r).All(x => x.Rounds.Count > 0 && x.OpenTitle.Length > 0)).IsTrue();
                await Assert.That(r.Reads[0].Text).StartsWith("p3 lurks most: 2 of 4 rounds");
            }
        }
    }

    [Test]
    public async Task TheSplitOff_IsAMedianAndClockBins()
    {
        (ScoutingLayerResult r, Harness h) = await Build(ScoutingBuy.All);
        using (h)
        {
            ScoutingInspectorRow median = Titled(r, "de_nuke T · split-off median");
            using (Assert.Multiple())
            {
                await Assert.That(median.Value).IsEqualTo("0:15").Because("splits at 15, 12 and 20 s");
                await Assert.That(Titled(r, "de_nuke T · lurk 15-20 s").Rounds.Single().RoundNumber).IsEqualTo(1);
                await Assert.That(Titled(r, "de_nuke T · lurk 10-15 s").Rounds.Single().RoundNumber).IsEqualTo(2);
                await Assert.That(Titled(r, "de_nuke T · lurk 20-25 s").Rounds.Single().RoundNumber).IsEqualTo(3);
                await Assert.That(LurksLayer.Median([10, 20])).IsEqualTo(15);
                await Assert.That(LurksLayer.Median([])).IsEqualTo(0);
            }
        }
    }

    [Test]
    public async Task Via_IsTheSplitPlaceAndTheRouteAfterIt_OncePerRound()
    {
        (ScoutingLayerResult r, Harness h) = await Build(ScoutingBuy.All);
        using (h)
        {
            ScoutingInspectorRow palace = Titled(r, "de_nuke T · via Palace");
            ScoutingInspectorRow mid = Titled(r, "de_nuke T · via Mid");
            using (Assert.Multiple())
            {
                await Assert.That(palace.Value).IsEqualTo("3 of 3 rounds");
                await Assert.That(mid.Rounds.Select(x => x.RoundNumber)).IsEquivalentTo([1])
                    .Because("only the round 1 lurker walks on to Mid within 20 s");
                await Assert.That(r.Reads[0].Text).Contains("via Palace");
            }
        }
    }

    [Test]
    public async Task TheMap_ShowsTheLurkerAt0030_AndNothingForADeadOrAbsentLurker()
    {
        (ScoutingLayerResult r, Harness h) = await Build(ScoutingBuy.All);
        using (h)
        {
            using (Assert.Multiple())
            {
                await Assert.That(r.Marks.Count).IsEqualTo(2).Because("round 3's lurker is dead by 28 s and round 4 has none");
                await Assert.That(r.Marks.All(m => m is { X: 3000, Y: 111, Z: 7, Shape: ScoutingMarkShape.Dot })).IsTrue();
                await Assert.That(r.Marks.Select(m => m.Color)).IsEquivalentTo(
                    [Titled(r, "de_nuke T · lurker p3").Swatch!.Value, Titled(r, "de_nuke T · lurker p4").Swatch!.Value])
                    .Because("a lurker's dot is in their row's colour");
                await Assert.That(r.Legend.Single().Text).IsEqualTo("dot = the lurker at 0:30, in their Players colour");
            }
        }
    }

    [Test]
    public async Task TheBuyFilter_RecountsEveryNumberOverThatBuysRounds()
    {
        (ScoutingLayerResult full, Harness h1) = await Build(ScoutingBuy.Full);
        (ScoutingLayerResult eco, Harness h2) = await Build(ScoutingBuy.Eco);
        (ScoutingLayerResult force, Harness h3) = await Build(ScoutingBuy.Force);
        using (h1)
        using (h2)
        using (h3)
        {
            using (Assert.Multiple())
            {
                await Assert.That(Titled(full, "de_nuke T · lurker p3").Value).IsEqualTo("2 of 3 rounds");
                await Assert.That(Rows(full).Any(x => x.OpenTitle == "de_nuke T · lurker p4")).IsFalse();
                await Assert.That(Titled(full, "de_nuke T · split-off median").Value).IsEqualTo("0:18")
                    .Because("15 and 20 s");
                await Assert.That(full.Marks.Count).IsEqualTo(1);
                await Assert.That(Titled(eco, "de_nuke T · lurker p4").Value).IsEqualTo("1 of 1 round");
                await Assert.That(eco.Marks.Count).IsEqualTo(1);
                await Assert.That(force.Status.State).IsEqualTo(ScoutingLayerState.Empty);
                await Assert.That(new LurksLayer().UsesBuy).IsTrue();
            }
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly TeamIdentityService _teams;

        private Harness(TeamIdentityService teams, ScoutingReportData data)
        {
            _teams = teams;
            Data = data;
        }

        public ScoutingReportData Data { get; }

        public TeamIdentityService Teams => _teams;

        public void Dispose() => _teams.Dispose();

        public static async Task<Harness> Create()
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: _inline);
            await teams.StartAsync();
            using (cache.BeginBatch())
            {
                cache.Upsert(Record());
                cache.Upsert(Record("/d/other.dem", "de_mirage", false));
            }

            await teams.Idle;
            RoundIndexStore store = new(cache.Data());
            RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
            store.WritePositions(Demo, Positions(sources.FingerprintFor("de_nuke")));
            using GrenadeIndex grenades = new(cache.Library(), new RoundIndexEvaluatorTests.MapZones(
                ("de_nuke", new RoundIndexBuilderTests.FakeZoneResolver("zv-nuke", _ => "Ramp"))));
            grenades.Load();
            OpeningTendenciesService service = new(teams, cache.Library(), cache.RoundFacts(), grenades, store,
                sources.FingerprintFor);
            Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(1)) == true)).Id;
            ScoutingReportData data = ScoutingReportData.Start(teamA, "Team A",
                new ScoutingReportSources { Teams = teams, Library = cache.Library(), Openings = service },
                _ => _inline);
            await data.All;
            return new Harness(teams, data);
        }
    }
}
