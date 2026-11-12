#region

using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Portability;

/// <summary>
///     The stores a Strat Book import writes into. Any of them may be null, which skips its step: the browser has
///     none, and a test passes only the ones it checks.
/// </summary>
internal sealed record StratBookImportTargets(
    TeamIdentityService? Teams,
    GrenadeIndex? Lineups,
    StratStore? Strats,
    ScoutingReportNotesStore? Notes,
    VetoHistoryStore? Vetoes,
    TeamRanksStore? Ranks,
    WatchedSituationsService? Watches,
    StratMiningService? Mining,
    LineupClipService? Clips,
    TagStore? Tags);

/// <summary>
///     The Strat Book's side of a Library Transfer import. The steps run in the order later ones need: teams first,
///     since strats, notes, vetoes, ranks and watches point at them; then lineups, since strat steps and clips do;
///     then strats, the stores that name teams, Strat Mining (whose promotions name strats), the clips and the
///     demos' tags. Each step reads the bundle's copy and settles every item against one ledger, whose counts are
///     the result. Ids are random Guids or derived from the item, so a second import of the same bundle changes
///     nothing.
/// </summary>
internal sealed class StratBookDataPortability : IExtensionDataPortability
{
    private readonly Func<Action, Task> _onUi;
    private readonly Func<StratBookImportTargets> _targets;

    /// <param name="targets">The stores to write into, read when an import starts.</param>
    /// <param name="onUi">Runs work on the UI thread, where the Scouting Report's stores mutate; inline when null.</param>
    public StratBookDataPortability(Func<StratBookImportTargets> targets, Func<Action, Task>? onUi = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _targets = targets;
        _onUi = onUi ?? (static work =>
        {
            work();
            return Task.CompletedTask;
        });
    }

    public string FeatureId => StratBookPack.PackFeatureId;

    /// <summary>
    ///     UI-thread work as a task, over a post that only queues it: the import job waits for the Scouting Report's
    ///     stores to take their part before it moves on.
    /// </summary>
    /// <param name="post">Queues work onto the UI thread.</param>
    public static Func<Action, Task> Posting(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return work =>
        {
            TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            post(() =>
            {
                try
                {
                    work();
                    done.SetResult();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    done.SetException(e);
                }
            });
            return done.Task;
        };
    }

    public IReadOnlyList<PortableOwnPath> OwnPaths => StratBookStores.PortableOwnPaths;

    public async Task<ExtensionImportResult> MergeAsync(IExtensionImport import, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(import);
        StratBookImportTargets to = _targets();
        // Before the lifecycle attaches Team Identity, teams merged into it are never saved while the stores that
        // point at them would be rewritten to their ids. Nothing is imported until it is up.
        if (to.Teams is { IsAttached: false })
        {
            import.Report("the Strat Book is still starting, so nothing of it was imported; import the bundle again "
                          + "in a moment");
            return new ExtensionImportResult(0, 0, 0, 0);
        }

        ImportLedger ledger = new(import.Policy, import.Report);
        IExtensionFolder? strats = Within(import.Staged(StratBookStores.Strats), StratBookStores.Strats);

        TeamIdMap teams = to.Teams?.ImportTeams(import.Staged(StratBookStores.Teams), ledger,
            ReferencedTeams(import, strats)) ?? TeamIdMap.Identity;
        ct.ThrowIfCancellationRequested();

        IReadOnlyDictionary<Guid, Guid> lineupMap = new Dictionary<Guid, Guid>();
        if (to.Lineups is { } lineups && import.StagedOwn(StoreRoot.Cache) is { } ownCache)
        {
            LineupImport result = lineups.ImportLineups(ownCache, import.Policy);
            lineupMap = result.LineupMap;
            Count(result.Added, ledger.CountAdded);
            Count(result.Aliased + result.Paths, ledger.CountMerged);
            Count(result.Skipped, ledger.CountSkipped);
        }

        ct.ThrowIfCancellationRequested();
        to.Strats?.Import(strats, teams, ledger);
        ct.ThrowIfCancellationRequested();

        // The Scouting Report's stores mutate on the UI thread only, as every edit to them does.
        await _onUi(() =>
        {
            to.Notes?.Import(import.Staged(StratBookStores.ScoutingReportNotes), teams, ledger);
            to.Vetoes?.Import(import.Staged(StratBookStores.VetoHistory), teams, ledger);
            to.Ranks?.Import(import.Staged(StratBookStores.TeamRanks), teams, ledger);
            to.Watches?.Import(import.Staged(StratBookStores.WatchedSituations), teams, ledger);
        }).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        to.Mining?.Import(import.StagedOwn(StoreRoot.Config), ledger);
        ct.ThrowIfCancellationRequested();

        if (to.Clips is { } clips && import.Staged(StratBookStores.LineupClips) is { } stagedClips)
        {
            (int added, int skipped) = clips.ImportPairs(stagedClips, PathOf(StratBookStores.LineupClips), lineupMap);
            Count(added, ledger.CountAdded);
            Count(skipped, ledger.CountSkipped);
        }

        if (to.Tags is { } tags && Within(import.Staged(StratBookStores.Tags), StratBookStores.Tags) is { } stagedTags)
        {
            foreach (string sha in import.DemoSha256s.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                TagImportResult result = tags.Import(sha, stagedTags, import.Policy);
                Settle(result.Tags, ledger);
                Settle(result.Verdicts, ledger);
                foreach (string note in result.Notes)
                {
                    ledger.Report(note);
                }
            }
        }

        return ledger.Result;
    }

    // The imported teams another store of the bundle points at, so the teams import keeps them even when clustering
    // made them: a strat's owner folder, a note's, veto's or rank's team, a watch's opponent.
    private static HashSet<Guid> ReferencedTeams(IExtensionImport import, IExtensionFolder? strats)
    {
        HashSet<Guid> referenced = [];
        if (strats is not null)
        {
            foreach (string folder in strats.EnumerateDirectories(""))
            {
                string name = Path.GetFileName(folder);
                if (name.StartsWith("team-", StringComparison.Ordinal)
                    && Guid.TryParseExact(name["team-".Length..], "D", out Guid id))
                {
                    referenced.Add(id);
                }
            }
        }

        // Read only to find the ids; the store's own import reports a file that does not read.
        ImportLedger quiet = new(import.Policy, static _ => { });
        if (quiet.ReadJson<ScoutingReportNotesFile>(import.Staged(StratBookStores.ScoutingReportNotes),
                ScoutingReportNotesStore.FileName, ScoutingReportNotesFile.JsonOptions) is { } notes)
        {
            referenced.UnionWith(notes.Teams.Select(t => t.TeamId));
        }

        if (quiet.ReadJson<VetoHistoryFile>(import.Staged(StratBookStores.VetoHistory), VetoHistoryStore.FileName,
                VetoHistoryFile.JsonOptions) is { } vetoes)
        {
            referenced.UnionWith(vetoes.Entries.Select(e => e.OpponentTeamId));
        }

        if (quiet.ReadJson<TeamRanksFile>(import.Staged(StratBookStores.TeamRanks), TeamRanksStore.FileName,
                TeamRanksFile.JsonOptions) is { } ranks)
        {
            referenced.UnionWith(ranks.Entries.Select(e => e.TeamId));
        }

        if (quiet.ReadJson<WatchedSituationsFile>(import.Staged(StratBookStores.WatchedSituations),
                WatchedSituationsService.FileName, WatchedSituationsFile.JsonOptions) is { } watches)
        {
            referenced.UnionWith(watches.Watches.Select(w => w.Filters.Opponent).OfType<Guid>());
        }

        return referenced;
    }

    private static void Settle(TagImportOutcome outcome, ImportLedger ledger)
    {
        switch (outcome)
        {
            case TagImportOutcome.Added:
                ledger.CountAdded();
                break;
            case TagImportOutcome.Replaced or TagImportOutcome.Merged:
                ledger.CountMerged();
                break;
            case TagImportOutcome.KeptMine:
                ledger.CountConflict();
                break;
            case TagImportOutcome.Unchanged or TagImportOutcome.CheckedOut or TagImportOutcome.Refused
                or TagImportOutcome.Failed:
                ledger.CountSkipped();
                break;
        }
    }

    private static void Count(int times, Action count)
    {
        for (int i = 0; i < times; i++)
        {
            count();
        }
    }

    // The one path a folder store declares, under the config root.
    private static string PathOf(string storeId) =>
        StratBookStores.All.Single(s => string.Equals(s.Id, storeId, StringComparison.Ordinal)).Paths[0];

    // A store's staged copy is laid out as the config root is; the store itself works inside its own folder.
    private static Subfolder? Within(IExtensionFolder? staged, string storeId) =>
        staged is null ? null : new Subfolder(staged, PathOf(storeId));

    /// <summary>A read-only view of one folder inside a staged store, with paths relative to that folder.</summary>
    private sealed class Subfolder(IExtensionFolder inner, string prefix) : IExtensionFolder
    {
        public string Path => System.IO.Path.Combine(inner.Path, prefix);

        public void Create() => throw ReadOnly();

        public bool FileExists(string relativePath) => inner.FileExists(In(relativePath));

        public bool DirectoryExists(string relativePath) => inner.DirectoryExists(In(relativePath));

        public DateTime? LastWriteTimeUtc(string relativePath) => inner.LastWriteTimeUtc(In(relativePath));

        public IEnumerable<string> EnumerateFiles(string relativePath, string searchPattern) =>
            inner.EnumerateFiles(In(relativePath), searchPattern).Select(Out);

        public IEnumerable<string> EnumerateDirectories(string relativePath) =>
            inner.EnumerateDirectories(In(relativePath)).Select(Out);

        public byte[]? Read(string relativePath) => inner.Read(In(relativePath));

        public Stream? OpenRead(string relativePath) => inner.OpenRead(In(relativePath));

        public void WriteAtomic(string relativePath, ReadOnlySpan<byte> content) => throw ReadOnly();

        public void WriteAtomic(string relativePath, Action<Stream> write) => throw ReadOnly();

        public void Append(string relativePath, string line) => throw ReadOnly();

        public void Move(string fromRelativePath, string toRelativePath, bool overwrite) => throw ReadOnly();

        public void Delete(string relativePath) => throw ReadOnly();

        public void SetLastWriteTimeUtc(string relativePath, DateTime value) => throw ReadOnly();

        private string In(string relativePath)
        {
            ArgumentNullException.ThrowIfNull(relativePath);
            return relativePath.Length == 0 || relativePath == "." ? prefix : prefix + "/" + relativePath;
        }

        // The inner folder lists paths from its own root; this one lists them from the subfolder.
        private string Out(string path) =>
            path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                        && path[prefix.Length] is '/' or '\\'
                ? path[(prefix.Length + 1)..]
                : path;

        private static InvalidOperationException ReadOnly() => new("A bundle's copy of a store is read-only.");
    }
}
