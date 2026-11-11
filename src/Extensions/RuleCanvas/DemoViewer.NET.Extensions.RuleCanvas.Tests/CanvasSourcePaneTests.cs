#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;
using DemoViewer.NET.Extensions.Testing;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The source pane's unsaved text against the canvas's redraws: a focus, a group or a reset of the
///     arrangement redraws the nodes, and the pane keeps what was typed, dirty, until Save or Revert.
/// </summary>
[NotInParallel]
[Category("Render")]
public class CanvasSourcePaneTests
{
    private const string Typed = "# typed, not saved\n";

    private static void Pump(int frames = 3)
    {
        for (int i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static (Window Window, CanvasTabViewModel Vm, TextEditor Source) Show(RulesTree rules)
    {
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        CanvasTabView view = new() { DataContext = vm };
        Window window = new() { Width = 1600, Height = 900, Content = view };
        window.Show();
        vm.ShowSource = true;
        Pump();
        return (window, vm, view.FindControl<TextEditor>("Source")!);
    }

    [Test]
    [Arguments("focus")]
    [Arguments("group")]
    [Arguments("clear focus")]
    public async Task ARedraw_KeepsTheUnsavedText_AndThePaneStaysDirty(string redraw)
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            (Window window, CanvasTabViewModel vm, TextEditor source) = Show(rules);
            if (redraw == "clear focus")
            {
                vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_kills"));
                Pump();
            }

            string file = File.ReadAllText(rules.UserPath("player_round"));
            source.Document.Insert(0, Typed);
            Pump();
            await Assert.That(vm.SourceDirty).IsTrue();

            switch (redraw)
            {
                case "focus":
                    vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_kills"));
                    break;
                case "group":
                    vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "round_kills"));
                    vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "round_deaths"));
                    vm.GroupSelection(null);
                    break;
                default:
                    vm.ClearFocusCommand.Execute(null);
                    break;
            }

            Pump();
            using (Assert.Multiple())
            {
                await Assert.That(source.Text).IsEqualTo(Typed + file);
                await Assert.That(vm.SourceDirty).IsTrue();
                await Assert.That(vm.SourceText).IsEqualTo(Typed + file);
            }

            vm.RevertSourceCommand.Execute(null);
            Pump();
            using (Assert.Multiple())
            {
                await Assert.That(source.Text).IsEqualTo(file).Because("Revert still puts the file's text back");
                await Assert.That(vm.SourceDirty).IsFalse();
            }

            window.Close();
        });
    }
}
