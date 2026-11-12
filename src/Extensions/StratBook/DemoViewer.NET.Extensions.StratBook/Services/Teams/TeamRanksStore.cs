#region

using System.Globalization;
using System.Text.Json;
using DemoViewer.NET.Extensions.StratBook.Services.Portability;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     The VRS and HLTV rank of each team, entered by hand (no network), each with the day it was set. Older
///     entries stay as history, so <see cref="RankAt" /> answers "what was this team ranked when that demo was
///     played" and <see cref="TierAt" /> turns it into the opponent tier.
///     <para>
///         <b>Persistence.</b> <c>team-ranks.json</c> beside <c>veto-history.json</c>, written whole after every
///         mutation. No file (the browser, tests) keeps the ranks for the session; a file that cannot be read, or
///         is at a newer schema, is refused and never overwritten.
///     </para>
///     <para><b>Threading.</b> UI thread only.</para>
/// </summary>
public sealed class TeamRanksStore
{
    /// <summary>The file's name.</summary>
    public const string FileName = "team-ranks.json";

    /// <summary>A rank this good or better is top-30.</summary>
    public const int TopTierCutoff = 30;

    private readonly List<RankEntry> _entries = [];
    private readonly StoredFile? _file;
    private bool _refused;

    /// <param name="file">The <see cref="FileName" /> file, or null for a session-only store.</param>
    public TeamRanksStore(StoredFile? file)
    {
        _file = file;
        Load();
    }

    public bool IsSessionOnly => _file is null;

    /// <summary>Why the file could not be read, or null. While set the file is never written.</summary>
    public string? FileProblem { get; private set; }

    /// <summary>Every team this store holds a rank for; Team Identity keeps these through a rebuild.</summary>
    public IReadOnlyList<Guid> TeamIds => [.. _entries.Select(e => e.TeamId).Distinct()];

    /// <summary>True when any team has any rank, so "unknown" can be told from "nothing entered yet".</summary>
    public bool HasAny => _entries.Count > 0;

    public event Action? Changed;

    /// <summary>Reads a rank box: empty is valid and means no rank; otherwise a positive integer.</summary>
    /// <param name="text">What the user typed (a leading "#" is allowed).</param>
    /// <param name="rank">The rank, null when empty.</param>
    /// <returns>False when the text is neither empty nor a positive integer.</returns>
    public static bool TryParse(string? text, out int? rank)
    {
        rank = null;
        string trimmed = (text ?? "").Trim().TrimStart('#').Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0)
        {
            rank = value;
            return true;
        }

        return false;
    }

    /// <summary>The team's entries for one ranking, oldest first.</summary>
    public IReadOnlyList<RankEntry> History(Guid teamId, RankSystem system) =>
    [
        .. _entries.Where(e => e.TeamId == teamId && e.System == system)
            .Select((e, i) => (e, i)).OrderBy(p => p.e.AsOf).ThenBy(p => p.i).Select(p => p.e)
    ];

    /// <summary>The newest entry, or null with none.</summary>
    public RankEntry? Current(Guid teamId, RankSystem system) => History(teamId, system) is [.., { } last] ? last : null;

    /// <summary>
    ///     The rank in force on a day: the latest entry set on or before it, else the earliest entry (the best
    ///     the user has for a demo older than anything entered). Null with no entry at all.
    /// </summary>
    /// <param name="teamId">The team.</param>
    /// <param name="system">The ranking.</param>
    /// <param name="day">The demo's date.</param>
    public int? RankAt(Guid teamId, RankSystem system, DateOnly day)
    {
        IReadOnlyList<RankEntry> history = History(teamId, system);
        RankEntry? onOrBefore = history.LastOrDefault(e => e.AsOf <= day);
        return (onOrBefore ?? (history.Count > 0 ? history[0] : null))?.Rank;
    }

    /// <summary>HLTV preferred, VRS when there is no HLTV rank; ranked 30 or better is top-30.</summary>
    /// <param name="teamId">The opponent; null (no team identified) is unknown.</param>
    /// <param name="day">The demo's date.</param>
    public OpponentTier TierAt(Guid? teamId, DateOnly day)
    {
        if (teamId is not { } id)
        {
            return OpponentTier.Unknown;
        }

        int? rank = RankAt(id, RankSystem.Hltv, day) ?? RankAt(id, RankSystem.Vrs, day);
        return rank is null ? OpponentTier.Unknown : rank <= TopTierCutoff ? OpponentTier.Top30 : OpponentTier.Other;
    }

    /// <summary>
    ///     Sets the rank as of a day: an entry for the same day is replaced, otherwise one is added and the
    ///     older ones stay as history.
    /// </summary>
    /// <param name="teamId">The team.</param>
    /// <param name="system">The ranking.</param>
    /// <param name="rank">A positive rank.</param>
    /// <param name="asOf">The day it was set.</param>
    public void Set(Guid teamId, RankSystem system, int rank, DateOnly asOf)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rank);
        _entries.RemoveAll(e => e.TeamId == teamId && e.System == system && e.AsOf == asOf);
        _entries.Add(new RankEntry { TeamId = teamId, System = system, Rank = rank, AsOf = asOf });
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    ///     Clears the current rank (an emptied box): the newest entry is dropped, so the one before it, if any,
    ///     is current again.
    /// </summary>
    /// <param name="teamId">The team.</param>
    /// <param name="system">The ranking.</param>
    public void ClearCurrent(Guid teamId, RankSystem system)
    {
        if (Current(teamId, system) is { } current && _entries.Remove(current))
        {
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>Drops everything and re-reads the file, the recovery after "delete extension data".</summary>
    public void Reload()
    {
        _entries.Clear();
        _refused = false;
        FileProblem = null;
        Load();
        Changed?.Invoke();
    }

    /// <summary>
    ///     Folds another machine's ranks in by id, each filed under the team it landed on. An entry for a team,
    ///     ranking and day this machine already has is the same entry whatever its id, since <see cref="Set" />
    ///     keeps one per day: a different rank there keeps this machine's unless the user asked for the imported
    ///     one. A second import of the same file changes nothing. UI thread, like every other mutator.
    /// </summary>
    /// <param name="staged">The bundle's copy of the store, holding <see cref="FileName" />; read only.</param>
    /// <param name="teams">Where each imported team landed.</param>
    /// <param name="ledger">The policy, the report and the counts.</param>
    public void Import(IExtensionFolder? staged, TeamIdMap teams, ImportLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(ledger);
        if (ledger.ReadJson<TeamRanksFile>(staged, FileName, TeamRanksFile.JsonOptions) is not { } file)
        {
            return;
        }

        if (file.SchemaVersion > TeamRanksFile.CurrentSchema || _refused)
        {
            ledger.Report(_refused
                ? $"no team ranks were imported: {FileProblem}"
                : $"{FileName} in the bundle is newer than this build reads; no ranks were imported");
            return;
        }

        bool changed = false;
        foreach (RankEntry entry in file.Entries.Where(e => e.Rank > 0))
        {
            RankEntry incoming = new()
            {
                Id = entry.Id, TeamId = teams.Map(entry.TeamId), System = entry.System, Rank = entry.Rank,
                AsOf = entry.AsOf
            };
            int at = _entries.FindIndex(e => e.Id == incoming.Id
                || (e.TeamId == incoming.TeamId && e.System == incoming.System && e.AsOf == incoming.AsOf));
            if (at < 0)
            {
                _entries.Add(incoming);
                ledger.CountAdded();
                changed = true;
            }
            else if (_entries[at].TeamId == incoming.TeamId && _entries[at].System == incoming.System
                     && _entries[at].AsOf == incoming.AsOf && _entries[at].Rank == incoming.Rank)
            {
                ledger.CountSkipped();
            }
            else
            {
                ledger.CountConflict();
                if (ledger.Policy == ImportConflictPolicy.TakeImported)
                {
                    _entries[at] = incoming;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Save();
            Changed?.Invoke();
        }
    }

    private void Load()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            if (_file.Read() is not { } bytes)
            {
                return;
            }

            TeamRanksFile? file = JsonSerializer.Deserialize<TeamRanksFile>(bytes, TeamRanksFile.JsonOptions);
            if (file is null || file.SchemaVersion > TeamRanksFile.CurrentSchema)
            {
                Refuse(
                    $"{FileName} is at schema {file?.SchemaVersion.ToString(CultureInfo.InvariantCulture) ?? "?"}, newer than this build reads");
                return;
            }

            _entries.AddRange(file.Entries.Where(e => e.Rank > 0));
        }
        catch (Exception ex)
        {
            Refuse($"{FileName} could not be read: {ex.Message}");
        }
    }

    private void Refuse(string problem)
    {
        _refused = true;
        FileProblem = problem;
        _entries.Clear();
    }

    private void Save()
    {
        if (_file is null || _refused)
        {
            return;
        }

        try
        {
            TeamRanksFile file = new() { Entries = [.. _entries] };
            _file.Write(JsonSerializer.SerializeToUtf8Bytes(file, TeamRanksFile.JsonOptions));
        }
        catch (Exception)
        {
            // Best effort: the in-memory ranks stand for the session and the next mutation retries.
        }
    }
}
