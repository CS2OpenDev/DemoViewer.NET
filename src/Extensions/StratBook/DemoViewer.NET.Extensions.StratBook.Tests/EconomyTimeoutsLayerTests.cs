#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Services.DemoCache;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Economy layer's pistol loadout and low-buy sections and the Timeouts layer, built through their own
///     <c>BuildAsync</c> over the synthetic team's stored round captures (four de_nuke demos): what reaches the
///     inspector, and what each says on a host with no round index. No window: the shell renders live in
///     <see cref="ScoutingReportShellRenderTests" />.
/// </summary>
[NotInParallel]
public class EconomyTimeoutsLayerTests
{
    private static async Task<(ScoutingReportShellRenderTests.SyntheticTeam Team, ScoutingReportTabViewModel Vm)> Open(
        bool withIndex = true)
    {
        ScoutingReportShellRenderTests.SyntheticTeam team = await ScoutingReportShellRenderTests.SyntheticTeam.Create();
        QueuedPost posted = new();
        ScoutingReportTabViewModel vm = withIndex
            ? team.Tab(posted)
            : new ScoutingReportTabViewModel(team.Teams, team.Cache.Library(), new VetoHistoryStore(null),
                isBrowser: true, post: posted.Post, runSection: _ => a =>
                {
                    a();
                    return Task.CompletedTask;
                }, roundFacts: new DemoViewer.NET.Services.Facts.RoundFactsSource(team.Cache));
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == team.TeamA);
        await ScoutingReportTestKit.Settle(vm, posted);
        vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_nuke"));
        await ScoutingReportTestKit.Settle(vm, posted);
        return (team, vm);
    }

    private static ScoutingInspectorRow Row(ScoutingLayerResult result, string label) =>
        result.Sections.SelectMany(s => s.Rows).Single(r => r.Label == label);

    [Test]
    public async Task Economy_FromCaptures_CountsThePistolKitPerSide_AndTheLowBuyWeapons()
    {
        (ScoutingReportShellRenderTests.SyntheticTeam team, ScoutingReportTabViewModel vm) = await Open();
        using ScoutingReportShellRenderTests.SyntheticTeam _ = team;
        using ScoutingReportTabViewModel __ = vm;
        ScoutingLayerContext context = vm.Layers.Find("econ")!.Context!;
        ScoutingLayerResult result = await new EconomyLayer().BuildAsync(context, CancellationToken.None);
        string[] titles = [.. result.Sections.Select(s => s.Title)];
        ScoutingInspectorSection low = result.Sections.Single(s => s.Title == "Weapons bought on low buys");
        List<ScoutingInspectorRow> lowBuys = [.. low.Rows.Where(r => r.Rounds.Count > 0)];

        using (Assert.Multiple())
        {
            await Assert.That(result.Status.State).IsEqualTo(ScoutingLayerState.Ready);
            await Assert.That(titles).Contains("Pistol loadout · CT");
            await Assert.That(titles).Contains("Pistol loadout · T");

            // Four demos, five loadouts a pistol round: slots 0..2 buy kevlar, slot 0 a helmet, 0 and 1 a kit on CT.
            await Assert.That(Row(result, "Pistol rounds · CT").Value).IsEqualTo("4 rounds");
            await Assert.That(Row(result, "Kevlar · CT").Value).IsEqualTo("12 of 20 players");
            await Assert.That(Row(result, "Helmet · T").Value).IsEqualTo("4 of 20 players");
            await Assert.That(Row(result, "Defuse kit · CT").Value).IsEqualTo("8 of 20 players");
            await Assert.That(Row(result, "Kevlar · CT").Rounds.Count).IsEqualTo(4);

            // Eco, force and semi rounds name the guns bought; full buys and pistols stay out.
            await Assert.That(lowBuys).IsNotEmpty();
            await Assert.That(lowBuys.All(r => r.Value.Contains("AWP", StringComparison.Ordinal))).IsTrue();
            await Assert.That(lowBuys.Any(r => r.Label.StartsWith("Full", StringComparison.Ordinal))).IsFalse();
            await Assert.That(low.Rows[^1].Note).IsEqualTo(DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport.EconomyLoadouts.BuyCaveat);
        }
    }

    // A loss with no following round (a map's last round) is in no "next" row, so it is not in their total.
    [Test]
    public async Task Economy_AfterALoss_SharesAreOfTheLossesThatHaveANextRound()
    {
        using ScoutingReportShellRenderTests.SyntheticTeam team =
            await ScoutingReportShellRenderTests.SyntheticTeam.Create();
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = team.Tab(posted);
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == team.TeamA);
        await ScoutingReportTestKit.Settle(vm, posted);
        vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_nuke"));
        await ScoutingReportTestKit.Settle(vm, posted);

        int unpaired = 0;
        List<double> totals = [];
        foreach (ScoutingSide side in (ScoutingSide[])[ScoutingSide.T, ScoutingSide.Ct])
        {
            vm.Side = side;
            await ScoutingReportTestKit.Settle(vm, posted);
            ScoutingLayerResult result = await new EconomyLayer()
                .BuildAsync(vm.Layers.Find("econ")!.Context!, CancellationToken.None);
            ScoutingInspectorSection section =
                result.Sections.Single(s => s.Title.StartsWith("After a loss", StringComparison.Ordinal));
            unpaired += section.Rows[0].Rounds.Count - section.Rows.Skip(1).Sum(r => r.Rounds.Count);
            totals.Add(section.Rows.Skip(1).Sum(r => r.BarPercent ?? 0));
        }

        using (Assert.Multiple())
        {
            await Assert.That(unpaired).IsGreaterThan(0)
                .Because("the synthetic maps end on a loss, which has no round after it");
            await Assert.That(totals.All(total => Math.Abs(total - 100.0) < 0.001)).IsTrue()
                .Because($"the shares after a loss add up to the whole, and they add up to {string.Join(" and ", totals)}");
        }
    }

    // A pistol with no live round after it on the same side is in no follow-up row, so it is not in their total.
    [Test]
    public async Task Economy_PistolFollowUp_SharesAreOfThePistolsThatHaveAFollowingRound()
    {
        using ScoutingReportShellRenderTests.SyntheticTeam team =
            await ScoutingReportShellRenderTests.SyntheticTeam.Create();
        // On one of the nuke demos the round after the second-half pistol was not live (a restart, say).
        DemoCacheRecord record = team.Cache.LoadRecords(e => e.Path == "/d/de_nuke-0.dem").Single();
        record.RoundFacts!.Rounds.Single(r => r.Number == 10).IsLive = false;
        team.Cache.Upsert(record);
        await team.Teams.Idle;
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = team.Tab(posted);
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == team.TeamA);
        await ScoutingReportTestKit.Settle(vm, posted);
        vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_nuke"));
        await ScoutingReportTestKit.Settle(vm, posted);

        int unpaired = 0;
        List<double> totals = [];
        foreach (ScoutingSide side in (ScoutingSide[])[ScoutingSide.T, ScoutingSide.Ct])
        {
            vm.Side = side;
            await ScoutingReportTestKit.Settle(vm, posted);
            ScoutingLayerResult result = await new EconomyLayer()
                .BuildAsync(vm.Layers.Find("econ")!.Context!, CancellationToken.None);
            ScoutingInspectorSection section =
                result.Sections.Single(s => s.Title.StartsWith("Pistols ·", StringComparison.Ordinal));
            List<ScoutingInspectorRow> followUp =
                [.. section.Rows.Where(r => r.Label.Contains("the bonus", StringComparison.Ordinal))];
            if (followUp.Count == 0)
            {
                continue;
            }

            unpaired += section.Rows[0].Rounds.Count - followUp.Sum(r => r.Rounds.Count);
            totals.Add(followUp.Sum(r => r.BarPercent ?? 0));
        }

        using (Assert.Multiple())
        {
            await Assert.That(unpaired).IsGreaterThan(0)
                .Because("one second-half pistol has no live round after it");
            await Assert.That(totals.All(total => Math.Abs(total - 100.0) < 0.001)).IsTrue()
                .Because($"the follow-up shares add up to the whole, and they add up to {string.Join(" and ", totals)}");
        }
    }

    [Test]
    public async Task Timeouts_FromCaptures_AreTheTeamsTacticalOnes_OnTheMapInView()
    {
        (ScoutingReportShellRenderTests.SyntheticTeam team, ScoutingReportTabViewModel vm) = await Open();
        using ScoutingReportShellRenderTests.SyntheticTeam _ = team;
        using ScoutingReportTabViewModel __ = vm;
        ScoutingLayerResult result = await new TimeoutsLayer()
            .BuildAsync(vm.Layers.Find("timeouts")!.Context!, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(result.Status.State).IsEqualTo(ScoutingLayerState.Ready);
            await Assert.That(result.CountLabel).IsEqualTo("4 timeouts");
            await Assert.That(result.Sections.Select(s => s.Title)).IsEquivalentTo(["Tactical timeouts", "By side"], CollectionOrdering.Matching);
            await Assert.That(Row(result, "Taken").Value).IsEqualTo("4 timeouts");
        }
    }

    [Test]
    public async Task WithoutARoundIndex_BothSayTheyAreInTheDesktopApp()
    {
        (ScoutingReportShellRenderTests.SyntheticTeam team, ScoutingReportTabViewModel vm) = await Open(withIndex: false);
        using ScoutingReportShellRenderTests.SyntheticTeam _ = team;
        using ScoutingReportTabViewModel __ = vm;
        ScoutingLayerResult timeouts = await new TimeoutsLayer()
            .BuildAsync(vm.Layers.Find("timeouts")!.Context!, CancellationToken.None);
        ScoutingLayerResult economy = await new EconomyLayer()
            .BuildAsync(vm.Layers.Find("econ")!.Context!, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(timeouts.Status.Message).IsEqualTo(ScoutingLayerResult.NotOnThisHost);
            await Assert.That(timeouts.Status.Display).StartsWith("Available in the desktop app");
            await Assert.That(Row(economy, "Pistol loadout").Note).IsEqualTo(ScoutingLayerResult.NotOnThisHost);
        }
    }
}
