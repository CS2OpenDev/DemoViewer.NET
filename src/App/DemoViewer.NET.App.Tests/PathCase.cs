namespace DemoViewer.NET.AppTests;

/// <summary>
///     The same demo path written in another case, where the file system folds case (Windows, macOS); on Linux,
///     where another case names another file, the path as it is.
/// </summary>
internal static class PathCase
{
    internal static string Other(string path) =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? path.ToUpperInvariant() : path;
}
