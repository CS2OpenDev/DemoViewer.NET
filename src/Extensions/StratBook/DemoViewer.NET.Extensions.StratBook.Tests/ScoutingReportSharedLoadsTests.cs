#region

using System.Collections.Concurrent;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using SyntheticTeam = DemoViewer.NET.AppTests.ScoutingReportShellRenderTests.SyntheticTeam;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Both Scouting Report tabs read one set of loads: the same team loads once and is shared, different teams load
///     apart, a team nobody holds is dropped, and a stamp change reloads once for both tabs.
/// </summary>
[NotInParallel]
public class ScoutingReportSharedLoadsTests
{
    // Counts every section each team runs, inline.
    private sealed class Sections
    {
        private readonly ConcurrentDictionary<(Guid Team, string Section), int> _calls = new();

        public ScoutingReportLoads Loads() => new((team, section) => work =>
        {
            _calls.AddOrUpdate((team, section), 1, (_, n) => n + 1);
            work();
            return Task.CompletedTask;
        });

        public int Count(Guid team, string section) => _calls.GetValueOrDefault((team, section));

        public IReadOnlyList<int> CountsOf(Guid team) => [.. _calls.Where(p => p.Key.Team == team).Select(p => p.Value)];
    }

    private static ScoutingReportTeamRow Other(ScoutingReportTabViewModel vm, SyntheticTeam team) =>
        vm.Teams.First(t => t.Id != team.TeamA && team.Teams.DemosOf(t.Id).Count > 0);

    [Test]
    public async Task TwoReports_OnTheSameTeam_LoadItOnce_AndShareIt()
    {
        using SyntheticTeam team = await SyntheticTeam.Create();
        Sections sections = new();
        ScoutingReportLoads loads = sections.Loads();
        QueuedPost layersPost = new();
        QueuedPost dashPost = new();
        using ScoutingReportTabViewModel layers = team.Tab(layersPost, loads: loads);
        using ScoutingReportTabViewModel second = team.Tab(dashPost, loads: loads);

        layers.SelectedTeam = layers.Teams.Single(t => t.Id == team.TeamA);
        await ScoutingReportTestKit.Settle(layers, layersPost);
        second.SelectedTeam = second.Teams.Single(t => t.Id == team.TeamA);
        await ScoutingReportTestKit.Settle(second, dashPost);

        using (Assert.Multiple())
        {
            await Assert.That(sections.Count(team.TeamA, "heatmaps")).IsEqualTo(1);
            await Assert.That(sections.Count(team.TeamA, "pool")).IsEqualTo(1);
            await Assert.That(sections.Count(team.TeamA, "post-plant")).IsEqualTo(1);
            await Assert.That(sections.Count(team.TeamA, "situational")).IsEqualTo(1);
            await Assert.That(sections.CountsOf(team.TeamA).All(n => n == 1)).IsTrue()
                .Because("no section, the players' numbers included, runs twice for one team");
            await Assert.That(ReferenceEquals(layers.Data, second.Data)).IsTrue();
            await Assert.That(loads.Holders(team.TeamA)).IsEqualTo(2);
            await Assert.That(loads.Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TabsOnDifferentTeams_LoadEachTeamOnItsOwn()
    {
        using SyntheticTeam team = await SyntheticTeam.Create();
        Sections sections = new();
        ScoutingReportLoads loads = sections.Loads();
        QueuedPost aPost = new();
        QueuedPost bPost = new();
        using ScoutingReportTabViewModel a = team.Tab(aPost, loads: loads);
        using ScoutingReportTabViewModel b = team.Tab(bPost, loads: loads);
        ScoutingReportTeamRow other = Other(b, team);

        a.SelectedTeam = a.Teams.Single(t => t.Id == team.TeamA);
        b.SelectedTeam = other;
        await ScoutingReportTestKit.Settle(a, aPost);
        await ScoutingReportTestKit.Settle(b, bPost);

        using (Assert.Multiple())
        {
            await Assert.That(sections.Count(team.TeamA, "heatmaps")).IsEqualTo(1);
            await Assert.That(sections.Count(other.Id, "heatmaps")).IsEqualTo(1);
            await Assert.That(ReferenceEquals(a.Data, b.Data)).IsFalse();
            await Assert.That(a.Data!.TeamId).IsEqualTo(team.TeamA);
            await Assert.That(b.Data!.TeamId).IsEqualTo(other.Id);
            await Assert.That(loads.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Releasing_DropsATeamOnceNoTabHoldsIt_AndReopeningLoadsAgain()
    {
        using SyntheticTeam team = await SyntheticTeam.Create();
        Sections sections = new();
        ScoutingReportLoads loads = sections.Loads();
        QueuedPost aPost = new();
        QueuedPost bPost = new();
        ScoutingReportTabViewModel a = team.Tab(aPost, loads: loads);
        using ScoutingReportTabViewModel b = team.Tab(bPost, loads: loads);
        ScoutingReportTeamRow other = Other(b, team);

        a.SelectedTeam = a.Teams.Single(t => t.Id == team.TeamA);
        b.SelectedTeam = b.Teams.Single(t => t.Id == team.TeamA);
        await ScoutingReportTestKit.Settle(a, aPost);
        await ScoutingReportTestKit.Settle(b, bPost);
        int bothHold = loads.Holders(team.TeamA);

        b.SelectedTeam = other;
        await ScoutingReportTestKit.Settle(b, bPost);
        int afterSwitch = loads.Holders(team.TeamA);

        a.Dispose();
        int afterDispose = loads.Holders(team.TeamA);
        int teamsAfterDispose = loads.Count;

        b.SelectedTeam = null;
        int teamsAfterClear = loads.Count;

        b.SelectedTeam = b.Teams.Single(t => t.Id == team.TeamA);
        await ScoutingReportTestKit.Settle(b, bPost);

        using (Assert.Multiple())
        {
            await Assert.That(bothHold).IsEqualTo(2);
            await Assert.That(afterSwitch).IsEqualTo(1).Because("switching team lets go of the first");
            await Assert.That(afterDispose).IsEqualTo(0);
            await Assert.That(teamsAfterDispose).IsEqualTo(1).Because("only the other team is still held");
            await Assert.That(teamsAfterClear).IsEqualTo(0);
            await Assert.That(sections.Count(team.TeamA, "heatmaps")).IsEqualTo(2)
                .Because("a team nobody held is loaded again when it is reopened");
        }
    }

    [Test]
    public async Task AStampChange_ReloadsOnce_ForBothTabs()
    {
        using SyntheticTeam team = await SyntheticTeam.Create();
        Sections sections = new();
        ScoutingReportLoads loads = sections.Loads();
        QueuedPost aPost = new();
        QueuedPost bPost = new();
        using ScoutingReportTabViewModel a = team.Tab(aPost, loads: loads);
        using ScoutingReportTabViewModel b = team.Tab(bPost, loads: loads);
        a.SelectedTeam = a.Teams.Single(t => t.Id == team.TeamA);
        b.SelectedTeam = b.Teams.Single(t => t.Id == team.TeamA);
        await ScoutingReportTestKit.Settle(a, aPost);
        await ScoutingReportTestKit.Settle(b, bPost);
        ScoutingReportData? before = a.Data;

        team.Store.Delete(team.Teams.DemosOf(team.TeamA)[0].Path);
        a.OnActivated(null!);
        b.OnActivated(null!);
        await ScoutingReportTestKit.Settle(a, aPost);
        await ScoutingReportTestKit.Settle(b, bPost);

        using (Assert.Multiple())
        {
            await Assert.That(ReferenceEquals(before, a.Data)).IsFalse().Because("the round index stamp moved");
            await Assert.That(ReferenceEquals(a.Data, b.Data)).IsTrue();
            await Assert.That(sections.Count(team.TeamA, "heatmaps")).IsEqualTo(2);
            await Assert.That(sections.Count(team.TeamA, "pool")).IsEqualTo(2);
            await Assert.That(loads.Holders(team.TeamA)).IsEqualTo(2);
        }
    }

    [Test]
    public async Task OpeningATeamInBothTabs_AsksForItsDemosOnce()
    {
        using SyntheticTeam team = await SyntheticTeam.Create();
        ScoutingReportLoads loads = new Sections().Loads();
        List<string[]> calls = [];
        QueuedPost aPost = new();
        QueuedPost bPost = new();
        using ScoutingReportTabViewModel a = team.Tab(aPost, loads: loads, requestDemos: p => calls.Add([.. p]));
        using ScoutingReportTabViewModel b = team.Tab(bPost, loads: loads, requestDemos: p => calls.Add([.. p]));

        a.SelectedTeam = a.Teams.Single(t => t.Id == team.TeamA);
        b.SelectedTeam = b.Teams.Single(t => t.Id == team.TeamA);
        int opened = calls.Count;
        b.ProcessTeamFirstCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(opened).IsEqualTo(1).Because("the second tab opens inside the first one's window");
            await Assert.That(calls).Count().IsEqualTo(2).Because("the button always asks");
        }
    }

    [Test]
    public async Task GiveFeedback_OpensTheIssue()
    {
        using SyntheticTeam team = await SyntheticTeam.Create();
        List<string> opened = [];
        QueuedPost posted = new();
        using ScoutingReportTabViewModel layers = team.Tab(posted, openUri: uri =>
        {
            opened.Add(uri);
            return true;
        });
        layers.GiveFeedbackCommand.Execute(null);

        await Assert.That(opened).IsEquivalentTo((string[])["https://github.com/CS2OpenDev/DemoViewer.NET/issues/78"]);
    }
}
