namespace DemoViewer.NET.Services.DemoCache;

/// <summary>One file of a demo's cache data on disk, as <see cref="DemoCacheStore.SnapshotFiles" /> lists it.</summary>
/// <param name="Rest">
///     The name after the file key: <see cref="DemoCacheStore.RecordSuffix" /> (or the pre-gzip <c>.json</c>) for
///     the record, else the sibling's suffix. The same for every key, so it names the file on any machine.
/// </param>
/// <param name="FullPath">Where it is now.</param>
/// <param name="Size">Its length when it was listed.</param>
internal sealed record DemoCacheFile(string Rest, string FullPath, long Size)
{
    /// <summary>The record, not a sibling.</summary>
    public bool IsRecord =>
        string.Equals(Rest, DemoCacheStore.RecordSuffix, StringComparison.Ordinal)
        || string.Equals(Rest, ".json", StringComparison.Ordinal);
}

// Read-only access for a library export. Nothing here saves, upserts, confirms or settles: the exporter must
// leave the cache exactly as it found it.
public sealed partial class DemoCacheStore
{
    /// <summary>
    ///     The files of the hashed, listed row <paramref name="sha256" />: every <c>demos/&lt;key&gt;.*</c> under
    ///     the row's file keys, one per <see cref="DemoCacheFile.Rest" />, siblings first and the record last.
    ///     Empty when the content has no listed row or the store has no cache root.
    /// </summary>
    /// <param name="sha256">The demo's content hash.</param>
    internal IReadOnlyList<DemoCacheFile> SnapshotFiles(string sha256) => SnapshotFiles(sha256, files => files);

    /// <summary>
    ///     Lists the row's files as <see cref="SnapshotFiles(string)" /> does and hands them to
    ///     <paramref name="read" /> while every stripe of the row's file keys is held, so no write, settle or
    ///     key move of this demo can rename or replace them until it returns. <paramref name="read" /> must
    ///     only read them, and must not call back into the store.
    /// </summary>
    /// <param name="sha256">The demo's content hash.</param>
    /// <param name="read">Runs under the locks with the files, possibly none.</param>
    internal T SnapshotFiles<T>(string sha256, Func<IReadOnlyList<DemoCacheFile>, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        while (true)
        {
            List<string> keys = SnapshotKeys(sha256);
            if (keys.Count == 0)
            {
                return read([]);
            }

            object[] stripes = [.. keys.Select(StripeIndex).Distinct().Order().Select(i => _fileStripes[i])];
            EnterAll(stripes);
            try
            {
                // A settle, a key release or a new write key between the listing of the keys and the locks moves
                // the files under a key the list does not name, so the list is only good if it still holds.
                if (!SnapshotKeys(sha256).SequenceEqual(keys, StringComparer.Ordinal))
                {
                    continue;
                }

                return read(ListFiles(keys));
            }
            finally
            {
                ExitAll(stripes);
            }
        }
    }

    // The keys the row's files may sit under, in read order; none when the content has no listed, hashed row.
    private List<string> SnapshotKeys(string sha256)
    {
        lock (_gate)
        {
            if (CacheRoot is null || string.IsNullOrEmpty(sha256)
                                  || !_rows.TryGetValue(sha256, out DemoCacheIndexEntry? row)
                                  || IsProvisional(row) || row.IsOrphaned)
            {
                return [];
            }

            // The read order: the current write key, then the row's own, then any older key. A settle moves the
            // write key's files to the row's own key first, so a later key holds the lesser copy.
            List<string> writeKeys = WriteKeys(row);
            return
            [
                .. writeKeys.Take(1).Append(FileKeyOf(row)).Concat(writeKeys.Skip(1))
                    .Distinct(StringComparer.Ordinal)
            ];
        }
    }

    // Under the row's stripes. The first key holding a name wins.
    private List<DemoCacheFile> ListFiles(List<string> keys)
    {
        string dir = SidecarDir!;
        Dictionary<string, DemoCacheFile> byRest = new(StringComparer.Ordinal);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        foreach (string key in keys)
        {
            foreach (string file in Directory.EnumerateFiles(dir, key + ".*"))
            {
                string rest = Path.GetFileName(file)[key.Length..];
                if (byRest.ContainsKey(rest))
                {
                    continue;
                }

                try
                {
                    byRest[rest] = new DemoCacheFile(rest, file, new FileInfo(file).Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone or unreadable between the listing and the stat: not part of the snapshot.
                }
            }
        }

        // A pre-gzip record left beside the current one is never read again.
        if (byRest.ContainsKey(RecordSuffix))
        {
            byRest.Remove(LegacyRecordSuffix);
        }

        return
        [
            .. byRest.Values.OrderBy(f => f.IsRecord).ThenBy(f => f.Rest, StringComparer.Ordinal)
        ];
    }
}
