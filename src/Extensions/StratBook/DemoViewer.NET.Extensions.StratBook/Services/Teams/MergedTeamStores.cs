namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     Keeps what the stores file per team reachable after a merge. <see cref="TeamIdentityService.Merge" />
///     tombstones the merged team's id and nothing lists it again, while the Scouting Report's notes, the veto
///     history and the ranks are looked up by the exact id: left alone, everything entered for the merged team
///     stays in the files and is never shown. So it moves onto the team that took the id over.
/// </summary>
internal static class MergedTeamStores
{
    /// <summary>
    ///     Moves every entry filed under an id a team was merged from onto that team. Cheap when there is nothing to
    ///     move, so it runs on every Team Identity change: that also catches up a merge made before the stores
    ///     followed one. UI thread, where the three stores mutate.
    /// </summary>
    /// <param name="teams">Every team, hidden included.</param>
    /// <param name="notes">The Scouting Report notes.</param>
    /// <param name="vetoes">The veto history.</param>
    /// <param name="ranks">The team ranks.</param>
    public static void Follow(IReadOnlyList<Team> teams, ScoutingReportNotesStore notes, VetoHistoryStore vetoes,
        TeamRanksStore ranks)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(vetoes);
        ArgumentNullException.ThrowIfNull(ranks);
        HashSet<Guid> live = [.. teams.Select(t => t.Id)];
        foreach (Team team in teams)
        {
            // Copied first: a merge on the teams queue adds to this list while the UI thread is here. An id that
            // names a team again is that team's, whatever an older list says.
            foreach (Guid from in team.MergedFrom.ToArray().Where(id => !live.Contains(id)))
            {
                notes.Rekey(from, team.Id);
                vetoes.Rekey(from, team.Id);
                ranks.Rekey(from, team.Id);
            }
        }
    }
}
