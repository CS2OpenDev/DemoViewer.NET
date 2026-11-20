#region

using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Controls;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Parser tab's two hex view models live as long as the shell, and each visit to the tab builds a
///     new view whose panes keep their DataContext after the tab is left. A pane that stayed subscribed to
///     its view model kept that whole dead view reachable, one more per visit.
/// </summary>
[TUnit.Core.Category("Render")]
public class BinaryPaneLifetimeTests
{
    [Test]
    public async Task ADetachedPane_LetsGoOfItsViewModel_AndSubscribesAgainWhenReattached()
    {
        int detached = -1, reattached = -1;

        await HeadlessSession.RunOnUi(() =>
        {
            HarvestHexViewModel vm = new();
            BinaryPane pane = new() { DataContext = vm };
            Window window = new() { Content = pane };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            detached = PaneSubscribers(vm);

            window.Content = pane;
            Dispatcher.UIThread.RunJobs();
            reattached = PaneSubscribers(vm);

            window.Close();
            return Task.CompletedTask;
        });

        using (Assert.Multiple())
        {
            await Assert.That(detached).IsEqualTo(0)
                .Because("a pane off the visual tree must not be rooted by the long-lived view model");
            await Assert.That(reattached).IsEqualTo(1);
        }
    }

    private static int PaneSubscribers(HarvestHexViewModel vm)
    {
        FieldInfo field = typeof(ObservableObject).GetField(nameof(ObservableObject.PropertyChanged),
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return field.GetValue(vm) is PropertyChangedEventHandler handler
            ? handler.GetInvocationList().Count(d => d.Target is BinaryPane)
            : 0;
    }
}
