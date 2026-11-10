#region

using System.Text.Json;
using DemoViewer.NET.Playback2D.Pipeline.Review;
using DemoViewer.NET.Services.Review;
using TUnit.Assertions.Enums;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Lineup clips left the Review Queue: the load drops the ones the lineup service generated and nobody
///     touched, keeps the rest, and rewrites the file once, verified, with the old one backed up. The same pass
///     rewrites the retired "dossier" source as "scouting-report".
/// </summary>
public class ReviewQueueMigrationTests
{
    private const string ConsoleLine = "setpos -613.43 615.90 -78.98; setang -51.19 -169.10 0.00";

    // The source id the Scouting Report had as the Dossier, literal so the test reads a file an older build wrote.
    private const string DossierSource = "dossier";

    private static ReviewEntry Lineup(string note) =>
        ReviewEntry.Clip("/d/a.dem", 100, 200, note, ReviewSources.Lineup, 64) with { LineupId = Guid.NewGuid() };

    private static string Root() => Directory.CreateTempSubdirectory("dv-review-migration-").FullName;

    private static void Write(string root, IEnumerable<ReviewEntry> entries) =>
        File.WriteAllText(Path.Combine(root, ReviewQueue.FileName),
            JsonSerializer.Serialize(new ReviewQueueFile { Entries = [.. entries] }, ReviewQueueFile.JsonOptions));

    [Test]
    public async Task TheLoad_DropsUntouchedLineupClips_KeepsWhatAUserWroteOn_AndBacksUpTheOldFile()
    {
        string root = Root();
        try
        {
            ReviewEntry asked = Lineup("Flash into B. " + ConsoleLine) with { Question = "who pops it?" };
            ReviewEntry renoted = Lineup("my favourite B flash");
            ReviewEntry dossier = ReviewEntry.Clip("/d/x.dem", 1, 2, "plant", DossierSource);
            Write(root,
            [
                ReviewEntry.Section("Lineup clips, de_mirage"), Lineup("Smoke into CT. " + ConsoleLine),
                Lineup("Molotov into Jungle. " + ConsoleLine),
                ReviewEntry.Section("Lineup clips, de_nuke"), Lineup("Smoke into Outside. " + ConsoleLine), asked,
                renoted,
                ReviewEntry.Section("Dossier"), dossier
            ]);
            string original = await File.ReadAllTextAsync(Path.Combine(root, ReviewQueue.FileName));

            ReviewQueue queue = new(root);
            await queue.Loaded;

            using (Assert.Multiple())
            {
                await Assert.That(queue.Migration).IsEqualTo(new ReviewQueueMigrationResult(3, 2, 1, Renamed: 1));
                await Assert.That(queue.Entries.Select(e => e.Kind == ReviewEntryKind.Section ? e.Title : e.Note))
                    .IsEquivalentTo(["Lineup clips, de_nuke", asked.Note, renoted.Note, "Dossier", "plant"],
                        CollectionOrdering.Matching);
                await Assert.That(queue.Clips.Single(c => c.Id == dossier.Id).Source)
                    .IsEqualTo(ReviewSources.ScoutingReport)
                    .Because("the retired source is rewritten in the same pass that drops the lineups");
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, ReviewQueueMigration.BackupFileName)))
                    .IsEqualTo(original);
                await Assert.That(File.Exists(Path.Combine(root, ReviewQueue.FileName + ".migrating"))).IsFalse();
            }

            ReviewQueue again = new(root);
            await again.Loaded;
            using (Assert.Multiple())
            {
                await Assert.That(again.Migration).IsNull().Because("a second load finds nothing to drop");
                await Assert.That(again.Entries.Count).IsEqualTo(5);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AGeneratedClipTheUserMovedOrWhoseCardTheyRenamed_IsKept()
    {
        ReviewEntry moved = Lineup("Smoke into CT. " + ConsoleLine);
        ReviewEntry renamed = Lineup("Flash into B. " + ConsoleLine);
        List<ReviewEntry> kept = ReviewQueueMigration.DropGeneratedLineupClips(
        [
            ReviewEntry.Section("Lineup clips, de_mirage"), Lineup("Smoke into Jungle. " + ConsoleLine),
            ReviewEntry.Section("Mine"), moved,
            ReviewEntry.Section("B flashes to learn"), renamed
        ], out ReviewQueueMigrationResult result);

        using (Assert.Multiple())
        {
            await Assert.That(result).IsEqualTo(new ReviewQueueMigrationResult(1, 2, 1));
            await Assert.That(kept.Select(e => e.Kind == ReviewEntryKind.Section ? e.Title : e.Note))
                .IsEquivalentTo(["Mine", moved.Note, "B flashes to learn", renamed.Note], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task AFailedRewrite_KeepsTheMigratedSet_AndTheNextSaveWritesIt()
    {
        string root = Root();
        try
        {
            Write(root, [
                ReviewEntry.Section("Lineup clips, de_nuke"), Lineup("Smoke into Outside. " + ConsoleLine),
                ReviewEntry.Section("Dossier"), ReviewEntry.Clip("/d/x.dem", 1, 2, "plant", DossierSource)
            ]);
            string path = Path.Combine(root, ReviewQueue.FileName);
            string original = await File.ReadAllTextAsync(path);
            Directory.CreateDirectory(path + ".migrating"); // the rewrite cannot be written

            ReviewQueue queue = new(root, TimeSpan.FromHours(1));
            await queue.Loaded;
            using (Assert.Multiple())
            {
                await Assert.That(queue.Migration).IsEqualTo(new ReviewQueueMigrationResult(1, 0, 1, false, 1));
                await Assert.That(queue.Entries.Count).IsEqualTo(2).Because("the migrated set stays in memory");
                await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(original);
            }

            queue.Flush();
            ReviewQueue reread = new(root);
            await reread.Loaded;
            using (Assert.Multiple())
            {
                await Assert.That(reread.Clips.Select(c => c.Note))
                    .IsEquivalentTo(["plant"], CollectionOrdering.Matching);
                await Assert.That(reread.Clips.Single().Source).IsEqualTo(ReviewSources.ScoutingReport);
                await Assert.That(reread.Migration).IsNull().Because("the saved set is already migrated");
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, ReviewQueueMigration.BackupFileName)))
                    .IsEqualTo(original);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task DossierClips_SurviveAsScoutingReport_TheirCardKeepsItsTitle_AndTheSourcesBackupIsTaken()
    {
        string root = Root();
        try
        {
            ReviewEntry first = ReviewEntry.Clip("/d/x.dem", 1, 2, "plant", DossierSource) with
            {
                Question = "who rotates?", Reviewed = true
            };
            ReviewEntry second = ReviewEntry.Clip("/d/y.dem", 3, 4, "retake", DossierSource);
            ReviewEntry manual = ReviewEntry.Clip("/d/z.dem", 5, 6, "mine", ReviewSources.Manual);
            ReviewEntry card = ReviewEntry.Section("Dossier", "Team A");
            Write(root, [card, first, second, manual]);
            string path = Path.Combine(root, ReviewQueue.FileName);
            string original = await File.ReadAllTextAsync(path);

            ReviewQueue queue = new(root);
            await queue.Loaded;

            using (Assert.Multiple())
            {
                await Assert.That(queue.Migration).IsEqualTo(new ReviewQueueMigrationResult(0, 0, 0, Renamed: 2));
                await Assert.That(queue.Entries.Select(e => e.Id))
                    .IsEquivalentTo(new[] { card.Id, first.Id, second.Id, manual.Id }, CollectionOrdering.Matching)
                    .Because("every entry survives, in order, with its id");
                await Assert.That(queue.Clips.Select(c => c.Source))
                    .IsEquivalentTo([ReviewSources.ScoutingReport, ReviewSources.ScoutingReport,
                        ReviewSources.Manual], CollectionOrdering.Matching);
                await Assert.That(queue.Clips[0].Question).IsEqualTo("who rotates?");
                await Assert.That(queue.Clips[0].Reviewed).IsTrue();
                await Assert.That(queue.Entries[0].Title).IsEqualTo("Dossier").Because("a title card is user text");
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(root,
                    ReviewQueueMigration.SourcesBackupFileName))).IsEqualTo(original);
                await Assert.That(File.Exists(Path.Combine(root, ReviewQueueMigration.BackupFileName))).IsFalse()
                    .Because("a rename-only pass never overwrites the lineup backup");
                await Assert.That(await File.ReadAllTextAsync(path)).DoesNotContain("\"dossier\"");
            }

            ReviewQueue again = new(root);
            await again.Loaded;
            using (Assert.Multiple())
            {
                await Assert.That(again.Migration).IsNull().Because("a second load finds nothing to rename");
                await Assert.That(again.Clips.Count).IsEqualTo(3);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task WithoutABackup_NothingIsMigrated()
    {
        string root = Root();
        try
        {
            Write(root, [ReviewEntry.Section("Lineup clips, de_nuke"), Lineup("Smoke into Outside. " + ConsoleLine)]);
            string path = Path.Combine(root, ReviewQueue.FileName);
            string original = await File.ReadAllTextAsync(path);
            Directory.CreateDirectory(Path.Combine(root, ReviewQueueMigration.BackupFileName));

            ReviewQueue queue = new(root);
            await queue.Loaded;
            using (Assert.Multiple())
            {
                await Assert.That(queue.Migration).IsNull();
                await Assert.That(queue.Clips.Count).IsEqualTo(1);
                await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(original);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ARefusedFile_IsNotMigrated()
    {
        string root = Root();
        try
        {
            string newer =
                """{ "schemaVersion": 99, "entries": [ { "kind": "clip", "source": "lineup", "note": "x" } ] }""";
            string path = Path.Combine(root, ReviewQueue.FileName);
            await File.WriteAllTextAsync(path, newer);

            ReviewQueue queue = new(root);
            await queue.Loaded;

            using (Assert.Multiple())
            {
                await Assert.That(queue.FileProblem).IsNotNull();
                await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(newer);
                await Assert.That(File.Exists(Path.Combine(root, ReviewQueueMigration.BackupFileName))).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // DV_REVIEW_QUEUE names a copy of a real queue; the copy is copied again, so even it is not written.
    [Test]
    public async Task TheOwnersQueue_LosesItsGeneratedLineupClips_AndKeepsTheDossierSection()
    {
        if (Environment.GetEnvironmentVariable("DV_REVIEW_QUEUE") is not { Length: > 0 } source || !File.Exists(source))
        {
            throw new SkipTestException("DV_REVIEW_QUEUE is not set to a copy of a queue");
        }

        string root = Root();
        try
        {
            File.Copy(source, Path.Combine(root, ReviewQueue.FileName));
            ReviewQueue queue = new(root);
            await queue.Loaded;
            Console.WriteLine($"[review-migration] {queue.Migration}; entries left {queue.Entries.Count}");
            using (Assert.Multiple())
            {
                await Assert.That(queue.Migration!.Dropped).IsEqualTo(4792);
                await Assert.That(queue.Migration.Kept).IsEqualTo(0);
                await Assert.That(queue.Migration.CardsDropped).IsEqualTo(11);
                await Assert.That(queue.Clips.Count).IsEqualTo(7);
                await Assert.That(queue.Entries.Count(e => e.Kind == ReviewEntryKind.Section)).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
