#region

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions.StratBook.Modules.StratBook.Canvas;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using DemoViewer.NET.Extensions.StratBook.Views.StratBook;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>New Strat's menu offers Blank and every template; Apply Template shows only while the strat is bare.</summary>
[NotInParallel(TestLanes.HeadlessUi)]
[Category("Integration")]
public class StratTemplateMenuTests
{
    [Test]
    public async Task TheMenus_OfferEveryTemplate_AndApplyHidesOnceTheStratHasSteps() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = new(new StratStore(null), null, null, false);
            vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
            vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
            vm.SelectedMap = "de_mirage";
            vm.NewStratCommand.Execute(null);

            Window window = new() { Width = 1280, Height = 800, Content = new StratBookTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            SplitButton newStrat = window.GetVisualDescendants().OfType<SplitButton>()
                .Single(b => b.Name == "NewStratButton");
            DropDownButton apply = window.GetVisualDescendants().OfType<DropDownButton>()
                .Single(b => b.Name == "ApplyTemplateButton");
            MenuItem[] items = [.. ((MenuFlyout)newStrat.Flyout!).Items.OfType<MenuItem>()];
            string[] ids =
            [
                .. items.SelectMany(i => i.Items.Count > 0 ? i.Items.OfType<MenuItem>() : [i])
                    .Select(i => i.CommandParameter as string).OfType<string>()
            ];

            using (Assert.Multiple())
            {
                await Assert.That(items[0].Header).IsEqualTo("Blank");
                await Assert.That(ids)
                    .IsEquivalentTo(StratTemplates.Templates.Select(t => t.Id), CollectionOrdering.Matching);
                await Assert.That(apply.IsEffectivelyVisible).IsTrue().Because("a blank strat can take a template");
            }

            await Assert.That(items.Single(i => Equals(i.Header, "Default (T)")).Command)
                .IsSameReferenceAs(vm.NewStratCommand);
            vm.ApplyTemplateCommand.Execute("default");
            Dispatcher.UIThread.RunJobs();

            await Assert.That(apply.IsEffectivelyVisible).IsFalse().Because("the strat has steps now");
            window.Close();
        });

    // The host drops the view on every deactivation and keeps the view model: a handler the view leaves on the
    // editor or on the canvas's tools keeps each dropped view alive and running.
    [Test]
    public async Task ADetachedView_LeavesNoHandlerOnTheEditor_NorItsCanvasOnTheTools() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = new(new StratStore(null), null, null, false);
            vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
            vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
            vm.SelectedMap = "de_mirage";
            vm.NewStratCommand.Execute(null);

            StratBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1280, Height = 800, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            StratCanvasView canvas = view.GetVisualDescendants().OfType<StratCanvasView>()
                .First(c => c.DataContext is StratCanvasViewModel);
            StratCanvasViewModel canvasVm = (StratCanvasViewModel)canvas.DataContext!;
            bool Editor() => Targets(vm.Editor, nameof(StratEditorViewModel.StepFocusRequested)).Contains(view);
            bool Tools() => Targets(canvasVm.Tools, nameof(StratCanvasTools.ToolSelected)).Contains(canvas);

            bool editorShown = Editor(), toolsShown = Tools();
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            bool editorDropped = Editor(), toolsDropped = Tools();
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            bool editorBack = Editor(), toolsBack = Tools();

            using (Assert.Multiple())
            {
                await Assert.That(editorShown).IsTrue();
                await Assert.That(toolsShown).IsTrue();
                await Assert.That(editorDropped).IsFalse().Because("the editor outlives the view");
                await Assert.That(toolsDropped).IsFalse().Because("the canvas view model outlives its view");
                await Assert.That(editorBack).IsTrue().Because("a view shown again listens again");
                await Assert.That(toolsBack).IsTrue();
            }

            window.Close();
        });

    // Who listens to a field-like event: the targets of its backing delegate.
    private static object?[] Targets(object source, string eventName) =>
        source.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(source) is
            Delegate handlers
            ? [.. handlers.GetInvocationList().Select(h => h.Target)]
            : [];
}
