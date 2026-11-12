#region

using System.Text.Json;
using System.Text.Json.Nodes;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.Portability;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strat Book's whole import between two machines on disk: one root's teams, strats with history, lineups
///     and their clips, notes and tags, byte-copied into the layout a bundle hands over and merged into a second
///     root. Everything arrives, the imported team lands on the local one with the same five everywhere it is
///     named, the strat's history replays, the clips outlive the sweep, a second import changes nothing, and
///     keep mine leaves every local edit alone.
/// </summary>
[NotInParallel]
public class StratBookDataPortabilityTests
{
    private const string Mirage = "de_mirage";
    private const string Technique = "stand-throw-left";
    private const string Sha = TagTestData.Sha;

    private static readonly string[] _five = ["76561", "76562", "76563", "76564", "76565"];
    private static readonly Guid _lineup = Guid.Parse("5c1a0000-0000-4000-8000-000000000001");

    private static readonly Func<Action, Task> _inline = work =>
    {
        work();
        return Task.CompletedTask;
    };

    // One machine's stores over a config root and the extension's two own folders, opened the way the pack opens
    // them. Its teams file is written before the service first reads it.
    private sealed class Machine : IDisposable
    {
        public Machine(string root, Team team)
        {
            Config = Path.Combine(root, "config");
            OwnConfig = Path.Combine(root, "own-config");
            OwnCache = Path.Combine(root, "own-cache");
            Directory.CreateDirectory(Config);
            Directory.CreateDirectory(OwnConfig);
            Directory.CreateDirectory(OwnCache);
            File.WriteAllBytes(Path.Combine(Config, TeamIdentityService.TeamsFileName),
                JsonSerializer.SerializeToUtf8Bytes(new TeamsFile { Teams = { team } }, TeamsFile.JsonOptions));
            Teams = new TeamIdentityService(TestFiles.Teams(Config), new DemoCacheStore(null).Library(), run: _inline);
            LineupStore = new GrenadeLineupStore(OwnCache);
            Lineups = new GrenadeIndex(new DemoCacheStore(null).Library(), lineups: LineupStore,
                scheduleSave: _inline);
            Lineups.Load();
            Strats = new StratStore(PackStores.At(StratsPath));
            Notes = new ScoutingReportNotesStore(
                StoredFile.At(Path.Combine(Config, ScoutingReportNotesStore.FileName)));
            Vetoes = new VetoHistoryStore(StoredFile.At(Path.Combine(Config, VetoHistoryStore.FileName)));
            Ranks = new TeamRanksStore(StoredFile.At(Path.Combine(Config, TeamRanksStore.FileName)));
            Tags = new TagStore(PackStores.At(TagsPath));
            Clips = new LineupClipService(() => [], PackStores.At(ClipsPath), () => true, new NoRenderer(),
                orphanGrace: TimeSpan.Zero, storedLineups: Lineups.StoredLineupIds);
        }

        public string Config { get; }
        public string OwnConfig { get; }
        public string OwnCache { get; }
        public string StratsPath => Path.Combine(Config, "strats");
        public string TagsPath => Path.Combine(Config, "tags");
        public string ClipsPath => Path.Combine(Config, LineupClipService.DirectoryName);
        public TeamIdentityService Teams { get; }
        public GrenadeLineupStore LineupStore { get; }
        public GrenadeIndex Lineups { get; }
        public StratStore Strats { get; }
        public ScoutingReportNotesStore Notes { get; }
        public VetoHistoryStore Vetoes { get; }
        public TeamRanksStore Ranks { get; }
        public TagStore Tags { get; }
        public LineupClipService Clips { get; }

        public StratBookImportTargets Targets =>
            new(Teams, Lineups, Strats, Notes, Vetoes, Ranks, null, null, Clips, Tags);

        // Every file the import may write, the derived indexes aside.
        public SortedDictionary<string, string> Files()
        {
            SortedDictionary<string, string> files = StagedFolder.Snapshot(Config, "index.json", "team-index.json");
            foreach ((string path, string bytes) in StagedFolder.Snapshot(OwnCache))
            {
                files["own-cache/" + path] = bytes;
            }

            return files;
        }

        public void Dispose()
        {
            Clips.Dispose();
            Lineups.Dispose();
            Teams.Dispose();
        }
    }

    private sealed class Machines : IDisposable
    {
        private readonly string _root = StratTestData.TempRoot();

        public Machines()
        {
            Team theirs = FiveOf(Theirs, "Theirs");
            Team ours = FiveOf(Ours, "Ours");
            There = new Machine(Path.Combine(_root, "there"), theirs);
            Here = new Machine(Path.Combine(_root, "here"), ours);
        }

        public Guid Theirs { get; } = Guid.NewGuid();
        public Guid Ours { get; } = Guid.NewGuid();
        public Machine There { get; }
        public Machine Here { get; }
        public string Bundle => Path.Combine(_root, "bundle");

        // The export's byte copy: each store that travels, as its declared path under the config root, its
        // excluded names left behind, and the own files the pack names.
        public void Export()
        {
            if (Directory.Exists(Bundle))
            {
                Directory.Delete(Bundle, true);
            }

            foreach (StoreDescriptor store in StratBookStores.All.Where(s => s.Portability != StorePortability.None))
            {
                Copy(Path.Combine(There.Config, store.Paths[0]),
                    Path.Combine(Bundle, "stores", store.Id, store.Paths[0]), store.PortableExcludes);
            }

            foreach (PortableOwnPath own in StratBookStores.PortableOwnPaths)
            {
                string from = own.Root == StoreRoot.Cache ? There.OwnCache : There.OwnConfig;
                Copy(Path.Combine(from, own.Path), Path.Combine(Bundle, "own", own.Root.ToString(), own.Path), []);
            }
        }

        public async Task<(ExtensionImportResult Result, List<string> Lines)> Import(
            ImportConflictPolicy policy = ImportConflictPolicy.KeepMine)
        {
            Staging import = new(Bundle, policy);
            ExtensionImportResult result =
                await new StratBookDataPortability(() => Here.Targets, _inline).MergeAsync(import,
                    CancellationToken.None);
            return (result, import.Lines);
        }

        public void Dispose()
        {
            There.Dispose();
            Here.Dispose();
            StratTestData.DeleteQuietly(_root);
        }

        private static void Copy(string from, string to, IReadOnlyList<string> excludes)
        {
            if (excludes.Contains(Path.GetFileName(from), StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            if (File.Exists(from))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
            }
            else if (Directory.Exists(from))
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(from))
                {
                    Copy(entry, Path.Combine(to, Path.GetFileName(entry)), excludes);
                }
            }
        }
    }

    // What the host hands the merge, over the copied folders.
    private sealed class Staging(string bundle, ImportConflictPolicy policy) : IExtensionImport
    {
        public List<string> Lines { get; } = [];

        public ImportConflictPolicy Policy => policy;

        public IReadOnlySet<string> DemoSha256s { get; } = new HashSet<string>([Sha], StringComparer.Ordinal);

        public IExtensionFolder? Staged(string storeId) => At(Path.Combine(bundle, "stores", storeId));

        public IExtensionFolder? StagedOwn(StoreRoot root) => At(Path.Combine(bundle, "own", root.ToString()));

        public void Report(string line) => Lines.Add(line);

        private static StagedFolder? At(string path) => Directory.Exists(path) ? new StagedFolder(path) : null;
    }

    private static Team FiveOf(Guid id, string name)
    {
        Team team = new() { Id = id, Name = name, NameSource = TeamNameSource.User };
        team.Rosters.Add(new Roster { Id = "r1", CoreLineup = [.. _five] });
        return team;
    }

    private static string Stem => LineupClipPlanner.FileStem(Mirage, GrenadeKind.Smoke, _lineup, Technique);

    private static string ClipGif(Machine machine) =>
        Path.Combine(machine.ClipsPath, Stem + LineupClipPlanner.GifExtension);

    private static StratDocument Committed(StratStore store, StratOwner owner)
    {
        StratDocument document = StratTestData.Minimal(owner: owner);
        StratSaveResult saved = store.Save(document, [], "created");
        return saved.Saved ? document : throw new InvalidOperationException(saved.Reason);
    }

    private static void Rename(StratStore store, Guid id, string name)
    {
        StratDocument document = store.TryLoad(id)!;
        string old = document.Name;
        document.Name = name;
        StratSaveResult saved =
            store.Save(document, [PatchOp.ReplaceOp("/name", JsonValue.Create(old), JsonValue.Create(name))], name);
        if (!saved.Saved)
        {
            throw new InvalidOperationException(saved.Reason);
        }
    }

    // The other machine's library: a strat of its team with a second revision, a lineup with a clip, notes on the
    // team and a demo's tags.
    private static StratDocument Populate(Machines m)
    {
        StratDocument strat = Committed(m.There.Strats, StratOwner.Team(m.Theirs));
        Rename(m.There.Strats, strat.Id, "A split");

        m.There.LineupStore.For(Mirage).Anchors.Add(new LineupAnchor(_lineup, GrenadeKind.Smoke,
            new WorldPoint(512, 288, -160), new WorldPoint(-1400, -1400, -170), Sha + "/a"));
        m.There.LineupStore.Save();
        Directory.CreateDirectory(m.There.ClipsPath);
        File.WriteAllText(ClipGif(m.There), "gif");
        File.WriteAllText(Path.Combine(m.There.ClipsPath, Stem + LineupClipPlanner.SetposExtension), "setpos");

        m.There.Notes.SetSummary(m.Theirs, "Fast A");
        m.There.Notes.AddNote(m.Theirs, "smoke window early");

        m.There.Tags.Save(TagTestData.Document(Sha, TagTestData.Instance("A execute", 100, 200)));
        m.Export();
        return strat;
    }

    [Test]
    public async Task TwoRoots_EverythingArrives_TeamsLandOnTheLocalTeam_HistoryReplays_AndClipsSurviveTheSweep()
    {
        using Machines m = new();
        StratDocument strat = Populate(m);

        (ExtensionImportResult result, List<string> lines) = await m.Import();

        StratStore restarted = new(PackStores.At(m.Here.StratsPath));
        StratLoadResult loaded = restarted.Load(strat.Id);
        using (Assert.Multiple())
        {
            await Assert.That(result.Added).IsGreaterThan(0).Because(string.Join("; ", lines));
            await Assert.That(m.Here.Teams.AllTeams.Select(t => t.Id)).IsEquivalentTo([m.Ours])
                .Because("the same five make the same team");
            await Assert.That(loaded.Document?.Name).IsEqualTo("A split");
            await Assert.That(loaded.Document?.Owner.TeamId).IsEqualTo(m.Ours);
            await Assert.That(loaded.Note).IsNull();
            await Assert.That(restarted.History(strat.Id).Count).IsEqualTo(3)
                .Because("both imported commits replay, and the move onto the local team is a third");
            await Assert.That(m.Here.Notes.TeamIds).IsEquivalentTo([m.Ours]);
            await Assert.That(m.Here.Notes.For(m.Ours).Summary).IsEqualTo("Fast A");
            await Assert.That(m.Here.Notes.For(m.Ours).Notes.Select(n => n.Text))
                .IsEquivalentTo(["smoke window early"]);
            await Assert.That(m.Here.LineupStore.For(Mirage).Anchors.Select(a => a.Id)).Contains(_lineup);
            await Assert.That(File.ReadAllText(ClipGif(m.Here))).IsEqualTo("gif");
            await Assert.That(m.Here.Tags.TryLoad(Sha)?.Instances.Count).IsEqualTo(1);
        }

        m.Here.Clips.Plan();
        await m.Here.Clips.SweepTask;
        await Assert.That(File.Exists(ClipGif(m.Here))).IsTrue()
            .Because("the imported lineup is stored, so its clip waits for its demos");
    }

    [Test]
    public async Task ASecondImportOfTheSameBundle_ChangesNothing()
    {
        using Machines m = new();
        Populate(m);
        await m.Import();
        SortedDictionary<string, string> before = m.Here.Files();

        (ExtensionImportResult again, List<string> lines) = await m.Import();

        using (Assert.Multiple())
        {
            await Assert.That(m.Here.Files()).IsEquivalentTo(before);
            // The teams still differ by name, and keep mine says so again; nothing is written for it.
            await Assert.That(again.Added + again.Merged).IsEqualTo(0).Because(string.Join("; ", lines));
        }
    }

    [Test]
    public async Task KeepMine_NeverOverwritesALocalEdit()
    {
        using Machines m = new();
        StratDocument strat = Populate(m);
        await m.Import();

        // Both machines edit the same things after the first transfer.
        Rename(m.Here.Strats, strat.Id, "Our split");
        m.Here.Notes.SetSummary(m.Ours, "Slow B");
        m.Here.Tags.Save(TagTestData.Document(Sha, TagTestData.Instance("B default", 300, 400)));
        Rename(m.There.Strats, strat.Id, "Their split");
        m.There.Notes.SetSummary(m.Theirs, "Fast A again");
        m.There.Tags.Save(TagTestData.Document(Sha, TagTestData.Instance("A rush", 500, 600)));
        m.Export();
        string tags = File.ReadAllText(TagStore.SidecarPathFor(m.Here.TagsPath, Sha));

        (ExtensionImportResult result, List<string> lines) = await m.Import();

        using (Assert.Multiple())
        {
            await Assert.That(new StratStore(PackStores.At(m.Here.StratsPath)).Load(strat.Id).Document?.Name)
                .IsEqualTo("Our split");
            await Assert.That(m.Here.Notes.For(m.Ours).Summary).IsEqualTo("Slow B");
            await Assert.That(File.ReadAllText(TagStore.SidecarPathFor(m.Here.TagsPath, Sha))).IsEqualTo(tags);
            await Assert.That(result.Conflicts).IsGreaterThan(0).Because(string.Join("; ", lines));
        }
    }

    [Test]
    public async Task BeforeTeamIdentityIsAttached_NothingIsImported()
    {
        using Machines m = new();
        Populate(m);
        SortedDictionary<string, string> before = m.Here.Files();
        using TeamIdentityService detached = new(TestFiles.Teams(m.Here.Config), new DemoCacheStore(null).Library(),
            run: _inline, loadAtStart: false);
        Staging import = new(m.Bundle, ImportConflictPolicy.KeepMine);

        ExtensionImportResult result = await new StratBookDataPortability(
            () => m.Here.Targets with { Teams = detached }, _inline).MergeAsync(import, CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(result.Added + result.Merged).IsEqualTo(0);
            await Assert.That(m.Here.Files()).IsEquivalentTo(before);
            await Assert.That(import.Lines.Single()).Contains("still starting");
        }
    }

    private sealed class NoRenderer : ILineupClipRenderer
    {
        public Task<IReadOnlyList<LineupClipJob>> RenderAsync(string demoPath, ParsedDemo? demo,
            IReadOnlyList<LineupClipJob> jobs, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LineupClipJob>>([]);
    }
}
