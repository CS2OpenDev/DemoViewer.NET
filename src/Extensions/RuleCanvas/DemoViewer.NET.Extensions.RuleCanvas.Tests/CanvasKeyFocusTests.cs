#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;
using DemoViewer.NET.Extensions.Testing;
using Nodify.Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The canvas keys after a selection made with the pointer: a click on a header and a rubber band both
///     leave the keyboard with the editor, so Cmd or Ctrl+G, Ctrl+D and Delete reach it.
/// </summary>
[NotInParallel]
[Category("Render")]
public class CanvasKeyFocusTests
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

    private static (Window Window, CanvasTabView View, CanvasTabViewModel Vm, NodifyEditor Editor) Show(RulesTree rules)
    {
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasTabView view = new() { DataContext = vm };
        Window window = new() { Width = 1600, Height = 900, Content = view };
        window.Show();
        Pump();
        NodifyEditor editor = view.GetVisualDescendants().OfType<NodifyEditor>().First();
        return (window, view, vm, editor);
    }

    private static Point Header(NodifyEditor editor, Window window, CanvasNodeViewModel node)
    {
        Control container = (Control)editor.ContainerFromItem(node)!;
        return container.TranslatePoint(new Point(container.Bounds.Width / 2, 12), window)!.Value;
    }

    // Where a user typing a gallery search last left the keyboard.
    private static TextBox GallerySearch(CanvasTabView view) =>
        view.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText is string w && w.StartsWith("Search stats", StringComparison.Ordinal));

    private static string Focused(Window window) =>
        TopLevel.GetTopLevel(window)?.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing";

    [Test]
    [Arguments(RawInputModifiers.Meta)]
    [Arguments(RawInputModifiers.Control)]
    public async Task AfterClickingANodeHeader_PrimaryG_SaysWhyOneNodeIsNotGrouped_AndPrimaryD_Duplicates(RawInputModifiers primary)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            (Window window, CanvasTabView view, CanvasTabViewModel vm, NodifyEditor editor) = Show(rules);
            CanvasNodeViewModel kills = vm.Nodes.Single(n => n.Id == "round_kills");
            editor.ViewportZoom = 1;
            editor.ViewportLocation = new Point(kills.Location.X - 100, kills.Location.Y - 100);
            Pump();
            GallerySearch(view).Focus();
            Point at = Header(editor, window, kills);
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Pump();
            await Assert.That(vm.SelectedNodes).Contains(kills);

            window.KeyPressQwerty(PhysicalKey.G, primary);
            Pump();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Groups).IsEmpty();
                await Assert.That(vm.Toast?.Message).IsEqualTo(CanvasTabViewModel.GroupNeedsTwo).Because("the key arrived and one node is refused aloud");
            }

            window.KeyPressQwerty(PhysicalKey.D, primary);
            Pump();
            await Assert.That(vm.Nodes.Any(n => n.Id.EndsWith("_copy", StringComparison.Ordinal))).IsTrue().Because("Cmd or Ctrl+D duplicates");
            window.Close();
        });
    }

    [Test]
    public async Task AfterARubberBand_PrimaryG_Groups()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            (Window window, CanvasTabView view, CanvasTabViewModel vm, NodifyEditor editor) = Show(rules);
            CanvasNodeViewModel kills = vm.Nodes.Single(n => n.Id == "round_kills");
            editor.ViewportZoom = 0.5;
            editor.ViewportLocation = new Point(kills.Location.X - 200, kills.Location.Y - 200);
            Pump();
            GallerySearch(view).Focus();
            Point from = editor.TranslatePoint(new Point(20, 20), window)!.Value;
            Point to = editor.TranslatePoint(new Point(editor.Bounds.Width - 300, editor.Bounds.Height - 200), window)!.Value;
            window.MouseMove(from);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Vector(30, 30));
            window.MouseMove(to);
            window.MouseUp(to, MouseButton.Left);
            Pump();
            await Assert.That(vm.SelectedNodes.Count).IsGreaterThanOrEqualTo(2).Because("the band took the nodes it covered");

            window.KeyPressQwerty(PhysicalKey.G, RawInputModifiers.Meta);
            Pump();
            await Assert.That(vm.Groups.Count).IsEqualTo(1).Because("focus after the band: " + Focused(window));
            window.Close();
        });
    }

    [Test]
    public async Task AfterClickingAWire_DeleteRemovesItsField()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            (Window window, CanvasTabView view, CanvasTabViewModel vm, NodifyEditor editor) = Show(rules);
            CanvasConnectionViewModel gate = vm.Connections.Single(c => c.Source.Owner?.Id == "MatchLive" && c.Target.Owner?.Id == "round_deaths" && !c.IsRead);
            Point mid = new((gate.Source.Anchor.X + gate.Target.Anchor.X) / 2, (gate.Source.Anchor.Y + gate.Target.Anchor.Y) / 2);
            editor.ViewportZoom = 1;
            editor.ViewportLocation = new Point(mid.X - 300, mid.Y - 200);
            Pump();
            mid = new((gate.Source.Anchor.X + gate.Target.Anchor.X) / 2, (gate.Source.Anchor.Y + gate.Target.Anchor.Y) / 2);
            Point at = editor.TranslatePoint(new Point(mid.X - editor.ViewportLocation.X, mid.Y - editor.ViewportLocation.Y), window)!.Value;
            GallerySearch(view).Focus();
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Pump();
            await Assert.That(vm.SelectedWires.Count).IsEqualTo(1);

            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Pump();
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).DoesNotContain("while: match.live").Because("focus after the click: " + Focused(window));
            window.Close();
        });
    }

    [Test]
    public async Task WithFocusOutsideTheCanvas_AClickOnANodeHandsTheKeysToTheEditor()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            CanvasTabViewModel vm = rules.NewViewModel();
            vm.OnActivated(null!);
            CanvasTabView view = new() { DataContext = vm };
            TextBox outside = new() { Width = 200 };
            DockPanel host = new();
            DockPanel.SetDock(outside, Dock.Top);
            host.Children.Add(outside);
            host.Children.Add(view);
            Window window = new() { Width = 1600, Height = 900, Content = host };
            window.Show();
            Pump();
            NodifyEditor editor = view.GetVisualDescendants().OfType<NodifyEditor>().First();
            CanvasNodeViewModel kills = vm.Nodes.Single(n => n.Id == "round_kills");
            editor.ViewportZoom = 1;
            editor.ViewportLocation = new Point(kills.Location.X - 100, kills.Location.Y - 100);
            Pump();
            outside.Focus();
            Point at = Header(editor, window, kills);
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Pump();
            using (Assert.Multiple())
            {
                await Assert.That(vm.SelectedNodes).Contains(kills);
                await Assert.That(editor.IsKeyboardFocusWithin).IsTrue().Because("focus after the click: " + Focused(window));
            }

            window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Meta);
            Pump();
            await Assert.That(vm.Nodes.Any(n => n.Id == "round_kills_copy")).IsTrue();
            window.Close();
        });
    }
}
