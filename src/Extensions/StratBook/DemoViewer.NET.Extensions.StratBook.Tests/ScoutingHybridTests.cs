#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingHybrid;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Extensions.StratBook.Views.ScoutingHybrid;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using SyntheticTeam = DemoViewer.NET.AppTests.ScoutingReportShellRenderTests.SyntheticTeam;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Scouting Report (Hybrid) tab: its persisted ids, and its layout over the shell tests' synthetic team in
///     both themes: the roster and the map pool side by side above the map, and the map tabs, the side switch and
///     the stage inside one panel. The PNGs land in <see cref="HeadlessSession.ArtifactDir" /> as
///     <c>hybrid-*.png</c>.
/// </summary>
[NotInParallel]
[Category("Render")]
public class ScoutingHybridTests
{
    private const int Width = 1440;

    [Test]
    public async Task TheModule_ContributesTheHybridSection_UnderItsPersistedIds()
    {
        ScoutingHybridModule module = new(() => throw new InvalidOperationException("never built here"));
        WorkspaceTabDescriptor tab = module.CreateTabs(null!).Single();
        FeatureDescriptor? feature = FeatureCatalog.ById("tab.scouting-report-hybrid");

        using (Assert.Multiple())
        {
            await Assert.That(module.Id).IsEqualTo("net.demoviewer.scouting-report-hybrid");
            await Assert.That(tab.TabId).IsEqualTo("scouting-report-hybrid.browser");
            await Assert.That(tab.FeatureId).IsEqualTo("tab.scouting-report-hybrid");
            await Assert.That(tab.Header).IsEqualTo("Scouting Report (Hybrid)");
            await Assert.That(tab.HostId).IsEqualTo(HostIds.StratBookHub);
            await Assert.That(tab.Order).IsEqualTo(7);
            await Assert.That(tab.ViewModelFactory is not null).IsTrue().Because("lazy and retained, never DataContext");
            await Assert.That(tab.DataContext).IsNull();
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
    [Arguments("light")]
    [Arguments("dark")]
    public async Task TheRosterAndPoolShareARow_AboveOnePanelHoldingTheMapTabsAndTheStage(string theme)
    {
        var seen = await OnUi(async () =>
        {
            ThemeVariant? original = Application.Current?.RequestedThemeVariant;
            try
            {
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                }

                using SyntheticTeam team = await SyntheticTeam.Create();
                QueuedPost posted = new();
                using ScoutingReportTabViewModel vm = team.Tab(posted);
                vm.SelectedTeam = vm.Teams.Single(t => t.Id == team.TeamA);
                await ScoutingReportTestKit.Settle(vm, posted);
                vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_nuke"));
                vm.Side = ScoutingSide.Ct;
                await ScoutingReportTestKit.Settle(vm, posted);

                Window window = new()
                {
                    Width = Width,
                    Height = 2800,
                    RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light,
                    Content = new ScoutingHybridTabView { DataContext = vm }
                };
                window.Show();
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Save(window, $"hybrid-ct-nuke-{theme}-{Width}.png");

                Rect BoundsOf(Control c) => new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);
                TextBlock Heading(string text) => window.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.IsEffectivelyVisible && t.Text == text);
                Border PanelOf(Control c) => c.GetVisualAncestors().OfType<Border>().Last(b => b.Classes.Contains("panel"));

                Rect roster = BoundsOf(PanelOf(Heading(vm.PlayersTitle)));
                Rect pool = BoundsOf(PanelOf(Heading("Map pool")));
                ItemsControl tabs = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "MapTabs");
                Border mapPanel = PanelOf(tabs);
                Border stage = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "StagePanel");
                RadioButton ct = window.GetVisualDescendants().OfType<RadioButton>().First(r => r.Classes.Contains("ct"));
                bool sameRow = Math.Abs(roster.Y - pool.Y) < 1;
                bool rosterLeft = roster.Right <= pool.X;
                bool halves = Math.Abs(roster.Width - pool.Width) < 2 && roster.Width > Width * 0.4;
                bool above = Math.Max(roster.Bottom, pool.Bottom) <= BoundsOf(mapPanel).Y;
                bool oneMapPanel = ReferenceEquals(PanelOf(ct), mapPanel)
                                   && stage.GetVisualAncestors().Contains(mapPanel);
                window.Close();
                return new { sameRow, rosterLeft, halves, above, oneMapPanel };
            }
            finally
            {
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = original;
                }
            }
        });

        using (Assert.Multiple())
        {
            await Assert.That(seen.sameRow).IsTrue().Because("the roster and the map pool share one row");
            await Assert.That(seen.rosterLeft).IsTrue().Because("the roster is on the left");
            await Assert.That(seen.halves).IsTrue().Because("each takes half the page");
            await Assert.That(seen.above).IsTrue().Because("both sit above the map");
            await Assert.That(seen.oneMapPanel).IsTrue()
                .Because("the map tabs, the side switch and the stage sit in one containing panel");
        }
    }

    private static async Task<T> OnUi<T>(Func<Task<T>> body)
    {
        T result = default!;
        await HeadlessSession.RunOnUi(async () => result = await body());
        return result;
    }
}
