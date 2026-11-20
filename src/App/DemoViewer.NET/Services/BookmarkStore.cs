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
///         every demo; it cannot be told which demo each entry belonged to, so it is neither read nor touched.
///     </para>
/// </summary>
public sealed class BookmarkStore
{
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
            Dictionary<string, List<Bookmark>> all = ReadAll();
            if (bookmarks.Count > 0)
            {
                all[demoKey] = [.. bookmarks];
            }
            else
            {
                all.Remove(demoKey);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(all, _writeOptions));
        }
        catch
        {
            // Persistence is best-effort; never crash the app on a write failure.
        }
    }

    private Dictionary<string, List<Bookmark>> ReadAll()
    {
        Dictionary<string, List<Bookmark>> all = new(StringComparer.Ordinal);
        if (_path is null || !File.Exists(_path))
        {
            return all;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<Bookmark>>>(File.ReadAllText(_path)) is { } read
                ? new Dictionary<string, List<Bookmark>>(read, StringComparer.Ordinal)
                : all;
        }
        catch
        {
            return all; // bookmark restore is best-effort
        }
    }
}
