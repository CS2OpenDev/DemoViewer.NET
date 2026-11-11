#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>What the file system under a folder reports: its format name and the space free to this user.</summary>
/// <param name="Format"><see cref="DriveInfo.DriveFormat" />, e.g. <c>NTFS</c>, <c>FAT32</c>, <c>exfat</c>, <c>vfat</c>, <c>msdos</c>.</param>
/// <param name="AvailableFreeSpace"><see cref="DriveInfo.AvailableFreeSpace" />.</param>
public sealed record TransferDriveInfo(string Format, long AvailableFreeSpace);

/// <summary>The outcome of <see cref="TransferTarget.Check" />.</summary>
/// <param name="Warnings">Every finding; a <see cref="TransferWarning.Blocking" /> one stops the transfer.</param>
/// <param name="Drive">What the drive reported, or null when it could not be read.</param>
public sealed record TransferTargetCheck(IReadOnlyList<TransferWarning> Warnings, TransferDriveInfo? Drive)
{
    /// <summary>No finding blocks the transfer.</summary>
    public bool CanProceed => Warnings.All(w => !w.Blocking);

    /// <summary>The drive is FAT32, which cannot hold a file of 4 GiB or more.</summary>
    public bool IsFat32 => Drive is not null && TransferTarget.IsFat32Format(Drive.Format);
}

/// <summary>
///     The checks a folder must pass before a bundle is written into it (export) or <c>.dem</c> files are copied
///     into it (import): it exists and is writable, it is not inside the app's own data or a library folder, it
///     has the room with a margin, and a FAT32 drive gets no file it cannot hold.
/// </summary>
/// <param name="driveInfo">
///     Reads the drive under a folder; null for the real <see cref="DriveInfo" />. A test seam: FAT32 media and
///     a full disk cannot be made on demand.
/// </param>
public sealed class TransferTarget(Func<string, TransferDriveInfo?>? driveInfo = null)
{
    /// <summary>The largest file FAT32 can hold: 4 GiB less one byte.</summary>
    public const long Fat32MaxFileBytes = uint.MaxValue;

    /// <summary>The free space asked for on top of the bytes to write: 5 %.</summary>
    public const double FreeSpaceMargin = 0.05;

    private readonly Func<string, TransferDriveInfo?> _driveInfo = driveInfo ?? ReadDrive;

    /// <summary>
    ///     True for the names <see cref="DriveInfo.DriveFormat" /> gives FAT32: <c>FAT32</c> on Windows,
    ///     <c>vfat</c> on Linux and <c>msdos</c> on macOS.
    /// </summary>
    public static bool IsFat32Format(string? format) =>
        format is not null && (format.Equals("FAT32", StringComparison.OrdinalIgnoreCase)
                               || format.Equals("vfat", StringComparison.OrdinalIgnoreCase)
                               || format.Equals("msdos", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Checks <paramref name="folder" /> for a transfer of <paramref name="bytesNeeded" /> bytes whose largest
    ///     single file is <paramref name="largestFileBytes" />. Writes and deletes one probe file in the folder;
    ///     touches nothing else.
    /// </summary>
    /// <param name="folder">The folder the user picked. It must exist.</param>
    /// <param name="bytesNeeded">Everything the transfer will write there.</param>
    /// <param name="largestFileBytes">Its largest single file, for the FAT32 limit.</param>
    /// <param name="protectedRoots">
    ///     Folders the target may not be inside or equal to: the config root, the cache root and every library
    ///     root. Null and empty entries are ignored.
    /// </param>
    public TransferTargetCheck Check(string folder, long bytesNeeded, long largestFileBytes,
        IEnumerable<string?> protectedRoots)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(protectedRoots);
        List<TransferWarning> warnings = [];
        string full = Path.GetFullPath(folder);

        if (!Directory.Exists(full))
        {
            warnings.Add(new TransferWarning(TransferWarningKind.TargetMissing, "The folder does not exist.", true));
            return new TransferTargetCheck(warnings, null);
        }

        string? inside = protectedRoots.FirstOrDefault(root => !string.IsNullOrEmpty(root) && IsSameOrInside(root, full));
        if (inside is not null)
        {
            warnings.Add(new TransferWarning(TransferWarningKind.TargetProtected,
                $"Choose a folder outside DemoViewer's own data and your library folders ({inside}).", true));
        }

        if (!CanWrite(full))
        {
            warnings.Add(new TransferWarning(TransferWarningKind.TargetNotWritable,
                "DemoViewer cannot write to this folder.", true));
        }

        TransferDriveInfo? drive = _driveInfo(full);
        if (drive is null)
        {
            warnings.Add(new TransferWarning(TransferWarningKind.SpaceUnknown,
                "The free space on this drive could not be read."));
            return new TransferTargetCheck(warnings, null);
        }

        long wanted = bytesNeeded + (long)Math.Ceiling(bytesNeeded * FreeSpaceMargin);
        if (drive.AvailableFreeSpace < wanted)
        {
            warnings.Add(new TransferWarning(TransferWarningKind.InsufficientSpace,
                $"This needs {FormatBytes(wanted)} free; the drive has {FormatBytes(drive.AvailableFreeSpace)}.",
                true));
        }

        if (IsFat32Format(drive.Format) && largestFileBytes > Fat32MaxFileBytes)
        {
            warnings.Add(new TransferWarning(TransferWarningKind.Fat32FileTooLarge,
                $"This drive is FAT32, which cannot hold a file of 4 GB or more, and one file is "
                + $"{FormatBytes(largestFileBytes)}. Format the drive as exFAT, or leave the .dem files out.", true));
        }

        return new TransferTargetCheck(warnings, drive);
    }

    /// <summary>
    ///     True when <paramref name="path" /> is <paramref name="root" /> or under it. Case-insensitive on every
    ///     OS, so on a case-sensitive disk it refuses a little more than it must, never less.
    /// </summary>
    public static bool IsSameOrInside(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes and deletes a probe file in <paramref name="folder" />. False when either step fails.</summary>
    public static bool CanWrite(string folder)
    {
        string probe = Path.Combine(folder, ".dv-transfer-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                       FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }

            return !File.Exists(probe) || TryDelete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(probe);
            return false;
        }
    }

    // The drive whose mount point is the longest prefix of the folder: on Unix a folder sits under "/" and
    // under its own mount, and only the longer one is the right file system.
    private static TransferDriveInfo? ReadDrive(string folder)
    {
        try
        {
            DriveInfo? best = null;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string mount = drive.RootDirectory.FullName;
                if ((IsSameOrInside(mount, folder) || mount == "/")
                    && (best is null || mount.Length > best.RootDirectory.FullName.Length))
                {
                    best = drive;
                }
            }

            return best is { IsReady: true } ? new TransferDriveInfo(best.DriveFormat, best.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30
            ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";
}
