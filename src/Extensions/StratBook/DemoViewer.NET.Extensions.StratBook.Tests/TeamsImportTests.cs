#region

using System.Text.Json;
using DemoViewer.NET.Extensions.StratBook.Services.Portability;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Library Transfer's teams import: each rule that matches an imported team to a local one, the us mark, the
///     teams an import adds and the ones it leaves out, the overrides it takes and the path-keyed ones it drops,
///     and a second import that changes nothing.
/// </summary>
public class TeamsImportTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly string[] _five = ["76561", "76562", "76563", "76564", "76565"];

    private sealed class Run : IDisposable
    {
        public required string Root { get; init; }
        public required TeamIdentityService Teams { get; init; }
        public required StagedFolder Staged { get; init; }
        public List<string> Lines { get; } = [];

        public string TeamsPath => Path.Combine(Root, "here", TeamIdentityService.TeamsFileName);

        public TeamsFile OnDisk =>
            JsonSerializer.Deserialize<TeamsFile>(File.ReadAllBytes(TeamsPath), TeamsFile.JsonOptions)!;

        public ImportLedger Ledger(ImportConflictPolicy policy = ImportConflictPolicy.KeepMine) =>
            new(policy, Lines.Add);

        public void Dispose()
        {
            Teams.Dispose();
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    // Both files are written before the service reads, the way a machine with teams of its own meets a bundle.
    private static Run Setup(TeamsFile here, TeamsFile there)
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-teams-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "here"));
        Directory.CreateDirectory(Path.Combine(root, "there"));
        File.WriteAllBytes(Path.Combine(root, "here", TeamIdentityService.TeamsFileName),
            JsonSerializer.SerializeToUtf8Bytes(here, TeamsFile.JsonOptions));
        File.WriteAllBytes(Path.Combine(root, "there", TeamIdentityService.TeamsFileName),
            JsonSerializer.SerializeToUtf8Bytes(there, TeamsFile.JsonOptions));
        TeamIdentityService teams = new(TestFiles.Teams(Path.Combine(root, "here")), new DemoCacheStore(null).Library(),
            run: _inline);
        return new Run { Root = root, Teams = teams, Staged = new StagedFolder(Path.Combine(root, "there")) };
    }

    private static Team Named(Guid id, string name, string notes = "") =>
        new() { Id = id, Name = name, NameSource = TeamNameSource.User, Notes = notes };

    [Test]
    public async Task SameId_KeepsMine_AndMapsToItself()
    {
        Guid id = Guid.NewGuid();
        using Run run = Setup(new TeamsFile { Teams = { Named(id, "Alpha", "ours") } },
            new TeamsFile { Teams = { Named(id, "Alpha FC", "theirs") } });

        ImportLedger ledger = run.Ledger();
        TeamIdMap map = run.Teams.ImportTeams(run.Staged, ledger);

        Team only = run.Teams.AllTeams.Single();
        using (Assert.Multiple())
        {
            await Assert.That(map.Map(id)).IsEqualTo(id);
            await Assert.That(only.Name).IsEqualTo("Alpha");
            await Assert.That(only.Notes).IsEqualTo("ours");
            await Assert.That(ledger.Conflicts).IsEqualTo(1);
        }
    }

    [Test]
    public async Task TakeImported_TakesTheNameAndNotes()
    {
        Guid id = Guid.NewGuid();
        using Run run = Setup(new TeamsFile { Teams = { Named(id, "Alpha", "ours") } },
            new TeamsFile { Teams = { Named(id, "Alpha FC", "theirs") } });

        run.Teams.ImportTeams(run.Staged, run.Ledger(ImportConflictPolicy.TakeImported));

        Team only = run.Teams.AllTeams.Single();
        await Assert.That(only.Name).IsEqualTo("Alpha FC");
        await Assert.That(only.Notes).IsEqualTo("theirs");
    }

    [Test]
    public async Task KeepMine_TakesATypedName_OverOneThisMachineDerived()
    {
        Guid id = Guid.NewGuid();
        Team derived = Named(id, "ALPHA");
        derived.NameSource = TeamNameSource.ClanTag;
        using Run run = Setup(new TeamsFile { Teams = { derived } },
            new TeamsFile { Teams = { Named(id, "Alpha FC") } });

        ImportLedger ledger = run.Ledger();
        run.Teams.ImportTeams(run.Staged, ledger);

        Team only = run.Teams.AllTeams.Single();
        using (Assert.Multiple())
        {
            // Keep mine guards what the user wrote; dropping the typed name would lose the only copy of it here.
            await Assert.That(only.Name).IsEqualTo("Alpha FC");
            await Assert.That(only.NameSource).IsEqualTo(TeamNameSource.User);
            await Assert.That(ledger.Conflicts).IsEqualTo(0);
            await Assert.That(ledger.Merged).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AnIdThisMachineMergedAway_MapsToItsHeir()
    {
        Guid heir = Guid.NewGuid();
        Guid folded = Guid.NewGuid();
        Team local = Named(heir, "Heir");
        local.MergedFrom.Add(folded);
        using Run run = Setup(new TeamsFile { Teams = { local }, Tombstones = { folded } },
            new TeamsFile { Teams = { Named(folded, "Folded") } });

        TeamIdMap map = run.Teams.ImportTeams(run.Staged, run.Ledger());

        await Assert.That(map.Map(folded)).IsEqualTo(heir);
        await Assert.That(run.Teams.AllTeams.Select(t => t.Id)).IsEquivalentTo([heir]);
    }

    [Test]
    public async Task ATeamTheOtherMachineMergedIntoItsOwn_MapsHere_AndIsRecorded()
    {
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        Team incoming = Named(theirs, "Their name");
        incoming.MergedFrom.Add(mine);
        using Run run = Setup(new TeamsFile { Teams = { Named(mine, "Mine") } },
            new TeamsFile { Teams = { incoming }, Tombstones = { mine } });

        TeamIdMap map = run.Teams.ImportTeams(run.Staged, run.Ledger());

        Team only = run.Teams.AllTeams.Single();
        TeamsFile disk = run.OnDisk;
        using (Assert.Multiple())
        {
            await Assert.That(map.Map(theirs)).IsEqualTo(mine);
            await Assert.That(only.Id).IsEqualTo(mine);
            await Assert.That(only.MergedFrom).Contains(theirs);
            await Assert.That(disk.Tombstones).Contains(theirs);
            await Assert.That(disk.Tombstones).DoesNotContain(mine).Because("a live team is never tombstoned");
        }
    }

    [Test]
    public async Task BothUs_Match_AndOneUsRemains()
    {
        Guid ours = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        Team here = Named(ours, "Us");
        here.IsUs = true;
        Team there = Named(theirs, "Us too");
        there.IsUs = true;
        using Run run = Setup(new TeamsFile { Teams = { here } }, new TeamsFile { Teams = { there } });

        TeamIdMap map = run.Teams.ImportTeams(run.Staged, run.Ledger());

        await Assert.That(map.Map(theirs)).IsEqualTo(ours);
        await Assert.That(run.Teams.AllTeams.Count(t => t.IsUs)).IsEqualTo(1);
        await Assert.That(run.Teams.Us!.Name).IsEqualTo("Us");
    }

    [Test]
    public async Task AnImportedUs_IsUs_WhenThisMachineHasNone()
    {
        Guid theirs = Guid.NewGuid();
        Team there = Named(theirs, "Us");
        there.IsUs = true;
        using Run run = Setup(new TeamsFile(), new TeamsFile { Teams = { there } });

        run.Teams.ImportTeams(run.Staged, run.Ledger());

        await Assert.That(run.Teams.Us?.Id).IsEqualTo(theirs);
    }

    [Test]
    public async Task TheSameFixedFive_OrTheSameSquad_Match()
    {
        Guid fiveHere = Guid.NewGuid();
        Guid fiveThere = Guid.NewGuid();
        Guid squadHere = Guid.NewGuid();
        Guid squadThere = Guid.NewGuid();
        Team a = Named(fiveHere, "Five");
        a.Rosters.Add(new Roster { Id = "r1", CoreLineup = [.. _five] });
        Team b = Named(fiveThere, "Five elsewhere");
        b.Rosters.Add(new Roster { Id = "r1", CoreLineup = [.. _five.Reverse()] });
        Team c = Named(squadHere, "Trio");
        c.Rosters.Add(new Roster { Id = "squad", Squad = ["7", "8", "9"] });
        Team d = Named(squadThere, "Trio elsewhere");
        d.Rosters.Add(new Roster { Id = "squad", Squad = ["9", "8", "7"] });
        using Run run = Setup(new TeamsFile { Teams = { a, c } }, new TeamsFile { Teams = { b, d } });

        TeamIdMap map = run.Teams.ImportTeams(run.Staged, run.Ledger());

        using (Assert.Multiple())
        {
            await Assert.That(map.Map(fiveThere)).IsEqualTo(fiveHere);
            await Assert.That(map.Map(squadThere)).IsEqualTo(squadHere);
            await Assert.That(run.Teams.AllTeams.Count).IsEqualTo(2);
        }
    }

    [Test]
    public async Task AnUnmatchedUserTeam_IsAdded_AndAnAutoTeamOnlyWhenSomethingPointsAtIt()
    {
        Guid user = Guid.NewGuid();
        Guid autoAlone = Guid.NewGuid();
        Guid autoNamed = Guid.NewGuid();
        using Run run = Setup(new TeamsFile(), new TeamsFile
        {
            Teams =
            {
                Named(user, "Named there"),
                new Team { Id = autoAlone, Name = "auto 1" },
                new Team { Id = autoNamed, Name = "auto 2" }
            }
        });

        ImportLedger ledger = run.Ledger();
        TeamIdMap map = run.Teams.ImportTeams(run.Staged, ledger, new HashSet<Guid> { autoNamed });

        using (Assert.Multiple())
        {
            await Assert.That(run.Teams.AllTeams.Select(t => t.Id)).IsEquivalentTo([user, autoNamed]);
            await Assert.That(map.Map(user)).IsEqualTo(user);
            await Assert.That(ledger.Added).IsEqualTo(2);
            await Assert.That(ledger.Skipped).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Overrides_ByHashAreTaken_ThisMachinesAreKept_PathKeyedOnesAreDropped()
    {
        Guid ours = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        Team incoming = Named(theirs, "Theirs");
        incoming.MergedFrom.Add(ours);
        using Run run = Setup(
            new TeamsFile
            {
                Teams = { Named(ours, "Ours") },
                Overrides = { new TeamOverride { DemoSha256 = ShaB, Side = 3, TeamId = null } }
            },
            new TeamsFile
            {
                Teams = { incoming },
                Overrides =
                {
                    new TeamOverride { DemoSha256 = ShaA, Side = 2, TeamId = theirs },
                    new TeamOverride { DemoSha256 = ShaB, Side = 3, TeamId = theirs },
                    new TeamOverride { DemoStableKey = "p-0123", Side = 2, TeamId = theirs }
                },
                Provenance =
                {
                    Overrides =
                    {
                        new ProvenanceOverride { DemoSha256 = ShaA, Label = "scrim" },
                        new ProvenanceOverride { DemoStableKey = "p-0123", Label = "scrim" }
                    }
                }
            });

        run.Teams.ImportTeams(run.Staged, run.Ledger());

        TeamsFile disk = run.OnDisk;
        using (Assert.Multiple())
        {
            await Assert.That(disk.Overrides.Single(o => o.DemoSha256 == ShaA).TeamId).IsEqualTo(ours)
                .Because("the side is pinned to the team the imported one landed on");
            await Assert.That(disk.Overrides.Single(o => o.DemoSha256 == ShaB).TeamId).IsNull()
                .Because("this machine's override for that demo and side stays");
            await Assert.That(disk.Overrides.Any(o => o.DemoSha256 is null)).IsFalse();
            await Assert.That(disk.Provenance.Overrides.Select(o => o.DemoSha256!)).IsEquivalentTo([ShaA]);
            await Assert.That(run.Lines.Any(l => l.Contains("2 demo override(s) were left out"))).IsTrue();
        }
    }

    [Test]
    public async Task DismissalsAndTombstonesAreUnited_AndTheAccountsStayThisMachines()
    {
        Guid gone = Guid.NewGuid();
        using Run run = Setup(
            new TeamsFile { DismissedSuggestions = { "s1" }, Me = { SteamIds = ["mine"] } },
            new TeamsFile
            {
                DismissedSuggestions = { "s1", "s2" }, Tombstones = { gone }, Me = { SteamIds = ["theirs"] }
            });

        run.Teams.ImportTeams(run.Staged, run.Ledger());

        TeamsFile disk = run.OnDisk;
        await Assert.That(disk.DismissedSuggestions).IsEquivalentTo(["s1", "s2"]);
        await Assert.That(disk.Tombstones).IsEquivalentTo([gone]);
        await Assert.That(disk.Me.SteamIds).IsEquivalentTo(["mine"]);
    }

    [Test]
    public async Task ASecondImport_ChangesNothing()
    {
        Guid ours = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        Guid added = Guid.NewGuid();
        Team local = Named(ours, "Ours");
        local.Rosters.Add(new Roster { Id = "r1", CoreLineup = [.. _five] });
        Team matched = Named(theirs, "Theirs", "notes");
        matched.Rosters.Add(new Roster { Id = "r1", CoreLineup = [.. _five] });
        using Run run = Setup(new TeamsFile { Teams = { local } }, new TeamsFile
        {
            Teams = { matched, Named(added, "New") },
            Overrides = { new TeamOverride { DemoSha256 = ShaA, Side = 2, TeamId = theirs } },
            DismissedSuggestions = { "s" }
        });

        TeamIdMap first = run.Teams.ImportTeams(run.Staged, run.Ledger());
        byte[] after = File.ReadAllBytes(run.TeamsPath);
        ImportLedger again = run.Ledger();
        TeamIdMap second = run.Teams.ImportTeams(run.Staged, again);

        using (Assert.Multiple())
        {
            await Assert.That(File.ReadAllBytes(run.TeamsPath)).IsEquivalentTo(after);
            await Assert.That(second.Map(theirs)).IsEqualTo(first.Map(theirs)).And.IsEqualTo(ours);
            await Assert.That(again.Added).IsEqualTo(0);
            await Assert.That(again.Merged).IsEqualTo(0);
        }
    }
}
