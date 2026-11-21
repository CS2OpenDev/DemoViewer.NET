#region

using System.IO.Compression;
using System.Text.Json;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Extensions.StratBook.Services.Portability;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>
///     A lineup's fixed identity: where it is thrown from and where it lands, as first seen. Never moves, so
///     a throw added later joins the same id.
/// </summary>
/// <param name="Id">The lineup id strat steps, mined patterns and clip files store.</param>
/// <param name="Kind">What is thrown.</param>
/// <param name="Origin">The members' mean release point when it was minted.</param>
/// <param name="Landing">The members' mean landing when it was minted.</param>
/// <param name="Seed">The key of the hashed throw it was seeded from, or empty when that demo was never hashed.</param>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record LineupAnchor(Guid Id, GrenadeKind Kind, WorldPoint Origin, WorldPoint Landing, string Seed);

/// <summary>One position's representative flight: the throw it came from and its trajectory.</summary>
/// <param name="Throw">The throw's <see cref="IndexedGrenade.Key" />.</param>
/// <param name="Points">The flight at the walk's stride.</param>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record LineupPath(string Throw, IReadOnlyList<TrajectoryPoint> Points);

/// <summary>One map's lineups.</summary>
public sealed class MapLineups
{
    public List<LineupAnchor> Anchors { get; init; } = [];

    /// <summary>Old (grid) lineup id to the one lineup that took most of its throws. Built once per map, then frozen.</summary>
    public Dictionary<Guid, Guid> Aliases { get; init; } = [];

    /// <summary>True once <see cref="Aliases" /> has been built from this map's rows.</summary>
    public bool AliasesBuilt { get; set; }

    /// <summary>Representative flights by <c>{lineupId:N}/{technique}</c>.</summary>
    public Dictionary<string, LineupPath> Paths { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The file: every map's lineups.</summary>
public sealed class GrenadeLineupDocument
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; init; } = CurrentSchema;

    public Dictionary<string, MapLineups> Maps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
///     The persisted lineup identities, alias maps and representative flights: one gzipped JSON file. In the app
///     it is a file of the extension's own cache folder, copied once from where an older build kept it, since
///     it holds hand-tuned state a rebuild would lose. A store with nowhere to write (the browser) keeps them in
///     memory. Not thread-safe: the <see cref="GrenadeIndex" /> calls it under its own lock.
/// </summary>
public sealed class GrenadeLineupStore
{
    public const string FileName = "grenade-lineups.json.gz";

    // The waits between tries at a file another process holds. A scan or a sync client lets go within
    // milliseconds, and giving up on the first try costs the session every lineup edit.
    private static readonly int[] _lockedReadWaitsMs = [50, 150];

    private static ILogger? _diagLog;

    private readonly Func<byte[]?> _read;
    private readonly Action<byte[]>? _write;
    private GrenadeLineupDocument? _loaded;

    // Set by each read of the file: true when one was there and could not be read or parsed (held open by
    // another process, written by a newer build, corrupt). Writes are then refused, so the empty document
    // the store falls back to never replaces the lineups whose ids strat steps and clips are named by.
    private volatile bool _refused;

    // True when the refusal came from a read that threw (a lock, a denied open) and not from a file that was
    // read and is not a lineup document: the first may read a moment later, the second will not.
    private bool _retryable;

    /// <param name="directory">The folder holding <see cref="FileName" />, or null to keep everything in memory.</param>
    public GrenadeLineupStore(string? directory)
        : this(directory is null ? () => null : () => ReadFile(Path.Combine(directory, FileName)),
            directory is null ? null : bytes => AtomicFile.WriteAllBytes(Path.Combine(directory, FileName), bytes))
    {
    }

    private GrenadeLineupStore(Func<byte[]?> read, Action<byte[]>? write)
    {
        _read = read;
        _write = write;
    }

    /// <summary>
    ///     The store in the extension's own cache folder. When that has no file yet and <paramref name="legacy" />
    ///     returns a lineup document, its bytes are copied over first. No folder (the browser build) keeps the
    ///     lineups in memory for the session.
    /// </summary>
    /// <param name="folder">The extension's own cache folder, as <see cref="IExtensionStores.Own" /> returned it.</param>
    /// <param name="legacy">Reads the file where an older build kept it, or null when there is none.</param>
    public static GrenadeLineupStore In(IExtensionFolder? folder, Func<byte[]?>? legacy)
    {
        if (folder is null)
        {
            return new GrenadeLineupStore(static () => null, null);
        }

        return new GrenadeLineupStore(() =>
        {
            byte[]? bytes = folder.Read(FileName);
            if (bytes is not null || legacy is null || ReadLegacy(legacy) is not { } old || Parse(old) is null)
            {
                return bytes;
            }

            folder.WriteAtomic(FileName, old);
            return old;
        }, bytes => folder.WriteAtomic(FileName, bytes));
    }

    /// <summary>
    ///     The lineup document in <paramref name="folder" />, as an import reads a bundle's copy. Null when the
    ///     folder has none or it does not parse.
    /// </summary>
    /// <param name="folder">A folder holding <see cref="FileName" />.</param>
    public static GrenadeLineupDocument? ReadFrom(IExtensionFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            return folder.Read(FileName) is { } bytes
                ? Parse(bytes, ImportLedger.Strict(GrenadeSidecar.JsonOptions))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every map's anchor ids. Not thread-safe, like the rest of the store.</summary>
    public HashSet<Guid> AnchorIds() => [.. Doc.Maps.Values.SelectMany(m => m.Anchors).Select(a => a.Id)];

    // Read on first use, not in the constructor: the file holds every flight and the index is built on the UI thread.
    private GrenadeLineupDocument Doc => _loaded ??= Load();

    /// <summary>Drops the in-memory document; the next use reads the file again. In memory this empties the store.</summary>
    public void Unload() => _loaded = null;

    /// <summary>
    ///     True while the file is there and was not read: the store holds an empty document in its place and
    ///     <see cref="Write" /> does nothing, so an edit made now is not kept. Reads the file on first use.
    /// </summary>
    public bool IsRefused
    {
        get
        {
            _ = Doc;
            return _refused;
        }
    }

    /// <summary>True when <see cref="Recover" /> has a read to try again. Cheap; reads nothing.</summary>
    public bool CanRecover => _loaded is not null && _refused && _retryable;

    /// <summary>
    ///     Tries the file again after a read that threw. A scan or a sync client can hold it for longer than
    ///     <see cref="Load" /> waits, and without this the session would stay on the empty document, dropping
    ///     every edit, until the store is unloaded. True when the file's document replaced the empty one: what
    ///     was edited meanwhile is gone with it, a <see cref="MapLineups" /> handed out earlier is stale, and a
    ///     <see cref="Snapshot" /> taken earlier must not be written. Not thread-safe, like the rest of the store.
    /// </summary>
    public bool Recover()
    {
        if (!CanRecover)
        {
            return false;
        }

        try
        {
            if (_read() is not { } bytes)
            {
                // The file went away: there is nothing left to protect, and the session's document stands.
                _refused = false;
                _retryable = false;
                return false;
            }

            if (Parse(bytes) is not { } document)
            {
                _retryable = false; // it reads now, and it is not a lineup document: refused until an unload
                return false;
            }

            _loaded = document;
            _refused = false;
            _retryable = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private GrenadeLineupDocument Load()
    {
        GrenadeLineupDocument? document;
        bool present;
        bool threw = false;
        for (int attempt = 0;; attempt++)
        {
            try
            {
                byte[]? bytes = _read();
                present = bytes is not null;
                document = bytes is null ? null : Parse(bytes);
                break;
            }
            catch (IOException) when (attempt < _lockedReadWaitsMs.Length)
            {
                Thread.Sleep(_lockedReadWaitsMs[attempt]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                present = true;
                document = null;
                threw = true;
                break;
            }
        }

        _refused = present && document is null;
        _retryable = threw;
        if (_refused)
        {
            GrenadeIndexLog.LineupsRefused(_diagLog ??= DiagnosticsLog.CreateLogger(GrenadeIndexLog.Category));
        }

        return document ?? new GrenadeLineupDocument();
    }

    /// <summary>A map's lineups, created empty on first use.</summary>
    public MapLineups For(string map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!Doc.Maps.TryGetValue(map, out MapLineups? lineups))
        {
            lineups = new MapLineups();
            Doc.Maps[map] = lineups;
        }

        return lineups;
    }

    /// <summary>Writes the file whole. A no-op in memory.</summary>
    public void Save() => Write(Snapshot());

    /// <summary>
    ///     A copy of the document that later edits do not reach, for writing without the index's lock. The
    ///     anchors and paths are immutable records, so copying the collections is enough.
    /// </summary>
    public GrenadeLineupDocument Snapshot()
    {
        GrenadeLineupDocument copy = new() { SchemaVersion = Doc.SchemaVersion };
        foreach ((string map, MapLineups lineups) in Doc.Maps)
        {
            copy.Maps[map] = new MapLineups
            {
                Anchors = [.. lineups.Anchors],
                Aliases = new Dictionary<Guid, Guid>(lineups.Aliases),
                AliasesBuilt = lineups.AliasesBuilt,
                Paths = new Dictionary<string, LineupPath>(lineups.Paths, StringComparer.Ordinal)
            };
        }

        return copy;
    }

    /// <summary>Writes a <see cref="Snapshot" />. Thread-safe with respect to the live document.</summary>
    public void Write(GrenadeLineupDocument snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_write is null || _refused)
        {
            return;
        }

        using MemoryStream buffer = new();
        using (GZipStream gzip = new(buffer, CompressionLevel.Optimal, true))
        {
            JsonSerializer.Serialize(gzip, snapshot, GrenadeSidecar.JsonOptions);
        }

        _write(buffer.ToArray());
    }

    /// <summary>Re-reads the file, as a check that a save reads back. False when it does not.</summary>
    public bool ReadsBack()
    {
        if (_write is null)
        {
            return true;
        }

        GrenadeLineupDocument? back = Read();
        return back is not null && back.Maps.Count == Doc.Maps.Count
            && back.Maps.All(m => Doc.Maps.TryGetValue(m.Key, out MapLineups? mine)
                && mine.Anchors.Count == m.Value.Anchors.Count
                && mine.Paths.Count == m.Value.Paths.Count
                && mine.Aliases.Count == m.Value.Aliases.Count);
    }

    private GrenadeLineupDocument? Read()
    {
        try
        {
            return _read() is { } bytes ? Parse(bytes) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[]? ReadLegacy(Func<byte[]?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Throws when the file is there but cannot be read, which the caller tells apart from no file.
    private static byte[]? ReadFile(string file) => File.Exists(file) ? File.ReadAllBytes(file) : null;

    // Gzipped or plain, sniffed by the magic bytes.
    private static GrenadeLineupDocument? Parse(byte[] bytes, JsonSerializerOptions? options = null)
    {
        options ??= GrenadeSidecar.JsonOptions;
        try
        {
            GrenadeLineupDocument? document;
            if (bytes is [0x1F, 0x8B, ..])
            {
                using GZipStream gzip = new(new MemoryStream(bytes), CompressionMode.Decompress);
                document = JsonSerializer.Deserialize<GrenadeLineupDocument>(gzip, options);
            }
            else
            {
                document = JsonSerializer.Deserialize<GrenadeLineupDocument>(bytes, options);
            }

            // The local read is not strict about nulls, and everything below and after dereferences these. Not even
            // the strict one reaches a dictionary's values, so a null path reads under both.
            if (document is not { SchemaVersion: GrenadeLineupDocument.CurrentSchema, Maps: not null }
                || document.Maps.Values.Any(m => m is null || m.Anchors is null || m.Aliases is null
                                                 || m.Paths is null || m.Anchors.Contains(null!)
                                                 || m.Paths.Values.Any(p => p?.Throw is null || p.Points is null)))
            {
                return null;
            }

            // The reader builds dictionaries with the default comparers.
            document.Maps = new Dictionary<string, MapLineups>(document.Maps, StringComparer.OrdinalIgnoreCase);
            foreach (MapLineups map in document.Maps.Values)
            {
                map.Paths = new Dictionary<string, LineupPath>(map.Paths, StringComparer.Ordinal);
            }

            return document;
        }
        // ArgumentException: two map keys that differ only in case collide under the comparer above. The app
        // never writes that, so the file is not one of its own and reads as not a lineup document.
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or ArgumentException)
        {
            return null;
        }
    }
}
