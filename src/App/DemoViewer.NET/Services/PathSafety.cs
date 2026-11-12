namespace DemoViewer.NET.Services;

/// <summary>
///     Checks for relative paths that come from outside the app (a zip entry, a transfer manifest) before any of
///     them is joined to a folder on disk: no rooted path, no drive or stream colon, no <c>..</c> or empty
///     segment, nothing that resolves outside the folder, and no two names that differ only by case.
/// </summary>
public static class PathSafety
{
    /// <summary>
    ///     The comparer two relative paths are told apart with. Case-insensitive, because two names that differ
    ///     only by case are one file on Windows and macOS, and which one wins would depend on the order.
    /// </summary>
    public static StringComparer CaseInsensitivePaths => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    ///     <paramref name="relativePath" /> joined with the platform separator, or null when it is empty, rooted,
    ///     carries a <c>:</c> or a NUL, or has a <c>.</c>, <c>..</c>, empty or blank segment. A single trailing
    ///     separator (a directory entry) is allowed.
    /// </summary>
    // Both separators are split on: a zip written on Windows may carry backslashes, and on Unix a
    // backslash is a legal file-name character, which is exactly how ".." hides.
    public static string? SafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal)
            || relativePath.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        string[] segments = relativePath.Split('/', '\\');
        List<string> kept = [];
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0 && i == segments.Length - 1)
            {
                continue; // a directory entry's trailing separator
            }

            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Trim().Length == 0)
            {
                return null;
            }

            kept.Add(segment);
        }

        return kept.Count == 0 ? null : Path.Combine(kept.ToArray());
    }

    /// <summary>
    ///     The full path of <paramref name="relativePath" /> under <paramref name="root" />, or null when the
    ///     relative path is unsafe (<see cref="SafeRelativePath" />) or resolves to the root itself or outside it.
    /// </summary>
    public static string? FullPathInside(string root, string? relativePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        string? relative = SafeRelativePath(relativePath);
        if (relative is null)
        {
            return null;
        }

        string rootFull = Path.GetFullPath(root);
        string full = Path.GetFullPath(Path.Combine(rootFull, relative));
        return IsStrictlyInside(rootFull, full) ? full : null;
    }

    /// <summary>
    ///     True when <paramref name="path" /> sits strictly under <paramref name="root" /> once both are made full.
    ///     Ordinal: a case-only difference counts as outside, which refuses rather than allows.
    /// </summary>
    public static bool IsStrictlyInside(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        string rootFull = Path.GetFullPath(root);
        string rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.StartsWith(rootWithSeparator, StringComparison.Ordinal) && full.Length > rootWithSeparator.Length;
    }

    /// <summary>
    ///     The second of the first two paths that differ only by case (or not at all), or null when every path is
    ///     distinct under <see cref="CaseInsensitivePaths" />. Separators are compared as written, so callers pass
    ///     paths already through <see cref="SafeRelativePath" />.
    /// </summary>
    public static string? FindCaseDuplicate(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        HashSet<string> seen = new(CaseInsensitivePaths);
        foreach (string path in relativePaths)
        {
            if (!seen.Add(path))
            {
                return path;
            }
        }

        return null;
    }
}
