namespace DemoViewer.NET.Services;

/// <summary>
///     How the library and the demo cache tell two demo paths apart in memory: by case on Linux, whose file
///     systems keep two names that differ only by case as two files, and ignoring case on Windows and macOS,
///     whose default file systems fold it. The same rule as the extension updater and the rule workbench.
///     Keys that reach disk (the cache's path-derived file names) still fold case everywhere.
/// </summary>
internal static class PathComparer
{
    private static readonly bool FoldsCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>The comparer for a set or map keyed by demo path.</summary>
    internal static StringComparer Paths { get; } = FoldsCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The comparison for two demo paths, or a path and a folder it may sit under.</summary>
    internal static StringComparison Comparison { get; } =
        FoldsCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
