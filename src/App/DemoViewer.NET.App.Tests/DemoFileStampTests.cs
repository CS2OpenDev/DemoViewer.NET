#region

using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The library keys freshness on local write-time ticks, so a change of the machine's time zone moves every
///     file's stamp by a whole number of quarter hours while the files stay as they were. That is no change to
///     the demo: what was recorded still matches, and takes the stamp the file has now.
/// </summary>
public class DemoFileStampTests
{
    private static readonly long At = new DateTime(2026, 3, 1, 12, 0, 0).Ticks;

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-stamp-{Guid.NewGuid():N}");

    private static DemoCacheRecord Analysed(string path, string sha, long size, long mtime)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = size,
            ModifiedTicks = mtime,
            Sha256 = sha,
            Map = "de_nuke",
            Scoreboard = [new CachedStatRow { Slot = 1, Team = 3, Kills = 21 }],
            AnalysisState = DemoAnalysisState.Indexed
        };
        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Test]
    [Arguments(0, 0, true)]
    [Arguments(2, 0, true)]
    [Arguments(-5, -45, true)]
    [Arguments(26, 0, true)]
    [Arguments(26, 15, false)]
    [Arguments(0, 7, false)]
    [Arguments(0, 1, false)]
    public async Task AQuarterHourStepUpTo26Hours_IsTheSameWriteTime(int hours, int minutes, bool same)
    {
        long shifted = At + TimeSpan.FromHours(hours).Ticks + TimeSpan.FromMinutes(minutes).Ticks;
        using (Assert.Multiple())
        {
            await Assert.That(DemoFileStamp.Matches(1000, At, 1000, shifted)).IsEqualTo(same);
            await Assert.That(DemoFileStamp.Matches(1000, At, 1001, shifted)).IsFalse()
                .Because("another size is another file, whatever the write time");
            await Assert.That(DemoFileStamp.SameWriteTime(At, shifted + 1)).IsFalse()
                .Because("a zone shift moves the stamp by whole quarter hours, to the tick");
        }
    }

    [Test]
    public async Task TheRecordAndRow_MatchTheFileAcrossAZoneShift()
    {
        DemoCacheRecord record = Analysed("/m/a.dem", "sha-a", 1000, At);
        long shifted = At + TimeSpan.FromHours(3).Ticks;
        using (Assert.Multiple())
        {
            await Assert.That(record.MatchesFile(1000, shifted)).IsTrue();
            await Assert.That(record.ToIndexEntry().MatchesFile(1000, shifted)).IsTrue();
            await Assert.That(record.MatchesFile(1000, At + TimeSpan.FromMinutes(1).Ticks)).IsFalse();
        }
    }

    /// <summary>
    ///     A write to a demo whose stamps a zone change moved starts from its record, every tier kept, and the
    ///     record takes the stamps the file has now. One written with other stamps is still a different demo.
    /// </summary>
    [Test]
    public async Task AWriteAfterAZoneShift_KeepsTheTiers_UnderTheNewStamps()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-a", 1000, At));
            long shifted = At + TimeSpan.FromHours(-7).Ticks;

            DemoCacheRecord same = store.LoadOrCreate("/m/a.dem", 1000, shifted);
            DemoCacheRecord other = store.LoadOrCreate("/m/a.dem", 1000, At + TimeSpan.FromMinutes(1).Ticks);
            using (Assert.Multiple())
            {
                await Assert.That(same.Scoreboard).Count().IsEqualTo(1);
                await Assert.That(same.ModifiedTicks).IsEqualTo(shifted);
                await Assert.That(other.Scoreboard).IsEmpty();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    ///     An orphan takes its path back when the file comes back a zone shift away from the stamps it had, and
    ///     the path is listed at the stamps it has now.
    /// </summary>
    [Test]
    public async Task AnOrphan_IsReattachedAcrossAZoneShift_AtTheNewStamps()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-a", 1000, At));
            store.Detach("/m/a.dem");
            long shifted = At + TimeSpan.FromHours(2).Ticks;

            await Assert.That(store.Reattach("/m/a.dem", 1000, At + TimeSpan.FromMinutes(5).Ticks)).IsFalse();
            await Assert.That(store.Reattach("/m/a.dem", 1000, shifted)).IsTrue();
            using (Assert.Multiple())
            {
                await Assert.That(store.TryGetIndex("/m/a.dem")!.ModifiedTicks).IsEqualTo(shifted);
                await Assert.That(store.TryGetIndex("/m/a.dem")!.Tier).IsEqualTo(DemoCacheTier.Analysis);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Restamp_MovesAShiftedPathToTheNewStamps_AndLeavesAnyOtherChangeAlone()
    {
        string root = TempRoot();
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(Analysed("/m/a.dem", "sha-a", 1000, At));
            long shifted = At + TimeSpan.FromHours(1).Ticks;

            store.Restamp("/m/a.dem", 1000, At + TimeSpan.FromMinutes(2).Ticks);
            await Assert.That(store.TryGetIndex("/m/a.dem")!.ModifiedTicks).IsEqualTo(At);
            store.Restamp("/m/a.dem", 1001, shifted);
            await Assert.That(store.TryGetIndex("/m/a.dem")!.ModifiedTicks).IsEqualTo(At);

            store.Restamp("/m/a.dem", 1000, shifted);
            store.SaveIndex();
            await Assert.That(new DemoCacheStore(root).TryGetIndex("/m/a.dem")!.ModifiedTicks).IsEqualTo(shifted);
        }
        finally
        {
            Cleanup(root);
        }
    }
}
