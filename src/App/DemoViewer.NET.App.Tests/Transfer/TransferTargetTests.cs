#region

using DemoViewer.NET.Services.Transfer;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     The checks on the folder a bundle is written to or <c>.dem</c> files are copied into: it exists and takes
///     a write, it is not inside the app's data or a library, it has the room with a 5 % margin, and a FAT32
///     drive is never handed a file of 4 GiB or more. The drive is faked: FAT32 media cannot be made on demand.
/// </summary>
public class TransferTargetTests
{
    private const long GiB = 1L << 30;

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvtarget_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static TransferTarget Drive(string format, long free) => new(_ => new TransferDriveInfo(format, free));

    [Test]
    public async Task AnOrdinaryFolderWithRoom_Passes_AndKeepsNoProbeFile()
    {
        string dir = NewTempDir();
        try
        {
            TransferTargetCheck check = Drive("NTFS", 100 * GiB).Check(dir, 10 * GiB, 5 * GiB, [null, ""]);
            using (Assert.Multiple())
            {
                await Assert.That(check.CanProceed).IsTrue();
                await Assert.That(check.Warnings).IsEmpty();
                await Assert.That(Directory.EnumerateFileSystemEntries(dir)).IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    [Arguments(105L, true)]
    [Arguments(104L, false)]
    public async Task FreeSpace_NeedsAFivePercentMargin(long freeMiB, bool passes)
    {
        string dir = NewTempDir();
        try
        {
            TransferTargetCheck check = Drive("exfat", freeMiB << 20).Check(dir, 100L << 20, 1, []);
            await Assert.That(check.CanProceed).IsEqualTo(passes);
            if (!passes)
            {
                await Assert.That(check.Warnings.Single().Kind).IsEqualTo(TransferWarningKind.InsufficientSpace);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    [Arguments("FAT32")]
    [Arguments("vfat")]
    [Arguments("msdos")]
    public async Task AFat32Drive_RefusesAFileOf4GiB_ButTakesOneByteLess(string format)
    {
        string dir = NewTempDir();
        try
        {
            TransferTarget target = Drive(format, 100 * GiB);
            TransferTargetCheck tooBig = target.Check(dir, 5 * GiB, 4 * GiB, []);
            TransferTargetCheck fits = target.Check(dir, 5 * GiB, TransferTarget.Fat32MaxFileBytes, []);
            using (Assert.Multiple())
            {
                await Assert.That(tooBig.IsFat32).IsTrue();
                await Assert.That(tooBig.CanProceed).IsFalse();
                await Assert.That(tooBig.Warnings.Single().Kind).IsEqualTo(TransferWarningKind.Fat32FileTooLarge);
                await Assert.That(tooBig.Warnings.Single().Message).Contains("exFAT");
                await Assert.That(fits.CanProceed).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task AnExFatDrive_TakesALargeFile()
    {
        string dir = NewTempDir();
        try
        {
            await Assert.That(Drive("exFAT", 100 * GiB).Check(dir, 10 * GiB, 8 * GiB, []).CanProceed).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task AFolderInsideOrEqualToAProtectedRoot_IsRefused()
    {
        string root = NewTempDir();
        string inside = Path.Combine(root, "cache", "sub");
        Directory.CreateDirectory(inside);
        string beside = root + "-beside";
        Directory.CreateDirectory(beside);
        try
        {
            TransferTarget target = Drive("NTFS", 100 * GiB);
            using (Assert.Multiple())
            {
                await Assert.That(target.Check(inside, 1, 1, [root]).Warnings.Single().Kind)
                    .IsEqualTo(TransferWarningKind.TargetProtected);
                await Assert.That(target.Check(root, 1, 1, [root.ToUpperInvariant()]).CanProceed).IsFalse();
                await Assert.That(target.Check(root, 1, 1, [root + Path.DirectorySeparatorChar]).CanProceed)
                    .IsFalse();
                await Assert.That(target.Check(beside, 1, 1, [root]).CanProceed).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(beside, true);
        }
    }

    [Test]
    public async Task AMissingFolder_IsRefused_AndAnUnreadableDrive_OnlyWarns()
    {
        string dir = NewTempDir();
        try
        {
            TransferTargetCheck missing = Drive("NTFS", GiB).Check(Path.Combine(dir, "gone"), 1, 1, []);
            TransferTargetCheck unknown = new TransferTarget(_ => null).Check(dir, 1, 1, []);
            using (Assert.Multiple())
            {
                await Assert.That(missing.Warnings.Single().Kind).IsEqualTo(TransferWarningKind.TargetMissing);
                await Assert.That(missing.CanProceed).IsFalse();
                await Assert.That(unknown.Warnings.Single().Kind).IsEqualTo(TransferWarningKind.SpaceUnknown);
                await Assert.That(unknown.CanProceed).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task TheRealDrive_ReadsTheTempFolder()
    {
        TransferTargetCheck check = new TransferTarget().Check(Path.GetTempPath(), 1, 1, []);
        using (Assert.Multiple())
        {
            await Assert.That(check.Drive).IsNotNull();
            await Assert.That(check.Drive!.AvailableFreeSpace).IsGreaterThan(0);
        }
    }
}
