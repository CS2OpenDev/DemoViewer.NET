#region

using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Views.ScoutingReport;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;

/// <summary>
///     The Scouting Report module: the Scouting Report section of the Strat Book tab's rail (<see cref="BrowserTabId" />,
///     last on it) keyed by a Team Identity team: the Map Pool Record (the demo-derivable substitute for the veto
///     model), Setup Heatmaps By Buy, Opening Tendencies, Post-Plant And Retake, Situational Behaviour,
///     Period Diff, and the editable long form with its one-pager export.
///     <para>
///         <b>The ids are persisted keys.</b> <see cref="BrowserTabId" /> and <see cref="TabFeatureId" />
///         key the user's per-tab session state and <c>Features:Overrides:{id}</c>; the header
///         "Scouting Report (Layers)" is display text (it was "Scouting Report" until the Dashboard tab arrived). These were renamed once (2026-11, from <c>tab.dossier</c>,
///         <c>dossier.browser</c> and <c>net.demoviewer.dossier</c>); the old values travel as
///         <c>ExtensionFeature.PreviousIds</c> and <c>WorkspaceTabDescriptor.PreviousTabIds</c>, which the host
///         reads to carry the user's saved overrides and session state forward.
///     </para>
///     <para>
///         <b>Wiring contract.</b> <see cref="WorkspaceTabDescriptor.ViewModelFactory" /> (lazy and
///         retained), never <c>DataContext</c>. The VM is delegate-injected (the Teams precedent): the
///         composition root supplies Team Identity, the cache and the veto store; the module references
///         no shell.
///     </para>
/// </summary>
public sealed class ScoutingReportModule : IWorkspaceModule
{
    /// <summary>The tab's feature id. A persisted key, renamed once (2026-11) and otherwise never changed. On by default for every category.</summary>
    public const string TabFeatureId = "tab.scouting-report";

    /// <summary>The section's tab id; its descriptor declares <see cref="TabFeatureId" /> directly.</summary>
    private const string BrowserTabId = "scouting-report.browser";

    /// <summary>The feature id before the 2026-11 rename; carried as <c>ExtensionFeature.PreviousIds</c>.</summary>
    public const string PreviousTabFeatureId = "tab.dossier";

    /// <summary>The tab id before the 2026-11 rename; carried as <c>WorkspaceTabDescriptor.PreviousTabIds</c>.</summary>
    private const string PreviousBrowserTabId = "dossier.browser";

    private readonly Func<ScoutingReportTabViewModel> _viewModelFactory;

    /// <param name="viewModelFactory">Builds the tab VM on first activation, at the composition root.</param>
    public ScoutingReportModule(Func<ScoutingReportTabViewModel> viewModelFactory)
    {
        ArgumentNullException.ThrowIfNull(viewModelFactory);
        _viewModelFactory = viewModelFactory;
    }

    public string Id => "net.demoviewer.scouting-report";
    public string DisplayName => "Scouting Report";
    public Version ContractVersion => new(1, 0, 0);

    public IEnumerable<WorkspaceTabDescriptor> CreateTabs(IModuleHost host)
    {
        yield return new WorkspaceTabDescriptor
        {
            TabId = BrowserTabId,
            PreviousTabIds = [PreviousBrowserTabId],
            Header = "Scouting Report (Layers)",
            Order = 5, // after Review (4)
            HostId = HostIds.StratBookHub,
            FeatureId = TabFeatureId,
            ViewModelFactory = _viewModelFactory,
            ViewFactory = () => new ScoutingReportTabView()
        };
    }
}
