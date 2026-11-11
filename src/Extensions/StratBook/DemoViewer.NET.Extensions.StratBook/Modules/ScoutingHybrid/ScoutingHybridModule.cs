#region

using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Views.ScoutingHybrid;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.ScoutingHybrid;

/// <summary>
///     The Scouting Report (Hybrid): the section after the Dashboard on the Strat Book rail. The Layers tab's view
///     model and view, laid out tighter (the roster and the map pool side by side above one panel for the map in
///     view), as the start of a layout that mixes the Layers and Dashboard tabs. Its own team, map and side; the
///     stars, notes, ranks and vetoes are shared with the other two.
///     <para>
///         <b>The ids are persisted keys</b> and are never renamed: <see cref="TabFeatureId" />,
///         <see cref="BrowserTabId" /> and <see cref="Id" />. The header is display text.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and retained),
///         never <c>DataContext</c>.
///     </para>
/// </summary>
public sealed class ScoutingHybridModule : IWorkspaceModule
{
    /// <summary>The tab's feature id. A persisted key. On by default for every category.</summary>
    public const string TabFeatureId = "tab.scouting-report-hybrid";

    /// <summary>The section's tab id.</summary>
    public const string BrowserTabId = "scouting-report-hybrid.browser";

    private readonly Func<ScoutingReportTabViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    public ScoutingHybridModule(Func<ScoutingReportTabViewModel> viewModelFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
    }

    public string Id => "net.demoviewer.scouting-report-hybrid";
    public string DisplayName => "Scouting Report (Hybrid)";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = BrowserTabId,
            Header = "Scouting Report (Hybrid)",
            Order = 7, // after the Layers (5) and Dashboard (6) tabs
            HostId = HostIds.StratBookHub,
            FeatureId = TabFeatureId,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new ScoutingHybridTabView()
        };
    }
}
