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

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The wire editor's gestures through the headless window: a double-click on a wire's label opens the
///     editor over it, Escape closes it with nothing written, a press on the canvas commits and goes no
///     further. The view is shown in a window of its own; the host's brushes resolve to nothing here.
/// </summary>
[NotInParallel]
[Category("Render")]
public class CanvasWireGestureTests
{
    private static (Window Window, CanvasTabView View, CanvasTabViewModel Vm) Show(RulesTree rules)
    {
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasTabView view = new() { DataContext = vm };
        Window window = new() { Width = 1600, Height = 1000, Content = view };
        window.Show();
        Pump();
        return (window, view, vm);
    }

    private static void Pump(int frames = 3)
    {
        for (int i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static LabelledConnection ConnectionOf(CanvasTabView view, CanvasConnectionViewModel wire) =>
        view.GetVisualDescendants().OfType<LabelledConnection>().FirstOrDefault(c => ReferenceEquals(c.DataContext, wire))
        ?? throw new InvalidOperationException("the wire's connection is not in the view");

    private static Point LabelOf(LabelledConnection connection, Window window) =>
        connection.TranslatePoint(connection.LabelAnchor, window) ?? throw new InvalidOperationException("the connection is not in the window");

    private static CanvasConnectionViewModel LogicWire(CanvasTabViewModel vm) =>
        vm.Connections.Single(c => c.Source.Owner?.Id == "round_kast_assists" && c.Target.Owner?.Id == "round_kast");

    // The editor seeds the drag origin from the first press; the label is moved to before anything is pressed.
    private static void DoubleClick(Window window, Point at)
    {
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Pump();
    }

    [Test]
    public async Task ADoubleClickOnAWireLabel_OpensTheEditorOverIt_AndEscapeClosesItWithoutAWrite()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            (Window window, CanvasTabView view, CanvasTabViewModel vm) = Show(rules);
            string before = File.ReadAllText(rules.UserPath("player_round"));
            CanvasConnectionViewModel wire = LogicWire(vm);
            LabelledConnection connection = ConnectionOf(view, wire);
            await Assert.That(connection.LabelBounds.HasValue).IsTrue().Because("the label was drawn, so the editor has a box to sit on");
            Point label = LabelOf(connection, window);

            DoubleClick(window, label);
            ContentControl host = view.FindControl<ContentControl>("WireEditorHost") ?? throw new InvalidOperationException("no host");
            using (Assert.Multiple())
            {
                await Assert.That(vm.WireEditor).IsNotNull().Because("the double-click opened the editor");
                await Assert.That(ReferenceEquals(vm.WireEditor!.Wire, wire)).IsTrue();
                await Assert.That(vm.WireEditor.IsRow).IsTrue();
                await Assert.That(host.IsEffectivelyVisible).IsTrue();
                Rect placed = new(Canvas.GetLeft(host), Canvas.GetTop(host), host.Bounds.Width, host.Bounds.Height);
                Point inLayer = connection.TranslatePoint(connection.LabelAnchor, host.GetVisualParent()!) ?? default;
                await Assert.That(placed.Contains(inLayer)).IsTrue().Because($"the editor {placed} sits over the label at {inLayer}");
                await Assert.That(vm.SelectedNodes).IsEmpty().Because("nothing was selected by the clicks");
            }

            TextBox? focused = TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() as TextBox;
            await Assert.That(focused?.Name).IsEqualTo("WireLiteral").Because("the literal box takes focus, so typing lands in it");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();
            using (Assert.Multiple())
            {
                await Assert.That(vm.WireEditor).IsNull().Because("Escape closes the editor");
                await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before).Because("and writes nothing");
                await Assert.That(vm.CanUndo).IsFalse();
            }

            window.Close();
        });
    }

    [Test]
    public async Task APressOnTheCanvasWhileTheEditorIsOpen_CommitsIt_AndNeitherPansNorSelects()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            (Window window, CanvasTabView view, CanvasTabViewModel vm) = Show(rules);
            string before = File.ReadAllText(rules.UserPath("player_round"));
            NodifyEditorHandle editor = new(view);
            Point viewport = editor.Location;

            vm.BeginEditWire(LogicWire(vm));
            Pump();
            vm.WireEditor!.Row!.Literal = "3";
            Point bare = editor.BarePoint(window);
            window.MouseMove(bare);
            window.MouseDown(bare, MouseButton.Left);
            window.MouseMove(new Point(bare.X + 40, bare.Y + 40));
            window.MouseMove(new Point(bare.X + 80, bare.Y + 80));
            window.MouseUp(new Point(bare.X + 80, bare.Y + 80), MouseButton.Left);
            Pump();

            string after = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(vm.WireEditor).IsNull().Because("the press committed and closed the editor");
                await Assert.That(after).IsEqualTo(before.Replace("round_kast_assists > 0 or", "round_kast_assists > 3 or", StringComparison.Ordinal));
                await Assert.That(editor.Location).IsEqualTo(viewport).Because("the press was swallowed, so the drag did not pan");
                await Assert.That(vm.SelectedNodes).IsEmpty();
            }

            window.Close();
        });
    }

    // The Nodify editor inside the view: its viewport and a point on bare canvas, away from every node.
    private sealed class NodifyEditorHandle(CanvasTabView view)
    {
        private readonly Nodify.Avalonia.NodifyEditor _editor = view.GetVisualDescendants().OfType<Nodify.Avalonia.NodifyEditor>().First();

        public Point Location => _editor.ViewportLocation;

        public Point BarePoint(Window window)
        {
            // The bottom-right of the editor less the overlays' corners: the zoom cluster is bottom-left, the
            // minimap bottom-right and hidden by default; the chain is placed from the top-left.
            Point local = new(_editor.Bounds.Width - 40, 60);
            return _editor.TranslatePoint(local, window) ?? throw new InvalidOperationException("the editor is not in the window");
        }
    }
}
