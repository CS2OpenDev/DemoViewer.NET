#region

using System.Globalization;
using System.Text.Json;
using DemoViewer.NET.Extensions.StratBook.Services.Portability;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     The optional, user-entered veto history the Map Pool Record's "Done" line asks for:
///     manual entry only, no scraping, filed per opponent team.
///     <para>
///         <b>Persistence.</b> <c>veto-history.json</c> beside <c>teams.json</c>, written whole through its
///         <see cref="StoredFile" /> after every mutation, the same small-file rule
///         <c>ReviewQueue</c> follows. No file (the browser, tests) keeps the
///         history for the session; a file that cannot be read, or is at a newer schema, is refused and
///         never overwritten.
///     </para>
///     <para><b>Threading.</b> UI thread only: every caller is a view model reacting to a click.</para>
/// </summary>
public sealed class VetoHistoryStore
{
    /// <summary>The file's name.</summary>
    public const string FileName = "veto-history.json";

    private readonly List<VetoEntry> _entries = [];
    private readonly StoredFile? _file;
    private bool _refused;

    /// <param name="file">The <see cref="FileName" /> file, or null for a session-only store (the browser, tests).</param>
    public VetoHistoryStore(StoredFile? file)
    {
        _file = file;
        Load();
    }

    /// <summary>True when nothing persists: the browser host, and tests without a file.</summary>
    public bool IsSessionOnly => _file is null;

    /// <summary>Why the file could not be read, or null. While set the file is never written.</summary>
    public string? FileProblem { get; private set; }

    /// <summary>Every team this store holds something for; Team Identity keeps these through a rebuild.</summary>
    public IReadOnlyList<Guid> TeamIds => [.. _entries.Select(e => e.OpponentTeamId).Distinct()];

    /// <summary>Raised on the calling thread after every mutation that changed something.</summary>
    public event Action? Changed;

    /// <summary>The opponent's steps, in the order the user gave them.</summary>
    /// <param name="opponentTeamId">The opponent the history is filed under.</param>
    public IReadOnlyList<VetoEntry> For(Guid opponentTeamId) =>
        [.. _entries.Where(e => e.OpponentTeamId == opponentTeamId).OrderBy(e => e.Order)];

    /// <summary>
    ///     Drops every step and re-reads the file: the recovery after it is deleted out from under the
    ///     store by the "delete extension data" action, so a later mutation does not resave what the delete
    ///     just removed.
    /// </summary>
    public void Reload()
    {
        _entries.Clear();
        _refused = false;
        FileProblem = null;
        Load();
        Changed?.Invoke();
    }

    /// <summary>Appends one step. The caller sets <see cref="VetoEntry.Order" />.</summary>
    /// <param name="entry">The step to append.</param>
    public void Add(VetoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Removes one step by id. No-op when it is not there.</summary>
    /// <param name="id">The step's id.</param>
    public void Remove(Guid id)
    {
        if (_entries.RemoveAll(e => e.Id == id) > 0)
        {
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>
    ///     Folds another machine's veto steps in by id, each filed under the team its opponent landed on. A step both
    ///     hold in different versions keeps this machine's unless the user asked for the imported one; a step is not
    ///     something to hold twice, so keep both keeps this machine's too. A second import of the same file changes
    ///     nothing. UI thread, like every other mutator.
    /// </summary>
    /// <param name="staged">The bundle's copy of the store, holding <see cref="FileName" />; read only.</param>
    /// <param name="teams">Where each imported team landed.</param>
    /// <param name="ledger">The policy, the report and the counts.</param>
    public void Import(IExtensionFolder? staged, TeamIdMap teams, ImportLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(ledger);
        if (ledger.ReadJson<VetoHistoryFile>(staged, FileName, VetoHistoryFile.JsonOptions) is not { } file)
        {
            return;
        }

        if (file.SchemaVersion > VetoHistoryFile.CurrentSchema || _refused)
        {
            ledger.Report(_refused
                ? $"no veto history was imported: {FileProblem}"
                : $"{FileName} in the bundle is newer than this build reads; no veto history was imported");
            return;
        }

        bool changed = false;
        foreach (VetoEntry entry in file.Entries)
        {
            VetoEntry incoming = new()
            {
                Id = entry.Id,
                OpponentTeamId = teams.Map(entry.OpponentTeamId),
                Order = entry.Order,
                Map = entry.Map,
                Action = entry.Action,
                ByOpponent = entry.ByOpponent,
                Note = entry.Note
            };
            int at = _entries.FindIndex(e => e.Id == incoming.Id);
            if (at < 0)
            {
                _entries.Add(incoming);
                ledger.CountAdded();
                changed = true;
            }
            else if (Same(_entries[at], incoming))
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

    private static bool Same(VetoEntry a, VetoEntry b) =>
        a.OpponentTeamId == b.OpponentTeamId && a.Order == b.Order && a.Action == b.Action
        && a.ByOpponent == b.ByOpponent && string.Equals(a.Map, b.Map, StringComparison.Ordinal)
        && string.Equals(a.Note, b.Note, StringComparison.Ordinal);

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

            VetoHistoryFile? file = JsonSerializer.Deserialize<VetoHistoryFile>(bytes, VetoHistoryFile.JsonOptions);
            if (file is null || file.SchemaVersion > VetoHistoryFile.CurrentSchema)
            {
                Refuse(
                    $"{FileName} is at schema {file?.SchemaVersion.ToString(CultureInfo.InvariantCulture) ?? "?"}, newer than this build reads");
                return;
            }

            _entries.AddRange(file.Entries);
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
            VetoHistoryFile file = new() { Entries = [.. _entries] };
            _file.Write(JsonSerializer.SerializeToUtf8Bytes(file, VetoHistoryFile.JsonOptions));
        }
        catch (Exception)
        {
            // Best effort: the in-memory history stands for the session and the next mutation retries.
        }
    }
}
