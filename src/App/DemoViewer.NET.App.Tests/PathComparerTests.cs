#region

using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Demo paths are told apart by case on Linux, whose file systems keep two names that differ only by case
///     as two files, and ignoring case on Windows and macOS, whose default file systems fold it.
/// </summary>
public class PathComparerTests
{
    [Test]
    public async Task PathsDifferingOnlyByCase_AreOnePathOnlyWhereTheFileSystemFoldsCase()
    {
        bool folds = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        using (Assert.Multiple())
        {
            await Assert.That(PathComparer.Paths.Equals("/d/Match.dem", "/d/match.dem")).IsEqualTo(folds);
            await Assert.That(string.Equals("/d/Match.dem", "/d/match.dem", PathComparer.Comparison))
                .IsEqualTo(folds);
            await Assert.That(PathComparer.Paths.Equals("/d/match.dem", "/d/match.dem")).IsTrue();
        }
    }

    /// <summary>
    ///     On Linux the cache keeps a row per path for two hashed demos whose paths differ only by case, each
    ///     with its own content and record.
    /// </summary>
    [Test]
    public async Task OnLinux_TheCacheKeepsTwoHashedDemosWhosePathsDifferOnlyByCase()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new SkipTestException("only a Linux file system keeps two names that differ only by case");
        }

        string root = Path.Combine(Path.GetTempPath(), $"dv-pathcase-{Guid.NewGuid():N}");
        try
        {
            DemoCacheStore store = new(root);
            store.Upsert(new DemoCacheRecord
            {
                Path = "/d/Match.dem", Size = 1000, ModifiedTicks = 2000, Sha256 = new string('a', 64),
                Map = "de_nuke"
            });
            store.Upsert(new DemoCacheRecord
            {
                Path = "/d/match.dem", Size = 1001, ModifiedTicks = 2001, Sha256 = new string('b', 64),
                Map = "de_inferno"
            });

            using (Assert.Multiple())
            {
                await Assert.That(store.TryGetIndex("/d/Match.dem")?.Sha256).IsEqualTo(new string('a', 64));
                await Assert.That(store.TryGetIndex("/d/match.dem")?.Sha256).IsEqualTo(new string('b', 64));
                await Assert.That(store.TryLoadRecord("/d/Match.dem")?.Map).IsEqualTo("de_nuke");
                await Assert.That(store.TryLoadRecord("/d/match.dem")?.Map).IsEqualTo("de_inferno");
                await Assert.That(store.SameDemo("/d/Match.dem", "/d/match.dem")).IsFalse();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
