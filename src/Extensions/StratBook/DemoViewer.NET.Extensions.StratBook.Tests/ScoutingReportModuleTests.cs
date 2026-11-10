#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Services.DemoCache;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Scouting Report module's persisted ids, its feature-catalog row (on by default, every
///     category), the Map Pool Record service's per-map aggregation and decider inference, the tab VM's
///     projection and sample-size lines, and the veto history store beside it.
/// </summary>
[NotInParallel]
public class ScoutingReportModuleTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static DemoCacheRecord Record(
        string path, int day, string[] t, string[] ct, string map,
        int ctScore, int tScore, int ctSideWins, int tSideWins)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = map,
            CtScore = ctScore,
            TScore = tScore,
            CtSideWins = ctSideWins,
            TSideWins = tSideWins
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

    // Team A (1..5) against team B (11..15): a same-day best-of-three (A wins the decider on the third
    // demo), a lone fourth demo against B on another day (excluded from the decider group by size), and
    // a repeat of de_nuke on a fifth day so the map row aggregates across more than one demo.
    private static async Task<(DemoCacheStore Cache, TeamIdentityService Teams, Guid TeamA, Guid TeamB)> Library()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache.Library(), run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            // Day 10, game 1: A (T) beats B (CT) on de_nuke.
            cache.Upsert(Record("/d/s1.dem", 10, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_nuke",
                ctScore: 9, tScore: 16, ctSideWins: 9, tSideWins: 16));
            // Day 10, game 2: A (CT) loses to B (T) on de_mirage.
            cache.Upsert(Record("/d/s2.dem", 10, Ids(11, 12, 13, 14, 15), Ids(1, 2, 3, 4, 5), "de_mirage",
                ctScore: 9, tScore: 13, ctSideWins: 9, tSideWins: 13));
            // Day 10, game 3 (the decider): A (T) beats B (CT) on de_inferno.
            cache.Upsert(Record("/d/s3.dem", 10, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_inferno",
                ctScore: 6, tScore: 13, ctSideWins: 6, tSideWins: 13));
            // Day 20, alone: A (T) beats B (CT) on de_dust2. A group of one is not a decider.
            cache.Upsert(Record("/d/s4.dem", 20, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_dust2",
                ctScore: 5, tScore: 13, ctSideWins: 5, tSideWins: 13));
            // Day 30, alone: A (CT) loses to B (T), de_nuke again.
            cache.Upsert(Record("/d/s5.dem", 30, Ids(11, 12, 13, 14, 15), Ids(1, 2, 3, 4, 5), "de_nuke",
                ctScore: 8, tScore: 13, ctSideWins: 8, tSideWins: 13));
        }

        await teams.Idle;
        Guid teamA = teams.Teams
            .First(t => t.Rosters.Any(r => r.CoreLineup != null && r.CoreLineup.Contains(Ids(1)[0]))).Id;
        Guid teamB = teams.Teams.First(t => t.Id != teamA).Id;
        return (cache, teams, teamA, teamB);
    }

    [Test]
    public async Task TheModule_ContributesTheScoutingReportSection_UnderThePersistedIds()
    {
        ScoutingReportModule module = new(() => throw new InvalidOperationException("never built here"));
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();

        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.scouting-report");
            await Assert.That(tab.TabId).IsEqualTo("scouting-report.browser");
            await Assert.That(tab.Header).IsEqualTo("Scouting Report");
            await Assert.That(tab.PreviousTabIds).IsEquivalentTo(["dossier.browser"]);
            await Assert.That(tab.HostId).IsEqualTo(HostIds.StratBookHub);
            await Assert.That(tab.Order).IsEqualTo(5).Because("last on the rail, after Review");
            await Assert.That(tab.ViewModelFactory is not null).IsTrue()
                .Because("lazy and retained, never DataContext");
            await Assert.That(tab.DataContext).IsNull();
        }
    }

    [Test]
    public async Task TheFeatureId_IsATab_OnByDefault_ForEveryCategory()
    {
        FeatureDescriptor? feature = FeatureCatalog.ById("tab.scouting-report");

        using (Assert.Multiple())
        {
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.Tab);
            await Assert.That(feature.ParentId).IsEqualTo("pack.stratbook")
                .Because("the tab cascades off with the Strat Book extension");
            await Assert.That(feature.GroupId).IsNull().Because("it must not disturb the leader-lock ordering");
            foreach (UserCategory category in Enum.GetValues<UserCategory>())
            {
                await Assert.That(feature.Defaults[category]).IsTrue().Because($"{category} sees the tab by default");
            }

            await Assert.That(ShellModuleFeatureGate.DesktopOnlyIds).DoesNotContain("tab.scouting-report")
                .Because("the tab renders on the browser and says teams are session only");
        }
    }

    [Test]
    public async Task MapPoolRecordService_AggregatesPerMap_AndInfersTheDecider()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, _) = await Library();

        MapPoolRecord record = MapPoolRecordService.Build(teams, cache.Library(), teamA);

        using (Assert.Multiple())
        {
            await Assert.That(record.TotalDemos).IsEqualTo(5);
            await Assert.That(record.Maps.Count).IsEqualTo(4);

            MapPoolMapRow nuke = record.Maps.Single(m => m.Map == "de_nuke");
            await Assert.That(nuke.Played).IsEqualTo(2);
            await Assert.That(nuke.Wins).IsEqualTo(1);
            await Assert.That(nuke.Losses).IsEqualTo(1);
            await Assert.That(nuke.CtRoundsWon).IsEqualTo(9 + 8);
            await Assert.That(nuke.TRoundsWon).IsEqualTo(16 + 13);

            MapPoolMapRow inferno = record.Maps.Single(m => m.Map == "de_inferno");
            await Assert.That(inferno.Played).IsEqualTo(1);
            await Assert.That(inferno.Wins).IsEqualTo(1);
            await Assert.That(inferno.WinRate).IsEqualTo(1.0);

            await Assert.That(record.Deciders.Played).IsEqualTo(1)
                .Because("only the day-10 group of three is a recognizable series");
            await Assert.That(record.Deciders.Wins).IsEqualTo(1);
            await Assert.That(record.Deciders.Losses).IsEqualTo(0);
        }
    }

    [Test]
    public async Task AReactivation_KeepsTheProjection_ANewDemoOfTheTeamRebuildsIt_AndAHiddenTabWaits()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, _) = await Library();
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), isBrowser: false);
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);
        MapPoolRowViewModel first = vm.Maps[0];
        vm.OnActivated(null!);
        bool kept = ReferenceEquals(vm.Maps[0], first);

        vm.OnDeactivated();
        cache.Upsert(Record("/d/s6.dem", 40, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_ancient",
            ctScore: 4, tScore: 13, ctSideWins: 4, tSideWins: 13));
        await teams.Idle;
        string hiddenLine = vm.SampleSizeLine;
        vm.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(kept).IsTrue().Because("nothing the projection reads changed");
            await Assert.That(hiddenLine).Contains("5 demos").Because("a hidden tab does not re-project");
            await Assert.That(vm.SampleSizeLine).Contains("6 demos");
            await Assert.That(vm.SelectedTeam?.Id).IsEqualTo(teamA);
        }
    }

    [Test]
    public async Task EachMap_GetsATabAndAPoolLine_FiledUnderItsScopeKey_AndTheDecidersStayTeamWide()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, _) = await Library();
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), isBrowser: false,
            notes: new ScoutingReportNotesStore(null), now: () => new DateTime(2026, 4, 5));
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);
        MapPoolRowViewModel nuke = vm.Maps.Single(m => m.Map == "de_nuke");
        ScoutingReportFindingViewModel line = vm.Editor.Findings.Single(f => f.Key == "de_nuke||pool|||record");
        using (Assert.Multiple())
        {
            await Assert.That(vm.Maps[0].Map).IsEqualTo("de_nuke").Because("most played first");
            await Assert.That(vm.SelectedMap).IsSameReferenceAs(vm.Maps[0]).Because("the first tab opens");
            await Assert.That(vm.Maps[0].IsSelected).IsTrue();
            await Assert.That(nuke.TabDetail).IsEqualTo("2 · 5d");
            await Assert.That(nuke.WinLossLabel).IsEqualTo("1–1");
            await Assert.That(nuke.LastPlayedLabel).IsEqualTo("5d ago");
            await Assert.That(line.Map).IsEqualTo("de_nuke");
            await Assert.That(line.Section).IsEqualTo(ScoutingReportEditorViewModel.MapPoolSection);
            await Assert.That(line.Text).StartsWith("de_nuke: 2 played, 1-1, win rate 50%");
            await Assert.That(vm.Editor.Findings.Any(f => f.Key == "||pool|||deciders")).IsTrue()
                .Because("the decider record is about the team, not a map");
            await Assert.That(vm.Wins).IsEqualTo(3);
            await Assert.That(vm.Losses).IsEqualTo(2);
            await Assert.That(vm.RecordLine).IsEqualTo("60% of 5 maps");
            await Assert.That(vm.DemosLine).IsEqualTo("5 demos · Mar 11 to Mar 31 · newest 5d ago");
            await Assert.That(vm.Timeline.Count).IsEqualTo(5);
            await Assert.That(vm.Timeline.Count(d => d.IsSelectedMap)).IsEqualTo(2);
            await Assert.That(vm.Timeline.Count(d => d.Won)).IsEqualTo(3);
        }
    }

    [Test]
    public async Task DismissRestoreAndTheSettledToggle_WorkOnAPoolLine()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, _) = await Library();
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), isBrowser: false,
            notes: new ScoutingReportNotesStore(null));
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);
        ScoutingReportFindingViewModel line = vm.Editor.Findings.Single(f => f.Key == "de_nuke||pool|||record");

        vm.Editor.RemoveCommand.Execute(line);
        bool goneAfterDismiss = vm.Editor.Findings.All(f => f.Key != line.Key);
        vm.Editor.ShowSettled = true;
        ScoutingReportFindingViewModel? settled = vm.Editor.Findings.SingleOrDefault(f => f.Key == line.Key);
        string label = settled?.RemoveLabel ?? "";
        vm.Editor.RemoveCommand.Execute(settled);
        vm.Editor.ShowSettled = false;
        using (Assert.Multiple())
        {
            await Assert.That(goneAfterDismiss).IsTrue();
            await Assert.That(settled).IsNotNull().Because("the settled toggle lists dismissed lines");
            await Assert.That(label).IsEqualTo("Restore");
            await Assert.That(vm.Editor.Findings.Any(f => f.Key == line.Key)).IsTrue().Because("restored");
        }
    }

    [Test]
    public async Task TheMapTabs_PickTheMap_AndTheScopeLineFollowsTheSideAndTheBuy_AndTheBelowSectionsStartClosed()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, _) = await Library();
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), isBrowser: false);
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);
        bool closedAtStart = !vm.IsPeriodOpen && !vm.IsVetoOpen && !vm.IsNotesOpen && vm.IsPoolOpen;
        string first = vm.ScopeLine;

        vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_inferno"));
        vm.Side = DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers.ScoutingSide.T;
        vm.Buy = DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers.ScoutingBuy.Force;
        vm.TogglePeriodCommand.Execute(null);
        vm.TogglePoolCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(closedAtStart).IsTrue();
            await Assert.That(first).IsEqualTo("de_nuke · CT · all buys · 2 maps");
            await Assert.That(vm.ScopeLine).IsEqualTo("de_inferno · T · Force buy · 1 map");
            await Assert.That(vm.MapName).IsEqualTo("de_inferno");
            await Assert.That(vm.Maps.Single(m => m.IsSelected).Map).IsEqualTo("de_inferno");
            await Assert.That(vm.IsT).IsTrue();
            await Assert.That(vm.IsPeriodOpen).IsTrue();
            await Assert.That(vm.IsPoolOpen).IsFalse();
            await Assert.That(vm.PlayersTitle).IsEqualTo("Players · de_inferno");
        }
    }

    [Test]
    public async Task TheGrenadeIndexFinishingItsLoad_ReProjectsTheSelectedTeam()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, _) = await Library();
        using GrenadeIndex grenades = new(cache.Library());
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), new VetoHistoryStore(null), isBrowser: false,
            grenades: grenades);
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);
        MapPoolRowViewModel before = vm.Maps[0];
        vm.OnActivated(null!);
        bool keptWhileLoading = ReferenceEquals(vm.Maps[0], before);
        grenades.Load();
        using (Assert.Multiple())
        {
            await Assert.That(keptWhileLoading).IsTrue();
            await Assert.That(vm.Maps[0]).IsNotSameReferenceAs(before)
                .Because("the openings read the index that just loaded");
            await Assert.That(vm.SelectedTeam?.Id).IsEqualTo(teamA);
        }
    }

    [Test]
    public async Task TheTab_ProjectsTheSelectedTeam_WithSampleSizes_AndRunsTheVetoActions()
    {
        (DemoCacheStore cache, TeamIdentityService teams, Guid teamA, Guid teamB) = await Library();
        VetoHistoryStore vetoes = new(null);
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), vetoes, isBrowser: false);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasTeams).IsTrue();
            await Assert.That(vm.Teams.Count).IsEqualTo(2);
            await Assert.That(vm.HasSelection).IsFalse();
            await Assert.That(vm.IsBrowser).IsFalse();
        }

        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasSelection).IsTrue();
            await Assert.That(vm.HasMaps).IsTrue();
            await Assert.That(vm.Maps.Count).IsEqualTo(4);
            await Assert.That(vm.SampleSizeLine).Contains("5 demos");
            await Assert.That(vm.SampleSizeLine).Contains("4 maps");
            await Assert.That(vm.HasDeciderData).IsTrue();
            await Assert.That(vm.DeciderLine).Contains("1-0");
        }

        await Assert.That(vm.HasVetoes).IsFalse();
        vm.NewVetoMap = "de_ancient";
        vm.NewVetoAction = VetoAction.Ban;
        vm.NewVetoByOpponent = false;
        vm.AddVetoCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasVetoes).IsTrue();
            await Assert.That(vm.Vetoes.Single().Map).IsEqualTo("de_ancient");
            await Assert.That(vm.Vetoes.Single().ActionLabel).IsEqualTo("Ban");
            await Assert.That(vm.Vetoes.Single().ByLabel).IsEqualTo("us");
            await Assert.That(vetoes.For(teamA).Count).IsEqualTo(1);
        }

        vm.RemoveVetoCommand.Execute(vm.Vetoes.Single());
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasVetoes).IsFalse();
            await Assert.That(vetoes.For(teamA)).IsEmpty();
        }

        // The other team's own record has no shared history with A's veto entries.
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamB);
        await Assert.That(vm.HasVetoes).IsFalse();
    }

    [Test]
    public async Task WithNoDemos_TheRecordSaysSo_AndVetoesStaySessionOnly()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache.Library(), run: _inline);
        await teams.StartAsync();
        VetoHistoryStore vetoes = new(null);
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), vetoes);

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasTeams).IsFalse();
            await Assert.That(vm.VetoesAreSessionOnly).IsTrue();
            await Assert.That(vetoes.IsSessionOnly).IsTrue();
        }
    }
}
