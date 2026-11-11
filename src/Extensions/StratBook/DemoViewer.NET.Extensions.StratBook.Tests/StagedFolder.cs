namespace DemoViewer.NET.AppTests;

/// <summary>
///     A bundle's copy of a store as an import sees it: the host's folder over a plain path, with every write
///     refused, so a test fails loudly if an import ever writes to what it was handed.
/// </summary>
internal sealed class StagedFolder(string path) : IExtensionFolder
{
    private readonly IExtensionFolder _inner = PackStores.At(path);

    public string Path => _inner.Path;

    public void Create() => throw Refused();

    public bool FileExists(string relativePath) => _inner.FileExists(relativePath);

    public bool DirectoryExists(string relativePath) => _inner.DirectoryExists(relativePath);

    public void SetLastWriteTimeUtc(string relativePath, DateTime value) => throw Refused();

    public DateTime? LastWriteTimeUtc(string relativePath) => _inner.LastWriteTimeUtc(relativePath);

    public IEnumerable<string> EnumerateFiles(string relativePath, string searchPattern) =>
        _inner.EnumerateFiles(relativePath, searchPattern);

    public IEnumerable<string> EnumerateDirectories(string relativePath) => _inner.EnumerateDirectories(relativePath);

    public byte[]? Read(string relativePath) => _inner.Read(relativePath);

    public Stream? OpenRead(string relativePath) => _inner.OpenRead(relativePath);

    public void WriteAtomic(string relativePath, ReadOnlySpan<byte> content) => throw Refused();

    public void WriteAtomic(string relativePath, Action<Stream> write) => throw Refused();

    public void Append(string relativePath, string line) => throw Refused();

    public void Move(string fromRelativePath, string toRelativePath, bool overwrite) => throw Refused();

    public void Delete(string relativePath) => throw Refused();

    private static InvalidOperationException Refused() => new("an import wrote to the bundle's copy");

    /// <summary>Every file under a folder with its bytes, for a before-and-after comparison.</summary>
    /// <param name="root">The folder.</param>
    /// <param name="except">File names left out, such as a derived index.</param>
    public static SortedDictionary<string, string> Snapshot(string root, params string[] except)
    {
        SortedDictionary<string, string> files = new(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return files;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!except.Contains(System.IO.Path.GetFileName(file), StringComparer.Ordinal))
            {
                files[System.IO.Path.GetRelativePath(root, file).Replace('\\', '/')] =
                    Convert.ToHexString(File.ReadAllBytes(file));
            }
        }

        return files;
    }
}
