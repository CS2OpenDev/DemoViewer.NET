#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingDashboard;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingDashboard;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using SyntheticTeam = DemoViewer.NET.AppTests.ScoutingReportShellRenderTests.SyntheticTeam;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Scouting Report Dashboard over the shell tests' synthetic team: its persisted ids, the cards as the
///     layers of the side in view (split where the board splits a layer), the map pictures asked for with each
///     layer's own marks, the header tiles, and a star shared with the Layers tab both ways.
/// </summary>
[NotInParallel]
public class ScoutingDashboardTests
{
    private static async Task Settle(ScoutingDashboardTabViewModel vm, QueuedPost posted)
    {
        for (int i = 0; i < 4; i++)
        {
            await vm.Report.LoadTask;
            await vm.Report.Layers.Idle;
            await vm.RendersIdle;
            posted.Drain();
        }
    }

    private static async Task<T> OnUi<T>(Func<Task<T>> body)
    {
        T result = default!;
        await HeadlessSession.RunOnUi(async () => result = await body());
        return result;
    }

    private static ScoutingDashboardCardViewModel Card(ScoutingDashboardTabViewModel vm, string key) =>
        vm.VisibleCards.Single(c => c.Key == key);

    private static IEnumerable<string> SectionTitles(ScoutingDashboardCardViewModel card) =>
        card.Sections.Select(s => s.Title);

    [Test]
    public async Task TheModule_ContributesTheDashboardSection_UnderItsPersistedIds()
    {
        ScoutingDashboardModule module = new(() => throw new InvalidOperationException("never built here"));
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();

        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.scouting-report-dashboard");
            await Assert.That(tab.TabId).IsEqualTo("scouting-report-dashboard.browser");
            await Assert.That(tab.FeatureId).IsEqualTo("tab.scouting-report-dashboard");
            await Assert.That(tab.Header).IsEqualTo("Scouting Report (Dashboard)");
            await Assert.That(tab.HostId).IsEqualTo(HostIds.StratBookHub);
            await Assert.That(tab.Order).IsEqualTo(6);
            await Assert.That(tab.ViewModelFactory is not null).IsTrue().Because("lazy and retained, never DataContext");
            await Assert.That(tab.DataContext).IsNull();
        }
    }

    [Test]
    public async Task TheFeatureId_IsATab_OnByDefault_ForEveryCategory()
    {
        FeatureDescriptor? feature = FeatureCatalog.ById("tab.scouting-report-dashboard");

        using (Assert.Multiple())
        {
            await Assert.That(feature).IsNotNull();
            await Assert.That(feature!.Scope).IsEqualTo(FeatureScope.Tab);
            await Assert.That(feature.ParentId).IsEqualTo("pack.stratbook");
            foreach (UserCategory category in Enum.GetValues<UserCategory>())
            {
                await Assert.That(feature.Defaults[category]).IsTrue().Because($"{category} sees the tab by default");
            }
        }
    }

    [Test]
    public async Task TheCards_AreTheLayersOfTheSideInView_SplitWhereTheBoardSplitsThem()
    {
        var seen = await OnUi(async () =>
        {
            using SyntheticTeam team = await SyntheticTeam.Create();
            QueuedPost posted = new();
            List<(string Map, IReadOnlyList<ScoutingMapMark> Marks)> drawn = [];
            using ScoutingDashboardTabViewModel vm = new(team.Tab(posted), posted.Post,
                (map, marks) =>
                {
                    lock (drawn)
                    {
                        drawn.Add((map, marks));
                    }

                    return null;
                }, _ => null);
            vm.Report.SelectedTeam = vm.Report.Teams.Single(t => t.Id == team.TeamA);
            await Settle(vm, posted);
            vm.Report.SelectMapCommand.Execute(vm.Report.Maps.Single(m => m.Map == "de_nuke"));
            vm.Report.Side = ScoutingSide.Ct;
            await Settle(vm, posted);

            ScoutingDashboardCardViewModel setups = Card(vm, "ct.setups");
            ScoutingDashboardCardViewModel rotations = Card(vm, "ct.rotations");
            ScoutingDashboardCardViewModel pistol = Card(vm, "ct.pistol");
            ScoutingDashboardCardViewModel econ = Card(vm, "econ");
            IReadOnlyList<ScoutingMapMark> setupMarks = vm.Report.Layers.Find("setups")!.Result.Marks;
            var ct = new
            {
                Keys = vm.SideCards.Select(c => c.Key).ToList(),
                SetupReads = setups.Reads.Count,
                SetupTitles = SectionTitles(setups).ToList(),
                RotationTitles = SectionTitles(rotations).ToList(),
                PistolTitles = SectionTitles(pistol).ToList(),
                EconTitles = SectionTitles(econ).ToList(),
                Opens = vm.VisibleCards.SelectMany(c => c.AllRows).EveryNumberOpensItsRounds(),
                Rows = vm.VisibleCards.Sum(c => c.AllRows.Count()),
                SetupsDrawn = drawn.Any(d => d.Map == "de_nuke" && ReferenceEquals(d.Marks, setupMarks)),
                SetupNote = setups.MapNote,
                Building = vm.VisibleCards.Any(c => c.IsBuilding)
            };

            vm.Report.Side = ScoutingSide.T;
            await Settle(vm, posted);
            var t = new
            {
                Keys = vm.SideCards.Select(c => c.Key).ToList(),
                IsTList = ReferenceEquals(vm.SideCards, vm.TCards),
                Plants = Card(vm, "t.plants").AllRows.Count(),
                ContactTitles = SectionTitles(Card(vm, "t.contact")).ToList(),
                PaceChart = Card(vm, "t.pace").HasChart,
                AwpTitle = vm.AwpCard.Title,
                Heading = vm.SideHeading
            };
            return (ct, t);
        });

        using (Assert.Multiple())
        {
            await Assert.That(seen.ct.Keys).IsEquivalentTo(
                ["ct.setups", "ct.retakes", "ct.opening", "ct.pistol", "ct.sites", "ct.against", "ct.rotations", "ct.util"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(seen.ct.SetupReads).IsGreaterThan(0).Because("the setups card leads with its finding");
            await Assert.That(seen.ct.SetupTitles).DoesNotContain("Rotations")
                .Because("the rotations rows are on the site distribution card");
            await Assert.That(seen.ct.RotationTitles).Contains("CTs per area · A · mid · B");
            await Assert.That(seen.ct.RotationTitles).DoesNotContain("Rotations")
                .Because("the Rotations layer's own rows stand in for the Setups copy of them");
            await Assert.That(seen.ct.PistolTitles).IsNotEmpty();
            await Assert.That(seen.ct.PistolTitles.All(s => s.StartsWith("Pistol", StringComparison.Ordinal))).IsTrue();
            await Assert.That(seen.ct.PistolTitles.Any(s => s.EndsWith(" · T", StringComparison.Ordinal))).IsFalse()
                .Because("the CT pistol card shows the CT loadout only");
            await Assert.That(seen.ct.EconTitles.Any(s => s.StartsWith("Pistol", StringComparison.Ordinal))).IsFalse()
                .Because("the pistol rows are the pistol card's, not printed twice");
            await Assert.That(seen.ct.Opens).IsTrue().Because("every number opens exactly its rounds, as in the inspector");
            await Assert.That(seen.ct.Rows).IsGreaterThan(20);
            await Assert.That(seen.ct.SetupsDrawn).IsTrue().Because("the setups picture is the setups layer's own marks");
            await Assert.That(seen.ct.SetupNote).StartsWith("No map picture on this host");
            await Assert.That(seen.ct.Building).IsFalse();
            await Assert.That(seen.t.IsTList).IsTrue();
            await Assert.That(seen.t.Keys).Contains("t.plants");
            await Assert.That(seen.t.Plants).IsGreaterThan(0);
            await Assert.That(seen.t.ContactTitles).IsEquivalentTo(["Time to first contact, by buy"]);
            await Assert.That(seen.t.PaceChart).IsTrue();
            await Assert.That(seen.t.AwpTitle).IsEqualTo("AWP · T · Nuke");
            await Assert.That(seen.t.Heading).IsEqualTo("T side · Nuke");
        }
    }

    [Test]
    public async Task AStarOnACard_IsStarredInTheLayersTab_AndAnUnstarThereClearsTheCard()
    {
        var seen = await OnUi(async () =>
        {
            using SyntheticTeam team = await SyntheticTeam.Create();
            QueuedPost posted = new();
            ScoutingReportNotesStore notes = new(null);
            using ScoutingReportTabViewModel layers = team.Tab(posted, notes: notes);
            using ScoutingDashboardTabViewModel dash = new(team.Tab(posted, notes: notes), posted.Post,
                (_, _) => null, _ => null);
            layers.SelectedTeam = layers.Teams.Single(t => t.Id == team.TeamA);
            dash.Report.SelectedTeam = dash.Report.Teams.Single(t => t.Id == team.TeamA);
            await ScoutingReportTestKit.Settle(layers, posted);
            await Settle(dash, posted);
            layers.SelectMapCommand.Execute(layers.Maps.Single(m => m.Map == "de_nuke"));
            dash.Report.SelectMapCommand.Execute(dash.Report.Maps.Single(m => m.Map == "de_nuke"));
            await ScoutingReportTestKit.Settle(layers, posted);
            await Settle(dash, posted);

            // A row, not a read: a suggested read may already be starred by its seed.
            ScoutingInspectorRowViewModel row = Card(dash, "ct.retakes").Sections.SelectMany(s => s.Rows)
                .First(r => !r.IsPlaceholder && !r.IsStarred);
            row.ToggleStarCommand.Execute(null);
            layers.Layers.Focus(layers.Layers.Find("retakes")!);
            bool inLayersInspector = layers.Layers.Inspector.AllRows.Single(r => r.Key == row.Key).IsStarred;
            bool inLayersEditor = layers.Editor.IsStarred(row.Key);
            int layersOnePager = layers.Editor.StarredCount;
            int dashOnePager = dash.Report.Editor.StarredCount;

            layers.Layers.Inspector.AllRows.Single(r => r.Key == row.Key).ToggleStarCommand.Execute(null);
            return (row.Key, inLayersInspector, inLayersEditor, layersOnePager, dashOnePager,
                afterUnstar: row.IsStarred);
        });

        using (Assert.Multiple())
        {
            await Assert.That(seen.Key).Contains("de_nuke|CT|retakes");
            await Assert.That(seen.inLayersInspector).IsTrue().Because("the Layers inspector reads the shared store");
            await Assert.That(seen.inLayersEditor).IsTrue();
            await Assert.That(seen.layersOnePager).IsEqualTo(seen.dashOnePager)
                .Because("both tabs print the same one-pager");
            await Assert.That(seen.afterUnstar).IsFalse().Because("an unstar in the Layers tab clears the card's star");
        }
    }

    [Test]
    public async Task TheHeaderTiles_ReadTheReport_TheFormTileOverTheLastTen()
    {
        var seen = await OnUi(async () =>
        {
            using SyntheticTeam team = await SyntheticTeam.Create();
            QueuedPost posted = new();
            using ScoutingDashboardTabViewModel vm = new(team.Tab(posted), posted.Post, (_, _) => null, _ => null);
            vm.Report.SelectedTeam = vm.Report.Teams.Single(t => t.Id == team.TeamA);
            await Settle(vm, posted);
            TeamMapFactsSet facts = (await vm.Report.Data!.Facts)!;
            return new
            {
                vm.PistolsLine,
                Expected = $"{facts.Maps.Sum(m => m.PistolsWon)} of {facts.Maps.Sum(m => m.PistolsPlayed)}",
                vm.FormTitle,
                vm.FormValue,
                Recent = vm.Report.PeriodDiffRecent?.RecordLabel,
                vm.Report.WindowSize,
                vm.DecidersValue,
                vm.VetoLine,
                vm.StandInNote
            };
        });

        using (Assert.Multiple())
        {
            await Assert.That(seen.PistolsLine).IsEqualTo(seen.Expected);
            await Assert.That(seen.WindowSize).IsEqualTo(ScoutingDashboardTabViewModel.FormWindow);
            await Assert.That(seen.FormTitle).IsEqualTo("Form, last 10");
            await Assert.That(seen.FormValue).IsEqualTo(seen.Recent);
            await Assert.That(seen.DecidersValue).IsNotEmpty();
            await Assert.That(seen.VetoLine).IsEqualTo("none entered");
            await Assert.That(seen.StandInNote).StartsWith("Stand-ins are not listed per player yet");
        }
    }
}
