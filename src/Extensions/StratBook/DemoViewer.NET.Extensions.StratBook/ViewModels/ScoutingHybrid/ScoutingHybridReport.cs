#region

using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingHybrid;

/// <summary>
///     The Hybrid tab's own report: a second <see cref="ScoutingReportTabViewModel" /> beside the Layers tab's, held
///     under its own type so the container keeps the two apart. The tab binds <see cref="Report" /> directly.
/// </summary>
/// <param name="report">The report, built over the same services and stores as the Layers tab's.</param>
public sealed class ScoutingHybridReport(ScoutingReportTabViewModel report)
{
    public ScoutingReportTabViewModel Report { get; } = report;
}
