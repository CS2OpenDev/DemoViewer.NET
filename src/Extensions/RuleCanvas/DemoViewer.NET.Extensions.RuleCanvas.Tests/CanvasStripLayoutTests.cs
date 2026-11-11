#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;
using DemoViewer.NET.Extensions.Testing;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The strip at narrow widths: nothing in it overlaps anything else, the status trims, and the
///     secondary buttons move into the overflow menu below the compact width.
/// </summary>
[NotInParallel]
[Category("Render")]
public class CanvasStripLayoutTests
{
    private static void Pump(int frames = 3)
    {
        for (int i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // The strip's leaf controls as laid out: every visible control in either group that is not a container of others.
    private static List<(string Name, Rect Box)> Leaves(CanvasTabView view)
    {
        Control strip = view.GetVisualDescendants().OfType<Control>().First(c => c.Name == "Strip");
        List<(string, Rect)> boxes = [];
        foreach (Control group in strip.GetVisualDescendants().OfType<Control>().Where(c => c.Name is "StripLeft" or "StripRight" or "StripStatus"))
        {
            foreach (Control child in group.GetVisualChildren().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0))
            {
                Point at = child.TranslatePoint(default, strip)!.Value;
                string name = child.Name ?? (child as ContentControl)?.Content as string ?? (child as TextBlock)?.Text ?? child.GetType().Name;
                boxes.Add((group.Name + "/" + name, new Rect(at, child.Bounds.Size)));
            }
        }

        return boxes;
    }

    // With a demo open and a run shown, the live group is at its widest; a shipped ruleset adds its chip and Edit a copy.
    [Test]
    [Arguments(1600d, false)]
    [Arguments(1280d, false)]
    [Arguments(1024d, false)]
    [Arguments(1280d, true)]
    [Arguments(1024d, true)]
    public async Task AtThisWidth_NoTwoStripControlsOverlap(double width, bool shipped)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = shipped ? RulesTree.WithShipped("player_round") : RulesTree.WithUser("player_round");
            (CanvasTabViewModel vm, _) = LiveCanned.Open(rules);
            vm.ShowLiveRun(LiveCanned.Run(vm));
            CanvasTabView view = new() { DataContext = vm };
            Window window = new() { Width = width, Height = 900, Content = view };
            window.Show();
            Pump();

            List<(string Name, Rect Box)> leaves = Leaves(view);
            List<string> overlaps = [];
            for (int i = 0; i < leaves.Count; i++)
            {
                for (int j = i + 1; j < leaves.Count; j++)
                {
                    if (leaves[i].Box.Intersects(leaves[j].Box))
                    {
                        overlaps.Add($"{leaves[i].Name} {leaves[i].Box} x {leaves[j].Name} {leaves[j].Box}");
                    }
                }
            }

            Control strip = view.GetVisualDescendants().OfType<Control>().First(c => c.Name == "Strip");
            bool compact = width < CanvasTabViewModel.CompactStripWidth;
            using (Assert.Multiple())
            {
                await Assert.That(vm.IsStripCompact).IsEqualTo(compact);
                await Assert.That(leaves.Any(l => l.Name.EndsWith("/Open Authoring", StringComparison.Ordinal))).IsEqualTo(!compact)
                    .Because("below the compact width the secondary buttons are in the overflow menu");
                await Assert.That(leaves.Any(l => l.Name.EndsWith("/Inspector", StringComparison.Ordinal))).IsEqualTo(!compact);
                await Assert.That(leaves.Any(l => l.Name.EndsWith("/+ Add", StringComparison.Ordinal))).IsTrue().Because("adding stays on the strip");
                await Assert.That(leaves.Count).IsGreaterThan(5);
                await Assert.That(overlaps).IsEmpty();
                await Assert.That(leaves.All(l => l.Box.Right <= strip.Bounds.Width + 0.5)).IsTrue()
                    .Because("nothing runs off the strip's right edge: " + string.Join(", ", leaves.Where(l => l.Box.Right > strip.Bounds.Width).Select(l => l.Name)));
            }

            window.Close();
        });
    }

    [Test]
    public async Task TheOverflowMenu_HoldsTheSecondaryButtons_OnlyWhileCompact()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            CanvasTabViewModel vm = rules.NewViewModel();
            vm.OnActivated(null!);
            CanvasTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1024, Height = 900, Content = view };
            window.Show();
            Pump();
            Button more = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "\u22EF");
            MenuFlyout menu = (MenuFlyout)more.Flyout!;
            menu.ShowAt(more);
            Pump();
            List<MenuItem> items = [.. menu.Items.OfType<MenuItem>()];
            MenuItem inspector = items.Single(m => m.Header as string == "Inspector");
            using (Assert.Multiple())
            {
                await Assert.That(items.Where(m => m.IsVisible).Select(m => m.Header as string)).Contains("Open Authoring");
                await Assert.That(inspector.IsVisible).IsTrue();
                await Assert.That(inspector.IsChecked).IsEqualTo(vm.ShowInspector);
            }

            inspector.IsChecked = !inspector.IsChecked;
            await Assert.That(vm.ShowInspector).IsEqualTo(inspector.IsChecked).Because("the menu toggle drives the panel");
            menu.Hide();

            vm.SetStripWidth(1600);
            menu.ShowAt(more);
            Pump();
            await Assert.That(inspector.IsVisible).IsFalse().Because("with room the toggles are back on the strip");
            menu.Hide();
            window.Close();
        });
    }
}
