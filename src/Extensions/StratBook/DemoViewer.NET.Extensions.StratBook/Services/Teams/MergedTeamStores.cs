#region

using DemoViewer.NET.Extensions.StratBook.Services.Strats;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     Keeps what the stores file per team reachable after a merge. <see cref="TeamIdentityService.Merge" />
///     tombstones the merged team's id and nothing lists it again, while the Scouting Report's notes, the veto
///     history, the ranks and the Strat Book are looked up by the exact id: left alone, everything entered for the
///     merged team stays in the files and is never shown. So it moves onto the team that took the id over.
/// </summary>
internal static class MergedTeamStores
{
    /// <summary>
    ///     Moves every entry filed under an id a team was merged from onto that team. Cheap when there is nothing to
    ///     move, so it runs on every Team Identity change: that also catches up a merge made before the stores
    ///     followed one. UI thread, where the stores mutate.
    /// </summary>
    /// <param name="teams">Every team, hidden included, as <see cref="TeamIdentityService.MergeLineage" /> copied them.</param>
    /// <param name="notes">The Scouting Report notes.</param>
    /// <param name="vetoes">The veto history.</param>
    /// <param name="ranks">The team ranks.</param>
    /// <param name="strats">The Strat Book: its strats, books and callouts are filed by the owner's exact id too.</param>
    public static void Follow(IReadOnlyList<(Guid Id, Guid[] MergedFrom)> teams, ScoutingReportNotesStore notes,
        VetoHistoryStore vetoes, TeamRanksStore ranks, StratStore strats)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(vetoes);
        ArgumentNullException.ThrowIfNull(ranks);
        ArgumentNullException.ThrowIfNull(strats);
        HashSet<Guid> live = [.. teams.Select(t => t.Id)];
        foreach ((Guid Id, Guid[] MergedFrom) team in teams)
        {
            // The lists are copies taken under the service's gate: a merge on the teams queue adds to the live
            // one while the UI thread is here. An id that names a team again is that team's, whatever an older
            // list says.
            foreach (Guid from in team.MergedFrom.Where(id => !live.Contains(id)))
            {
                notes.Rekey(from, team.Id);
                vetoes.Rekey(from, team.Id);
                ranks.Rekey(from, team.Id);
                strats.Rekey(from, team.Id);
            }
        }
    }
}
