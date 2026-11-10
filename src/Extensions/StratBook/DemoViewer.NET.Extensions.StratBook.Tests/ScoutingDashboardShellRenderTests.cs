#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingDashboard;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Extensions.StratBook.Views.ScoutingDashboard;
using SyntheticTeam = DemoViewer.NET.AppTests.ScoutingReportShellRenderTests.SyntheticTeam;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The whole Dashboard shell over the synthetic team in the light and the dark theme at 1440 wide: the CT
///     cards on de_nuke with the real radar under the map cards, and the T cards on de_mirage. The PNGs land in
///     <see cref="HeadlessSession.ArtifactDir" /> as <c>dashboard-*.png</c> for a person to look at.
/// </summary>
[NotInParallel]
[Category("Render")]
public class ScoutingDashboardShellRenderTests
{
    private const int Width = 1440;

    private static async Task<T> OnUi<T>(Func<Task<T>> body)
    {
        T result = default!;
        await HeadlessSession.RunOnUi(async () => result = await body());
        return result;
    }

    private static async Task Settle(ScoutingDashboardTabViewModel vm, QueuedPost posted)
    {
        for (int i = 0; i < 4; i++)
        {
            await vm.Report.LoadTask;
            await vm.Report.Layers.Idle;
            await vm.RendersIdle;
            posted.Drain();
            UxConsistencyRenderTests.Settle();
        }
    }

    [Test]
    [Arguments("light")]
    [Arguments("dark")]
    public async Task TheDashboard_RendersARealisticTeam_InEachTheme(string theme)
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
                using ScoutingDashboardTabViewModel vm = new(team.Tab(posted), posted.Post);
                vm.Report.SelectedTeam = vm.Report.Teams.Single(t => t.Id == team.TeamA);
                await Settle(vm, posted);
                vm.Report.SelectMapCommand.Execute(vm.Report.Maps.Single(m => m.Map == "de_nuke"));
                vm.Report.Side = ScoutingSide.Ct;
                await Settle(vm, posted);

                Window window = new()
                {
                    Width = Width,
                    Height = 7600,
                    RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light,
                    Content = new ScoutingDashboardTabView { DataContext = vm }
                };
                window.Show();
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Save(window, $"dashboard-ct-nuke-{theme}-{Width}.png");
                int pictures = window.GetVisualDescendants().OfType<Image>()
                    .Count(i => i.IsEffectivelyVisible && i.Source is not null);
                double grid = window.GetVisualDescendants().OfType<ScoutingCardGrid>().First().Bounds.Width;
                bool mono = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible)
                    .Any(t => t.FontFamily.ToString().Contains("mono", StringComparison.OrdinalIgnoreCase)
                              || t.FontFamily.ToString().Contains("Consolas", StringComparison.OrdinalIgnoreCase));
                List<string> text = [.. window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];
                // Nothing laid out wider than the page: a card that overflows would be clipped at the edge.
                double widest = window.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("card") && b.IsEffectivelyVisible)
                    .Max(b => b.Bounds.Width);

                vm.Report.SelectMapCommand.Execute(vm.Report.Maps.Single(m => m.Map == "de_mirage"));
                vm.Report.Side = ScoutingSide.T;
                await Settle(vm, posted);
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Save(window, $"dashboard-t-mirage-{theme}-{Width}.png");
                window.Close();
                return new { pictures, grid, mono, text, widest };
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
            await Assert.That(seen.pictures).IsGreaterThanOrEqualTo(3)
                .Because("setups, retakes, opening and AWP draw their marks on the radar");
            await Assert.That(seen.grid).IsGreaterThan(1300);
            await Assert.That(seen.widest).IsLessThanOrEqualTo(Width);
            await Assert.That(seen.mono).IsFalse().Because("the labels are words, not code");
            await Assert.That(seen.text).Contains("Starting setups");
            await Assert.That(seen.text).Contains("CT side · Nuke");
            await Assert.That(seen.text).Contains("Head to head");
        }
    }
}
