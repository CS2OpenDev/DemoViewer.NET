namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     Watches one rules directory for <c>*.rules.yaml</c> changes and reports them in batches: the paths
///     an editor, the Workbench or the canvas itself touched, debounced so one save (a temp file, a move,
///     a second write) arrives as one notice, and handed to the marshal the owner gave so the report
///     runs on the owner's thread. Who wrote, and whether a file appeared or went, is the owner's call:
///     it has the corpus and the open file's write time, the watcher has only paths. Watching is best
///     effort; a directory the platform cannot watch leaves <see cref="IsWatching" /> false.
/// </summary>
public sealed class RulesWatcher : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    private readonly Func<Action, Task> _marshal;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _batch = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <param name="directory">The directory to watch.</param>
    /// <param name="marshal">Runs the report where the owner wants it; a test runs it inline.</param>
    /// <param name="debounce">How long after the last event a batch waits; <see cref="DefaultDebounce" /> when null.</param>
    public RulesWatcher(string directory, Func<Action, Task> marshal, TimeSpan? debounce = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
        _marshal = marshal ?? throw new ArgumentNullException(nameof(marshal));
        _debounce = debounce ?? DefaultDebounce;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _watcher = new FileSystemWatcher(directory, "*.rules.yaml")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnRenamed;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _watcher = null;
        }
    }

    public string Directory { get; }

    /// <summary>Whether the platform is delivering events; false means only <see cref="Notify" /> reaches <see cref="Changed" />.</summary>
    public bool IsWatching => _watcher is not null;

    /// <summary>The paths touched since the last report, raised through the marshal once the directory has been quiet for the debounce.</summary>
    public event Action<IReadOnlyList<string>>? Changed;

    /// <summary>Records <paramref name="path" /> as touched, as the platform does; a test drives the watcher through this.</summary>
    public void Notify(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _batch.Add(path);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _batch.Clear();
        }

        _timer.Dispose();
        if (_watcher is { } watcher)
        {
            _watcher = null;
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private void OnEvent(object? sender, FileSystemEventArgs e) => Notify(e.FullPath);

    // The old name went and the new one came; the atomic write's move lands here as a temp name becoming the file.
    private void OnRenamed(object? sender, RenamedEventArgs e)
    {
        if (e.OldFullPath.EndsWith(".rules.yaml", StringComparison.OrdinalIgnoreCase))
        {
            Notify(e.OldFullPath);
        }

        Notify(e.FullPath);
    }

    private void Flush()
    {
        List<string> paths;
        lock (_gate)
        {
            if (_disposed || _batch.Count == 0)
            {
                return;
            }

            paths = [.. _batch];
            _batch.Clear();
        }

        _ = _marshal(() => Changed?.Invoke(paths));
    }
}
