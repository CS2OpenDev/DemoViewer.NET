#region

using System.Reflection;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strat Book builds its views on the SDK's UI kit, as a third-party extension would. The app's
///     <see cref="ViewModelBase" /> stays reachable through the first-party grants, so the compiler alone does
///     not keep it out.
/// </summary>
public class StratBookUiKitTests
{
    private static readonly Assembly _pack = typeof(StratBookPack).Assembly;

    [Test]
    public async Task NoStratBookType_DerivesFromTheAppsViewModelBase()
    {
        string[] offenders =
            [.. _pack.GetTypes().Where(t => t.IsSubclassOf(typeof(ViewModelBase))).Select(t => t.FullName!)];

        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task ItsViewModels_ResolveThroughTheSdkMarker()
    {
        Type[] viewModels = [.. _pack.GetTypes().Where(t => typeof(IExtensionViewModel).IsAssignableFrom(t))];

        using (Assert.Multiple())
        {
            // The marker is only on the view models with a view the naming rule can place; the row and panel
            // view models the pack's own markup draws are plain observables, so this floor sits well under
            // the pack's view-model count.
            await Assert.That(viewModels.Length).IsGreaterThanOrEqualTo(13);
            await Assert.That(viewModels.All(t => t.IsSubclassOf(typeof(ExtensionViewModel)))).IsTrue();
            await Assert.That(_pack.GetReferencedAssemblies().Select(a => a.Name!))
                .Contains("DemoViewer.NET.Extensions.Sdk.Ui");
        }
    }
}
