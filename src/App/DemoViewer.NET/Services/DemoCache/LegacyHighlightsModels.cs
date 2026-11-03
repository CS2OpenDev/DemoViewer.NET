namespace DemoViewer.NET.Services.DemoCache;

// ─────────────────────────────────────────────────────────────────────────────
//  MIGRATION-ONLY DTOs: the on-disk shape of the RETIRED <ConfigRoot>/highlights.json.
//
//  Nothing writes these types and nothing reads them at runtime. They exist so
//  LegacyCacheMigration can deserialize a file left behind by v0.5.2 and earlier, once, on a
//  machine that has one. The live model is DemoCacheRecord.
//
//  They live here, next to the migration that is their only consumer, rather than under
//  Modules/Highlights/ where the store that wrote them used to be. The store is gone, and
//  leaving its models in a feature folder invited exactly the mistake this file is named
//  against: reading them as a live model. The `Legacy` prefix is not decoration; without it
//  CachedRound and CachedPlayer collide with the real ones in this namespace.
//
//  Do not "modernize" these. Every property name, and the PascalCase convention itself, is
//  the format some user's file is already written in. The retired store serialized with plain
//  `new JsonSerializerOptions { WriteIndented = true }`, no naming policy, which is why the
//  migration reads them with default (case-sensitive PascalCase) options. Fields the migration
//  does not read are kept deliberately: this file is now the only surviving description of the
//  format, so it documents the whole of it, not the part that happened to survive.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
///     One demo's row in the retired library-wide highlights cache.
///     Superseded by <see cref="DemoCacheRecord" />; read only by <see cref="LegacyCacheMigration" />.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Access | ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public sealed class LegacyHighlightsRow
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public string FilePath { get; init; } = "";

    public long FileSize { get; init; }

    public long ModifiedTicks { get; init; }

    /// <summary>SHA-256 of the demo bytes: the <c>MatchChecksum</c> handed to CSVG (any stable string).</summary>
    public string? DemoSha256 { get; set; }

    // ── Demo facts (clip assembly without re-parse) ───────────────────────────
    public string? MapName { get; set; }

    public int TickRate { get; init; }

    public int TickCount { get; set; }

    /// <summary>Diagnostic only: events are frame clock; nothing here ever subtracts it.</summary>
    public int ServerStartTick { get; set; }

    public string? ProfileName { get; set; }

    public List<LegacyCachedPlayer> Players { get; init; } = [];

    public List<LegacyCachedRound> Rounds { get; set; } = [];

    // ── Invalidation ──────────────────────────────────────────────────────────
    /// <summary>The combined config fingerprint the row was scanned under.</summary>
    public string? ConfigFingerprint { get; init; }

    /// <summary>Per-highlight-type hashes, keyed <c>{rulesetId}.{highlightId}</c>.</summary>
    public Dictionary<string, string> HighlightHashes { get; set; } = new();

    public LegacyHighlightScanState ScanState { get; init; }

    // ── The harvest ───────────────────────────────────────────────────────────
    public List<LegacyCachedHighlight> Events { get; init; } = [];
}

/// <summary>A demo participant. Superseded by <see cref="CachedPlayerInfo" />.</summary>
public sealed class LegacyCachedPlayer
{
    public int Slot { get; init; }

    /// <summary>RAW name: never sanitized on the way in.</summary>
    public string Name { get; init; } = "";

    public string SteamId64 { get; init; } = "";

    /// <summary>2 = T, 3 = CT (demo convention).</summary>
    public int Team { get; init; }
}

/// <summary>A round boundary in frame clock. Superseded by <see cref="CachedRound" />.</summary>
[UsedImplicitly(ImplicitUseKindFlags.Access | ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public sealed class LegacyCachedRound
{
    public int Number { get; set; }

    public int StartTickFrameClock { get; set; }
}

/// <summary>One harvested highlight. Superseded by <see cref="CachedHighlightEvent" />.</summary>
[UsedImplicitly(ImplicitUseKindFlags.Access | ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public sealed class LegacyCachedHighlight
{
    public string RulesetId { get; init; } = "";

    public string HighlightId { get; init; } = "";

    public int FrameIndex { get; set; }

    /// <summary>Frame clock: identical semantics to <c>RuleChainEvent.Tick</c>; never converted.</summary>
    public int Tick { get; init; }

    public int PlayerSlot { get; init; }

    /// <summary>
    ///     RAW in-demo name at emission. NOT migrated: the unified record identifies a player by
    ///     <see cref="LegacyCachedHighlight.PlayerSlot" /> into its own roster instead of repeating the name on
    ///     every event. Kept here because it is in the file.
    /// </summary>
    public string PlayerName { get; set; } = "";

    /// <summary>Per-event steamId join. NOT migrated, for the same reason as <see cref="PlayerName" />.</summary>
    public string SteamId64 { get; set; } = "";

    public int RoundNumber { get; init; }

    public string RenderedTitle { get; init; } = "";

}

/// <summary>
///     The retired row scan lifecycle. Superseded by <see cref="DemoAnalysisState" />, which carries the
///     same three cases, but whose <c>Pending</c> is no longer PERSISTED: the scan backlog is derived from
///     the rules fingerprint now (see <see cref="DemoCacheRecord.NeedsAnalysis" />).
/// </summary>
public enum LegacyHighlightScanState
{
    /// <summary>Never scanned, or invalidated.</summary>
    Pending,

    /// <summary>Scanned successfully under <see cref="LegacyHighlightsRow.ConfigFingerprint" />.</summary>
    Indexed,

    /// <summary>The scan failed (parse or analysis).</summary>
    Failed
}

/// <summary>The on-disk wrapper of the retired <c>highlights.json</c>.</summary>
public sealed class LegacyHighlightsFile
{
    public int Version { get; init; } = 1;

    public List<LegacyHighlightsRow> Rows { get; init; } = [];
}
