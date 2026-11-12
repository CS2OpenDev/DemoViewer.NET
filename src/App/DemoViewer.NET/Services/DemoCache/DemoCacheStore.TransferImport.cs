#region

using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.MatchMeta;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>What <see cref="DemoCacheStore.ImportContent" /> did to one demo's row.</summary>
public enum DemoImportOutcome
{
    /// <summary>The store had no row for the content; it has an orphaned one now.</summary>
    Added,

    /// <summary>The store had a row; at least one tier or stamp came from the import.</summary>
    Merged,

    /// <summary>The store had a row at least as current as the import in every tier and stamp.</summary>
    Unchanged
}

/// <summary>The result of importing one demo's cache data.</summary>
/// <param name="Outcome">What happened to the row.</param>
/// <param name="TakenStamps">The pack stamps the row took from the import.</param>
public sealed record DemoImportResult(DemoImportOutcome Outcome, IReadOnlyList<string> TakenStamps);

/// <summary>
///     How this machine judges whether imported data is current, so a merge can prefer the current side. Either
///     delegate may be null or answer null for "cannot tell", and the merge then compares schemas and states only.
/// </summary>
/// <param name="AnalysisFingerprint">The analysis tier's fingerprint in force at a tick rate.</param>
/// <param name="StampCurrent">Whether a pack stamp is current here, given the demo's tick rate.</param>
public sealed record DemoImportCurrency(
    Func<int, string?>? AnalysisFingerprint = null,
    Func<PackStamp, int, bool?>? StampCurrent = null);

public sealed partial class DemoCacheStore
{
    /// <summary>
    ///     Brings one demo's record and sibling files in from a transfer bundle. With no row for the content the
    ///     record lands as an orphan from now, marked imported, with no path: a library file whose fingerprint
    ///     matches takes it later without a parse. With a row, each tier and pack stamp keeps whichever side is
    ///     current and never takes a lower schema; a sibling comes in only with the stamp it belongs to. Files are
    ///     written under the key's stripe lock, siblings first and the record last. The index is not saved: the
    ///     caller saves it once after a batch.
    /// </summary>
    /// <param name="sha256">The demo's content id, lowercase hex.</param>
    /// <param name="fingerprint">The demo's fingerprint from the manifest, or null.</param>
    /// <param name="recordBytes">The record file's bytes (<see cref="RecordSuffix" />).</param>
    /// <param name="siblings">The sibling files by suffix, each starting with a dot.</param>
    /// <param name="currency">How this machine judges currency, or null.</param>
    /// <param name="nowUtc">The import time, UTC; now when null.</param>
    /// <exception cref="ArgumentException">The content id or a suffix is malformed.</exception>
    /// <exception cref="InvalidDataException">The record does not read, or names other content.</exception>
    public DemoImportResult ImportContent(string sha256, DemoContentFingerprint? fingerprint, byte[] recordBytes,
        IReadOnlyList<KeyValuePair<string, byte[]>> siblings, DemoImportCurrency? currency = null,
        DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(recordBytes);
        ArgumentNullException.ThrowIfNull(siblings);
        if (sha256 is not { Length: 64 } || !sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("A content id is a lowercase-hex SHA-256.", nameof(sha256));
        }

        foreach (KeyValuePair<string, byte[]> sibling in siblings)
        {
            ValidateSuffix(sibling.Key);
        }

        DemoCacheRecord imported = TryDeserializeRecord(recordBytes)
            ?? throw new InvalidDataException("The demo's cache record does not read.");
        if (!string.Equals(imported.Sha256, sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The demo's cache record describes other content.");
        }

        long now = (nowUtc ?? DateTime.UtcNow).Ticks;
        DemoImportOutcome outcome;
        List<string> taken;
        string? raisePath;
        lock (_rmwGate)
        {
            DemoCacheIndexEntry? row;
            lock (_gate)
            {
                row = _rows.GetValueOrDefault(sha256);
            }

            DemoCacheRecord? local = row is null ? null : TryLoadRowRecord(sha256);
            DemoCacheRecord result;
            bool changed;
            if (local is null)
            {
                // A row whose record does not read has nothing to keep but where its demo is: the import stands in
                // for the rest. Rebuilding the index from records must still find a listed demo's path.
                result = imported;
                result.Path = row?.Path ?? "";
                result.Size = row?.Size ?? result.Size;
                result.ModifiedTicks = row?.ModifiedTicks ?? 0;
                result.Locations = row?.Locations ?? [];
                result.ContentFingerprint = fingerprint ?? result.ContentFingerprint;
                taken = [.. result.PackStamps.Select(s => s.Id)];
                changed = true;
            }
            else
            {
                result = local;
                changed = MergeImported(result, imported, currency, out taken);
                if (result.ContentFingerprint is null && fingerprint is not null)
                {
                    result.ContentFingerprint = fingerprint;
                    changed = true;
                }
            }

            outcome = local is null ? DemoImportOutcome.Added
                : changed ? DemoImportOutcome.Merged
                : DemoImportOutcome.Unchanged;

            HashSet<string> takenSet = new(taken, StringComparer.Ordinal);
            List<KeyValuePair<string, byte[]>> write =
                [.. siblings.Where(s => StampOfSibling(s.Key) is { } stamp && takenSet.Contains(stamp))];

            string key;
            lock (_gate)
            {
                key = row is null ? FileKeyOf(new DemoCacheIndexEntry { Sha256 = sha256 }) : WriteKeys(row)[0];
            }

            if (changed)
            {
                byte[] bytes = SidecarJson.SerializeGzip(result, _jsonOptions);
                lock (_fileStripes[StripeIndex(key)])
                {
                    // A facts stamp taken replaces that ruleset's outputs whole: one the import lacks is stale.
                    DeleteSiblingsOf(key, [.. takenSet.Select(StampedFacts.RulesetOf).OfType<string>()],
                        [.. write.Select(s => s.Key)]);
                    foreach ((string suffix, byte[] content) in write)
                    {
                        WriteFileBytes(key + suffix, content, true);
                    }

                    WriteFileBytes(key, bytes, false);
                }
            }

            lock (_gate)
            {
                DemoCacheIndexEntry entry;
                if (row is null)
                {
                    entry = result.ToIndexEntry();
                    entry.Sha256 = sha256;
                    entry.Path = "";
                    entry.Locations = [];
                    entry.OrphanedSinceUtcTicks = now;
                    entry.ImportedUtcTicks = now;
                }
                else
                {
                    entry = changed ? Reproject(row, result) : row.Copy();
                    if (entry.IsOrphaned)
                    {
                        // Imported again: the clock restarts, and the longer grace applies until a path lists it.
                        entry.OrphanedSinceUtcTicks = now;
                        entry.ImportedUtcTicks = now;
                    }
                }

                Link(sha256, entry);
                if (string.Equals(_lastRecordKey, sha256, StringComparison.Ordinal))
                {
                    _lastRecordKey = null;
                    _lastRecordBytes = null;
                }

                raisePath = entry.IsOrphaned ? null : entry.Path;
            }
        }

        RaiseChanged(raisePath);
        return new DemoImportResult(outcome, taken);
    }

    /// <summary>
    ///     Merges <paramref name="imported" /> into <paramref name="local" />, a record of the same content: per
    ///     tier and per pack stamp the current side wins, a lower schema never replaces a higher one, and when
    ///     currency cannot be told the local side is kept unless only the imported side holds a successful write.
    ///     Payloads that belong to a stamp move with it; the rest only fill what the local record lacks.
    /// </summary>
    /// <param name="local">The local record; changed in place.</param>
    /// <param name="imported">The imported record.</param>
    /// <param name="currency">How this machine judges currency, or null.</param>
    /// <param name="takenStamps">The stamp ids taken from the import.</param>
    /// <returns>True when anything changed.</returns>
    internal static bool MergeImported(DemoCacheRecord local, DemoCacheRecord imported, DemoImportCurrency? currency,
        out List<string> takenStamps)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(imported);
        bool changed = false;
        int tickRate = local.TickRate > 0 ? local.TickRate : imported.TickRate;

        if (imported.Header.Schema > local.Header.Schema)
        {
            local.Header = imported.Header;
            local.Map = imported.Map ?? local.Map;
            local.Server = imported.Server ?? local.Server;
            local.DemoVersion = imported.DemoVersion ?? local.DemoVersion;
            changed = true;
        }

        if (imported.Parse.Schema > local.Parse.Schema)
        {
            local.Parse = imported.Parse;
            local.Map = imported.Map ?? local.Map;
            local.Server = imported.Server ?? local.Server;
            local.SourceKind = imported.SourceKind ?? local.SourceKind;
            local.DurationSeconds = imported.DurationSeconds;
            local.TickRate = imported.TickRate;
            local.TickCount = imported.TickCount;
            local.ServerStartTick = imported.ServerStartTick;
            local.Players = imported.Players;
            local.Rounds = imported.Rounds;
            local.RoundCount = imported.RoundCount;
            local.CtScore = imported.CtScore;
            local.TScore = imported.TScore;
            local.CtClan = imported.CtClan;
            local.TClan = imported.TClan;
            changed = true;
        }

        if (TakesAnalysis(local, imported, currency?.AnalysisFingerprint?.Invoke(tickRate)))
        {
            local.Analysis = imported.Analysis;
            local.AnalysisState = imported.AnalysisState;
            local.Scoreboard = imported.Scoreboard;
            local.Highlights = imported.Highlights;
            local.CtSideWins = imported.CtSideWins;
            local.TSideWins = imported.TSideWins;
            local.AnalysisRoundCount = imported.AnalysisRoundCount;
            local.ProfileName = imported.ProfileName;
            local.ConfigFingerprint = imported.ConfigFingerprint;
            local.HighlightHashes = imported.HighlightHashes;
            local.ExcludedRulesets = imported.ExcludedRulesets;
            changed = true;
        }

        takenStamps = [];
        foreach (PackStamp theirs in imported.PackStamps)
        {
            if (!TakesStamp(local.Stamp(theirs.Id), theirs, currency?.StampCurrent, tickRate))
            {
                continue;
            }

            local.SetStamp(theirs);
            takenStamps.Add(theirs.Id);
            TakePayloadOf(local, imported, theirs.Id);
            changed = true;
        }

        // What no stamp vouches for only fills a gap, as a joining row does.
        foreach ((string packId, System.Text.Json.JsonElement payload) in imported.Packs)
        {
            changed |= local.Packs.TryAdd(packId, payload);
        }

        if (local.RoundFacts is null && imported.RoundFacts is not null)
        {
            local.RoundFacts = imported.RoundFacts;
            changed = true;
        }

        if (local.MatchMeta is null && imported.MatchMeta is not null)
        {
            local.MatchMeta = imported.MatchMeta;
            changed = true;
        }

        if (local.FacetPlayers is null && imported.FacetPlayers is not null)
        {
            local.FacetPlayers = imported.FacetPlayers;
            changed = true;
        }

        if (local.ErrorReport is null && imported.ErrorReport is not null)
        {
            local.ErrorReport = imported.ErrorReport;
            changed = true;
        }

        return changed;
    }

    private static bool TakesAnalysis(DemoCacheRecord local, DemoCacheRecord imported, string? currentFingerprint)
    {
        if (!imported.Analysis.IsPresent || imported.Analysis.Schema < local.Analysis.Schema)
        {
            return false;
        }

        if (imported.Analysis.Schema > local.Analysis.Schema)
        {
            return true;
        }

        if (currentFingerprint is not null)
        {
            return imported.IsAnalysisCurrent(currentFingerprint) && !local.IsAnalysisCurrent(currentFingerprint);
        }

        return imported.AnalysisState == DemoAnalysisState.Indexed && local.AnalysisState != DemoAnalysisState.Indexed;
    }

    private static bool TakesStamp(PackStamp? mine, PackStamp theirs, Func<PackStamp, int, bool?>? isCurrent,
        int tickRate)
    {
        if (mine is null)
        {
            return true;
        }

        if (theirs.Schema != mine.Schema)
        {
            return theirs.Schema > mine.Schema;
        }

        if (isCurrent?.Invoke(theirs, tickRate) is { } theirsCurrent
            && isCurrent(mine, tickRate) is { } mineCurrent)
        {
            return theirsCurrent && !mineCurrent;
        }

        return theirs.State == DemoAnalysisState.Indexed && mine.State != DemoAnalysisState.Indexed;
    }

    // The record fields a stamp vouches for besides its pack payload.
    private static void TakePayloadOf(DemoCacheRecord local, DemoCacheRecord imported, string stampId)
    {
        if (imported.Packs.TryGetValue(stampId, out System.Text.Json.JsonElement payload))
        {
            local.Packs[stampId] = payload;
        }

        if (string.Equals(stampId, RoundFactsRecords.FacetId, StringComparison.Ordinal))
        {
            local.RoundFacts = imported.RoundFacts ?? local.RoundFacts;
        }
        else if (string.Equals(stampId, StampedFacts.StampId(PlayerMatchFacet.RulesetId), StringComparison.Ordinal))
        {
            local.FacetPlayers = imported.FacetPlayers ?? local.FacetPlayers;
            local.CtSideWins = imported.CtSideWins ?? local.CtSideWins;
            local.TSideWins = imported.TSideWins ?? local.TSideWins;
        }
        else if ((string.Equals(stampId, MatchMetaPass.StampId, StringComparison.Ordinal)
                     || string.Equals(stampId, MatchMetaEvaluator.StampId, StringComparison.Ordinal))
                 && imported.MatchMeta is { } meta)
        {
            // A match time the user set by hand is theirs, whatever the import says.
            if (local.MatchMeta is { MatchTimeSource: MatchTimeSource.UserOverride } mine)
            {
                meta.MatchTimeUtcTicks = mine.MatchTimeUtcTicks;
                meta.MatchTimeSource = MatchTimeSource.UserOverride;
            }

            local.MatchMeta = meta;
        }
    }

    // The stamp a sibling belongs to: the facts stamp of the ruleset its suffix names, or null for one no stamp
    // vouches for, which is never imported.
    internal static string? StampOfSibling(string suffix)
    {
        const string Marker = ".facts.";
        const string Ending = ".json.gz";
        int at = suffix.LastIndexOf(Marker, StringComparison.Ordinal);
        if (at < 0 || !suffix.EndsWith(Ending, StringComparison.Ordinal) || at + Marker.Length >= suffix.Length - Ending.Length)
        {
            return null;
        }

        string rulesetId = suffix[(at + Marker.Length)..^Ending.Length];
        return string.Equals(suffix[at..], StampedFacts.RulesetSuffix(rulesetId), StringComparison.Ordinal)
            ? StampedFacts.StampId(rulesetId)
            : null;
    }

    // Caller holds the key's stripe. Deletes the key's facts siblings of the rulesets given, but those in keep.
    private void DeleteSiblingsOf(string key, IReadOnlyList<string> rulesetIds, IReadOnlyList<string> keep)
    {
        if (rulesetIds.Count == 0)
        {
            return;
        }

        HashSet<string> kept = new(keep, StringComparer.Ordinal);
        bool Stale(string suffix) =>
            !kept.Contains(suffix) && rulesetIds.Any(r => suffix.EndsWith(StampedFacts.RulesetSuffix(r),
                StringComparison.Ordinal));

        if (CacheRoot is null)
        {
            lock (_gate)
            {
                foreach (string name in _memorySiblings.Keys
                             .Where(k => k.StartsWith(key + ".", StringComparison.Ordinal) && Stale(k[key.Length..]))
                             .ToList())
                {
                    _memorySiblings.Remove(name);
                }
            }

            return;
        }

        string dir = SidecarDir!;
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(dir, key + ".*").ToList())
        {
            try
            {
                if (Stale(Path.GetFileName(file)[key.Length..]))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An output the stamp no longer names is never read; the demo's next facts write replaces it.
            }
        }
    }

    // Caller holds the key's stripe. A sibling is named key + suffix; a record by its key alone.
    private void WriteFileBytes(string name, byte[] bytes, bool sibling)
    {
        if (CacheRoot is null)
        {
            lock (_gate)
            {
                (sibling ? _memorySiblings : _memoryRecords)[name] = bytes;
            }

            return;
        }

        AtomicFile.WriteAllBytes(sibling ? Path.Combine(SidecarDir!, name) : RecordFile(name), bytes);
    }
}
