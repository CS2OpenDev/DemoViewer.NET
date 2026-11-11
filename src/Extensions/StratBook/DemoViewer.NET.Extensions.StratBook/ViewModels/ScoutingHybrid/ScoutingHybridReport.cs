#region

using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingHybrid;

/// <summary>
///     The Hybrid tab's own report: a second <see cref="ScoutingReportTabViewModel" /> beside the Layers tab's, held
///     under its own type so the container keeps the two apart. The tab binds <see cref="Report" /> directly.
/// </summary>
/// <remarks>Its export is the Hybrid one: every layer in turn for the long form, the buy pictures on the one-pager.</remarks>
public sealed class ScoutingHybridReport
{
    /// <param name="report">The report, built over the same services and stores as the Layers tab's.</param>
    public ScoutingHybridReport(ScoutingReportTabViewModel report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
        report.UseHybridExport();
    }

    public ScoutingReportTabViewModel Report { get; }
}
