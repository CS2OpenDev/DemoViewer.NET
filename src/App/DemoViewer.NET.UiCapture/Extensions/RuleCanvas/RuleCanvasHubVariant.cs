#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.RuleCanvas;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>
///     The whole shell with the Rule Canvas hub selected: the strip tab, the rail the host draws, and the
///     Canvas section's empty state. The pack is composed the way a shell test composes it, over an empty
///     library and a gate that answers on for everything.
/// </summary>
public static partial class Variants
{
    private static MainView RuleCanvasHub()
    {
        RuleCanvasPack pack = new();
        ServiceCollection services = new();
        pack.Register(services);
        ServiceProvider provider = services.BuildServiceProvider();
        PackContributionSet set = new([pack], provider);
        ModuleRegistry registry = new();
        foreach (IWorkspaceModule module in set.Packs.SelectMany(p => p.Modules))
        {
            registry.Register(module);
        }

        DemoLibraryService library = new(null,
            Path.Combine(Path.GetTempPath(), "dvlib_uicapture_" + Guid.NewGuid().ToString("N") + ".json"));
        MainViewModel vm = new(null, registry, library, null, new EverythingOn(), null, null, hubTabs: set.HubTabs);
        vm.RestoreSession();
        vm.TrySelectTab(RuleCanvasPack.HubId);
        return new MainView { DataContext = vm };
    }

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
}
