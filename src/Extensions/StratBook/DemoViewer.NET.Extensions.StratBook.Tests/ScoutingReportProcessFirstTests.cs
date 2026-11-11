#region

using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using SyntheticTeam = DemoViewer.NET.AppTests.ScoutingReportShellRenderTests.SyntheticTeam;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The team in view goes first: opening a team asks for its demos once, the button asks again, other teams'
///     demos are never asked for, and the notice counts the team's demos still being processed.
/// </summary>
[NotInParallel]
public class ScoutingReportProcessFirstTests
{
    private sealed class Requests
    {
        public List<string[]> Calls { get; } = [];

        public void Record(IReadOnlyList<string> paths) => Calls.Add([.. paths]);
    }

    private static string[] DemosOf(SyntheticTeam team, Guid id) => [.. team.Teams.DemosOf(id).Select(d => d.Path)];

    [Test]
    public async Task OpeningATeam_AsksForExactlyItsDemos_Once_AndAnotherTeamOnlyForItsOwn()
    {
        using SyntheticTeam team = await SyntheticTeam.Create();
        QueuedPost posted = new();
        Requests requests = new();
        using ScoutingReportTabViewModel vm = team.Tab(posted, requestDemos: requests.Record);
        ScoutingReportTeamRow a = vm.Teams.Single(t => t.Id == team.TeamA);
        ScoutingReportTeamRow other = vm.Teams.First(t => t.Id != team.TeamA && team.Teams.DemosOf(t.Id).Count > 0);
        int before = requests.Calls.Count;

        vm.SelectedTeam = a;
        await ScoutingReportTestKit.Settle(vm, posted);
        vm.SelectedTeam = other;
        await ScoutingReportTestKit.Settle(vm, posted);
        vm.SelectedTeam = a;
        await ScoutingReportTestKit.Settle(vm, posted);

        using (Assert.Multiple())
        {
            await Assert.That(before).IsEqualTo(0).Because("nothing is asked for before a team is open");
            await Assert.That(requests.Calls).Count().IsEqualTo(2).Because("going back to a team inside the window asks nothing again");
            await Assert.That(requests.Calls[0]).IsEquivalentTo(DemosOf(team, team.TeamA));
            await Assert.That(requests.Calls[0]).Count().IsEqualTo(8);
            await Assert.That(requests.Calls[1]).IsEquivalentTo(DemosOf(team, other.Id));
            await Assert.That(requests.Calls[1].Except(DemosOf(team, other.Id))).IsEmpty();
        }
    }

    [Test]
    public async Task TheButton_AsksAgain_AndTheWindowLapsing_LetsReopeningAsk()
    {
        using SyntheticTeam team = await SyntheticTeam.Create();
        QueuedPost posted = new();
        Requests requests = new();
        DateTime now = new(2026, 3, 18, 12, 0, 0);
        using ScoutingReportTabViewModel vm = team.Tab(posted, requestDemos: requests.Record, now: () => now);
        ScoutingReportTeamRow a = vm.Teams.Single(t => t.Id == team.TeamA);
        ScoutingReportTeamRow other = vm.Teams.First(t => t.Id != team.TeamA && team.Teams.DemosOf(t.Id).Count > 0);

        vm.SelectedTeam = a;
        await ScoutingReportTestKit.Settle(vm, posted);
        bool can = vm.CanProcessTeamFirst;
        vm.ProcessTeamFirstCommand.Execute(null);
        vm.ProcessTeamFirstCommand.Execute(null);
        int afterButton = requests.Calls.Count;

        now += ScoutingReportTabViewModel.AutoRequestWindow + TimeSpan.FromSeconds(1);
        vm.SelectedTeam = other;
        vm.SelectedTeam = a;
        await ScoutingReportTestKit.Settle(vm, posted);

        using (Assert.Multiple())
        {
            await Assert.That(can).IsTrue();
            await Assert.That(afterButton).IsEqualTo(3).Because("the button asks every time");
            await Assert.That(requests.Calls.Skip(1).Take(2).All(c => c.Order().SequenceEqual(DemosOf(team, team.TeamA).Order())))
                .IsTrue();
            await Assert.That(requests.Calls).Count().IsEqualTo(5).Because("past the window both teams ask again");
        }
    }

    [Test]
    public async Task WithoutAHostToQueue_TheButtonIsHidden_AndNothingIsAsked()
    {
        using SyntheticTeam team = await SyntheticTeam.Create();
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = team.Tab(posted);
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == team.TeamA);
        await ScoutingReportTestKit.Settle(vm, posted);

        await Assert.That(vm.CanProcessTeamFirst).IsFalse();
    }

    [Test]
    public async Task TheNotice_CountsTheTeamsDemosStillProcessing_AndIsGoneWhenAllAreCurrent()
    {
        using SyntheticTeam team = await SyntheticTeam.Create();
        QueuedPost posted = new();
        using ScoutingReportTabViewModel vm = team.Tab(posted, requestDemos: _ => { });
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == team.TeamA);
        await ScoutingReportTestKit.Settle(vm, posted);
        bool currentShowsNone = vm.Notices.All(n => !n.Contains("still processing", StringComparison.Ordinal));

        team.Store.InvalidateAll();
        vm.Side = vm.Side == ScoutingSide.Ct ? ScoutingSide.T : ScoutingSide.Ct;
        await ScoutingReportTestKit.Settle(vm, posted);

        using (Assert.Multiple())
        {
            await Assert.That(currentShowsNone).IsTrue();
            await Assert.That(vm.Notices).Contains(n =>
                n.StartsWith("8 of 8 demos for this team still processing", StringComparison.Ordinal));
        }
    }
}
