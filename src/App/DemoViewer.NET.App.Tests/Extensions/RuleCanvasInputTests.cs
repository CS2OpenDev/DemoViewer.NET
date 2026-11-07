#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.RuleCanvas;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The editors on a node face under real pointer input: the node container must not take the press away
///     from a picker or a checkbox inside it.
/// </summary>
[NotInParallel]
[Category("Render")]
public class RuleCanvasInputTests
{
    [Test]
    public async Task ClickingTheAddFacetPicker_OpensIt_AndAPickWrites() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using EditableCorpus corpus = new();
            (MainViewModel vm, ServiceProvider services) = corpus.NewShell();
            Window window = new() { Width = 1400, Height = 900, Content = new MainView { DataContext = vm } };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.TrySelectTab(RuleCanvasPack.HubId);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                ComboBox picker = window.GetVisualDescendants().OfType<ComboBox>()
                    .First(c => c.PlaceholderText == "add facet…" && c.IsEffectivelyVisible
                                && c.DataContext is CanvasNodeViewModel { Id: "round_kills" });
                CanvasNodeViewModel owner = (CanvasNodeViewModel)picker.DataContext!;
                Reveal(EditorOf(window), new Rect(owner.Location, owner.EstimatedSize));
                Point at = Center(picker, window);
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                await Assert.That(picker.IsDropDownOpen).IsTrue().Because("the click reached the picker rather than the node container");

                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                ComboBoxItem headshot = (ComboBoxItem?)picker.ContainerFromItem("headshot")
                    ?? throw new InvalidOperationException("the open dropdown has no item for headshot");
                Point item = Center(headshot, window);
                window.MouseDown(item, MouseButton.Left);
                window.MouseUp(item, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                using (Assert.Multiple())
                {
                    await Assert.That(File.ReadAllText(corpus.UserFile)).Contains("headshot: true")
                        .Because("the picked facet was written with its type's initial value");
                    await Assert.That(picker.SelectedItem).IsNull().Because("the picker clears for the next pick");
                }
            }
            finally
            {
                window.Close();
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    [Test]
    public async Task ClickingAFacetCheckbox_WritesTheFile() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using EditableCorpus corpus = new();
            (MainViewModel vm, ServiceProvider services) = corpus.NewShell();
            Window window = new() { Width = 1400, Height = 900, Content = new MainView { DataContext = vm } };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.TrySelectTab(RuleCanvasPack.HubId);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                CheckBox box = window.GetVisualDescendants().OfType<CheckBox>()
                    .First(c => c.DataContext is CanvasFacetViewModel { Name: "enemy" } && c.IsEffectivelyVisible);
                Nodify.Avalonia.NodifyEditor editor = EditorOf(window);
                CanvasNodeViewModel owner = ((CanvasTabViewModel)editor.DataContext!).Nodes.First(n => n.Facets.Contains(box.DataContext));
                Reveal(editor, new Rect(owner.Location, owner.EstimatedSize));
                Point at = Center(box, window);
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                await Assert.That(File.ReadAllText(corpus.UserFile)).Contains("enemy: false")
                    .Because("the toggle wrote through to the user's file");
            }
            finally
            {
                window.Close();
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    // The pencil only reveals the label editor; the editor must take focus itself for the gesture to be one.
    [Test]
    public async Task ClickingTheLabelPencil_FocusesTheEditor_SoTypingLandsInIt() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using EditableCorpus corpus = new();
            (MainViewModel vm, ServiceProvider services) = corpus.NewShell();
            Window window = new() { Width = 1400, Height = 900, Content = new MainView { DataContext = vm } };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.TrySelectTab(RuleCanvasPack.HubId);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                Button pencil = window.GetVisualDescendants().OfType<Button>()
                    .First(b => b.Content as string == "✎" && b.IsEffectivelyVisible && b.DataContext is CanvasNodeViewModel { Id: "round_kills" });
                CanvasNodeViewModel owner = (CanvasNodeViewModel)pencil.DataContext!;
                Reveal(EditorOf(window), new Rect(owner.Location, owner.EstimatedSize));
                Point at = Center(pencil, window);
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Pump();

                TextBox editor = window.GetVisualDescendants().OfType<TextBox>()
                    .First(t => ReferenceEquals(t.DataContext, owner) && t.IsVisible && !t.GetVisualAncestors().OfType<ComboBox>().Any());
                await Assert.That(editor.IsFocused).IsTrue().Because("the pencil is one gesture: the editor it reveals takes focus");

                window.KeyTextInput("opening");
                Dispatcher.UIThread.RunJobs();
                await Assert.That(editor.Text).Contains("opening").Because("the keystrokes went to the editor, not to the pencil");
            }
            finally
            {
                window.Close();
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    [Test]
    public async Task RightClickOnTheCanvas_OpensItsMenu_WithAddStatOnTheViews() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using EditableCorpus corpus = new();
            (MainViewModel vm, ServiceProvider services) = corpus.NewShell();
            Window window = new() { Width = 1400, Height = 900, Content = new MainView { DataContext = vm } };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.TrySelectTab(RuleCanvasPack.HubId);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                Nodify.Avalonia.NodifyEditor editor = window.GetVisualDescendants().OfType<Nodify.Avalonia.NodifyEditor>().First();
                Point at = EmptyCanvasPoint(editor, window);
                window.MouseDown(at, MouseButton.Right);
                window.MouseUp(at, MouseButton.Right);
                Dispatcher.UIThread.RunJobs();

                MenuItem addStat = editor.ContextMenu!.Items.OfType<MenuItem>().First(m => m.Name == "AddStatMenu");
                using (Assert.Multiple())
                {
                    await Assert.That(editor.ContextMenu.IsOpen).IsTrue().Because("a right click that did not pan opens the canvas menu");
                    await Assert.That(addStat.ItemsSource!.OfType<MenuItem>().Select(m => m.Header as string)).Contains("kill");
                }
            }
            finally
            {
                window.Close();
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    // The selection is made by the click, through SelectedConnections, so the binding and Nodify's wire
    // selection are what this proves; the Delete routing is the smaller half.
    [Test]
    public async Task ClickingAWireSelectsIt_AndDeleteRemovesTheWireNotTheStat() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using EditableCorpus corpus = new();
            (MainViewModel vm, ServiceProvider services) = corpus.NewShell();
            Window window = new() { Width = 1400, Height = 900, Content = new MainView { DataContext = vm } };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.TrySelectTab(RuleCanvasPack.HubId);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                Nodify.Avalonia.NodifyEditor editor = EditorOf(window);
                CanvasTabViewModel canvas = (CanvasTabViewModel)editor.DataContext!;
                CanvasConnectionViewModel gate = canvas.Connections.Single(c =>
                    c.Source.Owner?.Id == "MatchLive" && c.Target.Owner?.Id == "round_deaths" && !c.IsRead);
                Point middle = Midpoint(gate);
                Reveal(editor, new Rect(middle - new Vector(120, 120), new Size(240, 240)));
                Point at = OnWire(editor, window, gate);
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                await Assert.That(canvas.SelectedWires.Count).IsEqualTo(1).Because("the click selected the wire through SelectedConnections");
                await Assert.That(ReferenceEquals(canvas.SelectedWires[0], gate)).IsTrue();

                if (!editor.IsKeyboardFocusWithin)
                {
                    editor.Focus();
                }

                window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();

                string text = File.ReadAllText(corpus.UserFile);
                using (Assert.Multiple())
                {
                    await Assert.That(RulesetEdits.Field(text, "round_deaths", "while")).IsNull().Because("Delete with only a wire selected took the gate out");
                    await Assert.That(RulesetEdits.StatIds(text)).Contains("round_deaths").Because("the wire went, not the stat");
                }
            }
            finally
            {
                window.Close();
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    [Test]
    public async Task AnAltShiftDragAcrossAGateWire_CutsIt() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using EditableCorpus corpus = new();
            (MainViewModel vm, ServiceProvider services) = corpus.NewShell();
            Window window = new() { Width = 1400, Height = 900, Content = new MainView { DataContext = vm } };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.TrySelectTab(RuleCanvasPack.HubId);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                Nodify.Avalonia.NodifyEditor editor = window.GetVisualDescendants().OfType<Nodify.Avalonia.NodifyEditor>().First();
                CanvasTabViewModel canvas = (CanvasTabViewModel)editor.DataContext!;
                CanvasConnectionViewModel gate = canvas.Connections.Single(c =>
                    c.Source.Owner?.Id == "MatchLive" && c.Target.Owner?.Id == "round_deaths" && !c.IsRead);
                Point middle = Midpoint(gate);
                Reveal(editor, new Rect(middle - new Vector(120, 120), new Size(240, 240)));
                middle = Midpoint(gate);
                Point from = InWindow(editor, window, middle + new Vector(0, -30));
                Point to = InWindow(editor, window, middle + new Vector(0, 30));

                const RawInputModifiers cut = RawInputModifiers.Alt | RawInputModifiers.Shift;
                window.MouseDown(from, MouseButton.Left, cut);
                window.MouseMove(to, cut | RawInputModifiers.LeftMouseButton);
                window.MouseUp(to, MouseButton.Left, cut);
                Dispatcher.UIThread.RunJobs();

                string text = File.ReadAllText(corpus.UserFile);
                using (Assert.Multiple())
                {
                    await Assert.That(RulesetEdits.Field(text, "round_deaths", "while")).IsNull()
                        .Because("the cutting line crossed the gate wire, which is registered as a cuttable type");
                    await Assert.That(RulesetEdits.StatIds(text)).Contains("round_deaths").Because("the wire went, not the stat");
                }
            }
            finally
            {
                window.Close();
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // The canvas opens at a readable zoom anchored to the authored column, so a target can sit outside the
    // window; the editor is asked to frame it before the pointer goes there.
    private static void Reveal(Nodify.Avalonia.NodifyEditor editor, Rect graphRect)
    {
        // Set directly: the editor's own BringIntoView animates, and the headless clock does not run it.
        editor.ViewportZoom = 1;
        editor.ViewportLocation = new Point(graphRect.X - 60, graphRect.Y - 60);
        Pump();
    }

    private static Nodify.Avalonia.NodifyEditor EditorOf(Window window) =>
        window.GetVisualDescendants().OfType<Nodify.Avalonia.NodifyEditor>().First();

    // Read after Reveal: the anchors are written by the connectors as the containers arrange.
    private static Point Midpoint(CanvasConnectionViewModel wire) =>
        new((wire.Source.Anchor.X + wire.Target.Anchor.X) / 2, (wire.Source.Anchor.Y + wire.Target.Anchor.Y) / 2);

    // Graph space to window space: the editor scales and offsets its items host by the viewport.
    private static Point InWindow(Nodify.Avalonia.NodifyEditor editor, Window window, Point graph) =>
        editor.TranslatePoint(
            new Point((graph.X - editor.ViewportLocation.X) * editor.ViewportZoom, (graph.Y - editor.ViewportLocation.Y) * editor.ViewportZoom),
            window) ?? throw new InvalidOperationException("editor is not in the window");

    // A window point the wire's own stroke takes: the curve passes through the anchors' midpoint, and the
    // stroke is two pixels wide, so the scan around it absorbs the arrow offsets and rounding.
    private static Point OnWire(Nodify.Avalonia.NodifyEditor editor, Window window, CanvasConnectionViewModel wire)
    {
        Point middle = InWindow(editor, window, Midpoint(wire));
        foreach (double dy in new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4 })
        {
            Point at = middle + new Vector(0, dy);
            if (window.InputHitTest(at) is Visual hit
                && hit.GetSelfAndVisualAncestors().OfType<LabelledConnection>().Any(c => ReferenceEquals(c.DataContext, wire)))
            {
                return at;
            }
        }

        throw new InvalidOperationException("the wire is not under its midpoint");
    }

    // A point over the canvas that no node container covers, scanned over a grid of the editor's bounds.
    private static Point EmptyCanvasPoint(Nodify.Avalonia.NodifyEditor editor, Window window)
    {
        for (double y = 20; y < editor.Bounds.Height - 180; y += 40)
        {
            for (double x = 20; x < editor.Bounds.Width - 240; x += 40)
            {
                Point at = editor.TranslatePoint(new Point(x, y), window) ?? throw new InvalidOperationException("editor is not in the window");
                if (window.InputHitTest(at) is Visual hit && hit.GetSelfAndVisualAncestors().Contains(editor)
                    && !hit.GetSelfAndVisualAncestors().OfType<Nodify.Avalonia.ItemContainer>().Any())
                {
                    return at;
                }
            }
        }

        throw new InvalidOperationException("no empty canvas in view");
    }

    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException("control is not in the window");

    private sealed class EverythingOn : IFeatureGate
    {
        public UserCategory Category => UserCategory.Developer;

        public int HiddenCount => 0;

        public bool IsEnabled(string featureId) => true;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    /// <summary>player_round copied into a throwaway user directory, so the canvas opens it editable.</summary>
    private sealed class EditableCorpus : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rulecanvas-input-" + Guid.NewGuid().ToString("N"));

        public string UserFile => Path.Combine(_root, "user", "player_round.rules.yaml");

        public (MainViewModel Shell, ServiceProvider Services) NewShell()
        {
            string user = Path.Combine(_root, "user");
            Directory.CreateDirectory(user);
            string shipped = CS2DemoKit.Analysis.Yaml.RuleSetLocator.ResolveShippedRulesDirectory();
            File.Copy(Path.Combine(shipped, "player_round.rules.yaml"), UserFile);

            RuleCanvasPack pack = new();
            ServiceCollection services = new();
            pack.Register(services);
            services.AddSingleton(new CanvasTabViewModel(_ => true,
                () => RulesetCorpus.Load([(shipped, "shipped"), (user, "user")]), () => Path.Combine(_root, "own")));
            ServiceProvider provider = services.BuildServiceProvider();
            PackContributionSet set = new([pack], provider);
            ModuleRegistry registry = new();
            foreach (IWorkspaceModule module in set.Packs.SelectMany(p => p.Modules))
            {
                registry.Register(module);
            }

            MainViewModel vm = new(null, registry, TestLibraries.Empty(), null, new EverythingOn(), null, null, hubTabs: set.HubTabs);
            vm.RestoreSession();
            return (vm, provider);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                // A temp tree that outlives the test is tolerable.
            }
        }
    }
}
