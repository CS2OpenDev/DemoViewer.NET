#region

using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

/// <summary>
///     The team in view goes first in the processing queue: opening a team asks for every one of its demos at
///     user priority, and "Process this team first" asks again. A notice counts the team's demos whose analysis
///     is not current yet, read from the stamps the projection already reads.
/// </summary>
public sealed partial class ScoutingReportTabViewModel
{
    // Re-opening a team within this asks nothing again; the button always asks.
    internal static readonly TimeSpan AutoRequestWindow = TimeSpan.FromMinutes(2);

    private readonly Dictionary<Guid, DateTime> _requestedAt = [];
    private readonly Action<IReadOnlyList<string>>? _requestDemos;
    private string _processingNotice = "";

    /// <summary>True when there is a team open and a host that can queue its demos.</summary>
    public bool CanProcessTeamFirst => _requestDemos is not null && SelectedTeam is not null;

    /// <summary>Asks for every demo of the team in view ahead of the backlog.</summary>
    [RelayCommand]
    private void ProcessTeamFirst()
    {
        if (SelectedTeam is { } team)
        {
            RequestTeam(team.Id, force: true);
        }
    }

    private void RequestTeam(Guid teamId, bool force)
    {
        if (_requestDemos is null || _disposed)
        {
            return;
        }

        DateTime now = _now();
        if (!force && _requestedAt.TryGetValue(teamId, out DateTime at) && now - at < AutoRequestWindow)
        {
            return;
        }

        string[] paths = [.. _teams.DemosOf(teamId).Select(d => d.Path)];
        if (paths.Length == 0)
        {
            return;
        }

        _requestedAt[teamId] = now;
        _requestDemos(paths);
    }

    // "3 of 8 demos for this team still processing", or empty when every one is current. Only a host that
    // queues the team's demos says so: elsewhere nothing puts them ahead.
    private string ProcessingNotice()
    {
        if (_requestDemos is null || SelectedTeam is not { } team)
        {
            return "";
        }

        IReadOnlyList<DemoRef> demos = _teams.DemosOf(team.Id);
        int pending = demos.Count(d => IsStillProcessing(d.Path));
        return pending == 0
            ? ""
            : $"{pending} of {ScoutingLayerBase.Plural(demos.Count, "demo")} for this team still processing. They run ahead of the rest of the library; the report fills in as they finish.";
    }

    // Not parsed, Round Facts not written, the round index behind its map's places, or the player facts written
    // under an older ruleset. A failed write is not "processing": it waits for a retry.
    private bool IsStillProcessing(string path)
    {
        if (_library.Find(path) is not { } entry)
        {
            return false;
        }

        if (entry.State < LibraryDemoState.Parsed
            || entry.Fact(RoundFactsRows.FacetId) is not { } facts
            || (facts.State != DemoDataState.Failed && !facts.IsWritten))
        {
            return true;
        }

        if (_sources.AnalysisFacts?.Status(path, PlayerStatsService.PlayerMatch) == FactStatus.Stale)
        {
            return true;
        }

        return _roundIndex is not null && _sources.PositionsFingerprint is { } fingerprint
            && _roundIndex.Needs(path, fingerprint(entry.MapName));
    }

    // A library change moves the count as demos finish; the notices re-project only when the line changed.
    private void OnLibraryChangedForProcessing(LibraryChange change)
    {
        if (!_disposed && SelectedTeam is not null && ProcessingNotice() != _processingNotice)
        {
            ProjectNotices();
        }
    }
}
