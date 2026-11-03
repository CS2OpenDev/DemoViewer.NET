#region

using System.Text;
using DemoViewer.NET.Services;
using TUnit.Assertions.Enums;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The one whole-file write: a missing folder is created, a rewrite replaces the file, and a write that
///     throws halfway leaves the previous file and no temporary file beside it.
/// </summary>
public class AtomicFileTests
{
    [Test]
    public async Task AWrite_CreatesTheFolder_AndARewriteReplacesTheFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvatomic_" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "a", "b", "file.json");
            AtomicFile.WriteAllText(path, "one");
            AtomicFile.WriteAllText(path, "two");

            using (Assert.Multiple())
            {
                await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("two");
                await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!))
                    .IsEquivalentTo([path], CollectionOrdering.Matching);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AWriteThatThrowsHalfway_LeavesThePreviousFile_AndNoTemporaryFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvatomic_" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "file.bin");
            AtomicFile.WriteAllBytes(path, [1, 2, 3]);

            await Assert.ThrowsAsync<IOException>(() =>
            {
                AtomicFile.Write(path, stream =>
                {
                    stream.Write("half"u8);
                    throw new IOException("disk gone");
                });
                return Task.CompletedTask;
            });

            using (Assert.Multiple())
            {
                await Assert.That(await File.ReadAllBytesAsync(path))
                    .IsEquivalentTo(new byte[] { 1, 2, 3 }, CollectionOrdering.Matching);
                await Assert.That(Directory.GetFiles(root)).IsEquivalentTo([path], CollectionOrdering.Matching);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // A handle without FileShare.Delete is what antivirus or the indexer holds on a freshly written file, and
    // what made a settings write fail on the Windows runner.
    private static FileStream HoldWithoutShareDelete(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    [Test]
    public async Task OnWindows_ATargetHeldForAMoment_IsReplacedOnceReleased()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SkipTestException("only Windows refuses a rename over a held file");
        }

        string root = Path.Combine(Path.GetTempPath(), "dvatomic_" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "file.json");
            AtomicFile.WriteAllText(path, "one");

            FileStream hold = HoldWithoutShareDelete(path);
            // A dedicated thread, not the pool, so a busy runner cannot delay the release past the retries.
            Thread release = new(() =>
            {
                Thread.Sleep(30);
                hold.Dispose();
            });
            release.Start();
            try
            {
                AtomicFile.WriteAllText(path, "two");
            }
            finally
            {
                // Before the folder is deleted, so a failed write reports itself, not the cleanup's sharing violation.
                release.Join();
            }

            using (Assert.Multiple())
            {
                await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("two");
                await Assert.That(Directory.GetFiles(root)).IsEquivalentTo([path], CollectionOrdering.Matching);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task OnWindows_ATargetHeldThroughout_FailsAndLeavesThePreviousFile_AndNoTemporaryFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new SkipTestException("only Windows refuses a rename over a held file");
        }

        string root = Path.Combine(Path.GetTempPath(), "dvatomic_" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "file.json");
            AtomicFile.WriteAllText(path, "one");

            await using (HoldWithoutShareDelete(path))
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                {
                    AtomicFile.WriteAllText(path, "two");
                    return Task.CompletedTask;
                });
            }

            using (Assert.Multiple())
            {
                await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("one");
                await Assert.That(Directory.GetFiles(root)).IsEquivalentTo([path], CollectionOrdering.Matching);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
