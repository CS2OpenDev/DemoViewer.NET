namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>What applying an edit to the open document did.</summary>
public enum EditOutcomeKind
{
    /// <summary>The transform changed the text and the file now holds it.</summary>
    Written,

    /// <summary>The transform left the text as it was; nothing was written and the history is untouched.</summary>
    Unchanged,

    /// <summary>Nothing was written: no document is open, it is not the user's, the transform threw, or the disk refused.</summary>
    Refused
}

/// <summary>The outcome of one edit, with the reason when it was refused.</summary>
public readonly record struct EditOutcome(EditOutcomeKind Kind, string? Message)
{
    public static EditOutcome Written { get; } = new(EditOutcomeKind.Written, null);

    public static EditOutcome Unchanged { get; } = new(EditOutcomeKind.Unchanged, null);

    public static EditOutcome Refused(string? message) => new(EditOutcomeKind.Refused, message);

    public bool IsWritten => Kind == EditOutcomeKind.Written;

    public bool IsRefused => Kind == EditOutcomeKind.Refused;
}

/// <summary>
///     The open ruleset file: its entry, its text as last read or written, the write time that text was
///     read at, and the undo and redo history of the edits made here. Every write goes through
///     <see cref="Apply(Func{string,string},string)" />, which is the one place that touches the disk, and it
///     refuses anything that is not a file in the user's rules directory, undo, redo and a save included.
///     The history belongs to the path: opening another path clears it, re-opening the same path keeps it.
/// </summary>
public sealed class RulesetDocumentSession
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();

    /// <summary>The entry open, or null.</summary>
    public RulesetCorpus.Entry? Entry { get; private set; }

    /// <summary>The file's text as last read or written; empty when nothing is open or it could not be read.</summary>
    public string Text { get; private set; } = "";

    /// <summary>The file's write time when <see cref="Text" /> was taken; <see cref="DateTime.MinValue" /> when unknown.</summary>
    public DateTime LoadedWriteTimeUtc { get; private set; }

    /// <summary>Whether the open entry is one of the user's own files, which is the only kind written.</summary>
    public bool IsUserFile => Entry?.Source == "user";

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Whether another writer touched the file since its text was taken.</summary>
    public bool ChangedOnDisk => Entry is { } entry && WriteTimeOf(entry.Path) != LoadedWriteTimeUtc;

    /// <summary>
    ///     Opens <paramref name="entry" />, reading its text from disk. The history survives when the path is
    ///     the one already open and the file still holds what this session last read or wrote, which a
    ///     reload after a write hands back as a fresh entry; another path, none, or the same path after
    ///     another writer touched it clears it.
    /// </summary>
    public void Open(RulesetCorpus.Entry? entry)
    {
        // A history over another writer's text would put it back on the next undo.
        bool samePath = string.Equals(entry?.Path, Entry?.Path, StringComparison.Ordinal);
        if (!samePath || ChangedOnDisk)
        {
            _undo.Clear();
            _redo.Clear();
        }

        Entry = entry;
        Text = "";
        LoadedWriteTimeUtc = DateTime.MinValue;
        if (entry is null)
        {
            return;
        }

        try
        {
            Text = File.ReadAllText(entry.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Text = "";
        }

        LoadedWriteTimeUtc = WriteTimeOf(entry.Path);
    }

    /// <summary>
    ///     Runs <paramref name="transform" /> over the text and writes the result when it differs. A transform
    ///     that throws on the document's shape is reported as refused with the writer's reason, and a document
    ///     that is not the user's is refused outright.
    /// </summary>
    public EditOutcome Apply(Func<string, string> transform, string description)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (Entry is null || !IsUserFile)
        {
            return EditOutcome.Refused(null);
        }

        string before = Text;
        string after;
        try
        {
            after = transform(before);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or YamlDotNet.Core.YamlException or ArgumentOutOfRangeException)
        {
            return EditOutcome.Refused($"Could not {description}: {ex.Message}");
        }

        if (after == before)
        {
            return EditOutcome.Unchanged;
        }

        EditOutcome outcome = Write(after);
        if (outcome.IsWritten)
        {
            _undo.Push(before);
            _redo.Clear();
        }

        return outcome;
    }

    /// <summary>Applies <paramref name="request" />, or reports its refusal without touching the file.</summary>
    public EditOutcome Apply(EditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Transform is { } transform ? Apply(transform, request.Description) : EditOutcome.Refused(request.Refusal);
    }

    /// <summary>
    ///     Writes <paramref name="text" /> as the whole document, as the source pane's Save does, after
    ///     checking it still loads as the open ruleset: a text that does not would take the file out of the
    ///     picker and leave it broken on disk.
    /// </summary>
    public EditOutcome Save(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Entry is not { } entry || !IsUserFile)
        {
            return EditOutcome.Refused(null);
        }

        if (RulesetCorpus.WhyNotLoadable(text, entry.Path, entry.Id) is { } reason)
        {
            return EditOutcome.Refused("Not saved: " + reason);
        }

        return Apply(_ => text, "save");
    }

    /// <summary>Puts the text before the last edit back. The text undone becomes the next redo.</summary>
    public EditOutcome Undo()
    {
        if (_undo.Count == 0 || Entry is null || !IsUserFile)
        {
            return EditOutcome.Refused(null);
        }

        string current = Text;
        string previous = _undo.Pop();
        EditOutcome outcome = Write(previous);
        if (outcome.IsWritten)
        {
            _redo.Push(current);
        }
        else
        {
            _undo.Push(previous);
        }

        return outcome;
    }

    /// <summary>Puts the last undone text back.</summary>
    public EditOutcome Redo()
    {
        if (_redo.Count == 0 || Entry is null || !IsUserFile)
        {
            return EditOutcome.Refused(null);
        }

        string current = Text;
        string next = _redo.Pop();
        EditOutcome outcome = Write(next);
        if (outcome.IsWritten)
        {
            _undo.Push(current);
        }
        else
        {
            _redo.Push(next);
        }

        return outcome;
    }

    /// <summary>The one message every write path answers with when the file on disk is not the text it was read as.</summary>
    public const string ChangedOnDiskMessage = "The file changed on disk since it was read; reload it before editing.";

    // Atomic on disk: the whole text lands in a sibling and moves into place, so a reader never sees half.
    // The write time is checked first so undo and redo cannot put a history over another writer's text.
    private EditOutcome Write(string text)
    {
        if (Entry is not { } entry || !IsUserFile)
        {
            return EditOutcome.Refused(null);
        }

        if (!File.Exists(entry.Path))
        {
            return EditOutcome.Refused($"{Path.GetFileName(entry.Path)} is no longer on disk; nothing was written.");
        }

        if (ChangedOnDisk)
        {
            return EditOutcome.Refused(ChangedOnDiskMessage);
        }

        try
        {
            string temp = entry.Path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, entry.Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EditOutcome.Refused("The file could not be written: " + ex.Message);
        }

        Text = text;
        LoadedWriteTimeUtc = WriteTimeOf(entry.Path);
        return EditOutcome.Written;
    }

    private static DateTime WriteTimeOf(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
