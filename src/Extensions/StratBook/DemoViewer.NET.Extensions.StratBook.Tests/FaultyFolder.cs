namespace DemoViewer.NET.AppTests;

/// <summary>
///     The host's folder with an append or a move that fails on demand, as a full disk or a file another reader
///     holds open would: for what a store leaves on disk when the second of two writes does not land.
/// </summary>
internal sealed class FaultyFolder(IExtensionFolder inner) : IExtensionFolder
{
    /// <summary>Whether the append to this path fails.</summary>
    public Func<string, bool>? FailAppend { get; set; }

    /// <summary>Whether the move from the first path to the second fails.</summary>
    public Func<string, string, bool>? FailMove { get; set; }

    /// <summary>Called with the path once a read has its bytes and before the reader gets them.</summary>
    public Action<string>? AfterRead { get; set; }

    public string Path => inner.Path;

    public void Create() => inner.Create();

    public bool FileExists(string relativePath) => inner.FileExists(relativePath);

    public bool DirectoryExists(string relativePath) => inner.DirectoryExists(relativePath);

    public void SetLastWriteTimeUtc(string relativePath, DateTime value) =>
        inner.SetLastWriteTimeUtc(relativePath, value);

    public DateTime? LastWriteTimeUtc(string relativePath) => inner.LastWriteTimeUtc(relativePath);

    public IEnumerable<string> EnumerateFiles(string relativePath, string searchPattern) =>
        inner.EnumerateFiles(relativePath, searchPattern);

    public IEnumerable<string> EnumerateDirectories(string relativePath) => inner.EnumerateDirectories(relativePath);

    public byte[]? Read(string relativePath)
    {
        byte[]? bytes = inner.Read(relativePath);
        AfterRead?.Invoke(relativePath);
        return bytes;
    }

    public Stream? OpenRead(string relativePath) => inner.OpenRead(relativePath);

    public void WriteAtomic(string relativePath, ReadOnlySpan<byte> content) =>
        inner.WriteAtomic(relativePath, content);

    public void WriteAtomic(string relativePath, Action<Stream> write) => inner.WriteAtomic(relativePath, write);

    public void Append(string relativePath, string line)
    {
        if (FailAppend?.Invoke(relativePath) == true)
        {
            throw new IOException("the append was made to fail");
        }

        inner.Append(relativePath, line);
    }

    public void Move(string fromRelativePath, string toRelativePath, bool overwrite)
    {
        if (FailMove?.Invoke(fromRelativePath, toRelativePath) == true)
        {
            throw new IOException("the move was made to fail");
        }

        inner.Move(fromRelativePath, toRelativePath, overwrite);
    }

    public void Delete(string relativePath) => inner.Delete(relativePath);
}
