#region

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using DemoViewer.NET.Playback2D.Pipeline.Review;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Transfer;

#endregion

namespace DemoViewer.NET.Services.Review;

/// <summary>
///     The Review Queue: one ordered list of clips from any demo, each <c>(demo, from, to, note)</c> with
///     a one-line question, grouped by title cards into sections. Every surface that finds moments puts
///     them here (the Reels tray, Result Cards, a Matrix cell's refs, a pick at the playhead) and the
///     Review tab walks them; the Reels tray is one client among those, reading back the clips it staged
///     (plan F8: the tray was already cross-demo, and this is its generalisation rather than a second
///     subsystem beside it).
///     <para>
///         <b>Threading.</b> UI thread only, like the Reels tray it replaced: every caller is a view
///         model reacting to a click, and <see cref="Changed" /> fires synchronously after each mutation
///         (or once at the end of a <see cref="Defer" /> scope) so a client re-reads a list that is
///         already final.
///     </para>
///     <para>
///         <b>Persistence.</b> <c>review-queue.json</c> under the config root, written whole through
///         <see cref="AtomicFile" />. The file is read on a worker at construction and the
///         first access waits for it; writes are debounced onto a worker, and <see cref="Flush" /> writes
///         what is pending (shutdown calls it). A real queue reached thousands of rows, so neither
///         runs on the UI thread. A null root keeps the queue for the session: the browser host and
///         tests. A file that cannot be read, or is at a newer schema, is refused and never overwritten.
///     </para>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The plan's name for the feature; it is a queue a reviewer walks, not a Queue<T>.")]
public sealed class ReviewQueue
{
    /// <summary>The file under the config root.</summary>
    public const string FileName = "review-queue.json";

    /// <summary>The title card imported clips go under when the bundle put them under none.</summary>
    public const string ImportedSectionTitle = "Imported";

    private readonly List<ReviewEntry> _loaded = [];
    private readonly LoadOnce _load;
    private readonly string? _path;
    private readonly CoalescedWriter<ReviewEntry[]> _writer;
    private int _deferDepth;
    private bool _deferredChange;
    private string? _fileProblem;
    private Dictionary<string, JsonElement>? _fileExtra;
    private bool _refused;

    /// <param name="configRoot">The app config root, or null for a session-only queue (the browser, tests).</param>
    /// <param name="saveDelay">How long a write waits for more mutations; 300 ms when null.</param>
    /// <param name="scheduleSave">Runs a save after the delay: a processing-queue item in the app, the pool when null.</param>
    /// <param name="scheduleLoad">Runs the file read: a processing-queue item in the app, the pool when null.</param>
    public ReviewQueue(string? configRoot, TimeSpan? saveDelay = null, Func<Action, Task>? scheduleSave = null,
        Func<Action, Task>? scheduleLoad = null)
    {
        _path = configRoot is null ? null : Path.Combine(configRoot, FileName);
        TimeSpan delay = saveDelay ?? TimeSpan.FromMilliseconds(300);
        Func<Action, Task> schedule = scheduleSave ?? Task.Run;
        _writer = new CoalescedWriter<ReviewEntry[]>(Write, _ => { },
            drain => Task.Delay(delay).ContinueWith(_ => schedule(drain), TaskScheduler.Default).Unwrap());
        _load = new LoadOnce(_path is null ? () => { } : Load);
        if (_path is null)
        {
            _load.Ensure();
        }
        else
        {
            _ = (scheduleLoad ?? Task.Run)(_load.Ensure);
        }
    }

    /// <summary>What the load's migration dropped or renamed, or null when it had nothing to do.</summary>
    public ReviewQueueMigrationResult? Migration
    {
        get
        {
            _ = List;
            return field;
        }
        private set;
    }

    /// <summary>Completes when the file has been read (or refused).</summary>
    public Task Loaded => _load.Completion;

    // Every member reads the list through here, so nothing sees it before the file is in.
    private List<ReviewEntry> List
    {
        get
        {
            // A caller that gets here before the queue has run the read does it now.
            _load.Ensure();
            return _loaded;
        }
    }

    /// <summary>True when nothing persists: the browser host, and tests without a root.</summary>
    public bool IsSessionOnly => _path is null;

    /// <summary>Why the file could not be read, or null. While set the file is never written.</summary>
    public string? FileProblem
    {
        get
        {
            _ = List;
            return _fileProblem;
        }
    }

    /// <summary>The queue in order, title cards and clips together. A copy: the caller may hold it.</summary>
    public IReadOnlyList<ReviewEntry> Entries => [.. List];

    /// <summary>The clips in order, without the title cards.</summary>
    public IReadOnlyList<ReviewEntry> Clips => [.. List.Where(e => e.Kind == ReviewEntryKind.Clip)];

    /// <summary>How many clips are queued.</summary>
    public int ClipCount => List.Count(e => e.Kind == ReviewEntryKind.Clip);

    /// <summary>How many queued clips nobody has marked reviewed.</summary>
    public int UnreviewedCount => List.Count(e => e is { Kind: ReviewEntryKind.Clip, Reviewed: false });

    /// <summary>Marks clips reviewed or not, in one change and one save; ids of title cards are ignored.</summary>
    /// <param name="ids">The clips.</param>
    /// <param name="reviewed">The mark.</param>
    /// <returns>How many clips changed.</returns>
    public int SetReviewed(IEnumerable<Guid> ids, bool reviewed)
    {
        ArgumentNullException.ThrowIfNull(ids);
        HashSet<Guid> wanted = [.. ids];
        int changed = 0;
        for (int i = 0; i < List.Count; i++)
        {
            ReviewEntry e = List[i];
            if (e.Kind == ReviewEntryKind.Clip && e.Reviewed != reviewed && wanted.Contains(e.Id))
            {
                List[i] = e with { Reviewed = reviewed };
                changed++;
            }
        }

        if (changed > 0)
        {
            Commit();
        }

        return changed;
    }

    /// <summary>The clips under a title card, up to the next card.</summary>
    /// <param name="cardId">The card.</param>
    public IReadOnlyList<ReviewEntry> ClipsUnder(Guid cardId)
    {
        int card = IndexOf(cardId);
        if (card < 0 || List[card].Kind != ReviewEntryKind.Section)
        {
            return [];
        }

        return List.GetRange(card + 1, SectionEnd(card) - card - 1);
    }

    /// <summary>Raised on the calling thread after every mutation that changed something.</summary>
    public event Action? Changed;

    /// <summary>
    ///     Holds <see cref="Changed" /> and the save until the returned scope is disposed, then raises one
    ///     change for everything inside it. Scopes nest; the outermost one raises.
    /// </summary>
    /// <returns>The scope.</returns>
    public IDisposable Defer()
    {
        _deferDepth++;
        return new DeferScope(this);
    }

    /// <summary>
    ///     Writes a pending save now, waiting at most <paramref name="timeout" /> for one in progress. Never
    ///     throws.
    /// </summary>
    /// <param name="timeout">The wait for a write in progress; infinite when null.</param>
    public void Flush(TimeSpan? timeout = null) => _writer.Flush(timeout);

    /// <summary>The entry with this id, or null.</summary>
    /// <param name="id">The entry's id.</param>
    public ReviewEntry? Find(Guid id) => List.FirstOrDefault(e => e.Id == id);

    /// <summary>
    ///     The title of the section a clip sits in: the nearest title card above it, or null when the
    ///     clip is above every card.
    /// </summary>
    /// <param name="id">The clip's id.</param>
    public string? SectionOf(Guid id)
    {
        int index = IndexOf(id);
        for (int i = index - 1; i >= 0; i--)
        {
            if (List[i].Kind == ReviewEntryKind.Section)
            {
                return List[i].Title;
            }
        }

        return null;
    }

    /// <summary>
    ///     Appends entries at the end, under a new title card when <paramref name="sectionTitle" /> is
    ///     given. A clip already queued (same demo, same range, same highlight) is skipped, so sending a
    ///     result set twice does not double it; a title card with no clip left after the skip is not
    ///     added either.
    /// </summary>
    /// <param name="entries">Clips and title cards, in the order they should appear.</param>
    /// <param name="sectionTitle">A title card to open the batch with, or null.</param>
    /// <param name="sectionSubtitle">The line under that title.</param>
    /// <returns>How many clips were added.</returns>
    public int Add(IEnumerable<ReviewEntry> entries, string? sectionTitle = null, string sectionSubtitle = "")
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<ReviewEntry> batch = [];
        HashSet<ReviewEntry> queued = ClipIndex();
        int clips = 0;
        foreach (ReviewEntry entry in entries)
        {
            if (entry.Kind == ReviewEntryKind.Clip)
            {
                if (!queued.Add(entry))
                {
                    continue;
                }

                clips++;
            }

            batch.Add(entry.Id == Guid.Empty ? entry with { Id = Guid.NewGuid() } : entry);
        }

        if (batch.Count == 0 || (clips == 0 && sectionTitle is not null))
        {
            return 0;
        }

        if (sectionTitle is not null)
        {
            List.Add(ReviewEntry.Section(sectionTitle, sectionSubtitle));
        }

        List.AddRange(batch);
        Commit();
        return clips;
    }

    /// <summary>
    ///     Puts clips in the section titled <paramref name="sectionTitle" />, after its last entry, opening
    ///     the section at the end when there is none. Later cards with the same title are folded into the
    ///     first, their entries moved after it. A clip already queued is skipped as <see cref="Add" /> does;
    ///     an existing clip <paramref name="supersedes" /> matches is replaced where it stands, keeping its
    ///     id and question.
    /// </summary>
    /// <param name="clips">The clips.</param>
    /// <param name="sectionTitle">The section's title card.</param>
    /// <param name="supersedes">(queued, incoming): true when the incoming clip replaces the queued one.</param>
    /// <returns>How many clips were added or replaced.</returns>
    public int Merge(IEnumerable<ReviewEntry> clips, string sectionTitle,
        Func<ReviewEntry, ReviewEntry, bool> supersedes)
    {
        ArgumentNullException.ThrowIfNull(supersedes);
        return Merge(clips, sectionTitle,
            () => clip => List.FindIndex(e => e.Kind == ReviewEntryKind.Clip && supersedes(e, clip)));
    }

    /// <summary>
    ///     <see cref="Merge(IEnumerable{ReviewEntry}, string, Func{ReviewEntry, ReviewEntry, bool})" /> for a
    ///     supersede rule that is key equality: a queued clip and an incoming one with the same non-null key
    ///     are the same clip. Looked up by hash, so a batch of thousands does not scan the queue per clip.
    /// </summary>
    /// <param name="clips">The clips.</param>
    /// <param name="sectionTitle">The section's title card.</param>
    /// <param name="supersedeKey">A clip's key, or null for a clip nothing supersedes.</param>
    /// <returns>How many clips were added or replaced.</returns>
    public int MergeByKey<TKey>(IEnumerable<ReviewEntry> clips, string sectionTitle,
        Func<ReviewEntry, TKey?> supersedeKey)
        where TKey : struct
    {
        ArgumentNullException.ThrowIfNull(supersedeKey);
        return Merge(clips, sectionTitle, () =>
        {
            Dictionary<TKey, int> at = [];
            for (int i = 0; i < List.Count; i++)
            {
                if (List[i].Kind == ReviewEntryKind.Clip && supersedeKey(List[i]) is { } key)
                {
                    at.TryAdd(key, i);
                }
            }

            return clip => supersedeKey(clip) is { } key && at.TryGetValue(key, out int index) ? index : -1;
        });
    }

    // `lookup` is built after the fold, which moves entries; replacements keep positions, so it stays valid.
    private int Merge(IEnumerable<ReviewEntry> clips, string sectionTitle, Func<Func<ReviewEntry, int>> lookup)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(sectionTitle);

        bool changed = FoldSections(sectionTitle);
        Func<ReviewEntry, int> supersededAt = lookup();
        HashSet<ReviewEntry> queued = ClipIndex();
        List<ReviewEntry> appended = [];
        int count = 0;
        foreach (ReviewEntry clip in clips)
        {
            if (clip.Kind != ReviewEntryKind.Clip || !queued.Add(clip))
            {
                continue;
            }

            int old = supersededAt(clip);
            if (old >= 0)
            {
                List[old] = clip with
                {
                    Id = List[old].Id, Question = List[old].Question, Reviewed = List[old].Reviewed
                };
            }
            else
            {
                appended.Add(clip.Id == Guid.Empty ? clip with { Id = Guid.NewGuid() } : clip);
            }

            count++;
        }

        if (appended.Count > 0)
        {
            int card = List.FindIndex(e => IsCard(e, sectionTitle));
            if (card < 0)
            {
                List.Add(ReviewEntry.Section(sectionTitle));
                List.AddRange(appended);
            }
            else
            {
                List.InsertRange(SectionEnd(card), appended);
            }
        }

        if (changed || count > 0)
        {
            Commit();
        }

        return count;
    }

    /// <summary>
    ///     Brings in the clips of a transfer bundle's queue. Each clip is joined by its demo's content hash: its
    ///     path is this machine's copy of that demo (<paramref name="localPathOf" />), or empty until one is found,
    ///     and a clip without a hash is skipped. A clip already queued here for the same demo, range and highlight
    ///     is kept under <see cref="ConflictPolicy.KeepMine" />, replaced in place under
    ///     <see cref="ConflictPolicy.TakeImported" />, and under <see cref="ConflictPolicy.KeepBoth" /> joined by
    ///     the imported one when their note or question differ. New clips go under the bundle's title cards,
    ///     appended to a section of the same title here. One change and one save.
    /// </summary>
    /// <param name="entries">The bundle's entries, title cards and clips, in order.</param>
    /// <param name="policy">What a clip queued on both sides keeps.</param>
    /// <param name="localPathOf">A content hash's path on this machine, or null.</param>
    /// <param name="shaOfPath">The content hash of a queued clip's path when the clip carries none, or null.</param>
    /// <returns>What happened.</returns>
    public ReviewImportResult ImportClips(IReadOnlyList<ReviewEntry> entries, ConflictPolicy policy,
        Func<string, string?>? localPathOf = null, Func<string, string?>? shaOfPath = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int added = 0, replaced = 0, kept = 0;
        string section = ImportedSectionTitle;
        List<(string Section, ReviewEntry Clip)> appended = [];
        foreach (ReviewEntry entry in entries)
        {
            if (entry.Kind == ReviewEntryKind.Section)
            {
                section = string.IsNullOrWhiteSpace(entry.Title) ? ImportedSectionTitle : OneLine(entry.Title);
                continue;
            }

            if (entry.Sha256 is not { Length: > 0 } hash)
            {
                continue;
            }

            string sha = hash.ToLowerInvariant();
            ReviewEntry clip = entry with
            {
                Id = Guid.NewGuid(), Sha256 = sha, DemoPath = localPathOf?.Invoke(sha) ?? ""
            };
            int at = List.FindIndex(e => e.Kind == ReviewEntryKind.Clip && e.FromTick == clip.FromTick
                && e.ToTick == clip.ToTick && Equals(e.Highlight, clip.Highlight)
                && string.Equals(e.Sha256 ?? shaOfPath?.Invoke(e.DemoPath), sha, StringComparison.OrdinalIgnoreCase));
            if (at < 0 && appended.Any(a => SameClip.Instance.Equals(a.Clip, clip)))
            {
                kept++;
            }
            else if (at < 0)
            {
                appended.Add((section, clip));
                added++;
            }
            else if (policy == ConflictPolicy.TakeImported)
            {
                List[at] = clip with { Id = List[at].Id, DemoPath = List[at].DemoPath };
                replaced++;
            }
            else if (policy == ConflictPolicy.KeepBoth && (!string.Equals(List[at].Note, clip.Note, StringComparison.Ordinal)
                         || !string.Equals(List[at].Question, clip.Question, StringComparison.Ordinal)))
            {
                appended.Add((section, clip));
                added++;
            }
            else
            {
                kept++;
            }
        }

        foreach (IGrouping<string, (string Section, ReviewEntry Clip)> group in appended.GroupBy(a => a.Section))
        {
            int card = List.FindIndex(e => IsCard(e, group.Key));
            if (card < 0)
            {
                List.Add(ReviewEntry.Section(group.Key));
                List.AddRange(group.Select(g => g.Clip));
            }
            else
            {
                List.InsertRange(SectionEnd(card), group.Select(g => g.Clip));
            }
        }

        if (added + replaced > 0)
        {
            Commit();
        }

        return new ReviewImportResult(added, replaced, kept);
    }

    /// <summary>Puts a title card at <paramref name="index" />, clamped; at the end when null.</summary>
    /// <param name="title">The card's title.</param>
    /// <param name="index">Where it goes, or null for the end.</param>
    /// <returns>The card.</returns>
    public ReviewEntry AddSection(string title, int? index = null)
    {
        ReviewEntry card = ReviewEntry.Section(title);
        List.Insert(Math.Clamp(index ?? List.Count, 0, List.Count), card);
        Commit();
        return card;
    }

    /// <summary>Removes entries by id; ids not in the queue are ignored.</summary>
    /// <param name="ids">The entries to remove.</param>
    /// <returns>How many were removed.</returns>
    public int Remove(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        HashSet<Guid> doomed = [.. ids];
        int removed = List.RemoveAll(e => doomed.Contains(e.Id));
        if (removed > 0)
        {
            Commit();
        }

        return removed;
    }

    /// <summary>Empties the queue.</summary>
    public void Clear()
    {
        if (List.Count == 0)
        {
            return;
        }

        List.Clear();
        Commit();
    }

    /// <summary>Moves one entry by <paramref name="delta" /> places, clamped to the queue.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="delta">Signed places; negative is up.</param>
    public void Move(Guid id, int delta)
    {
        int from = IndexOf(id);
        if (from >= 0)
        {
            MoveTo(id, from + delta);
        }
    }

    /// <summary>Moves one entry to an absolute position, clamped to the queue.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="index">Zero-based destination.</param>
    public void MoveTo(Guid id, int index)
    {
        int from = IndexOf(id);
        if (from < 0)
        {
            return;
        }

        int to = Math.Clamp(index, 0, List.Count - 1);
        if (to == from)
        {
            return;
        }

        ReviewEntry entry = List[from];
        List.RemoveAt(from);
        List.Insert(to, entry);
        Commit();
    }

    /// <summary>
    ///     Re-orders a subset of the queue among the positions it already holds: the entries named keep
    ///     their slots as a set and take the given order within them, and every other entry stays where
    ///     it is. This is how a client that sees only its own clips (the Reels tray reordering its
    ///     groups) moves them without disturbing what other surfaces queued around them.
    /// </summary>
    /// <param name="orderedIds">The subset in its new order; ids not in the queue are ignored.</param>
    public void ReorderSubset(IReadOnlyList<Guid> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        List<ReviewEntry> ordered = [.. orderedIds.Select(Find).OfType<ReviewEntry>().Distinct()];
        HashSet<Guid> members = [.. ordered.Select(e => e.Id)];
        List<int> slots = [.. Enumerable.Range(0, List.Count).Where(i => members.Contains(List[i].Id))];
        bool changed = false;
        for (int i = 0; i < slots.Count; i++)
        {
            if (List[slots[i]].Id != ordered[i].Id)
            {
                List[slots[i]] = ordered[i];
                changed = true;
            }
        }

        if (changed)
        {
            Commit();
        }
    }

    /// <summary>Replaces one entry through an edit; the id and kind are kept whatever the edit returns.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="edit">The new value from the old.</param>
    /// <returns>False when the id is not in the queue.</returns>
    private bool Update(Guid id, Func<ReviewEntry, ReviewEntry> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        int index = IndexOf(id);
        if (index < 0)
        {
            return false;
        }

        ReviewEntry old = List[index];
        ReviewEntry updated = edit(old) with { Id = old.Id, Kind = old.Kind };
        if (updated == old)
        {
            return true;
        }

        List[index] = updated;
        Commit();
        return true;
    }

    /// <summary>Sets a clip's one-line question.</summary>
    /// <param name="id">The clip.</param>
    /// <param name="question">The question; trimmed, and one line.</param>
    public bool SetQuestion(Guid id, string question) =>
        Update(id, e => e with { Question = OneLine(question) });

    /// <summary>Sets a clip's note, or a title card's subtitle.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="note">The note; trimmed, and one line.</param>
    public bool SetNote(Guid id, string note) => Update(id, e => e with { Note = OneLine(note) });

    /// <summary>Renames a title card.</summary>
    /// <param name="id">The card.</param>
    /// <param name="title">The title; trimmed, and one line.</param>
    public bool SetTitle(Guid id, string title) => Update(id, e => e with { Title = OneLine(title) });

    private HashSet<ReviewEntry> ClipIndex()
    {
        HashSet<ReviewEntry> index = new(SameClip.Instance);
        foreach (ReviewEntry e in List)
        {
            if (e.Kind == ReviewEntryKind.Clip)
            {
                index.Add(e);
            }
        }

        return index;
    }

    // Two clips are the same when a second send could only duplicate the first: the demo (paths compare
    // the way the library does, case-insensitively), the range and the highlight identity. The note and
    // the question are not identity; a reviewer may have written one already. An imported clip whose demo
    // is not on this machine yet has no path, so two such clips are one demo only by their hash.
    private sealed class SameClip : IEqualityComparer<ReviewEntry>
    {
        public static readonly SameClip Instance = new();

        public bool Equals(ReviewEntry? x, ReviewEntry? y) =>
            x is { Kind: ReviewEntryKind.Clip } && y is { Kind: ReviewEntryKind.Clip }
            && x.FromTick == y.FromTick && x.ToTick == y.ToTick
            && SameDemo(x, y)
            && Equals(x.Highlight, y.Highlight);

        // The path is left out: two clips one hash makes the same demo may name different paths.
        public int GetHashCode(ReviewEntry obj) =>
            HashCode.Combine(obj.Kind, obj.FromTick, obj.ToTick, obj.Highlight);

        private static bool SameDemo(ReviewEntry x, ReviewEntry y) =>
            x.DemoPath.Length > 0 && y.DemoPath.Length > 0
                ? string.Equals(x.DemoPath, y.DemoPath, StringComparison.OrdinalIgnoreCase)
                : x.Sha256 is { Length: > 0 } a && string.Equals(a, y.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DeferScope(ReviewQueue queue) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            if (--queue._deferDepth == 0 && queue._deferredChange)
            {
                queue._deferredChange = false;
                queue.Commit();
            }
        }
    }

    private static bool IsCard(ReviewEntry e, string title) =>
        e.Kind == ReviewEntryKind.Section && string.Equals(e.Title, title, StringComparison.Ordinal);

    // The index after the last entry of the section whose card is at `card`.
    private int SectionEnd(int card)
    {
        int next = List.FindIndex(card + 1, e => e.Kind == ReviewEntryKind.Section);
        return next < 0 ? List.Count : next;
    }

    // Moves every later same-titled section's entries under the first card and drops the later cards.
    private bool FoldSections(string title)
    {
        int first = List.FindIndex(e => IsCard(e, title));
        if (first < 0)
        {
            return false;
        }

        bool changed = false;
        int later;
        while ((later = List.FindIndex(first + 1, e => IsCard(e, title))) >= 0)
        {
            int end = SectionEnd(later);
            List<ReviewEntry> moved = List.GetRange(later + 1, end - later - 1);
            List.RemoveRange(later, end - later);
            List.InsertRange(SectionEnd(first), moved);
            changed = true;
        }

        return changed;
    }

    private static string OneLine(string? text) =>
        (text ?? "").ReplaceLineEndings(" ").Trim();

    private int IndexOf(Guid id) => List.FindIndex(e => e.Id == id);

    private void Commit()
    {
        if (_deferDepth > 0)
        {
            _deferredChange = true;
            return;
        }

        Save();
        Changed?.Invoke();
    }

    // ── The file ──────────────────────────────────────────────────────────────

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            ReviewQueueFile? file =
                JsonSerializer.Deserialize<ReviewQueueFile>(File.ReadAllText(_path), ReviewQueueFile.JsonOptions);
            if (file is null || file.SchemaVersion > ReviewQueueFile.CurrentSchema)
            {
                Refuse(
                    $"{FileName} is at schema {file?.SchemaVersion.ToString(CultureInfo.InvariantCulture) ?? "?"}, newer than this build reads");
                return;
            }

            _fileExtra = file.Extra;
            IReadOnlyList<ReviewEntry> entries =
                ReviewQueueMigration.MigrateFile(_path, file, out ReviewQueueMigrationResult? migrated);
            Migration = migrated;

            // An entry with no id cannot be named by any mutation; give it one rather than drop it.
            _loaded.AddRange(entries.Select(e => e.Id == Guid.Empty ? e with { Id = Guid.NewGuid() } : e));
            if (migrated is { Written: false })
            {
                // The backup is taken; write the migrated set the way any save goes.
                QueueWrite([.. _loaded]);
            }
        }
        catch (Exception ex)
        {
            Refuse($"{FileName} could not be read: {ex.Message}");
        }
    }

    private void Refuse(string problem)
    {
        _refused = true;
        _fileProblem = problem;
        _loaded.Clear();
    }

    private void Save()
    {
        if (_path is null || _refused)
        {
            return;
        }

        // Entries are immutable records, so the array is a consistent snapshot for the worker.
        QueueWrite([.. List]);
    }

    private void QueueWrite(ReviewEntry[] snapshot) => _writer.Post(snapshot);

    // A failure is dropped: the in-memory queue stands for the session and the next mutation retries.
    private void Write(ReviewEntry[] snapshot)
    {
        ReviewQueueFile file = new() { Entries = [.. snapshot], Extra = _fileExtra };
        AtomicFile.WriteAllText(_path!, JsonSerializer.Serialize(file, ReviewQueueFile.JsonOptions));
    }
}

/// <summary>What <see cref="ReviewQueue.ImportClips" /> did.</summary>
/// <param name="Added">Clips added.</param>
/// <param name="Replaced">Queued clips replaced by the imported one.</param>
/// <param name="Kept">Imported clips left out because the queue already had them.</param>
public sealed record ReviewImportResult(int Added, int Replaced, int Kept);
