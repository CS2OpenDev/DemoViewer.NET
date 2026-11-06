#region

using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Extensions.RuleCanvas.Views;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Modules;

/// <summary>
///     The Canvas section of the Rule Canvas hub. <see cref="TabId" /> is a persisted key; the header is
///     display text. No feature of its own: the hub's switch is the section's switch while it is the only
///     one. The view model is built lazily on first activation through
///     <see cref="WorkspaceTabDescriptor.ViewModelFactory" />, never handed over as a DataContext.
/// </summary>
public sealed class CanvasModule(Func<CanvasTabViewModel> viewModelFactory) : IWorkspaceModule
{
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
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new CanvasTabView()
        };
    }
}
