#region

using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingDashboard;
using DemoViewer.NET.Extensions.StratBook.Views.ScoutingDashboard;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.ScoutingDashboard;

/// <summary>
///     The Scouting Report as a dashboard: the section after the Layers tab on the Strat Book rail. The same team,
///     layers and stars as <see cref="ScoutingReport.ScoutingReportModule" />, laid out as one card per finding for
///     the map and side in view instead of on a single map stage.
///     <para>
///         <b>The ids are persisted keys</b> and are never renamed: <see cref="TabFeatureId" />,
///         <see cref="BrowserTabId" /> and <see cref="Id" />. The header is display text.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and retained),
///         never <c>DataContext</c>.
///     </para>
/// </summary>
public sealed class ScoutingDashboardModule : IWorkspaceModule
{
    /// <summary>The tab's feature id. A persisted key. On by default for every category.</summary>
    public const string TabFeatureId = "tab.scouting-report-dashboard";

    /// <summary>The section's tab id.</summary>
    public const string BrowserTabId = "scouting-report-dashboard.browser";

    private readonly Func<ScoutingDashboardTabViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    public ScoutingDashboardModule(Func<ScoutingDashboardTabViewModel> viewModelFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
    }

    public string Id => "net.demoviewer.scouting-report-dashboard";
    public string DisplayName => "Scouting Report (Dashboard)";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = BrowserTabId,
            Header = "Scouting Report (Dashboard)",
            Order = 6, // after the Layers tab (5)
            HostId = HostIds.StratBookHub,
            FeatureId = TabFeatureId,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new ScoutingDashboardTabView()
        };
    }
}
