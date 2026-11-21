#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.ErrorReports;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A demo file whose write time changed but whose bytes did not (a restore, a sync tool, a time zone
///     change) keeps its record: a full read that proves the same content must not start a fresh one.
/// </summary>
public class SameBytesRewriteTests
{
    private const string Sha = "sha-same";
    private const string Path1 = "/lib/m.dem";

    private static DemoCacheStore Hashed()
    {
        DemoCacheStore store = new(null);
        DemoCacheRecord record = new() { Path = Path1, Size = 1000, ModifiedTicks = 2000, Map = "de_nuke" };
        record.SetContentHash(Sha, null);
        record.Scoreboard = [new CachedStatRow { Slot = 1, Team = 3, Kills = 21 }];
        record.ErrorReport = new DemoErrorReport { Notes = "mine" };
        DemoCacheStore.StampParse(record);
        store.Upsert(record);
        return store;
    }

    [Test]
    public async Task AWriteAfterAFullReadOfTheSameBytes_KeepsTheRecord()
    {
        DemoCacheStore store = Hashed();
        DateTime written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        long local = written.ToLocalTime().Ticks;

        store.NoteContentRead(Path1, Sha, null, 1000, written);
        store.Update(Path1, 1000, local, r => r.Server = "later");

        DemoCacheRecord? kept = store.TryLoadRecord(Path1);
        using (Assert.Multiple())
        {
            await Assert.That(kept?.Scoreboard.Count).IsEqualTo(1);
            await Assert.That(kept?.ErrorReport?.Notes).IsEqualTo("mine");
            await Assert.That(kept?.Server).IsEqualTo("later");
            await Assert.That(kept?.Sha256).IsEqualTo(Sha);
            await Assert.That(store.TryGetIndex(Path1)?.Sha256).IsEqualTo(Sha);
            await Assert.That(store.TryGetIndex(Path1)?.ModifiedTicks).IsEqualTo(local);
            await Assert.That(store.IsOrphaned(Sha)).IsFalse();
        }
    }

    [Test]
    public async Task AWriteAfterAReadOfOtherBytes_StillStartsFresh()
    {
        DemoCacheStore store = Hashed();
        DateTime written = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        store.NoteContentRead(Path1, "sha-other", null, 1000, written);
        store.Update(Path1, 1000, written.ToLocalTime().Ticks, r => r.Server = "later");

        await Assert.That(store.TryLoadRecord(Path1)?.Scoreboard).IsEmpty();
    }

    [Test]
    public async Task ARowWhoseFileChangedStamps_WantsAHash_ButNotWhileTheyMatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dv-samebytes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "m.dem");
            File.WriteAllBytes(path, new byte[100]);
            FileInfo info = new(path);
            DemoCacheStore store = new(null);
            DemoCacheRecord record = new() { Path = path, Size = info.Length, ModifiedTicks = info.LastWriteTime.Ticks };
            record.SetContentHash(Sha, null);
            store.Upsert(record);

            bool whileMatching = store.WantsContentHash(path);
            File.SetLastWriteTimeUtc(path, new DateTime(2020, 5, 5, 0, 0, 0, DateTimeKind.Utc));
            bool afterTouch = store.WantsContentHash(path);

            using (Assert.Multiple())
            {
                await Assert.That(whileMatching).IsFalse();
                await Assert.That(afterTouch).IsTrue();
                await Assert.That(store.WantsContentHash(Path.Combine(dir, "unknown.dem"))).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
