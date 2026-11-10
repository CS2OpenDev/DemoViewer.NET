#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Scouting Report's layer contract and shell: the registry's focus, fade and per-side toggles, a build
///     that lands behind the scope being dropped, the team-scoped CT and T round %, the thin-sample and
///     partial-positions notices, and a star filed from the inspector under its scope.
/// </summary>
[NotInParallel]
public class ScoutingReportLayerTests
{
    private const string Demo = "/d/scouting.dem";
    private const int Rate = 64;

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly int[] _a = [0, 1, 2, 3, 4];
    private static readonly int[] _b = [5, 6, 7, 8, 9];

    private static string Id(int n) => $"7656{n:D4}";

    private static RoundFacts Round(int number, bool aOnT, int winner, bool pistol = false, int? plantSeconds = null)
    {
        int freeze = number * 10000;
        BuyType buy = pistol ? BuyType.Pistol : BuyType.Full;
        return new RoundFacts
        {
            Number = number,
            IsLive = true,
            FreezeEndTick = freeze,
            EndTick = freeze + 90 * Rate,
            WinnerSide = winner,
            PlantTick = plantSeconds is { } s ? freeze + s * Rate : null,
            PlantSite = plantSeconds is null ? default : BombSite.A,
            Ct = new SideFacts { Side = 3, Slots = aOnT ? _b : _a, PlayersAtFreezeEnd = 5, BuyType = buy },
            T = new SideFacts { Side = 2, Slots = aOnT ? _a : _b, PlayersAtFreezeEnd = 5, BuyType = buy }
        };
    }

    // Team A is T for rounds 1 to 3 (wins 1 and 3, plants at 30 s and 50 s) and CT for 4 to 6 (wins 5 only).
    // The match's own CT side won rounds 2, 5 and 6: the team-scoped CT % is not the match-wide one.
    private static RoundFactsRows Rows() => new()
    {
        Schema = RoundFactsRecords.Schema,
        Clock = new RoundFactsClock { TickRate = Rate },
        Rounds =
        [
            Round(1, true, 2, pistol: true, plantSeconds: 30),
            Round(2, true, 3, plantSeconds: 50),
            Round(3, true, 2),
            Round(4, false, 2, pistol: true),
            Round(5, false, 3),
            Round(6, false, 2)
        ]
    };

    private static RoundFactsRows Rows(RoundFacts? extra) => extra is null
        ? Rows()
        : new RoundFactsRows
        {
            Schema = Rows().Schema, Clock = Rows().Clock, Rounds = [.. Rows().Rounds, extra]
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

    private static async Task<(DemoCacheStore Cache, TeamIdentityService Teams, Guid TeamA, PostPlantService PostPlant)>
        Library(RoundFacts? extra = null)
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            cache.Upsert(Record(Demo, "de_nuke", 0, Rows(extra)));
            cache.Upsert(Record("/d/other.dem", "de_mirage", 1, null));
        }

        await teams.Idle;
        // No positions written: the post-plant counts the demo as one without positions.
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        PostPlantService postPlant = new(teams, cache.Library(), cache.RoundFacts(), new RoundIndexStore(cache.Data()),
            sources.FingerprintFor);
        Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(1)) == true)).Id;
        return (cache, teams, teamA, postPlant);
    }

    [Test]
    public async Task ARecordingCutOffMidRound_IsNeitherAWinNorALoss_InThePool()
    {
        // Team A is on CT in round 7: a row with no winner must not read as a CT loss.
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, _) = await Library(Round(7, false, 0));

        TeamMapFacts nuke = TeamMapFactsService.Build(teams, cache.Library(), new RoundFactsSource(cache), teamA)
            .For("de_nuke")!;

        using (Assert.Multiple())
        {
            await Assert.That(nuke.CtPlayed).IsEqualTo(3);
            await Assert.That(nuke.CtWon).IsEqualTo(1);
            await Assert.That(nuke.TPlayed).IsEqualTo(3);
        }
    }

    [Test]
    public async Task ThePool_ShowsTheTeamsOwnCtAndTRoundPercent_NotTheMatchWideSideSplit()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, _) = await Library();

        TeamMapFactsSet set = TeamMapFactsService.Build(teams, cache.Library(), new RoundFactsSource(cache), teamA);
        TeamMapFacts nuke = set.For("de_nuke")!;
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), false,
            post: posted.Post, runSection: _ => _inline, roundFacts: new RoundFactsSource(cache));
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);
        await ScoutingReportTestKit.Settle(vm, posted);
        MapPoolRowViewModel row = vm.Maps.Single(m => m.Map == "de_nuke");
        using (Assert.Multiple())
        {
            await Assert.That(nuke.TPlayed).IsEqualTo(3);
            await Assert.That(nuke.TWon).IsEqualTo(2);
            await Assert.That(nuke.CtPlayed).IsEqualTo(3);
            await Assert.That(nuke.CtWon).IsEqualTo(1);
            await Assert.That(nuke.PistolsPlayed).IsEqualTo(2);
            await Assert.That(nuke.PistolsWon).IsEqualTo(1);
            await Assert.That(nuke.PlantMedianSeconds).IsEqualTo(40);
            await Assert.That(set.DemosWithoutRoundFacts).IsEqualTo(1);

            await Assert.That(row.CtLabel).IsEqualTo("33%");
            await Assert.That(row.TLabel).IsEqualTo("67%");
            await Assert.That(row.CtToolTip).IsEqualTo("won 1 of 3 CT rounds on their own CT side");
            await Assert.That(row.PistolsLabel).IsEqualTo("1 of 2");
            await Assert.That(row.PlantLabel).IsEqualTo("0:40");
            await Assert.That(row.AwpLabel).IsEqualTo("—").Because("no demo has round captures to count buys in");
            await Assert.That(row.AwpToolTip).IsEqualTo("no round captures on this map; rebuild the round index");
            await Assert.That(vm.Maps.Single(m => m.Map == "de_mirage").CtLabel).IsEqualTo("—")
                .Because("a map with no Round Facts has nothing to count");
        }
    }

    [Test]
    public async Task TheNotices_SayWhenTheSampleIsThin_AndWhenDemosHaveNoPositions()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, PostPlantService postPlant) = await Library();
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), false,
            post: posted.Post, runSection: _ => _inline, postPlant: postPlant);
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);
        await ScoutingReportTestKit.Settle(vm, posted);
        vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_nuke"));

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasNotices).IsTrue();
            await Assert.That(vm.Notices[0]).StartsWith("de_nuke has 1 map in the sample.");
            await Assert.That(vm.Notices[1]).StartsWith("Positions were read for 1 of 2 demos.");
            await Assert.That(vm.Maps.Single(m => m.Map == "de_nuke").IsThin).IsTrue();
        }
    }

    [Test]
    public async Task AStarFromTheInspector_IsFiledUnderItsScope_AndThePreviewUnstarsIt()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, PostPlantService postPlant) = await Library();
        QueuedPost posted = new();
        ScoutingReportNotesStore notes = new(null);
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), false,
            post: posted.Post, runSection: _ => _inline, postPlant: postPlant, notes: notes);
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);
        await ScoutingReportTestKit.Settle(vm, posted);
        await ScoutingReportTestKit.Inspect(vm, posted, "de_nuke", ScoutingSide.T, "plants");

        // The plants read is a suggestion: it starts starred, once.
        ScoutingInspectorRowViewModel read = vm.Layers.Inspector.Reads.Single();
        bool suggested = read.IsStarred;
        read.ToggleStarCommand.Execute(null);
        bool exportOff = !vm.Editor.CanExport;

        ScoutingInspectorRowViewModel planted =
            vm.Layers.Inspector.Sections.SelectMany(s => s.Rows).Titled("de_nuke T · plants");
        planted.ToggleStarCommand.Execute(null);
        ScoutingReportFindingViewModel preview = vm.Editor.Starred.Single();
        using (Assert.Multiple())
        {
            await Assert.That(suggested).IsTrue().Because("suggested lines start starred");
            await Assert.That(read.Key).IsEqualTo("de_nuke|T|plants|All||read");
            await Assert.That(exportOff).IsTrue().Because("the one-pager is empty once the suggestion is unstarred");
            await Assert.That(planted.Key).IsEqualTo("de_nuke|T|plants|All||plants");
            await Assert.That(planted.IsStarred).IsTrue();
            await Assert.That(notes.For(teamA).Starred).Contains(planted.Key);
            await Assert.That(preview.Key).IsEqualTo(planted.Key);
            await Assert.That(preview.Where).IsEqualTo("de_nuke T");
            await Assert.That(vm.Editor.ExportLabel).IsEqualTo("Export one-pager · 1 starred");
            await Assert.That(vm.Editor.CanExport).IsTrue();
        }

        vm.UnstarCommand.Execute(preview);
        using (Assert.Multiple())
        {
            await Assert.That(planted.IsStarred).IsFalse().Because("the inspector follows the preview");
            await Assert.That(vm.Editor.Starred).IsEmpty();
            await Assert.That(vm.Editor.CanExport).IsFalse();
        }

        vm.Editor.ExportForm = ScoutingReportForm.LongForm;
        await Assert.That(vm.Editor.CanExport).IsTrue().Because("the long form exports with nothing starred");
    }

    [Test]
    public async Task TheRegistry_FocusesWhatIsTurnedOn_FadesTheRest_AndMovesFocusOffALayerTurnedOff()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, _) = await Library();
        ScoutingReportData data = ScoutingReportData.Start(teamA, "A",
            new ScoutingReportSources { Teams = teams, Library = cache.Library() }, _ => _inline);
        QueuedPost posted = new();
        using ScoutingLayerRegistry registry = new(
        [
            new Fake("setups", ScoutingSides.Both, onCt: true),
            new Fake("opening", ScoutingSides.Both),
            new Fake("lurks", ScoutingSides.T, onT: true),
            new Fake("econ", ScoutingSides.Both, strip: true)
        ], posted.Post, new Host());
        ScoutingLayerChipViewModel setups = registry.Find("setups")!;
        ScoutingLayerChipViewModel opening = registry.Find("opening")!;
        ScoutingLayerChipViewModel lurks = registry.Find("lurks")!;
        ScoutingLayerChipViewModel econ = registry.Find("econ")!;

        registry.SetScope(data, "de_nuke", ScoutingSide.Ct, ScoutingBuy.All);
        bool startsOnTheDefault = registry.Focused == setups && setups.Marks.Visible && !opening.Marks.Visible
                                  && setups.Marks.Fade == 1f && setups.Marks.Marks.Count == 1;
        string[] ctChips = [.. registry.MapChips.Select(c => c.Id)];

        opening.IsOn = true; // the checkbox
        bool onFocuses = registry.Focused == opening && setups.Marks.Fade == ScoutingMarksDocument.FadedOpacity
                         && opening.Marks.Fade == 1f;

        opening.IsOn = false;
        bool offMovesFocus = registry.Focused == setups && !opening.Marks.Visible && setups.Marks.Fade == 1f;

        setups.IsOn = false;
        bool noneDrawnFocusesTheStrip = registry.Focused == econ;

        setups.InspectCommand.Execute(null); // the name: turns it on and inspects it
        econ.InspectCommand.Execute(null);
        bool stripDrawsEverythingAtFull = registry.Focused == econ && setups.Marks.Visible && setups.Marks.Fade == 1f;

        opening.InspectCommand.Execute(null);
        registry.SetScope(data, "de_nuke", ScoutingSide.T, ScoutingBuy.All);
        bool tHasItsOwnSet = registry.Focused == lurks && !opening.IsOn && lurks.IsOn
                             && registry.MapChips.Select(c => c.Id).SequenceEqual(["setups", "opening", "lurks"]);
        registry.SetScope(data, "de_nuke", ScoutingSide.Ct, ScoutingBuy.All);

        using (Assert.Multiple())
        {
            await Assert.That(startsOnTheDefault).IsTrue();
            await Assert.That(ctChips).IsEquivalentTo(["setups", "opening"], CollectionOrdering.Matching)
                .Because("a T-only layer is not offered on CT, and a strip layer is not a chip");
            await Assert.That(onFocuses).IsTrue();
            await Assert.That(offMovesFocus).IsTrue();
            await Assert.That(noneDrawnFocusesTheStrip).IsTrue();
            await Assert.That(stripDrawsEverythingAtFull).IsTrue();
            await Assert.That(tHasItsOwnSet).IsTrue();
            await Assert.That(opening.IsOn).IsTrue().Because("each side remembers its own drawn set");
            await Assert.That(registry.Legend.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ABuildThatLandsBehindTheScope_IsDropped()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, _) = await Library();
        ScoutingReportData data = ScoutingReportData.Start(teamA, "A",
            new ScoutingReportSources { Teams = teams, Library = cache.Library() }, _ => _inline);
        QueuedPost posted = new();
        Slow slow = new();
        using ScoutingLayerRegistry registry = new([slow], posted.Post, new Host());
        ScoutingLayerChipViewModel chip = registry.Find("setups")!;

        registry.SetScope(data, "de_nuke", ScoutingSide.Ct, ScoutingBuy.All);
        TaskCompletionSource<ScoutingLayerResult> first = slow.Pending!;
        registry.SetScope(data, "de_mirage", ScoutingSide.Ct, ScoutingBuy.All);
        TaskCompletionSource<ScoutingLayerResult> second = slow.Pending!;
        bool buildingMeanwhile = chip.Status.State == ScoutingLayerState.Building;

        second.SetResult(new ScoutingLayerResult { CountLabel = "mirage" });
        first.SetResult(new ScoutingLayerResult { CountLabel = "nuke" });
        await registry.Idle;
        posted.Drain();
        using (Assert.Multiple())
        {
            await Assert.That(buildingMeanwhile).IsTrue();
            await Assert.That(chip.CountLabel).IsEqualTo("mirage").Because("the de_nuke build landed behind the scope");
            await Assert.That(chip.Context?.Map).IsEqualTo("de_mirage");
            await Assert.That(slow.Cancelled).IsTrue().Because("the old scope's token is cancelled");
        }
    }

    private sealed class Host : IScoutingRowHost
    {
        private readonly HashSet<string> _stars = [];

        public bool IsStarred(string key) => _stars.Contains(key);

        public void SetStarred(string key, bool starred)
        {
            if (starred)
            {
                _stars.Add(key);
            }
            else
            {
                _stars.Remove(key);
            }
        }

        public void Open(ScoutingInspectorRowViewModel row)
        {
        }

        public void Play(ScoutingInspectorRowViewModel row)
        {
        }
    }

    private sealed class Fake(string id, ScoutingSides sides, bool onCt = false, bool onT = false, bool strip = false)
        : ScoutingLayerBase(id, id, strip ? ScoutingLayerPlacement.Strip : ScoutingLayerPlacement.Map, sides,
            new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Ct), "D")
    {
        public override bool OnByDefault(ScoutingSide side) => side == ScoutingSide.Ct ? onCt : onT;

        public override Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ScoutingLayerResult
            {
                CountLabel = "1",
                Marks = [new ScoutingMapMark(0, 0, 0, ScoutingMarkShape.Dot, ScoutingColor.Ct)],
                Legend = [new ScoutingLegendEntry(Swatch, Id)]
            });
    }

    private sealed class Slow() : ScoutingLayerBase("setups", "Setups", ScoutingLayerPlacement.Map, ScoutingSides.Ct,
        new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Ct), "D")
    {
        public TaskCompletionSource<ScoutingLayerResult>? Pending { get; private set; }

        public bool Cancelled { get; private set; }

        public override bool OnByDefault(ScoutingSide side) => true;

        public override Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource<ScoutingLayerResult> pending =
                new();
            cancellationToken.Register(() => Cancelled = true);
            Pending = pending;
            return pending.Task;
        }
    }
}
