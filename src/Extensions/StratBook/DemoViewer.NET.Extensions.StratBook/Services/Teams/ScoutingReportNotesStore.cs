#region

using System.Globalization;
using System.Text.Json;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     The user's edits to each team's Scouting Report: stars, rewritten
///     lines, left-out lines, free notes and the summary, filed per team.
///     <para>
///         <b>Persistence.</b> <c>scouting-report-notes.json</c> beside <c>teams.json</c>, written whole through its
///         <see cref="StoredFile" /> after every mutation, as <see cref="VetoHistoryStore" />
///         does. No file (the browser, tests) keeps the notes for the session; a file that cannot
///         be read, or is at a newer schema, is refused and never overwritten. When the file does not exist and
    ///         the legacy <see cref="LegacyFileName" /> does, that is read instead and written out as the new file
    ///         at once; the old file is left where it is.
///     </para>
///     <para><b>Threading.</b> UI thread only: every caller is a view model reacting to a click or an edit.</para>
/// </summary>
public sealed class ScoutingReportNotesStore
{
    /// <summary>The file's name.</summary>
    public const string FileName = "scouting-report-notes.json";

    /// <summary>The file's name before the 2026-11 rename (the feature was the Opponent Dossier). Same shape, read once.</summary>
    public const string LegacyFileName = "dossier-notes.json";

    /// <summary>The key prefix of a user-written finding.</summary>
    private const string NoteKeyPrefix = "note:";

    private readonly StoredFile? _file;
    private readonly StoredFile? _legacy;
    private readonly List<ScoutingReportTeamNotes> _teams = [];
    private bool _refused;

    /// <param name="file">The <see cref="FileName" /> file, or null for a session-only store (the browser, tests).</param>
    /// <param name="legacy">The <see cref="LegacyFileName" /> file, read when <paramref name="file" /> does not exist yet.</param>
    public ScoutingReportNotesStore(StoredFile? file, StoredFile? legacy = null)
    {
        _file = file;
        _legacy = legacy;
        Load();
    }

    /// <summary>True when nothing persists: the browser host, and tests without a file.</summary>
    public bool IsSessionOnly => _file is null;

    /// <summary>Why the file could not be read, or null. While set the file is never written.</summary>
    public string? FileProblem { get; private set; }

    /// <summary>Every team this store holds something for; Team Identity keeps these through a rebuild.</summary>
    public IReadOnlyList<Guid> TeamIds => [.. _teams.Select(t => t.TeamId).Distinct()];

    /// <summary>Raised on the calling thread after every mutation that changed something.</summary>
    public event Action? Changed;

    /// <summary>The key of a user-written note.</summary>
    /// <param name="id">The note's id.</param>
    public static string NoteKey(Guid id) => NoteKeyPrefix + id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>The team's notes; an empty record (not stored) when the user has done nothing yet.</summary>
    /// <param name="teamId">The team.</param>
    public ScoutingReportTeamNotes For(Guid teamId) =>
        _teams.FirstOrDefault(t => t.TeamId == teamId) ?? new ScoutingReportTeamNotes { TeamId = teamId };

    /// <summary>Stars or unstars a finding.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="key">The finding's key.</param>
    /// <param name="starred">True to put it on the one-pager.</param>
    public void SetStarred(Guid teamId, string key, bool starred)
    {
        ArgumentNullException.ThrowIfNull(key);
        ScoutingReportTeamNotes notes = Ensure(teamId);
        if (Toggle(notes.Starred, key, starred))
        {
            Commit();
        }
    }

    /// <summary>
    ///     Rewrites a generated finding. Null, blank, or the generated text itself drops the edit, so a
    ///     finding whose numbers later change is not pinned to the old wording by an edit that changed nothing.
    /// </summary>
    /// <param name="teamId">The team.</param>
    /// <param name="key">The finding's key.</param>
    /// <param name="text">The new text.</param>
    /// <param name="generated">The finding's generated text, which an edit equal to it is not kept over.</param>
    public void SetEdit(Guid teamId, string key, string? text, string generated)
    {
        ArgumentNullException.ThrowIfNull(key);
        ScoutingReportTeamNotes notes = Ensure(teamId);
        string? trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, generated, StringComparison.Ordinal))
        {
            if (notes.Edits.Remove(key))
            {
                Commit();
            }

            return;
        }

        if (notes.Edits.TryGetValue(key, out string? old) && string.Equals(old, trimmed, StringComparison.Ordinal))
        {
            return;
        }

        notes.Edits[key] = trimmed;
        Commit();
    }

    /// <summary>Leaves a generated finding out of both forms, or brings it back.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="key">The finding's key.</param>
    /// <param name="hidden">True to leave it out.</param>
    public void SetHidden(Guid teamId, string key, bool hidden)
    {
        ArgumentNullException.ThrowIfNull(key);
        ScoutingReportTeamNotes notes = Ensure(teamId);
        if (Toggle(notes.Hidden, key, hidden))
        {
            Commit();
        }
    }


    /// <summary>Sets the summary paragraph.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="summary">The text; null or blank clears it.</param>
    public void SetSummary(Guid teamId, string? summary)
    {
        ScoutingReportTeamNotes notes = Ensure(teamId);
        string value = summary?.Trim() ?? "";
        if (string.Equals(notes.Summary, value, StringComparison.Ordinal))
        {
            return;
        }

        notes.Summary = value;
        Commit();
    }

    /// <summary>Adds a note and returns its key, or null when the text is blank.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="text">The note.</param>
    public string? AddNote(Guid teamId, string? text)
    {
        string value = text?.Trim() ?? "";
        if (value.Length == 0)
        {
            return null;
        }

        ScoutingReportNote note = new() { Text = value };
        Ensure(teamId).Notes.Add(note);
        Commit();
        return NoteKey(note.Id);
    }

    /// <summary>Rewrites a note; blank text leaves it as it was (removing is its own action).</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="key">The note's key.</param>
    /// <param name="text">The new text.</param>
    public void EditNote(Guid teamId, string key, string? text)
    {
        string value = text?.Trim() ?? "";
        ScoutingReportTeamNotes notes = Ensure(teamId);
        if (value.Length == 0 || notes.Notes.FirstOrDefault(n => NoteKey(n.Id) == key) is not { } note
            || string.Equals(note.Text, value, StringComparison.Ordinal))
        {
            return;
        }

        note.Text = value;
        Commit();
    }

    /// <summary>
    ///     Drops every team's notes and re-reads the file: the recovery after it is deleted out from under
    ///     the store by the "delete extension data" action, so a later mutation does not resave what the
    ///     delete just removed.
    /// </summary>
    public void Reload()
    {
        _teams.Clear();
        _refused = false;
        FileProblem = null;
        Load();
        Changed?.Invoke();
    }

    /// <summary>Removes a note and its star.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="key">The note's key.</param>
    public void RemoveNote(Guid teamId, string key)
    {
        ScoutingReportTeamNotes notes = Ensure(teamId);
        if (notes.Notes.RemoveAll(n => NoteKey(n.Id) == key) == 0)
        {
            return;
        }

        notes.Starred.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
        Commit();
    }

    // Adds or removes a key; true when the list changed.
    private static bool Toggle(List<string> keys, string key, bool present)
    {
        bool has = keys.Contains(key, StringComparer.Ordinal);
        if (has == present)
        {
            return false;
        }

        if (present)
        {
            keys.Add(key);
        }
        else
        {
            keys.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
        }

        return true;
    }

    private ScoutingReportTeamNotes Ensure(Guid teamId)
    {
        if (_teams.FirstOrDefault(t => t.TeamId == teamId) is { } found)
        {
            return found;
        }

        ScoutingReportTeamNotes created = new() { TeamId = teamId };
        _teams.Add(created);
        return created;
    }

    private void Commit()
    {
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        if (_file is null)
        {
            return;
        }

        string name = FileName;
        try
        {
            byte[]? bytes = _file.Read();
            bool fromLegacy = false;
            if (bytes is null && _legacy?.Read() is { } old)
            {
                bytes = old;
                name = LegacyFileName;
                fromLegacy = true;
            }

            if (bytes is null)
            {
                return;
            }

            ScoutingReportNotesFile? file =
                JsonSerializer.Deserialize<ScoutingReportNotesFile>(bytes, ScoutingReportNotesFile.JsonOptions);
            if (file is null || file.SchemaVersion > ScoutingReportNotesFile.CurrentSchema)
            {
                Refuse(
                    $"{name} is at schema {file?.SchemaVersion.ToString(CultureInfo.InvariantCulture) ?? "?"}, newer than this build reads");
                return;
            }

            bool remapped = file.SchemaVersion < ScoutingReportNotesFile.CurrentSchema;
            foreach (ScoutingReportTeamNotes team in file.Teams)
            {
                team.Edits = new Dictionary<string, string>(team.Edits, StringComparer.Ordinal);
                if (remapped)
                {
                    RemapKeys(team);
                }

                _teams.Add(team);
            }

            if (fromLegacy || remapped)
            {
                Save();
            }
        }
        catch (Exception ex)
        {
            Refuse($"{name} could not be read: {ex.Message}");
        }
    }

    /// <summary>
    ///     Moves a schema-1 team's stars, edits and dismissals onto the scoped keys
    ///     (<see cref="ScoutingReportStarKeys.Remap" />). A key with no mapping stays as it is; two old keys that
    ///     land on one new key keep the first one's edit.
    /// </summary>
    /// <param name="team">The team's notes, changed in place.</param>
    internal static void RemapKeys(ScoutingReportTeamNotes team)
    {
        ArgumentNullException.ThrowIfNull(team);
        team.Starred = [.. team.Starred.Select(Mapped).Distinct(StringComparer.Ordinal)];
        team.Hidden = [.. team.Hidden.Select(Mapped).Distinct(StringComparer.Ordinal)];
        Dictionary<string, string> edits = new(StringComparer.Ordinal);
        foreach ((string key, string text) in team.Edits)
        {
            edits.TryAdd(Mapped(key), text);
        }

        team.Edits = edits;
        return;

        static string Mapped(string key) => ScoutingReportStarKeys.Remap(key) ?? key;
    }

    /// <summary>Stars a suggested line on the user's behalf, once ever; false when it was seeded before.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="key">The suggested line's key.</param>
    public bool Seed(Guid teamId, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ScoutingReportTeamNotes notes = Ensure(teamId);
        if (notes.Seeded.Contains(key, StringComparer.Ordinal))
        {
            return false;
        }

        notes.Seeded.Add(key);
        Toggle(notes.Starred, key, true);
        Commit();
        return true;
    }

    private void Refuse(string problem)
    {
        _refused = true;
        FileProblem = problem;
        _teams.Clear();
    }

    private void Save()
    {
        if (_file is null || _refused)
        {
            return;
        }

        try
        {
            ScoutingReportNotesFile file = new() { Teams = [.. _teams] };
            _file.Write(JsonSerializer.SerializeToUtf8Bytes(file, ScoutingReportNotesFile.JsonOptions));
        }
        catch (Exception)
        {
            // Best effort: the in-memory notes stand for the session and the next mutation retries.
        }
    }
}
