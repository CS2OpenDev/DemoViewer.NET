#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
///     Renaming an entry on its face: the title edits in place on a user ruleset, Enter renames through the
///     Inspector's path with references rewritten, a taken id is refused inline, Escape drops the edit. A
///     shipped ruleset's titles do not edit.
/// </summary>
[NotInParallel]
public class CanvasInlineRenameTests
{
    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id && !n.IsScaffold);

    private static string Text(RulesTree rules) => File.ReadAllText(rules.UserPath("player_round"));

    [Test]
    public async Task EnterRenames_AndRewritesTheReferences()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel kills = Node(vm, "round_kills");
            kills.BeginEditTitle();
            await Assert.That(kills.ShowTitleEditor).IsTrue();
            kills.TitleText = "enemy_kills";
            kills.CommitTitle();

            string text = Text(rules);
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.StatIds(text)).Contains("enemy_kills");
                await Assert.That(RulesetEdits.StatIds(text)).DoesNotContain("round_kills");
                await Assert.That(text).Contains("enemy_kills > 0 or").Because("the flag's when: read it by name");
                await Assert.That(vm.Nodes.Any(n => n.Id == "enemy_kills")).IsTrue();
                await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Single().Id).IsEqualTo("enemy_kills").Because("the renamed node stays selected");
            }
        }
    }

    [Test]
    public async Task ATakenId_IsRefusedInline_AndEscapeDropsTheEdit()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = Text(rules);
            CanvasNodeViewModel kills = Node(vm, "round_kills");
            kills.BeginEditTitle();
            kills.TitleText = "round_deaths";
            kills.CommitTitle();
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).IsEqualTo(before);
                await Assert.That(kills.TitleError).IsEqualTo("round_deaths is already declared in this ruleset.");
                await Assert.That(kills.ShowTitleEditor).IsTrue().Because("the editor stays open on the refusal");
            }

            kills.CancelTitle();
            using (Assert.Multiple())
            {
                await Assert.That(kills.ShowTitleEditor).IsFalse();
                await Assert.That(kills.TitleError).IsEmpty();
                await Assert.That(Text(rules)).IsEqualTo(before);
                await Assert.That(vm.CanUndo).IsFalse();
            }
        }
    }

    [Test]
    public async Task AShippedRulesetsTitles_DoNotEdit()
    {
        using RulesTree rules = RulesTree.WithShipped("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasNodeViewModel kills = Node(vm, "round_kills");
        kills.BeginEditTitle();
        using (Assert.Multiple())
        {
            await Assert.That(kills.CanRename).IsFalse();
            await Assert.That(kills.ShowTitleEditor).IsFalse();
        }
    }

    [Test]
    [Category("Render")]
    public async Task DoubleClickingTheTitle_OpensTheEditor_AndTheMenuOffersRename()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            CanvasTabViewModel vm = rules.NewViewModel();
            vm.OnActivated(null!);
            CanvasTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1600, Height = 900, Content = view };
            window.Show();
            Pump();
            NodifyEditor editor = view.GetVisualDescendants().OfType<NodifyEditor>().First();
            CanvasNodeViewModel kills = Node(vm, "round_kills");
            editor.ViewportZoom = 1;
            editor.ViewportLocation = new Point(kills.Location.X - 100, kills.Location.Y - 100);
            Pump();
            TextBlock title = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "NodeTitle" && ReferenceEquals(t.DataContext, kills));
            Point at = title.TranslatePoint(new Point(title.Bounds.Width / 2, title.Bounds.Height / 2), window)!.Value;
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Pump();
            using (Assert.Multiple())
            {
                await Assert.That(kills.ShowTitleEditor).IsTrue().Because("the double-click on the title edits it");
                await Assert.That(kills.IsCollapsed).IsFalse().Because("and does not fold the node");
            }

            TextBox box = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NodeTitleEditor" && ReferenceEquals(t.DataContext, kills));
            await Assert.That(box.IsEffectivelyVisible).IsTrue();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();
            await Assert.That(kills.ShowTitleEditor).IsFalse().Because("Escape cancels");

            ContextMenu menu = ((Control)editor.ContainerFromItem(kills)!).ContextMenu!;
            await Assert.That(menu.Items.OfType<MenuItem>().Any(m => m.Header as string == "Rename")).IsTrue();

            string before = File.ReadAllText(rules.UserPath("player_round"));
            // A press elsewhere first, so the headless clock does not count these as the third and fourth click.
            window.MouseDown(new Point(4, 4), MouseButton.Left);
            window.MouseUp(new Point(4, 4), MouseButton.Left);
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Pump();
            await Assert.That(kills.ShowTitleEditor).IsTrue();
            box.Text = "enemy_kills";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();
            string text = File.ReadAllText(rules.UserPath("player_round"));
            using (Assert.Multiple())
            {
                await Assert.That(RulesetEdits.StatIds(text)).Contains("enemy_kills").Because("Enter in the box renamed the entry");
                await Assert.That(vm.EditStatus).IsEmpty().Because("the redraw that took the box away did not commit a second time");
                await Assert.That(vm.Toast).IsNull();
            }

            vm.UndoCommand.Execute(null);
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before).Because("one Undo restores the old name");
            window.Close();
        });
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
}
