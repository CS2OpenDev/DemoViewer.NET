#region

using System.Text;

#endregion

namespace DemoViewer.NET.Services;

/// <summary>
///     Whole-file writes that a crash cannot tear: the bytes go to a hidden temporary file beside the target,
///     are flushed to the disk, and the file then replaces it. A reader sees the previous file or the new one,
///     never half of either, after a process crash or a power loss alike. Every store of the app that writes a
///     file under the config or cache root writes through here.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding _utf8 = new(false);

    // Pauses between attempts to replace the target, about 0.4 s in all before the failure surfaces.
    private static readonly int[] _replaceRetryDelaysMs = [10, 25, 50, 100, 200];

    /// <summary>Replaces <paramref name="path" /> with <paramref name="content" />. Missing folders are created.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="content">Its whole new content.</param>
    public static void WriteAllBytes(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Write(path, stream => stream.Write(content));
    }

    /// <summary>Replaces <paramref name="path" /> with <paramref name="content" /> as UTF-8 without a byte order mark.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="content">Its whole new content.</param>
    public static void WriteAllText(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        WriteAllBytes(path, _utf8.GetBytes(content));
    }

    /// <summary>Replaces <paramref name="path" /> with whatever <paramref name="write" /> puts in the stream it is handed.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="write">Writes the whole new content. A throw leaves the previous file.</param>
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        string temp = TempFor(path);
        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                // On disk before the rename: a rename can reach the disk before the data it points at.
                stream.Flush(flushToDisk: true);
            }

            Replace(temp, path);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    // Windows refuses to rename over a file while another handle on it lacks FILE_SHARE_DELETE, which antivirus
    // and the search indexer hold for a moment after a write, so that refusal is retried. Elsewhere a rename
    // over an open file succeeds, so a failure there is not transient and surfaces at once.
    private static void Replace(string temp, string target)
    {
        for (int attempt = 0;; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < _replaceRetryDelaysMs.Length && OperatingSystem.IsWindows()
                                                                             && IsTransientOnWindows(ex))
            {
                Thread.Sleep(_replaceRetryDelaysMs[attempt]);
            }
        }
    }

    // ERROR_ACCESS_DENIED is what a held target produces; ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION
    // (33) are the same contention reported another way.
    private static bool IsTransientOnWindows(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33);

    // Beside the target, so the move is a rename on the same volume.
    private static string TempFor(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
    }

    private static void TryDelete(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temp file is harmless; the original failure, if any, is already on its way out.
        }
    }
}
