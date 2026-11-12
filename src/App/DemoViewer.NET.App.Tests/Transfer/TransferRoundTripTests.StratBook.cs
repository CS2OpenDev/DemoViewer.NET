#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Features;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Playback2D.Annotations;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Transfer;
using Microsoft.Extensions.DependencyInjection;
using static DemoViewer.NET.AppTests.LibraryFingerprintTests;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

public partial class TransferRoundTripTests
{
    // The same trip with the Strat Book loaded on the importing side: its library data goes through the real
    // exporter and importer next to the demo's own, lands on the local team, and the demo still attaches unparsed.
    [Test]
    [Category("Integration")]
    public async Task WithTheStratBookLoaded_ItsLibraryArrives_AndTheDemoStillAttachesWithoutAParse()
    {
        using TempFolders temp = new();
        string libraryA = temp.New();
        string demo = Path.Combine(libraryA, "match one.dem");
        await File.WriteAllBytesAsync(demo, Bytes(92));
        string configB = temp.New();
        Guid ours = Guid.NewGuid();
        StratBookLeg.WriteTeam(configB, ours, "Ours");

        string? previous = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, configB);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                await using ServiceProvider services = App.BuildServices(new DesktopWindowService(() => null));
                // What the pack's lifecycle does at startup; the merge refuses a Team Identity that is not up.
                services.GetRequiredService<TeamIdentityService>().Attach();
                await RoundTripAsync(temp, demo, libraryA, null, new StratBookLeg(services, ours), configB);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, previous);
        }
    }

    // The Strat Book half of a round trip: machine A's files written the way its stores write them, the export's
    // sources and the import's host taken from machine B's composition root, and what B's live stores hold after.
    private sealed class StratBookLeg(IServiceProvider services, Guid ours)
    {
        private const string Mirage = "de_mirage";
        private const string Technique = "stand-throw-left";

        private static readonly string[] _five = ["76561", "76562", "76563", "76564", "76565"];
        private static readonly Guid _lineup = Guid.Parse("5c1a0000-0000-4000-8000-000000000002");
        private static readonly DateTime _created = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

        private readonly Guid _theirs = Guid.NewGuid();
        private Guid _strat;

        private static string Stem => LineupClipPlanner.FileStem(Mirage, GrenadeKind.Smoke, _lineup, Technique);

        public static void WriteTeam(string configRoot, Guid id, string name)
        {
            Team team = new() { Id = id, Name = name, NameSource = TeamNameSource.User };
            team.Rosters.Add(new Roster { Id = "r1", CoreLineup = [.. _five] });
            File.WriteAllBytes(Path.Combine(configRoot, TeamIdentityService.TeamsFileName),
                JsonSerializer.SerializeToUtf8Bytes(new TeamsFile { Teams = { team } }, TeamsFile.JsonOptions));
        }

        // A team of the same five under another id, its strat with a second revision, a lineup and its clip,
        // notes on the team and the demo's tags.
        public void Populate(string configA, string sha)
        {
            WriteTeam(configA, _theirs, "Theirs");

            StratStore strats = new(new ExtensionFolder(Path.Combine(configA, StratBookStores.Strats)));
            StratDocument strat = StratDocument.Create(Guid.NewGuid(), StratOwner.Team(_theirs), Mirage, "T",
                "execute", "A exec", _created);
            Saved(strats.Save(strat, [], "created"));
            strat.Name = "A split";
            Saved(strats.Save(strat,
                [PatchOp.ReplaceOp("/name", JsonValue.Create("A exec"), JsonValue.Create("A split"))], "A split"));
            _strat = strat.Id;

            GrenadeLineupStore lineups = new(Path.Combine(configA, "cache", ExtensionFolders.DataDirectoryName,
                StratBookPack.PackId, ExtensionFolders.OwnCacheDirectoryName));
            lineups.For(Mirage).Anchors.Add(new LineupAnchor(_lineup, GrenadeKind.Smoke,
                new WorldPoint(512, 288, -160), new WorldPoint(-1400, -1400, -170), sha + "/a"));
            lineups.Save();
            string clips = Path.Combine(configA, LineupClipService.DirectoryName);
            Directory.CreateDirectory(clips);
            File.WriteAllText(Path.Combine(clips, Stem + LineupClipPlanner.GifExtension), "gif");
            File.WriteAllText(Path.Combine(clips, Stem + LineupClipPlanner.SetposExtension), "setpos");

            ScoutingReportNotesStore notes =
                new(StoredFile.At(Path.Combine(configA, ScoutingReportNotesStore.FileName)));
            notes.SetSummary(_theirs, "Fast A");
            notes.AddNote(_theirs, "smoke window early");

            TagDocument tags = TagDocument.Create(new DemoIdentity(sha, "match one.dem", 1),
                new ClockIdentity(ClockIdentity.DvFrameClock, 64, 1000, 0, 0));
            tags.Instances.Add(new TagInstance
            {
                Id = Guid.NewGuid(), Code = "A execute", FromTick = 100, ToTick = 200, CreatedUtc = _created,
                ModifiedUtc = _created
            });
            if (!new TagStore(new ExtensionFolder(Path.Combine(configA, StratBookStores.Tags))).Save(tags))
            {
                throw new InvalidOperationException("The tags were not saved.");
            }
        }

        public IReadOnlyList<TransferExtensionSource> Sources() => App.TransferExtensionSources(services);

        public TransferExtensionHost Host(string configB) =>
            new(() => services.GetRequiredService<PackContributionSet>().DataTransfers,
                services.GetRequiredService<IFeatureGate>().IsEnabled, configB, Path.Combine(configB, "cache"));

        public async Task AssertArrivedAsync(string configB, string sha, TransferSummary imported)
        {
            TransferSummaryItem line = imported.Items.Single(i => i.Sha256 is null);
            StratStore strats = new(new ExtensionFolder(Path.Combine(configB, StratBookStores.Strats)));
            StratLoadResult strat = strats.Load(_strat);
            ScoutingReportNotesStore notes = services.GetRequiredService<ScoutingReportNotesStore>();
            using (Assert.Multiple())
            {
                await Assert.That(line.Outcome).IsNotEqualTo(TransferOutcome.Failed).Because(line.Reason ?? "");
                await Assert.That(services.GetRequiredService<TeamIdentityService>().AllTeams.Select(t => t.Id))
                    .IsEquivalentTo([ours]).Because("the same five make the same team");
                await Assert.That(strat.Document?.Name).IsEqualTo("A split");
                await Assert.That(strat.Document?.Owner.TeamId).IsEqualTo(ours);
                await Assert.That(strats.History(_strat).Count).IsEqualTo(3)
                    .Because("both imported commits replay, and the move onto the local team is a third");
                await Assert.That(notes.TeamIds).IsEquivalentTo([ours]);
                await Assert.That(notes.For(ours).Summary).IsEqualTo("Fast A");
                await Assert.That(notes.For(ours).Notes.Select(n => n.Text)).IsEquivalentTo(["smoke window early"]);
                await Assert.That(services.GetRequiredService<GrenadeIndex>().LineupStore.For(Mirage).Anchors
                    .Select(a => a.Id)).Contains(_lineup);
                await Assert.That(File.ReadAllText(Path.Combine(configB, LineupClipService.DirectoryName,
                    Stem + LineupClipPlanner.GifExtension))).IsEqualTo("gif");
                await Assert.That(new TagStore(new ExtensionFolder(Path.Combine(configB, StratBookStores.Tags)))
                    .TryLoad(sha)?.Instances.Select(i => i.Code)).IsEquivalentTo(["A execute"]);
            }
        }

        private static void Saved(StratSaveResult result)
        {
            if (!result.Saved)
            {
                throw new InvalidOperationException(result.Reason);
            }
        }
    }
}
