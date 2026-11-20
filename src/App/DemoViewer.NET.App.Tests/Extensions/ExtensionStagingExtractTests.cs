#region

using System.IO.Compression;
using DemoViewer.NET.Extensions.Updates;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="ExtensionStaging.Extract" />'s entry cap bounds what reading the zip's directory costs, not only
///     what it writes: ZipArchive reads every directory record into an entry object before it can count them, so
///     the cap has to hold while that read runs.
/// </summary>
public class ExtensionStagingExtractTests
{
    [Test]
    public async Task Extract_ADirectoryFarPastTheEntryCap_IsRefusedWhileItIsRead()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // 120 000 records, about 6 MB of directory behind a Zip64 end record: counting them used to mean
            // reading every one into an entry object first, and the same shape runs to millions.
            string zip = Path.Combine(root, "many.zip");
            File.WriteAllBytes(zip, ZipOf(120_000));
            string target = Path.Combine(root, "out");

            string? refused = ExtensionStaging.Extract(zip, target);

            using (Assert.Multiple())
            {
                await Assert.That(refused).IsEqualTo("the archive's list of entries is larger than allowed");
                await Assert.That(Directory.Exists(target)).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // The same archive, measured: listing 120 000 entries used to allocate about 68 MB, and the figure grew with
    // the directory. Under the read budget it stops near 13 MB whatever the directory holds.
    [Test]
    [Category("Budget")]
    public async Task Extract_ADirectoryFarPastTheEntryCap_AllocatesWithinTheReadBudget()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string zip = Path.Combine(root, "many.zip");
            File.WriteAllBytes(zip, ZipOf(120_000));

            long before = GC.GetAllocatedBytesForCurrentThread();
            _ = ExtensionStaging.Extract(zip, Path.Combine(root, "out"));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            await Assert.That(allocated).IsLessThan(32L * 1024 * 1024);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Extract_PastTheEntryCap_WithinTheDirectoryBudget_StillNamesTheCount()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string zip = Path.Combine(root, "some.zip");
            File.WriteAllBytes(zip, ZipOf(5000));

            string? refused = ExtensionStaging.Extract(zip, Path.Combine(root, "out"));

            await Assert.That(refused).IsEqualTo("the archive has 5000 entries; the most allowed is 4096");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Extract_AnOrdinaryZip_Unpacks()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string zip = Path.Combine(root, "small.zip");
            File.WriteAllBytes(zip, ZipOf(40));
            string target = Path.Combine(root, "out");

            string? refused = ExtensionStaging.Extract(zip, target);

            using (Assert.Multiple())
            {
                await Assert.That(refused).IsNull();
                await Assert.That(Directory.GetFiles(target).Length).IsEqualTo(40);
                await Assert.That(File.ReadAllBytes(Path.Combine(target, "39"))).IsEquivalentTo(new byte[] { 39 });
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static byte[] ZipOf(int entries)
    {
        using MemoryStream ms = new();
        using (ZipArchive zip = new(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 0; i < entries; i++)
            {
                using Stream s = zip.CreateEntry(i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    CompressionLevel.NoCompression).Open();
                s.WriteByte((byte)i);
            }
        }

        return ms.ToArray();
    }
}
