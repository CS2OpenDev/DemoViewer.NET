#region

using System.Text.Json;
using DemoViewer.NET.Models;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     Best-effort, <em>per-demo</em> disk persistence for <see cref="Bookmark" />s.
///     <para>
///         File I/O does NOT work in the WASM/browser sandbox, so every method short-circuits when
///         <see cref="System.OperatingSystem.IsBrowser()" /> is true: bookmarks stay in-memory there.
///         A runtime check is used rather than a <c>#if BROWSER</c> define because the same
///         <c>DemoViewer.NET</c> assembly is compiled once and shared by both the desktop and browser
///         hosts; the define would need a csproj flag and would silently no-op in the wrong host.
///     </para>
///     <para>
///         Desktop persists to <c>%AppData%/DemoViewer.NET/Bookmarks.v2.json</c>: a map of <em>demo content
///         key</em> (lowercase hex SHA-256 of the <c>.dem</c> bytes, as <see cref="GraphBreakpointStore" />
///         keys its file) → that demo's bookmarks. A bookmark is a frame index, which says nothing outside
///         the demo it was set on. The file before this one, <c>SessionState.json</c>, was one list shared by
///         every demo; it cannot be told which demo each entry belonged to, so it is never written or moved. It is
///         read only to count its entries for a one-time notice (<see cref="LegacyCount" />), and a dismissal of
///         that notice is kept as a marker file beside it.
///     </para>
/// </summary>
public sealed class BookmarkStore
{
    private const string LegacyFileName = "SessionState.json";
    private const string LegacyNoticeDismissedFileName = "SessionState.json.notice-dismissed";

    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    private readonly string? _path;

    /// <summary>Initializes a new <see cref="BookmarkStore" /> instance.</summary>
    public BookmarkStore()
    {
        if (OperatingSystem.IsBrowser())
        {
            return; // no filesystem on WASM
        }

        _path = AppPaths.BookmarksFile;
    }

    /// <summary>A store over <paramref name="path" />: for tests.</summary>
    /// <param name="path">The bookmarks file, or null for none.</param>
    internal BookmarkStore(string? path)
    {
        _path = path;
    }

    /// <summary>The bookmarks persisted for <paramref name="demoKey" />, or an empty list if none / unavailable.</summary>
    public List<Bookmark> Load(string demoKey) =>
        ReadAll().TryGetValue(demoKey, out List<Bookmark>? list) ? list : [];

    /// <summary>
    ///     Persists <paramref name="bookmarks" /> under <paramref name="demoKey" />, leaving every other demo's
    ///     entry untouched. An empty list removes the key. No-op on WASM or on I/O failure.
    /// </summary>
    public void Save(string demoKey, IReadOnlyList<Bookmark> bookmarks)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            // A file that is there but did not read is moved aside before anything is written: it was read as
            // empty, so writing over it would replace every demo's bookmarks with the one list in hand. When
            // it cannot be moved the throw ends the save.
            if (!TryReadAll(out Dictionary<string, List<Bookmark>> all))
            {
                File.Move(_path, $"{_path}.unreadable-{DateTime.UtcNow.Ticks}");
            }

            if (bookmarks.Count > 0)
            {
                all[demoKey] = [.. bookmarks];
            }
            else
            {
                all.Remove(demoKey);
            }

            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(all, _writeOptions));
        }
        catch
        {
            // Persistence is best-effort; never crash the app on a write failure.
        }
    }

    /// <summary>
    ///     How many bookmarks the shared pre-v2 <c>SessionState.json</c> holds, or 0 when there is none, it does not
    ///     read, or its notice was dismissed. The file is only read.
    /// </summary>
    public int LegacyCount()
    {
        string? dir = _path is null ? null : Path.GetDirectoryName(_path);
        if (dir is null || File.Exists(Path.Combine(dir, LegacyNoticeDismissedFileName)))
        {
            return 0;
        }

        try
        {
            string legacy = Path.Combine(dir, LegacyFileName);
            return File.Exists(legacy)
                ? JsonSerializer.Deserialize<List<Bookmark?>>(File.ReadAllText(legacy))?.OfType<Bookmark>().Count() ?? 0
                : 0;
        }
        catch
        {
            return 0; // a notice about a file that does not read would name no number worth showing
        }
    }

    /// <summary>Records that the notice about the pre-v2 bookmarks was dismissed. No-op on WASM or on I/O failure.</summary>
    public void DismissLegacyNotice()
    {
        string? dir = _path is null ? null : Path.GetDirectoryName(_path);
        if (dir is null)
        {
            return;
        }

        try
        {
            AtomicFile.WriteAllText(Path.Combine(dir, LegacyNoticeDismissedFileName), DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // Best-effort: the notice shows again next launch.
        }
    }

    private Dictionary<string, List<Bookmark>> ReadAll()
    {
        TryReadAll(out Dictionary<string, List<Bookmark>> all);
        return all;
    }

    // False when the file is there but does not read, which a save has to tell apart from no file. The map is
    // empty then, as it is for a load.
    private bool TryReadAll(out Dictionary<string, List<Bookmark>> all)
    {
        all = new Dictionary<string, List<Bookmark>>(StringComparer.Ordinal);
        if (_path is null || !File.Exists(_path))
        {
            return true;
        }

        try
        {
            if (JsonSerializer.Deserialize<Dictionary<string, List<Bookmark?>?>>(File.ReadAllText(_path)) is { } read)
            {
                // The serializer lets a JSON null through as a demo's list and as an item of one. Neither is a
                // bookmark, and a save writes the map back without them.
                foreach ((string demoKey, List<Bookmark?>? list) in read)
                {
                    if (list is not null)
                    {
                        all[demoKey] = [.. list.OfType<Bookmark>()];
                    }
                }
            }

            return true;
        }
        catch
        {
            return false; // bookmark restore is best-effort
        }
    }
}
