namespace DemoViewer.NET.TestSupport;

/// <summary>
///     File operations a test performs on files it has just written. On Windows, antivirus and the search
///     indexer open a new file for a moment after it is closed, and while they do a delete is refused with a
///     sharing violation or access denied. A test that deletes such a file as part of its scenario retries
///     through that; anywhere else the first failure is real and surfaces at once.
/// </summary>
public static class TestFiles
{
    private static readonly TimeSpan _patience = TimeSpan.FromSeconds(3);

    /// <summary>Deletes <paramref name="path" />, waiting out a transient hold on Windows.</summary>
    public static void Delete(string path)
    {
        DateTime deadline = DateTime.UtcNow + _patience;
        while (true)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (OperatingSystem.IsWindows() && IsTransientHold(ex) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    // ERROR_ACCESS_DENIED, ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33) are how a held file
    // refuses a delete.
    private static bool IsTransientHold(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33);
}
