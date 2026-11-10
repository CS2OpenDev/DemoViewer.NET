#region

using Avalonia.Controls;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Views.ScoutingReport;
using DemoViewer.NET.Extensions.Testing;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The manual ranks store (round trip, refusing a newer or unreadable file, the history lookup by date), the
///     opponent tier it turns into, the header's inline editors and the delete-data descriptor.
/// </summary>
[NotInParallel]
public class TeamRanksTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly Guid _team = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"dv-ranks-{Guid.NewGuid():N}", TeamRanksStore.FileName);

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct, string map, int ctScore,
        int tScore)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = map,
            CtScore = ctScore,
            TScore = tScore,
            CtSideWins = ctScore,
            TSideWins = tScore
        };
        int slot = 0;
        foreach (string id in t)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    // Team A (1..5) plays team B (11..15) five times: three on 2026-03-11, then 03-21 and 03-31.
    internal static async Task<(DemoCacheStore Cache, TeamIdentityService Teams, Guid A, Guid B)> Library()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache.Library(), run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            foreach ((string path, int day) in (ReadOnlySpan<(string, int)>)
                     [("/d/r1.dem", 10), ("/d/r2.dem", 10), ("/d/r3.dem", 10), ("/d/r4.dem", 20), ("/d/r5.dem", 30)])
            {
                cache.Upsert(Record(path, day, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_nuke", 9, 16));
            }
        }

        await teams.Idle;
        Guid a = teams.Teams
            .First(t => t.Rosters.Any(r => r.CoreLineup != null && r.CoreLineup.Contains(Ids(1)[0]))).Id;
        Guid b = teams.Teams.First(t => t.Id != a).Id;
        return (cache, teams, a, b);
    }

    [Test]
    public async Task TheStore_RoundTripsThroughItsFile_AndKeepsHistory()
    {
        string path = TempFile();
        TeamRanksStore store = new(StoredFile.At(path));
        store.Set(_team, RankSystem.Hltv, 40, new DateOnly(2026, 9, 1));
        store.Set(_team, RankSystem.Hltv, 22, new DateOnly(2026, 11, 9));
        store.Set(_team, RankSystem.Vrs, 31, new DateOnly(2026, 11, 9));

        TeamRanksStore again = new(StoredFile.At(path));
        using (Assert.Multiple())
        {
            await Assert.That(again.FileProblem).IsNull();
            await Assert.That(again.History(_team, RankSystem.Hltv).Select(e => e.Rank)).IsEquivalentTo([40, 22]);
            await Assert.That(again.Current(_team, RankSystem.Hltv)!.AsOf).IsEqualTo(new DateOnly(2026, 11, 9));
            await Assert.That(again.Current(_team, RankSystem.Vrs)!.Rank).IsEqualTo(31);
            await Assert.That(again.TeamIds).IsEquivalentTo([_team]);
            await Assert.That(again.IsSessionOnly).IsFalse();
        }
    }

    [Test]
    public async Task ASessionOnlyStore_HoldsRanks_WithoutAFile()
    {
        TeamRanksStore store = new(null);
        store.Set(_team, RankSystem.Vrs, 7, new DateOnly(2026, 11, 9));
        using (Assert.Multiple())
        {
            await Assert.That(store.IsSessionOnly).IsTrue();
            await Assert.That(store.Current(_team, RankSystem.Vrs)!.Rank).IsEqualTo(7);
        }
    }

    [Test]
    public async Task ANewerOrUnreadableFile_IsRefused_AndNeverOverwritten()
    {
        foreach (string content in new[] { """{"schemaVersion":99,"entries":[]}""", "not json at all" })
        {
            string path = TempFile();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);

            TeamRanksStore store = new(StoredFile.At(path));
            store.Set(_team, RankSystem.Hltv, 5, new DateOnly(2026, 11, 9));

            using (Assert.Multiple())
            {
                await Assert.That(store.FileProblem).IsNotNull();
                await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(content);
                await Assert.That(store.Current(_team, RankSystem.Hltv)!.Rank).IsEqualTo(5)
                    .Because("the session still works, it just never reaches the file");
            }
        }
    }

    [Test]
    public async Task TheRankAtADate_IsTheLatestEntryOnOrBefore_ElseTheFirstEntry()
    {
        TeamRanksStore store = new(null);
        store.Set(_team, RankSystem.Hltv, 50, new DateOnly(2026, 3, 1));
        store.Set(_team, RankSystem.Hltv, 20, new DateOnly(2026, 3, 15));
        store.Set(_team, RankSystem.Hltv, 8, new DateOnly(2026, 6, 1));

        using (Assert.Multiple())
        {
            await Assert.That(store.RankAt(_team, RankSystem.Hltv, new DateOnly(2026, 3, 1))).IsEqualTo(50)
                .Because("on the day counts");
            await Assert.That(store.RankAt(_team, RankSystem.Hltv, new DateOnly(2026, 3, 14))).IsEqualTo(50);
            await Assert.That(store.RankAt(_team, RankSystem.Hltv, new DateOnly(2026, 3, 15))).IsEqualTo(20);
            await Assert.That(store.RankAt(_team, RankSystem.Hltv, new DateOnly(2026, 12, 1))).IsEqualTo(8);
            await Assert.That(store.RankAt(_team, RankSystem.Hltv, new DateOnly(2025, 1, 1))).IsEqualTo(50)
                .Because("older than anything entered falls back to the first entry");
            await Assert.That(store.RankAt(_team, RankSystem.Vrs, new DateOnly(2026, 3, 15))).IsNull();
        }
    }

    [Test]
    public async Task TheTier_PrefersHltv_FallsBackToVrs_AndIsUnknownWithNeither()
    {
        TeamRanksStore store = new(null);
        Guid onlyVrs = Guid.NewGuid();
        Guid both = Guid.NewGuid();
        DateOnly day = new(2026, 11, 9);
        store.Set(onlyVrs, RankSystem.Vrs, 30, day);
        store.Set(both, RankSystem.Hltv, 31, day);
        store.Set(both, RankSystem.Vrs, 3, day);

        using (Assert.Multiple())
        {
            await Assert.That(store.TierAt(onlyVrs, day)).IsEqualTo(OpponentTier.Top30).Because("30 is inside");
            await Assert.That(store.TierAt(both, day)).IsEqualTo(OpponentTier.Other).Because("HLTV wins over VRS");
            await Assert.That(store.TierAt(Guid.NewGuid(), day)).IsEqualTo(OpponentTier.Unknown);
            await Assert.That(store.TierAt(null, day)).IsEqualTo(OpponentTier.Unknown);
        }
    }

    [Test]
    public async Task TheOpponentMix_CountsEachDemoByTheRankOnItsDay_AndSizesTheDots()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid a, Guid b) = await Library();
        TeamRanksStore ranks = new(null);
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), isBrowser: false,
            ranks: ranks);
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == a);

        string empty = vm.OpponentMixLine;
        ranks.Set(b, RankSystem.Hltv, 50, new DateOnly(2026, 3, 1));
        ranks.Set(b, RankSystem.Hltv, 20, new DateOnly(2026, 3, 15));
        string mixed = vm.OpponentMixLine;
        int large = vm.Timeline.Count(d => d.Size > 7);
        int tooltips = vm.Timeline.Count(d => d.ToolTip.Contains("vs top-30"));

        vm.SelectedTeam = vm.Teams.Single(t => t.Id == b);
        string reversed = vm.OpponentMixLine;

        using (Assert.Multiple())
        {
            await Assert.That(empty).Contains("add a rank").Because("no placeholder, the empty state teaches");
            await Assert.That(mixed).IsEqualTo("2 vs top-30 · 3 other · 0 unknown");
            await Assert.That(large).IsEqualTo(2);
            await Assert.That(tooltips).IsEqualTo(2);
            await Assert.That(reversed).Contains("add a rank").Because("team A has no rank");
        }
    }

    [Test]
    public async Task TheHeaderEditor_AcceptsAPositiveInteger_OrEmpty_AndRefusesTheRest()
    {
        TeamRanksStore ranks = new(null);
        RankFieldViewModel field = new(ranks, RankSystem.Vrs, () => new DateTime(2026, 11, 9));
        field.SetTeam(_team);

        foreach (string bad in new[] { "abc", "0", "-3", "1.5", "12 13" })
        {
            field.Text = bad;
            field.Commit();
            await Assert.That(field.Error).IsEqualTo(RankFieldViewModel.InvalidMessage).Because(bad);
            await Assert.That(ranks.HasAny).IsFalse();
        }

        field.Text = "#14";
        field.Commit();
        using (Assert.Multiple())
        {
            await Assert.That(field.Error).IsEqualTo("");
            await Assert.That(field.Text).IsEqualTo("14");
            await Assert.That(field.SetLine).IsEqualTo("set 2026-11-09");
            await Assert.That(ranks.Current(_team, RankSystem.Vrs)!.Rank).IsEqualTo(14);
        }

        field.Text = "";
        field.Commit();
        using (Assert.Multiple())
        {
            await Assert.That(field.Error).IsEqualTo("");
            await Assert.That(ranks.Current(_team, RankSystem.Vrs)).IsNull();
            await Assert.That(field.SetLine).IsEqualTo("");
        }
    }

    [Test]
    public async Task ANewRank_KeepsTheOldOneAsHistory_AndAnEmptiedBoxRestoresIt()
    {
        TeamRanksStore ranks = new(null);
        DateTime now = new(2026, 3, 1);
        RankFieldViewModel field = new(ranks, RankSystem.Hltv, () => now);
        field.SetTeam(_team);
        field.Text = "40";
        field.Commit();
        now = new DateTime(2026, 11, 9);
        field.Text = "12";
        field.Commit();
        int afterTwo = ranks.History(_team, RankSystem.Hltv).Count;
        field.Text = "";
        field.Commit();

        using (Assert.Multiple())
        {
            await Assert.That(afterTwo).IsEqualTo(2);
            await Assert.That(field.Text).IsEqualTo("40").Because("clearing drops the newest entry");
            await Assert.That(field.SetLine).IsEqualTo("set 2026-03-01");
        }
    }

    [Test]
    public async Task TheStoreDescriptor_NamesTheFile_AndIsUserWork()
    {
        StoreDescriptor descriptor = StratBookStores.All.Single(d => d.Id == StratBookStores.TeamRanks);
        using (Assert.Multiple())
        {
            await Assert.That(descriptor.Paths).IsEquivalentTo([TeamRanksStore.FileName]);
            await Assert.That(descriptor.IsUserWork).IsTrue();
            await Assert.That(descriptor.Root).IsEqualTo(StoreRoot.Config);
        }
    }
}

[NotInParallel]
[Category("Render")]
public class TeamRanksRenderTests
{
    [Test]
    public async Task TheHeader_RendersTheRankBoxesAndTheOpponentMix()
    {
        List<string> shown = [];
        await HeadlessSession.RunOnUi(async () =>
        {
            (DemoCacheStore cache, TeamIdentityService teams, Guid a, Guid b) = await TeamRanksTests.Library();
            TeamRanksStore ranks = new(null);
            ranks.Set(a, RankSystem.Vrs, 12, new DateOnly(2026, 11, 9));
            ranks.Set(a, RankSystem.Hltv, 18, new DateOnly(2026, 11, 2));
            ranks.Set(b, RankSystem.Hltv, 50, new DateOnly(2026, 3, 1));
            ranks.Set(b, RankSystem.Hltv, 20, new DateOnly(2026, 3, 15));
            using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null),
                isBrowser: false, ranks: ranks, now: () => new DateTime(2026, 11, 9));
            vm.SelectedTeam = vm.Teams.Single(t => t.Id == a);
            Window window = new() { Width = 1440, Height = 360, Content = new ScoutingReportTabView { DataContext = vm } };
            window.Show();
            UxConsistencyRenderTests.Settle();
            UxConsistencyRenderTests.Save(window, "ux-scouting-report-ranks-header.png");
            shown = [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
                .Select(t => t.Text ?? "")];
            window.Close();
        });

        using (Assert.Multiple())
        {
            await Assert.That(shown).Contains("set 2026-11-09");
            await Assert.That(shown).Contains("set 2026-11-02");
            await Assert.That(shown).Contains("2 vs top-30 · 3 other · 0 unknown");
            await Assert.That(shown.Any(t => t.Contains("manual entry comes later"))).IsFalse();
        }
    }
}
