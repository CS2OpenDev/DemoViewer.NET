#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

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
            await Assert.That(result.Sections.Single().Title).IsEqualTo("Tactical timeouts");
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
