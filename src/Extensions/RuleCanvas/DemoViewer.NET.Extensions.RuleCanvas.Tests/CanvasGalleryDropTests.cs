#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;
using DemoViewer.NET.Extensions.Testing;
using Nodify.Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Gallery drops through the view's own drop handler and the window's hit test: on a node, on a wire,
///     on bare canvas. The platform drag loop is skipped; the drop events are raised where it would raise them.
/// </summary>
[NotInParallel]
[Category("Render")]
public class CanvasGalleryDropTests
{
    private sealed record Canvas(Window Window, CanvasTabView View, CanvasTabViewModel Vm, NodifyEditor Editor);

    private static Canvas Show(CanvasTabViewModel vm)
    {
        CanvasTabView view = new() { DataContext = vm };
        Window window = new() { Width = 1600, Height = 900, Content = view };
        window.Show();
        Pump();
        return new Canvas(window, view, vm, view.GetVisualDescendants().OfType<NodifyEditor>().First());
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

    private static GalleryItem Item(CanvasTabViewModel vm, string group, string name) =>
        vm.Gallery.Single(g => g.Title == group).Items.First(i => i.Name == name);

    private static GalleryItem DefineItem(CanvasTabViewModel vm) =>
        vm.Gallery.Single(g => g.Title == "Stats").Items.Single(i => i.Kind == GalleryItemKind.Define);

    private static void Drop(Canvas c, GalleryItem item, Point at)
    {
        c.View.BeginGalleryDrag(item);
        DataTransfer payload = new();
        payload.Add(DataTransferItem.CreateText(item.Name));
        c.Window.DragDrop(at, RawDragEventType.DragEnter, payload, DragDropEffects.Copy);
        c.Window.DragDrop(at, RawDragEventType.DragOver, payload, DragDropEffects.Copy);
        c.Window.DragDrop(at, RawDragEventType.Drop, payload, DragDropEffects.Copy);
        c.View.EndGalleryDrag();
        Pump();
    }

    private static void Reveal(Canvas c, Point graph)
    {
        c.Editor.ViewportZoom = 1;
        c.Editor.ViewportLocation = new Point(graph.X - 200, graph.Y - 200);
        Pump();
    }

    private static Point InWindow(Canvas c, Point graph) =>
        c.Editor.TranslatePoint(
            new Point((graph.X - c.Editor.ViewportLocation.X) * c.Editor.ViewportZoom, (graph.Y - c.Editor.ViewportLocation.Y) * c.Editor.ViewportZoom),
            c.Window) ?? throw new InvalidOperationException("the editor is not in the window");

    private static Point OnHeader(Canvas c, CanvasNodeViewModel node)
    {
        Reveal(c, node.Location);
        Control container = (Control)c.Editor.ContainerFromItem(node)!;
        return container.TranslatePoint(new Point(container.Bounds.Width / 2, 12), c.Window)!.Value;
    }

    // A window point no node covers whose hit test lands on the wire's stroke, scanned out from the midpoint.
    private static Point OnWire(Canvas c, CanvasConnectionViewModel wire)
    {
        Point mid = new((wire.Source.Anchor.X + wire.Target.Anchor.X) / 2, (wire.Source.Anchor.Y + wire.Target.Anchor.Y) / 2);
        Reveal(c, mid);
        mid = new((wire.Source.Anchor.X + wire.Target.Anchor.X) / 2, (wire.Source.Anchor.Y + wire.Target.Anchor.Y) / 2);
        Point middle = InWindow(c, mid);
        foreach (double dx in new[] { 0, 10, -10, 20, -20, 35, -35, 50, -50, 70, -70 })
        {
            foreach (double dy in Enumerable.Range(0, 61).Select(i => (i % 2 == 0 ? 1 : -1) * ((i + 1) / 2)))
            {
                Point at = middle + new Vector(dx, dy);
                if (c.Window.InputHitTest(at) is Visual hit
                    && hit.GetSelfAndVisualAncestors().OfType<LabelledConnection>().Any(l => ReferenceEquals(l.DataContext, wire)))
                {
                    return at;
                }
            }
        }

        throw new InvalidOperationException("the wire is not under its midpoint or near it");
    }

    // A few pixels off the stroke, away from the label: the pointer of a hand drop is rarely on a 2 px line.
    private static Point NearWire(Canvas c, CanvasConnectionViewModel wire)
    {
        OnWire(c, wire);
        Point from = InWindow(c, wire.Source.Anchor), to = InWindow(c, wire.Target.Anchor);
        foreach (double t in new[] { 0.3, 0.7, 0.25, 0.75, 0.35, 0.65, 0.2, 0.8 })
        {
            Point line = from + (to - from) * t;
            for (int dy = -60; dy <= 60; dy++)
            {
                Point at = line + new Vector(0, dy);
                if (!OnStroke(c, wire, at))
                {
                    continue;
                }

                // Walk off the stroke, then three pixels more.
                for (int off = 1; off < 20; off++)
                {
                    Point beside = at + new Vector(0, off);
                    if (!OnStroke(c, wire, beside))
                    {
                        Point drop = beside + new Vector(0, 3);
                        if (c.Window.InputHitTest(drop) is Visual hit && hit.GetSelfAndVisualAncestors().Contains(c.Editor)
                            && !hit.GetSelfAndVisualAncestors().OfType<LabelledConnection>().Any()
                            && !hit.GetSelfAndVisualAncestors().OfType<ItemContainer>().Any() && OnlyWireNear(c, wire, drop))
                        {
                            return drop;
                        }

                        break;
                    }
                }
            }
        }

        throw new InvalidOperationException("no bare point beside the wire");
    }

    // No other wire within reach, so the drop can only mean this one.
    private static bool OnlyWireNear(Canvas c, CanvasConnectionViewModel wire, Point at)
    {
        for (int dx = -12; dx <= 12; dx += 2)
        {
            for (int dy = -12; dy <= 12; dy += 2)
            {
                if (c.Window.InputHitTest(at + new Vector(dx, dy)) is Visual v
                    && v.GetSelfAndVisualAncestors().OfType<LabelledConnection>().FirstOrDefault() is { } other && !ReferenceEquals(other.DataContext, wire))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool OnStroke(Canvas c, CanvasConnectionViewModel wire, Point at) =>
        c.Window.InputHitTest(at) is Visual hit
        && hit.GetSelfAndVisualAncestors().OfType<LabelledConnection>().FirstOrDefault(l => ReferenceEquals(l.DataContext, wire)) is { } connection
        && !(connection.LabelBounds is { } label && c.Window.TranslatePoint(at, connection) is { } local && label.Inflate(4).Contains(local));

    private static Point EmptyCanvas(Canvas c)
    {
        for (double y = 60; y < c.Editor.Bounds.Height - 200; y += 40)
        {
            for (double x = 60; x < c.Editor.Bounds.Width - 260; x += 40)
            {
                Point at = c.Editor.TranslatePoint(new Point(x, y), c.Window)!.Value;
                if (c.Window.InputHitTest(at) is Visual hit && hit.GetSelfAndVisualAncestors().Contains(c.Editor)
                    && !hit.GetSelfAndVisualAncestors().OfType<ItemContainer>().Any()
                    && !hit.GetSelfAndVisualAncestors().OfType<LabelledConnection>().Any())
                {
                    bool nearWire = false;
                    for (int d = -6; d <= 6 && !nearWire; d += 2)
                    {
                        nearWire = c.Window.InputHitTest(at + new Vector(d, d)) is Visual v && v.GetSelfAndVisualAncestors().OfType<LabelledConnection>().Any();
                    }

                    if (!nearWire)
                    {
                        return at;
                    }
                }
            }
        }

        throw new InvalidOperationException("no empty canvas in view");
    }

    private static CanvasConnectionViewModel TriggerWireInto(CanvasTabViewModel vm, string stat) =>
        vm.Connections.First(w => w.Target.Owner?.Id == stat && CanvasEditSemantics.IsTriggerWire(w));

    [Test]
    public async Task MatchLiveDroppedOnAStatOfANewRuleset_KeepsTheStatDrawnAndOnScreen_WithTheGateInFront()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.New();
            CanvasTabViewModel vm = rules.NewViewModel();
            vm.OnActivated(null!);
            vm.CreateNewRuleset(false);
            vm.PlaceItem(Item(vm, "Prefabs", vm.Gallery.Single(g => g.Title == "Prefabs").Items[0].Name), new Point(300, 200));
            Canvas c = Show(vm);
            CanvasNodeViewModel stat = vm.Nodes.Single(n => n.Id == "enemy_kills");

            Drop(c, Item(vm, "Gates", "match.live"), OnHeader(c, stat));

            CanvasNodeViewModel? after = vm.Nodes.SingleOrDefault(n => n.Id == "enemy_kills");
            Control? container = after is null ? null : c.Editor.ContainerFromItem(after) as Control;
            Rect viewport = new(c.Editor.ViewportLocation, new Size(c.Editor.Bounds.Width / c.Editor.ViewportZoom, c.Editor.Bounds.Height / c.Editor.ViewportZoom));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("my_rules")), "enemy_kills", "while")).IsEqualTo("match.live");
                await Assert.That(after).IsNotNull().Because("the gated stat is still a node");
                await Assert.That(container?.IsEffectivelyVisible).IsTrue().Because("and still has a visible container");
                await Assert.That(viewport.Intersects(new Rect(after!.Location, container!.Bounds.Size))).IsTrue().Because("and is still in the viewport");
                await Assert.That(vm.Connections.Any(w => w.IsGate && w.Source.Owner?.Id == "MatchLive" && w.Target.Owner?.Id == "enemy_kills")).IsTrue()
                    .Because("the MatchLive gate stands in front of it");
            }

            c.Window.Close();
        });
    }

    [Test]
    public async Task ADefineDroppedOnEmptyCanvas_AddsADefine()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            CanvasTabViewModel vm = rules.NewViewModel();
            vm.OnActivated(null!);
            Canvas c = Show(vm);

            Drop(c, DefineItem(vm), EmptyCanvas(c));

            await Assert.That(RulesetEdits.DefineNames(File.ReadAllText(rules.UserPath("player_round")))).Contains("new_define").Because(vm.EditStatus);
            c.Window.Close();
        });
    }

    [Test]
    public async Task ADefineDroppedOnAStat_PutsADefineInFrontOfIt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            CanvasTabViewModel vm = rules.NewViewModel();
            vm.OnActivated(null!);
            Canvas c = Show(vm);

            Drop(c, DefineItem(vm), OnHeader(c, vm.Nodes.Single(n => n.Id == "round_kills")));

            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.DefineNames(text)).Contains("new_define").Because(vm.EditStatus);
                await Assert.That(RulesetEdits.Field(text, "round_kills", "count")).IsEqualTo("new_define");
            }

            c.Window.Close();
        });
    }

    [Test]
    public async Task ADefineDroppedBesideAnEventWire_PutsADefineInFrontOfItsTarget()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            CanvasTabViewModel vm = rules.NewViewModel();
            vm.OnActivated(null!);
            Canvas c = Show(vm);

            Drop(c, DefineItem(vm), NearWire(c, TriggerWireInto(vm, "round_kills")));

            string text = File.ReadAllText(rules.UserPath("player_round"));
            await Assert.That(RulesetEdits.Field(text, "round_kills", "count")).IsEqualTo("new_define").Because(vm.EditStatus);
            c.Window.Close();
        });
    }

    [Test]
    public async Task AGateDroppedBesideAWire_GatesItsTarget()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            CanvasTabViewModel vm = rules.NewViewModel();
            vm.OnActivated(null!);
            Canvas c = Show(vm);

            Drop(c, Item(vm, "Gates", "round.active"), NearWire(c, TriggerWireInto(vm, "round_kills")));

            await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("player_round")), "round_kills", "while")).IsEqualTo("round.active")
                .Because(vm.EditStatus);
            c.Window.Close();
        });
    }

    [Test]
    public async Task AViewDroppedBesideATriggerWire_RetargetsItsTarget()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            CanvasTabViewModel vm = rules.NewViewModel();
            vm.OnActivated(null!);
            Canvas c = Show(vm);

            Drop(c, Item(vm, "Views", "death"), NearWire(c, TriggerWireInto(vm, "round_kills")));

            await Assert.That(RulesetEdits.Field(File.ReadAllText(rules.UserPath("player_round")), "round_kills", "count")).IsEqualTo("death")
                .Because(vm.EditStatus);
            c.Window.Close();
        });
    }
}
