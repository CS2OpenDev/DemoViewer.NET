#region

using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Modules;

/// <summary>
///     The Canvas section of the Rule Canvas hub. <see cref="TabId" /> and <see cref="TabFeatureId" /> are
///     persisted keys; the header is display text. The view model is built lazily on first activation
///     through <see cref="WorkspaceTabDescriptor.ViewModelFactory" />, never handed over as a DataContext.
/// </summary>
public sealed class CanvasModule(Func<CanvasTabViewModel> viewModelFactory) : IWorkspaceModule
{
    /// <summary>The section's feature id. A persisted override key.</summary>
    public const string TabFeatureId = "tab.rulecanvas";

    /// <summary>The section's tab id. Stored in the user's session.</summary>
    public const string TabId = "rulecanvas.canvas";

    private readonly Func<CanvasTabViewModel> _viewModelFactory =
        viewModelFactory ?? throw new ArgumentNullException(nameof(viewModelFactory));

    public string Id => "net.demoviewer.rulecanvas";

    public string DisplayName => "Rule Canvas";

    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = TabId,
            Header = "Canvas",
            Order = 0,
            HostId = RuleCanvasPack.HubId,
            FeatureId = TabFeatureId,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new CanvasTabView()
        };
    }
}
