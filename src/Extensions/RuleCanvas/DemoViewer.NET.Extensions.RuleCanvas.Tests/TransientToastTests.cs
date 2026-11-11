#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;
using DemoViewer.NET.Extensions.Testing;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>The toast's one action button in the canvas view: shown only when the toast carries an action, and pressing it runs the action and dismisses.</summary>
[NotInParallel]
[Category("Render")]
public class TransientToastTests
{
    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Test]
    public async Task TheActionButton_RunsTheActionAndDismisses()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            using RulesTree rules = RulesTree.WithUser("player_round");
            CanvasTabViewModel vm = rules.NewViewModel();
            vm.OnActivated(null!);
            CanvasTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1600, Height = 1000, Content = view };
            window.Show();
            Pump();
            TransientToast toast = view.GetVisualDescendants().OfType<TransientToast>().Single();
            int ran = 0;

            vm.Notify("plain");
            Pump();
            Button? act = toast.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "PART_Action");
            using (Assert.Multiple())
            {
                await Assert.That(toast.IsVisible).IsTrue();
                await Assert.That(toast.HasAction).IsFalse();
                await Assert.That(act?.IsVisible).IsFalse();
            }

            vm.Notify("with one", null, "Do it", () => ran++);
            Pump();
            using (Assert.Multiple())
            {
                await Assert.That(toast.HasAction).IsTrue();
                await Assert.That(toast.ActionLabel).IsEqualTo("Do it");
                await Assert.That(act?.IsVisible).IsTrue();
            }

            act!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using (Assert.Multiple())
            {
                await Assert.That(ran).IsEqualTo(1);
                await Assert.That(toast.IsVisible).IsFalse();
            }

            window.Close();
        });
    }
}
