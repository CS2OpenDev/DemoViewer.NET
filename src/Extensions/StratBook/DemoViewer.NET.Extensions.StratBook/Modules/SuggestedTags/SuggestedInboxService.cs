namespace DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;

/// <summary>One proposal as the library-wide Suggested section lists it.</summary>
/// <param name="DemoPath">The demo it was made on.</param>
/// <param name="Sha256">The hash its verdicts are keyed by.</param>
/// <param name="FileName">The demo's file name.</param>
/// <param name="Map">The demo's map, or null when the cache does not know it.</param>
/// <param name="ModifiedTicks">The demo file's time, newest first in the list.</param>
/// <param name="Entry">The proposal and its verdict.</param>
public sealed record SuggestedInboxItem(
    string DemoPath,
    string? Sha256,
    string FileName,
    string? Map,
    long ModifiedTicks,
    ProposalEntry Entry);

/// <summary>
///     Every demo's Suggested Tags proposals in one list, for the Strat Book's Suggested section. The first load reads
///     each built demo's proposals and verdicts as one queue item; after that a demo whose suggestions change is read
///     again on its own. Verdicts go through <see cref="SuggestedTagsService" />, the same path Review mode uses.
/// </summary>
public sealed class SuggestedInboxService : IDisposable
{
    private readonly IExtensionLibrary _library;
    private readonly Lock _gate = new();
    private readonly Action<Action> _post;
    private readonly IExtensionJobs? _jobs;
    private readonly Func<Action, Task> _run;
    private readonly SuggestedTagsService _suggestions;
    private Dictionary<string, IReadOnlyList<SuggestedInboxItem>> _byDemo = new(StringComparer.OrdinalIgnoreCase);

    // Demos whose suggestions changed while a library read ran, which may have read them before the change.
    // Under _gate; null when no library read is running.
    private HashSet<string>? _changedWhileReading;

    // The list each Publish took, numbered under _gate, and the newest number shown. A worker posts after it
    // lets go of the gate, so two of them can post in the other order to the one they took their lists in.
    private long _published;
    private long _shown;

    private volatile bool _isLoaded;
    private Task? _loading;

    /// <param name="suggestions">The engine: proposals, verdicts, accept, dismiss and restore.</param>
    /// <param name="library">The library: the maps and write times of the demos with proposals.</param>
    /// <param name="jobs">Where the library read runs; null runs it on <paramref name="run" />.</param>
    /// <param name="run">Runs work off the UI thread; defaults to <see cref="Task.Run(Action)" />.</param>
    /// <param name="post">UI-thread marshal for <see cref="Changed" />.</param>
    public SuggestedInboxService(SuggestedTagsService suggestions, IExtensionLibrary library,
        IExtensionJobs? jobs = null,
        Func<Action, Task>? run = null, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        ArgumentNullException.ThrowIfNull(library);
        _suggestions = suggestions;
        _library = library;
        _jobs = jobs;
        _run = run ?? Task.Run;
        _post = post ?? (a => a());
        _suggestions.Changed += OnSuggestionsChanged;
    }

    /// <summary>Every loaded proposal, newest demo first, then round and trigger order.</summary>
    public IReadOnlyList<SuggestedInboxItem> Items { get; private set; } = [];

    /// <summary>True once the first library read finished.</summary>
    public bool IsLoaded => _isLoaded;

    public bool IsLoading
    {
        get
        {
            lock (_gate)
            {
                return _loading is { IsCompleted: false };
            }
        }
    }

    /// <summary>Pending proposals across the library, from the proposals' stamps alone: the rail badge.</summary>
    public int PendingCount => _suggestions.Proposals.PendingTotal();

    /// <summary>Raised on the UI thread when <see cref="Items" /> changed.</summary>
    public event Action? Changed;

    public void Dispose() => _suggestions.Changed -= OnSuggestionsChanged;

    /// <summary>Reads every demo with built proposals, as one user-requested queue item. A call while one runs joins it.</summary>
    public Task LoadAsync()
    {
        lock (_gate)
        {
            if (_loading is { IsCompleted: false } running)
            {
                return running;
            }

            _loading = _jobs?.Enqueue(new JobRequest("Suggested tags: library", _ =>
                    {
                        ReadAll();
                        return Task.CompletedTask;
                    }, new JobOptions(StratBookJobKinds.SuggestionsInbox, JobPriority.UserRequested, "suggested-inbox")))
                    .Completion
                ?? _run(ReadAll);
            return _loading;
        }
    }

    /// <summary>Accepts as proposed into that demo's tag document. False when it is not pending or was not written.</summary>
    public Task<bool> AcceptAsync(SuggestedInboxItem item) =>
        VerdictAsync("Suggested tags: accept", item,
            () => _suggestions.Accept(item.DemoPath, item.Entry.Proposal.Id, null, item.Sha256));

    /// <summary>Dismisses: the proposal is not offered again until restored.</summary>
    public Task<bool> DismissAsync(SuggestedInboxItem item) =>
        VerdictAsync("Suggested tags: dismiss", item,
            () => _suggestions.Reject(item.DemoPath, item.Entry.Proposal.Id, item.Sha256));

    /// <summary>Offers a dismissed proposal again.</summary>
    public Task<bool> RestoreAsync(SuggestedInboxItem item) =>
        VerdictAsync("Suggested tags: restore", item,
            () => _suggestions.Restore(item.DemoPath, item.Entry.Proposal.Id, item.Sha256));

    // A verdict reads and writes the demo's proposals, verdicts and tags, so it runs as a queue item the user
    // asked for, never on the UI thread.
    private async Task<bool> VerdictAsync(string title, SuggestedInboxItem item, Func<bool> write)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool written = false;

        void Work()
        {
            written = write();
            if (written)
            {
                ReadDemo(item.DemoPath);
                Publish();
            }
        }

        if (_jobs is null)
        {
            await _run(Work).ConfigureAwait(true);
        }
        else
        {
            await _jobs.RunAsync(title, _ =>
            {
                Work();
                return Task.CompletedTask;
            }, new JobOptions(StratBookJobKinds.SuggestionsInbox, JobPriority.UserRequested)).ConfigureAwait(true);
        }

        return written;
    }

    private void ReadAll()
    {
        lock (_gate)
        {
            _changedWhileReading ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, IReadOnlyList<SuggestedInboxItem>> all = new(StringComparer.OrdinalIgnoreCase);
        foreach (DemoDataStamp stamp in _suggestions.Proposals.Stamps().Where(s => s.Fingerprint is not null))
        {
            if (_library.Find(stamp.DemoPath) is { } row)
            {
                all[row.FilePath] = Read(row);
            }
        }

        HashSet<string> changed;
        lock (_gate)
        {
            _byDemo = all;
            changed = _changedWhileReading ?? [];
            _changedWhileReading = null;
            _isLoaded = true;
        }

        // After the wholesale assignment, or it would discard these; a change from here on is read on its own.
        foreach (string path in changed)
        {
            ReadDemo(path);
        }

        Publish();
    }

    private void ReadDemo(string path)
    {
        if (_library.Find(path) is not { } row)
        {
            return;
        }

        IReadOnlyList<SuggestedInboxItem> items = Read(row);
        lock (_gate)
        {
            Dictionary<string, IReadOnlyList<SuggestedInboxItem>> next = new(_byDemo, StringComparer.OrdinalIgnoreCase)
            {
                [path] = items
            };
            _byDemo = next;
        }
    }

    private IReadOnlyList<SuggestedInboxItem> Read(LibraryDemo row)
    {
        ProposalSet set = _suggestions.Load(row.FilePath, row.Sha256);
        string fileName = Path.GetFileName(row.FilePath);
        return
        [
            .. set.Entries.Select(e =>
                new SuggestedInboxItem(row.FilePath, set.Sha256, fileName, row.MapName, row.Modified.Ticks, e))
        ];
    }

    private void Publish()
    {
        List<SuggestedInboxItem> items;
        long number;
        lock (_gate)
        {
            items =
            [
                .. _byDemo.Values.SelectMany(v => v)
                    .OrderByDescending(i => i.ModifiedTicks)
                    .ThenBy(i => i.DemoPath, StringComparer.Ordinal)
                    .ThenBy(i => i.Entry.Proposal.Round)
                    .ThenBy(i => i.Entry.Proposal.TriggerTick)
            ];
            number = ++_published;
        }

        _post(() =>
        {
            lock (_gate)
            {
                // A list taken before the one already shown: showing it would put a verdict back to pending.
                if (number < _shown)
                {
                    return;
                }

                _shown = number;
            }

            Items = items;
            Changed?.Invoke();
        });
    }

    // Another surface (Review mode, the background sweep) changed a demo: read it again off the UI thread.
    private void OnSuggestionsChanged(string path)
    {
        lock (_gate)
        {
            // A library read running may already have this demo, and replaces what a read beside it would
            // write, so it re-reads the demo when it lands. Before the first read nothing is shown.
            if (_changedWhileReading is { } pending)
            {
                pending.Add(path);
                return;
            }

            if (!_isLoaded)
            {
                return;
            }
        }

        _ = _run(() =>
        {
            ReadDemo(path);
            Publish();
        });
    }
}
