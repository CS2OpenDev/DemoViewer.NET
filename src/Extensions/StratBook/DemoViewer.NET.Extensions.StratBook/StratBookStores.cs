#region

using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     Every store and cache path the pack owns outside its own folders: what <see cref="StratBookPack.Stores" />
///     declares and the pack test's writer-coverage check reads. The user's own work stays where users have it. The entries marked "old layout" are where older
///     builds kept what the extension's own folders and per-demo data hold now; nothing writes them, and
///     "delete extension data" still removes them. <c>review-queue.json</c> is not here: the Review Queue is core, since
///     Reels uses it.
/// </summary>
internal static class StratBookStores
{
    // The ids the pack opens its stores by, through IExtensionStores. The paths are the user-facing layout and
    // do not change; the ids are the pack's own.
    public const string Strats = "strats";
    public const string Tags = "tags";
    public const string Palettes = "palettes";
    public const string SuggestedTags = "suggested-tags";
    public const string LineupClips = "lineup-clips";
    public const string Teams = "teams";
    public const string WatchedSituations = "watched-situations";
    public const string VetoHistory = "veto-history";
    public const string ScoutingReportNotes = "scouting-report-notes";
    public const string TeamRanks = "team-ranks";

    // The notes file's name before the 2026-11 rename (the feature was the Opponent Dossier).
    public const string ScoutingReportNotesOldName = "scouting-report-notes-old-name";
    public const string StratMiningState = "strat-mining-state";
    public const string GrenadeLineups = "grenade-lineups";

    public static readonly StoreDescriptor[] All =
    [
        // ── Config root: user truth ─────────────────────────────────────────────
        // Library Transfer carries all of it. The extension merges an import itself wherever the files hold
        // ids another store points at (teams, lineups, strats); palettes and the tuning have no such ids, so
        // the host's file-level merge is enough. Indexes and trash are rebuilt or local, so they stay behind.
        new(Strats, "Strats", StoreRoot.Config, ["strats"], IsUserWork: true)
        {
            Portability = StorePortability.LibraryWide, ExtensionMerges = true,
            PortableExcludes = [StratStore.IndexFileName, StratStore.TrashFolderName]
        },
        new(Tags, "Tags", StoreRoot.Config, ["tags"], IsUserWork: true)
        {
            Portability = StorePortability.PerDemo, ExtensionMerges = true,
            DemoKeyedPaths =
            [
                "tags/" + TagStore.DemosFolderName + "/{sha256}" + TagStore.SidecarExtension,
                "tags/" + TagStore.VerdictsFolderName + "/{sha256}" + TagStore.VerdictsExtension
            ],
            PortableExcludes = [TagStore.IndexFileName]
        },
        new(Palettes, "Tag palettes", StoreRoot.Config, ["palettes"], IsUserWork: true)
        {
            Portability = StorePortability.LibraryWide
        },
        new(SuggestedTags, "Suggested Tags tuning", StoreRoot.Config, ["suggested-tags"], IsUserWork: true)
        {
            Portability = StorePortability.LibraryWide
        },
        // evicted.txt is this machine's clip cap, not the user's: carried over, it would stop the target
        // rendering clips it has never had.
        new(LineupClips, "Lineup clips", StoreRoot.Config, ["lineup-clips"], IsUserWork: true)
        {
            Portability = StorePortability.LibraryWide, ExtensionMerges = true,
            PortableExcludes = [LineupClipService.EvictedFileName]
        },
        // Team overrides keyed by a demo's StableKey (a local path) stay behind: the extension's merge drops them.
        new(Teams, "Teams", StoreRoot.Config, [TeamIdentityService.TeamsFileName], IsUserWork: true)
        {
            Portability = StorePortability.LibraryWide, ExtensionMerges = true
        },
        new(WatchedSituations, "Watched Situations", StoreRoot.Config, [WatchedSituationsService.FileName],
            IsUserWork: true)
        {
            Portability = StorePortability.LibraryWide, ExtensionMerges = true
        },
        new(VetoHistory, "Veto history", StoreRoot.Config, [VetoHistoryStore.FileName], IsUserWork: true)
        {
            Portability = StorePortability.LibraryWide, ExtensionMerges = true
        },
        new(ScoutingReportNotes, "Scouting report notes", StoreRoot.Config, [ScoutingReportNotesStore.FileName],
            IsUserWork: true)
        {
            Portability = StorePortability.LibraryWide, ExtensionMerges = true
        },
        new(TeamRanks, "Team ranks", StoreRoot.Config, [TeamRanksStore.FileName], IsUserWork: true)
        {
            Portability = StorePortability.LibraryWide, ExtensionMerges = true
        },

        // ── Config root: the old layout, read once to copy into the extension's own folder ──
        // Everything from here down keeps the default portability, None: read once, derived, or keyed by a
        // local path, so a transfer leaves it behind.
        new(ScoutingReportNotesOldName, "Scouting report notes (old layout)", StoreRoot.Config,
            [ScoutingReportNotesStore.LegacyFileName], IsUserWork: true),
        new(StratMiningState, "Strat Mining dismissed and promoted (old layout)", StoreRoot.Config,
            [StratMiningFiles.StatePath],
            IsUserWork: true),

        // ── Cache root: the old layout of what the extension's own cache folder holds now ──
        new("strat-mining-cache", "Strat Mining detections (old layout)", StoreRoot.Cache, ["strat-mining"],
            IsUserWork: false),
        new("team-index", "Team index (old layout)", StoreRoot.Cache, ["team-index.json"], IsUserWork: false),

        // ── Cache root: the old layout, read once or not at all ─────────────────
        new("round-index", "Round Index (old layout)", StoreRoot.Cache, ["round-index"], IsUserWork: false),
        new("suggestions", "Suggested Tags proposals (old layout)", StoreRoot.Cache, ["suggestions"],
            IsUserWork: false),
        new(GrenadeLineups, "Grenade lineups (old layout)", StoreRoot.Cache, [GrenadeLineupStore.FileName],
            IsUserWork: false),
        new("grenades-v3-attempts", "Grenade migration attempts (old layout)", StoreRoot.Cache,
            ["grenades-v3.attempts.json"],
            IsUserWork: false),
        new("grenade-sidecars", "Grenade sidecars (old layout)", StoreRoot.Cache,
        [
            "demos/*.grenades.json.gz", "demos/*.grenades.paths.json.gz", "demos/*.grenades.log.gz",
            "demos/*.grenades.json", "demos/*.grenades.paths.json"
        ], IsUserWork: false)
    ];

    /// <summary>
    ///     The files of the extension's own folders that Library Transfer carries: the lineups strats and clips
    ///     point at, and Strat Mining's dismissed and promoted patterns. The rest of the own folders is derived and
    ///     keyed by local paths (the team index, the mining detections and signatures), so the target rebuilds it.
    /// </summary>
    public static readonly PortableOwnPath[] PortableOwnPaths =
    [
        new(StoreRoot.Config, StratMiningFiles.StatePath),
        new(StoreRoot.Cache, GrenadeLineupStore.FileName)
    ];

    /// <summary>
    ///     After "delete extension data": the stores that stay loaded while the pack is off re-read what is
    ///     left, so switching back on in the same session shows nothing that was deleted and the next edit
    ///     does not save it back.
    /// </summary>
    /// <param name="sp">The composition root.</param>
    public static void ReloadLiveStores(IServiceProvider sp)
    {
        sp.GetService<StratStore>()?.RebuildIndexFromDisk();
        sp.GetService<TagStore>()?.RebuildIndexFromDisk();
        sp.GetService<TagPaletteStore>()?.Reload();
        sp.GetService<ScoutingReportNotesStore>()?.Reload();
        sp.GetService<VetoHistoryStore>()?.Reload();
        sp.GetService<TeamRanksStore>()?.Reload();
        sp.GetService<WatchedSituationsService>()?.Reload();
        sp.GetService<StratMiningService>()?.ResetState();
        sp.GetService<ProfileStore>()?.Reload();
    }
}
