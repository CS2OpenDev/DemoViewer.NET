#region

using Avalonia;
using TUnit.Assertions.Enums;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Extensions.StratBook.Views.ScoutingReport;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using SyntheticTeam = DemoViewer.NET.AppTests.ScoutingReportShellRenderTests.SyntheticTeam;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Scouting Report tab: its layout over the shell tests' synthetic team in
///     both themes: the roster and the map pool (tabbed with the match history) side by side above the map, and the
///     map tabs, the side switch with the layer chips beside it, and the stage inside one panel. The PNGs land in <see cref="HeadlessSession.ArtifactDir" /> as
///     <c>scouting-*.png</c>.
/// </summary>
[NotInParallel]
[Category("Render")]
public class ScoutingReportTabViewTests
{
    private const int Width = 1440;

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
                    Content = new ScoutingReportTabView { DataContext = vm }
                };
                window.Show();
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Save(window, $"scouting-ct-nuke-{theme}-{Width}.png");

                Rect BoundsOf(Control c) => new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);
                TextBlock Heading(string text) => window.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Text == text && (t.IsEffectivelyVisible || text == "W–L"));
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
                bool layersOnFilterRow = Math.Abs(BoundsOf(Heading("Layers")).Y - BoundsOf(ct).Y) < 12;

                // Match history: its tab shows one row per demo, newest first; the pool table hides.
                window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "HistoryTab").IsChecked = true;
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Save(window, $"scouting-history-{theme}-{Width}.png");
                ItemsControl history = window.GetVisualDescendants().OfType<ItemsControl>()
                    .Single(i => ReferenceEquals(i.ItemsSource, vm.MatchHistory));
                int historyRows = history.IsEffectivelyVisible ? history.ItemCount : 0;
                bool poolHidden = !Heading("W–L").IsEffectivelyVisible;

                // The one-pager lives in a side panel the header opens, not in the inspector; Escape closes it.
                ScoutingReportTabView view = (ScoutingReportTabView)window.Content!;
                Border inspector = view.FindControl<Border>("InspectorPanel")!;
                bool onePagerOutOfInspector = !inspector.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Text == vm.Editor.OnePagerHeading);
                view.FindControl<Button>("OnePagerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                UxConsistencyRenderTests.Settle();
                Border scrim = view.FindControl<Border>("OnePagerScrim")!;
                bool onePagerOpens = scrim.IsVisible && scrim.GetVisualDescendants().OfType<ItemsControl>()
                    .Any(i => ReferenceEquals(i.ItemsSource, vm.Editor.Starred) && i.ItemCount == vm.Editor.Starred.Count);
                UxConsistencyRenderTests.Save(window, $"scouting-onepager-{theme}-{Width}.png");
                view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                bool onePagerCloses = !scrim.IsVisible;

                // The Setups inspector offers its tabs; opening one shows only its sections and the untabbed ones.
                List<string> tabNames = [.. inspector.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("maptab") && b.IsEffectivelyVisible).Select(b => b.Content as string ?? "")];
                vm.Layers.Inspector.OpenTab("Shape");
                UxConsistencyRenderTests.Settle();
                bool shapeOnly = vm.Layers.Inspector.TabSections.All(t => t.Tab is "" or "Shape")
                                 && vm.Layers.Inspector.TabSections.Any(t => t.Tab == "Shape");
                UxConsistencyRenderTests.Save(window, $"scouting-setups-shape-{theme}-{Width}.png");

                // A layer's own controls (the AWP trail window) sit on the map, not in the inspector.
                vm.Layers.Focus(vm.Layers.Find("awp")!);
                await ScoutingReportTestKit.Settle(vm, posted);
                UxConsistencyRenderTests.Settle();
                Border controls = window.GetVisualDescendants().OfType<Border>()
                    .Single(b => AutomationProperties.GetName(b) == "Layer controls");
                bool controlsOnMap = controls.IsEffectivelyVisible
                                     && controls.GetVisualParent() is Panel overMap
                                     && overMap.Children.OfType<ScoutingMapHost>().Any()
                                     && !inspector.GetVisualDescendants().OfType<RadioButton>().Any(r => r.Classes.Contains("pill"));
                UxConsistencyRenderTests.Save(window, $"scouting-awp-controls-{theme}-{Width}.png");

                // Rotations draw on the map: each real rotation as its own trail.
                ScoutingLayerChipViewModel rotationsChip = vm.Layers.Find("rotations")!;
                rotationsChip.IsOn = true;
                vm.Layers.Focus(rotationsChip);
                await ScoutingReportTestKit.Settle(vm, posted);
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Save(window, $"scouting-rotations-{theme}-{Width}.png");
                bool rotationsOnMap = rotationsChip.IsMap && vm.Layers.MapChips.Contains(rotationsChip)
                                      && !vm.Layers.StripMinis.Contains(rotationsChip);
                window.Close();
                return new { sameRow, rosterLeft, halves, above, oneMapPanel, layersOnFilterRow, historyRows, demos = vm.MatchHistory.Count, poolHidden, onePagerOutOfInspector, onePagerOpens, onePagerCloses, controlsOnMap, tabNames, shapeOnly, rotationsOnMap };
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
            await Assert.That(seen.layersOnFilterRow).IsTrue().Because("the layer chips share the side and buy line");
            await Assert.That(seen.demos).IsGreaterThan(0);
            await Assert.That(seen.historyRows).IsEqualTo(seen.demos).Because("Match history lists every demo");
            await Assert.That(seen.poolHidden).IsTrue().Because("one tab of the panel at a time");
            await Assert.That(seen.onePagerOutOfInspector).IsTrue().Because("the one-pager left the inspector");
            await Assert.That(seen.onePagerOpens).IsTrue().Because("the header button opens it with every starred line");
            await Assert.That(seen.onePagerCloses).IsTrue().Because("Escape closes it");
            await Assert.That(seen.controlsOnMap).IsTrue().Because("the AWP trail window sits on the map it changes");
            await Assert.That(seen.tabNames).IsEquivalentTo(["Players", "Shape", "By buy"], CollectionOrdering.Matching)
                .Because("the Setups inspector is split into tabs");
            await Assert.That(seen.shapeOnly).IsTrue().Because("a tab shows only its own sections");
            await Assert.That(seen.rotationsOnMap).IsTrue().Because("rotations is a map layer, not a strip mini");
        }
    }

    [Test]
    public async Task HoveringASetupDot_SaysWhoAndWhere_AndClickingPinsItWithTheRestOfThatRound()
    {
        var seen = await OnUi(async () =>
        {
            using SyntheticTeam team = await SyntheticTeam.Create();
            QueuedPost posted = new();
            using ScoutingReportTabViewModel vm = team.Tab(posted);
            vm.SelectedTeam = vm.Teams.Single(t => t.Id == team.TeamA);
            await ScoutingReportTestKit.Settle(vm, posted);
            vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_nuke"));
            vm.Side = ScoutingSide.Ct;
            await ScoutingReportTestKit.Settle(vm, posted);

            Window window = new() { Width = Width, Height = 2800, Content = new ScoutingReportTabView { DataContext = vm } };
            window.Show();
            UxConsistencyRenderTests.Settle();
            UxConsistencyRenderTests.Settle();

            ScoutingMapHost host = window.GetVisualDescendants().OfType<ScoutingMapHost>().Single();
            ScoutingLayerChipViewModel setups = vm.Layers.Find("setups")!;
            // A round dot: the small ones carry the round, the big ringed ones the player's main spot.
            ScoutingMapMark dot = setups.Marks.Marks.First(m => m.Info is not null && m.Size < 5);
            Point at = default;
            foreach (var pane in host.Map.Panes)
            {
                (double x, double y) = pane.Camera.Current.WorldToScreen(dot.X, dot.Y);
                Point p = new(x + pane.ViewportRect.Left, y + pane.ViewportRect.Top);
                if (host.MarkAt(p) is not null)
                {
                    at = p;
                    break;
                }
            }

            var hit = host.MarkAt(at);
            host.Click(at);
            UxConsistencyRenderTests.Settle();
            Border card = window.GetVisualDescendants().OfType<Border>()
                .Single(b => AutomationProperties.GetName(b) == "Selected map mark");
            bool cardShows = card.IsEffectivelyVisible && vm.Layers.MarkCard.Rows.Count > 0;
            string title = vm.Layers.MarkCard.Title;
            bool canWatch = vm.Layers.MarkCard.Rows.Any(r => r.CanOpen && r.CanPlay);
            int detail = vm.Layers.Selection.Marks.Count;
            bool selectionDrawn = vm.Layers.Selection.Visible;
            UxConsistencyRenderTests.Save(window, $"scouting-mark-pinned-{Width}.png");

            // Clicking the map away from every mark unpins it.
            host.Click(new Point(2, 2));
            bool cleared = !vm.Layers.MarkCard.IsOpen && !vm.Layers.Selection.Visible;
            window.Close();
            return new { hit = hit is not null, cardShows, title, canWatch, detail, selectionDrawn, cleared };
        });

        using (Assert.Multiple())
        {
            await Assert.That(seen.hit).IsTrue().Because("a setup dot answers the pointer");
            await Assert.That(seen.cardShows).IsTrue().Because("clicking pins the card over the map");
            await Assert.That(seen.title).Contains("round").Because("the card names the player and the round");
            await Assert.That(seen.canWatch).IsTrue().Because("the round opens in Review and plays in 2D");
            await Assert.That(seen.selectionDrawn).IsTrue();
            await Assert.That(seen.detail).IsGreaterThan(1).Because("the ring on the dot and the other CTs that round");
            await Assert.That(seen.cleared).IsTrue().Because("a click off every mark unpins it");
        }
    }

    [Test]
    public async Task Export_OnePagerDrawsEachBuy_AndTheFullReportCyclesEveryLayer()
    {
        var seen = await OnUi(async () =>
        {
            using SyntheticTeam team = await SyntheticTeam.Create();
            QueuedPost posted = new();
            using ScoutingReportTabViewModel vm = team.Tab(posted);
            vm.SelectedTeam = vm.Teams.Single(t => t.Id == team.TeamA);
            await ScoutingReportTestKit.Settle(vm, posted);
            vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_nuke"));
            await ScoutingReportTestKit.Settle(vm, posted);

            IReadOnlyList<ScoutingReportBuyStage> stages = await vm.BuyStagesAsync([]);
            List<ScoutingReportStageRow> rows =
            [
                .. stages.GroupBy(s => (s.Map, s.Side)).Select(g => new ScoutingReportStageRow(g.Key.Map, g.Key.Side,
                [
                    .. g.Select(s => ScoutingStageRenderer.TryRender(s.Map, s.Marks) is { } png
                        ? new ScoutingReportPicture(s.Caption, png)
                        : null).OfType<ScoutingReportPicture>()
                ]))
            ];
            vm.Editor.ExportForm = ScoutingReportForm.OnePager;
            string onePager = ScoutingReportExporter.Html(vm.Editor.Document(true, rows), ScoutingReportForm.OnePager);
            await File.WriteAllTextAsync(Path.Combine(HeadlessSession.ArtifactDir, "scouting-one-pager.html"), onePager);

            vm.Editor.ExportForm = ScoutingReportForm.LongForm;
            ScoutingReportDocument full = vm.Editor.Document(false);
            string fullMd = ScoutingReportExporter.Markdown(full, ScoutingReportForm.LongForm);
            return new
            {
                canExport = vm.Editor.CanExport,
                captions = stages.Select(s => s.Caption).ToList(),
                ctMap = stages.Where(s => s.Side == "CT").Select(s => s.Map).Distinct().ToList(),
                figures = onePager.Split("<div class=\"stages\">").Length - 1,
                cards = full.Cards.Select(c => (c.Side, c.Title)).ToList(),
                fullMd,
                label = vm.Editor.ExportLabel
            };
        });

        using (Assert.Multiple())
        {
            await Assert.That(seen.canExport).IsTrue().Because("the full report needs no star");
            await Assert.That(seen.ctMap).IsEquivalentTo(["de_nuke"]).Because("nothing starred: only the map in view");
            await Assert.That(seen.captions.Count).IsGreaterThan(0);
            await Assert.That(seen.captions.All(c => c.Contains("Full buy") || c.Contains("Force") || c.Contains("Eco")))
                .IsTrue().Because("one picture per buy, never All");
            await Assert.That(seen.captions.Any(c => c.Contains("CT") && c.Contains("setups"))).IsTrue()
                .Because("the CT pictures carry the setups");
            await Assert.That(seen.figures).IsGreaterThan(0).Because("the one-pager opens the map with its buy row");
            await Assert.That(seen.cards.Count(c => c is { Title: "Utility", Side.Length: > 0 })).IsEqualTo(2)
                .Because("the full report has a card per layer per side");
            await Assert.That(seen.cards.Count(c => c is { Title: "Utility", Side: "" })).IsEqualTo(1)
                .Because("and one map-wide card per layer for a line scoped to no side");
            await Assert.That(seen.cards.Any(c => c is { Side: "CT", Title: "Setups" })).IsTrue();
            await Assert.That(seen.fullMd).Contains("#### Setups");
            await Assert.That(seen.label).IsEqualTo("Export full report");
        }
    }

    private static async Task<T> OnUi<T>(Func<Task<T>> body)
    {
        T result = default!;
        await HeadlessSession.RunOnUi(async () => result = await body());
        return result;
    }
}
