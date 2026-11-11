#region

using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions.StratBook.Services.Portability;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using static DemoViewer.NET.AppTests.StratTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Library Transfer's strats import between two stores on disk: a new strat with its log, a team move committed
///     as a revision, an identical strat, a fast-forward, the three answers to a strat both machines changed, a
///     strat open in the Strat Book or deleted here, books and callouts, a fresh store that loads the result clean,
///     and a second import that changes nothing.
/// </summary>
public class StratsImportTests
{
    private sealed class Machines : IDisposable
    {
        private readonly string _root = TempRoot();

        public Machines()
        {
            There = new StratStore(PackStores.At(TherePath), utcNow: SteppingClock(Created));
            Here = new StratStore(PackStores.At(HerePath), utcNow: SteppingClock(Created.AddDays(1)));
        }

        public string TherePath => Path.Combine(_root, "there", "strats");
        public string HerePath => Path.Combine(_root, "here", "strats");
        public StratStore There { get; }
        public StratStore Here { get; }
        public List<string> Lines { get; } = [];

        public ImportLedger Import(ImportConflictPolicy policy = ImportConflictPolicy.KeepMine,
            TeamIdMap? teams = null)
        {
            ImportLedger ledger = new(policy, Lines.Add);
            Here.Import(new StagedFolder(TherePath), teams ?? TeamIdMap.Identity, ledger);
            return ledger;
        }

        public SortedDictionary<string, string> HereFiles() => StagedFolder.Snapshot(HerePath, "index.json");

        public void Dispose() => DeleteQuietly(_root);
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

    private static StratDocument Committed(StratStore store, StratOwner? owner = null)
    {
        StratDocument document = Minimal(owner: owner);
        StratSaveResult saved = store.Save(document, [], "created");
        return saved.Saved ? document : throw new InvalidOperationException(saved.Reason);
    }

    [Test]
    public async Task ANewStrat_IsCopiedWithItsLog_AndAFreshStoreLoadsItClean()
    {
        using Machines m = new();
        StratDocument strat = Committed(m.There);
        Rename(m.There, strat.Id, "A split");

        ImportLedger ledger = m.Import();

        StratStore restarted = new(PackStores.At(m.HerePath));
        StratLoadResult loaded = restarted.Load(strat.Id);
        using (Assert.Multiple())
        {
            await Assert.That(ledger.Added).IsEqualTo(1);
            await Assert.That(loaded.Document?.Name).IsEqualTo("A split");
            await Assert.That(loaded.Note).IsNull().Because("nothing needed reconciling");
            await Assert.That(loaded.IsUnreadable).IsFalse();
            await Assert.That(restarted.History(strat.Id).Count).IsEqualTo(2);
            await Assert.That(restarted.Index.Select(e => e.Id)).IsEquivalentTo([strat.Id]);
        }
    }

    [Test]
    public async Task ATeamTheImportMoved_IsCommittedAsARevision_AndTheStratMovesFolder()
    {
        using Machines m = new();
        Guid theirs = Guid.NewGuid();
        Guid ours = Guid.NewGuid();
        StratDocument strat = Committed(m.There, StratOwner.Team(theirs));

        m.Import(teams: new TeamIdMap(new Dictionary<Guid, Guid> { [theirs] = ours }));

        StratStore restarted = new(PackStores.At(m.HerePath));
        StratLoadResult loaded = restarted.Load(strat.Id);
        using (Assert.Multiple())
        {
            await Assert.That(loaded.Document!.Owner.TeamId).IsEqualTo(ours);
            await Assert.That(loaded.Note).IsNull();
            await Assert.That(restarted.History(strat.Id).Count).IsEqualTo(2)
                .Because("the move is a commit of its own");
            await Assert.That(Directory.Exists(Path.Combine(m.HerePath, $"team-{theirs:D}"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(m.HerePath, $"team-{ours:D}", "de_mirage",
                $"{strat.Id:D}{StratStore.StratExtension}"))).IsTrue();
        }
    }

    [Test]
    public async Task ASecondImport_ChangesNothing()
    {
        using Machines m = new();
        Guid theirs = Guid.NewGuid();
        TeamIdMap teams = new(new Dictionary<Guid, Guid> { [theirs] = Guid.NewGuid() });
        Committed(m.There);
        Committed(m.There, StratOwner.Team(theirs));
        m.Import(teams: teams);
        SortedDictionary<string, string> before = m.HereFiles();

        ImportLedger again = m.Import(teams: teams);

        await Assert.That(m.HereFiles()).IsEquivalentTo(before);
        await Assert.That(again.Added + again.Merged + again.Conflicts).IsEqualTo(0);
    }

    [Test]
    public async Task ALogThisMachineHoldsAPrefixOf_IsFastForwarded()
    {
        using Machines m = new();
        StratDocument strat = Committed(m.There);
        m.Import();
        Rename(m.There, strat.Id, "A split");
        Rename(m.There, strat.Id, "A split late");

        ImportLedger ledger = m.Import();

        StratStore restarted = new(PackStores.At(m.HerePath));
        using (Assert.Multiple())
        {
            await Assert.That(ledger.Merged).IsEqualTo(1);
            await Assert.That(m.Here.TryLoad(strat.Id)!.Name).IsEqualTo("A split late");
            await Assert.That(restarted.Load(strat.Id).Note).IsNull();
            await Assert.That(restarted.History(strat.Id).Select(e => e.Revision)).IsEquivalentTo([1, 2, 3]);
        }
    }

    [Test]
    public async Task BothChanged_KeepMine_KeepsThisMachines_AndSaysSo()
    {
        using Machines m = new();
        StratDocument strat = Committed(m.There);
        m.Import();
        Rename(m.There, strat.Id, "theirs");
        Rename(m.Here, strat.Id, "ours");

        ImportLedger ledger = m.Import();

        await Assert.That(m.Here.TryLoad(strat.Id)!.Name).IsEqualTo("ours");
        await Assert.That(ledger.Conflicts).IsEqualTo(1);
        await Assert.That(m.Lines.Any(l => l.Contains("kept this machine's 'ours'"))).IsTrue();
    }

    [Test]
    public async Task BothChanged_TakeImported_CommitsTheirsOnTopOfOurHistory()
    {
        using Machines m = new();
        StratDocument strat = Committed(m.There);
        m.Import();
        Rename(m.There, strat.Id, "theirs");
        Rename(m.Here, strat.Id, "ours");

        m.Import(ImportConflictPolicy.TakeImported);
        SortedDictionary<string, string> after = m.HereFiles();
        ImportLedger again = m.Import(ImportConflictPolicy.TakeImported);

        StratStore restarted = new(PackStores.At(m.HerePath));
        using (Assert.Multiple())
        {
            await Assert.That(restarted.Load(strat.Id).Document!.Name).IsEqualTo("theirs");
            await Assert.That(restarted.Load(strat.Id).Note).IsNull();
            await Assert.That(restarted.History(strat.Id).Select(e => e.Summary))
                .Contains("ours").Because("this machine's revision stays in the history");
            await Assert.That(m.HereFiles()).IsEquivalentTo(after);
            await Assert.That(again.Conflicts).IsEqualTo(0);
        }
    }

    [Test]
    public async Task BothChanged_KeepBoth_AddsTheImportedHeadOnce()
    {
        using Machines m = new();
        StratDocument strat = Committed(m.There);
        m.Import();
        Rename(m.There, strat.Id, "theirs");
        Rename(m.Here, strat.Id, "ours");

        m.Import(ImportConflictPolicy.KeepBoth);
        m.Import(ImportConflictPolicy.KeepBoth);

        IReadOnlyList<StratIndexEntry> index = new StratStore(PackStores.At(m.HerePath)).Index;
        using (Assert.Multiple())
        {
            await Assert.That(index.Select(e => e.Name)).IsEquivalentTo(["ours", "theirs (imported)"]);
            await Assert.That(index.Single(e => e.Id != strat.Id).Revision).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AStratOpenInTheStratBook_IsLeftAlone()
    {
        using Machines m = new();
        StratDocument strat = Committed(m.There);
        m.Import();
        Rename(m.There, strat.Id, "theirs");
        using StratSession session = new(m.Here, null, () => false, () => Created)
        {
            AutoSaveDelay = TimeSpan.FromHours(1), IdleCommitDelay = TimeSpan.FromHours(1)
        };
        session.Open(strat.Id);

        ImportLedger ledger = m.Import();

        await Assert.That(m.Here.TryLoad(strat.Id)!.Name).IsEqualTo("A exec");
        await Assert.That(ledger.Skipped).IsEqualTo(1);
        await Assert.That(m.Lines.Any(l => l.Contains("was open in the Strat Book"))).IsTrue();
    }

    [Test]
    public async Task AStratDeletedHere_IsNotBroughtBack()
    {
        using Machines m = new();
        StratDocument strat = Committed(m.There);
        m.Import();
        m.Here.Delete(strat.Id);

        m.Import();

        await Assert.That(m.Here.TryLoad(strat.Id)).IsNull();
        await Assert.That(m.Lines.Any(l => l.Contains("deleted on this machine"))).IsTrue();
    }

    [Test]
    public async Task Books_GainMissingEpochs_AndKeepTheirNotes_Callouts_UniteByAlias()
    {
        using Machines m = new();
        StratOwner owner = StratTestData.Team;
        m.There.SaveBook(new StratBook
        {
            Owner = owner,
            Notes = "their notes",
            SlotDefaults = { ["r1"] = new Dictionary<string, string> { ["A"] = "1" }, ["r2"] = new() { ["A"] = "2" } }
        });
        m.Here.SaveBook(new StratBook
        {
            Owner = owner, Notes = "our notes", SlotDefaults = { ["r1"] = new() { ["A"] = "9" } }
        });
        m.There.SaveCallouts(owner, new CalloutTable
        {
            Map = "de_mirage",
            Aliases =
            {
                new CalloutAlias { Alias = "Window", Place = "TopofMid" },
                new CalloutAlias { Alias = "palace", Place = "Palace" }
            }
        });
        m.Here.SaveCallouts(owner, new CalloutTable
        {
            Map = "de_mirage", Aliases = { new CalloutAlias { Alias = "window", Place = "Window" } }
        });

        m.Import();
        SortedDictionary<string, string> after = m.HereFiles();
        m.Import();

        StratBook book = m.Here.LoadBook(owner);
        CalloutTable callouts = m.Here.LoadCallouts(owner, "de_mirage");
        using (Assert.Multiple())
        {
            await Assert.That(book.Notes).IsEqualTo("our notes");
            await Assert.That(book.SlotDefaults["r1"]["A"]).IsEqualTo("9");
            await Assert.That(book.SlotDefaults["r2"]["A"]).IsEqualTo("2");
            await Assert.That(callouts.Aliases.Select(a => $"{a.Alias}={a.Place}"))
                .IsEquivalentTo(["window=Window", "palace=Palace"]);
            await Assert.That(m.HereFiles()).IsEquivalentTo(after);
        }
    }
}
