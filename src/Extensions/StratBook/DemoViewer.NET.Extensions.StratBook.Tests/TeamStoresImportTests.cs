#region

using System.Text.Json;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
using DemoViewer.NET.Extensions.StratBook.Services.Portability;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Services.DemoCache;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Library Transfer's imports of the stores filed per team (Scouting report notes, veto history, team ranks,
///     watched situations) and of Strat Mining's choices: entries move onto the team the import mapped, unions by
///     id, this machine's values kept, and a second import that changes nothing.
/// </summary>
public class TeamStoresImportTests
{
    private static readonly Guid _theirs = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid _ours = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly TeamIdMap _map = new(new Dictionary<Guid, Guid> { [_theirs] = _ours });

    private sealed class Folders : IDisposable
    {
        private readonly string _root = TempRoot();

        public Folders()
        {
            Directory.CreateDirectory(There);
            Directory.CreateDirectory(Here);
        }

        public string There => Path.Combine(_root, "there");
        public string Here => Path.Combine(_root, "here");
        public List<string> Lines { get; } = [];

        public StagedFolder Staged => new(There);

        public StoredFile HereFile(string name) => StoredFile.At(Path.Combine(Here, name));

        public void WriteThere<T>(string name, T file, JsonSerializerOptions options) =>
            File.WriteAllBytes(Path.Combine(There, name), JsonSerializer.SerializeToUtf8Bytes(file, options));

        public byte[] HereBytes(string name) => File.ReadAllBytes(Path.Combine(Here, name));

        public ImportLedger Ledger(ImportConflictPolicy policy = ImportConflictPolicy.KeepMine) =>
            new(policy, Lines.Add);

        public void Dispose() => DeleteQuietly(_root);
    }

    [Test]
    public async Task Notes_MoveOntoTheMappedTeam_UniteByKey_AndKeepThisMachinesSummary()
    {
        using Folders f = new();
        Guid noteId = Guid.NewGuid();
        f.WriteThere(ScoutingReportNotesStore.FileName, new ScoutingReportNotesFile
        {
            Teams =
            {
                new ScoutingReportTeamNotes
                {
                    TeamId = _theirs,
                    Summary = "their summary",
                    Igl = "765",
                    Starred = ["a", "b"],
                    Hidden = ["h"],
                    Edits = new Dictionary<string, string> { ["a"] = "their a", ["c"] = "their c" },
                    Notes = [new ScoutingReportNote { Id = noteId, Text = "watch the lurk" }]
                }
            }
        }, ScoutingReportNotesFile.JsonOptions);
        ScoutingReportNotesStore store = new(f.HereFile(ScoutingReportNotesStore.FileName));
        store.SetSummary(_ours, "our summary");
        store.SetStarred(_ours, "a", true);
        store.SetEdit(_ours, "a", "our a", "generated a");

        store.Import(f.Staged, _map, f.Ledger());
        byte[] after = f.HereBytes(ScoutingReportNotesStore.FileName);
        ImportLedger again = f.Ledger();
        store.Import(f.Staged, _map, again);

        ScoutingReportTeamNotes notes = new ScoutingReportNotesStore(f.HereFile(ScoutingReportNotesStore.FileName))
            .For(_ours);
        using (Assert.Multiple())
        {
            await Assert.That(notes.Summary).IsEqualTo("our summary");
            await Assert.That(notes.Igl).IsEqualTo("765").Because("this machine had none");
            await Assert.That(notes.Starred).IsEquivalentTo(["a", "b"]);
            await Assert.That(notes.Hidden).IsEquivalentTo(["h"]);
            await Assert.That(notes.Edits["a"]).IsEqualTo("our a");
            await Assert.That(notes.Edits["c"]).IsEqualTo("their c");
            await Assert.That(notes.Notes.Select(n => n.Id)).IsEquivalentTo([noteId]);
            await Assert.That(f.HereBytes(ScoutingReportNotesStore.FileName)).IsEquivalentTo(after);
            await Assert.That(again.Added + again.Merged).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Vetoes_UniteById_UnderTheMappedOpponent()
    {
        using Folders f = new();
        VetoEntry shared = new() { OpponentTeamId = _theirs, Order = 1, Map = "de_nuke", Note = "theirs" };
        VetoEntry added = new() { OpponentTeamId = _theirs, Order = 2, Map = "de_inferno" };
        f.WriteThere(VetoHistoryStore.FileName, new VetoHistoryFile { Entries = { shared, added } },
            VetoHistoryFile.JsonOptions);
        VetoHistoryStore store = new(f.HereFile(VetoHistoryStore.FileName));
        store.Add(new VetoEntry { Id = shared.Id, OpponentTeamId = _ours, Order = 1, Map = "de_nuke", Note = "ours" });

        ImportLedger ledger = f.Ledger();
        store.Import(f.Staged, _map, ledger);
        byte[] after = f.HereBytes(VetoHistoryStore.FileName);
        store.Import(f.Staged, _map, f.Ledger());

        IReadOnlyList<VetoEntry> steps = new VetoHistoryStore(f.HereFile(VetoHistoryStore.FileName)).For(_ours);
        using (Assert.Multiple())
        {
            await Assert.That(steps.Select(s => s.Map)).IsEquivalentTo(["de_nuke", "de_inferno"]);
            await Assert.That(steps[0].Note).IsEqualTo("ours");
            await Assert.That(ledger.Conflicts).IsEqualTo(1);
            await Assert.That(f.HereBytes(VetoHistoryStore.FileName)).IsEquivalentTo(after);
        }
    }

    [Test]
    public async Task Ranks_AddWhatIsMissing_AndKeepThisMachinesRankForTheSameDay()
    {
        using Folders f = new();
        DateOnly day = new(2026, 11, 1);
        f.WriteThere(TeamRanksStore.FileName, new TeamRanksFile
        {
            Entries =
            {
                new RankEntry { TeamId = _theirs, System = RankSystem.Hltv, Rank = 12, AsOf = day },
                new RankEntry { TeamId = _theirs, System = RankSystem.Vrs, Rank = 40, AsOf = day }
            }
        }, TeamRanksFile.JsonOptions);
        TeamRanksStore store = new(f.HereFile(TeamRanksStore.FileName));
        store.Set(_ours, RankSystem.Hltv, 10, day);

        store.Import(f.Staged, _map, f.Ledger());
        byte[] after = f.HereBytes(TeamRanksStore.FileName);
        store.Import(f.Staged, _map, f.Ledger());

        using (Assert.Multiple())
        {
            await Assert.That(store.Current(_ours, RankSystem.Hltv)!.Rank).IsEqualTo(10);
            await Assert.That(store.Current(_ours, RankSystem.Vrs)!.Rank).IsEqualTo(40);
            await Assert.That(f.HereBytes(TeamRanksStore.FileName)).IsEquivalentTo(after);
        }
    }

    [Test]
    public async Task Watches_TakeTheMappedOpponent_AndAreAddedOnce()
    {
        using Folders f = new();
        WatchedSituation watch = new()
        {
            Id = Guid.NewGuid(),
            Name = "A hold",
            Map = "de_nuke",
            WatermarkTicks = 7,
            Filters = SearchFilterValues.None with { Opponent = _theirs }
        };
        f.WriteThere(WatchedSituationsService.FileName, new WatchedSituationsFile { Watches = { watch } },
            WatchedSituationsFile.JsonOptions);
        DemoCacheStore cache = new(null);
        using SituationIndex index = new(cache.Library(), new RoundIndexStore(cache.Data()),
            new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn));
        using WatchedSituationsService service = new(f.HereFile(WatchedSituationsService.FileName), index,
            cache.Library(), now: () => 50);

        service.Import(f.Staged, _map, f.Ledger());
        byte[] after = f.HereBytes(WatchedSituationsService.FileName);
        ImportLedger again = f.Ledger();
        service.Import(f.Staged, _map, again);

        using (Assert.Multiple())
        {
            await Assert.That(service.Watches.Single().Filters.Opponent).IsEqualTo(_ours);
            await Assert.That(f.HereBytes(WatchedSituationsService.FileName)).IsEquivalentTo(after);
            await Assert.That(again.Skipped).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Mining_UnitesDismissals_AndRecordsAPromotionOnlyWhenItsStratIsHere()
    {
        using Folders f = new();
        StratStore strats = new(null);
        StratDocument present = Minimal();
        strats.Save(present, [], "created");
        File.WriteAllText(Path.Combine(f.There, StratMiningFiles.StatePath),
            """{"SchemaVersion":1,"Dismissed":["d1","d2"],"Promoted":{"p1":""" + $"\"{present.Id}\",\"p2\":\"{Guid.NewGuid()}\"" + "}}");
        File.WriteAllText(Path.Combine(f.Here, StratMiningFiles.StatePath),
            """{"SchemaVersion":1,"Dismissed":["d1"],"Promoted":{}}""");
        DemoCacheStore cache = new(null);
        using StratMiningService service = new(cache.Library(), cache.RoundFacts(), new RoundIndexStore(cache.Data()),
            StratMiningServiceTests.Sources.FingerprintFor, null, null, strats, null,
            TestFiles.Mining(Path.Combine(f.Here, "cache"), f.Here), run: a =>
            {
                a();
                return Task.CompletedTask;
            }) { QuietDelay = Timeout.InfiniteTimeSpan };

        service.Import(f.Staged, f.Ledger());
        byte[] after = f.HereBytes(StratMiningFiles.StatePath);
        service.Import(f.Staged, f.Ledger());

        using JsonDocument state = JsonDocument.Parse(after);
        using (Assert.Multiple())
        {
            await Assert.That(state.RootElement.GetProperty("Dismissed").EnumerateArray().Select(e => e.GetString()!))
                .IsEquivalentTo(["d1", "d2"]);
            await Assert.That(state.RootElement.GetProperty("Promoted").EnumerateObject().Select(p => p.Name))
                .IsEquivalentTo(["p1"]);
            await Assert.That(f.HereBytes(StratMiningFiles.StatePath)).IsEquivalentTo(after);
        }
    }
}
