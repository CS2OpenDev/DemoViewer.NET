#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
///     Every written row of a where: or when: has its own input, and a wire for a row lands on that row's
///     ring rather than on one anchor for the whole line. A facet's read, which no row names, keeps the line's.
/// </summary>
public class CanvasRowPortTests
{
    private const string Lab = """
        ruleset: rows
        title: Rows
        for: each_player

        stats:
          kills:
            count: kill
            per: round
          deaths:
            count: death
            per: round
          picky_kills:
            count: kill
            match: { enemy: true }
            where: "enrich.kill.was_trade_kill == true and enrich.kill.spray_kills > 1"
            per: round
          busy:
            flag:
              when: "kills > 0 or deaths > 0 or player.survived"
            per: round
        """;

    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.New();
        rules.User("rows", Lab);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "rows";
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id && !n.IsScaffold);

    private static CanvasConnectionViewModel Wire(CanvasTabViewModel vm, string from, string to, Func<CanvasConnectionViewModel, bool> kind) =>
        vm.Connections.Single(c => c.Source.Owner?.Id == from && c.Target.Owner?.Id == to && kind(c));

    [Test]
    public async Task EachWhereRow_HasItsOwnPort_AndTheWireForItsPathLandsThere()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            await Assert.That(vm.Diagnostics).IsEmpty();
            CanvasNodeViewModel picky = Node(vm, "picky_kills");
            CanvasExpressionViewModel where = picky.Lines.Single(l => l.Key == "where");
            using (Assert.Multiple())
            {
                await Assert.That(where.Rows.Select(r => r.Port?.Title ?? "")).IsEquivalentTo(["enrich.kill.was_trade_kill", "enrich.kill.spray_kills"]);
                await Assert.That(where.Rows.All(r => r.Port is { Kind: PortKind.Read, IsRow: true })).IsTrue();
                await Assert.That(Wire(vm, "enrich.kill.was_trade_kill", "picky_kills", c => c.IsRead).Target).IsSameReferenceAs(where.Rows[0].Port);
                await Assert.That(Wire(vm, "enrich.kill.spray_kills", "picky_kills", c => c.IsRead).Target).IsSameReferenceAs(where.Rows[1].Port);
                await Assert.That(where.Port).IsSameReferenceAs(picky.ReadPort).Because("the line keeps its ring for the reads no row names");
                await Assert.That(vm.Connections.Where(c => c.Target.Owner == picky && c.IsRead && c.Target == picky.ReadPort).Select(c => c.Source.Owner?.Id))
                    .Contains("enrich.kill.was_enemy_kill").Because("the enemy facet's read lands on the line's ring");
            }
        }
    }

    [Test]
    public async Task EachWhenRow_HasItsOwnPort_AndEachLogicWireLandsOnItsRow()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel busy = Node(vm, "busy");
            CanvasExpressionViewModel when = busy.Lines.Single(l => l.Key == "when");
            using (Assert.Multiple())
            {
                await Assert.That(when.Rows.Select(r => r.Port?.Title ?? "")).IsEquivalentTo(["kills", "deaths", "player.survived"]);
                await Assert.That(Wire(vm, "kills", "busy", c => c.IsOr).Target).IsSameReferenceAs(when.Rows[0].Port);
                await Assert.That(Wire(vm, "deaths", "busy", c => c.IsOr).Target).IsSameReferenceAs(when.Rows[1].Port);
                await Assert.That(Wire(vm, "Survived", "busy", c => c.IsOr).Target).IsSameReferenceAs(when.Rows[2].Port);
            }
        }
    }

    [Test]
    public async Task AFoldedFace_StacksOnlyTheRowPortsAWireLandsOn()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel picky = Node(vm, "picky_kills");
            picky.IsCollapsed = true;
            await Assert.That(picky.RailInputs.Count(i => i.IsRow)).IsEqualTo(2);
            await Assert.That(picky.RailInputs.Where(i => i.IsRow).All(i => i.IsWired)).IsTrue();
        }
    }

    [Test]
    [Category("Render")]
    [NotInParallel]
    public async Task OnTheFace_PerAndWhileSitOnRowsOfTheirOwn_AndEachConditionRowHasARingInsideTheFace()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (RulesTree rules, CanvasTabViewModel vm) = Open();
            using (rules)
            {
                CanvasTabView view = new() { DataContext = vm };
                Window window = new() { Width = 1600, Height = 900, Content = view };
                window.Show();
                NodifyEditor editor = view.GetVisualDescendants().OfType<NodifyEditor>().First();
                CanvasNodeViewModel picky = Node(vm, "picky_kills");
                editor.ViewportZoom = 1;
                editor.ViewportLocation = new Point(picky.Location.X - 100, picky.Location.Y - 100);
                for (int i = 0; i < 4; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }

                Control face = (Control)editor.ContainerFromItem(picky)!;
                Rect faceBox = new(face.Bounds.Size);
                Rect RingOf(CanvasConnectorViewModel port)
                {
                    Border halo = face.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PortHalo" && ReferenceEquals(b.DataContext, port));
                    Control ring = (Control)halo.GetVisualParent()!;
                    return new Rect(ring.TranslatePoint(default, face)!.Value, ring.Bounds.Size);
                }

                CanvasExpressionViewModel where = picky.Lines.Single(l => l.Key == "where");
                Rect first = RingOf(where.Rows[0].Port!), second = RingOf(where.Rows[1].Port!), gate = RingOf(picky.GatePort!);
                ComboBox per = face.GetVisualDescendants().OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, CanvasNodeViewModel.PerOptions));
                double perY = per.TranslatePoint(new Point(0, per.Bounds.Height / 2), face)!.Value.Y;
                using (Assert.Multiple())
                {
                    await Assert.That(second.Center.Y).IsGreaterThan(first.Center.Y).Because("each row's ring sits on its own row");
                    await Assert.That(faceBox.Contains(first) && faceBox.Contains(second)).IsTrue().Because("the rings are inside the face, which clips");
                    await Assert.That(Math.Abs(gate.Center.Y - perY)).IsGreaterThan(8d).Because("while is on a row of its own, below per");
                    await Assert.That(gate.Center.Y).IsGreaterThan(perY);
                }

                window.Close();
            }
        });
    }
}
