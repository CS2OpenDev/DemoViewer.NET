#region

using System.IO.Enumeration;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>
///     A bundle's copy of one extension store or own folder, as <see cref="IExtensionImport.Staged" /> and
///     <see cref="IExtensionImport.StagedOwn" /> hand it over: the files the manifest lists, at their paths relative
///     to the store's root, and nothing else. Each read is checked against the manifest's hash, and every write
///     throws, so the extension can neither touch the bundle nor see a file the manifest does not vouch for.
/// </summary>
internal sealed class StagedBundleFolder : IExtensionFolder
{
    private readonly string _bundle;
    private readonly Dictionary<string, TransferFile> _files;

    /// <param name="path">What <see cref="Path" /> reports; the folder need not exist.</param>
    /// <param name="bundle">The bundle's root.</param>
    /// <param name="files">Each file by its path relative to the store's root, with <c>/</c> between segments.</param>
    public StagedBundleFolder(string path, string bundle, IReadOnlyDictionary<string, TransferFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        Path = path;
        _bundle = bundle;
        _files = new Dictionary<string, TransferFile>(files, PathSafety.CaseInsensitivePaths);
    }

    public string Path { get; }

    /// <summary>Why a read found a file that no longer matches the manifest, or null while none has.</summary>
    public string? Damage { get; private set; }

    public void Create() => throw ReadOnly();

    public bool FileExists(string relativePath) => _files.ContainsKey(File(relativePath));

    public bool DirectoryExists(string relativePath)
    {
        string folder = Folder(relativePath);
        return folder.Length == 0 ? _files.Count > 0 : _files.Keys.Any(k => Inside(k, folder));
    }

    public DateTime? LastWriteTimeUtc(string relativePath) =>
        _files.TryGetValue(File(relativePath), out TransferFile? file)
            ? System.IO.File.GetLastWriteTimeUtc(TransferLayout.FullPath(_bundle, file.Path))
            : null;

    public IEnumerable<string> EnumerateFiles(string relativePath, string searchPattern)
    {
        ArgumentNullException.ThrowIfNull(searchPattern);
        string folder = Folder(relativePath);
        return
        [
            .. _files.Keys.Where(k =>
                {
                    int slash = k.LastIndexOf('/');
                    return string.Equals(slash < 0 ? "" : k[..slash], folder, StringComparison.OrdinalIgnoreCase)
                        && FileSystemName.MatchesSimpleExpression(searchPattern, k.AsSpan(slash + 1));
                })
                .Select(Native)
        ];
    }

    public IEnumerable<string> EnumerateDirectories(string relativePath)
    {
        string folder = Folder(relativePath);
        string prefix = folder.Length == 0 ? "" : folder + "/";
        return
        [
            .. _files.Keys.Where(k => folder.Length == 0 || Inside(k, folder))
                .Select(k => k[prefix.Length..])
                .Where(rest => rest.Contains('/', StringComparison.Ordinal))
                .Select(rest => prefix + rest[..rest.IndexOf('/', StringComparison.Ordinal)])
                .Distinct(PathSafety.CaseInsensitivePaths)
                .Select(Native)
        ];
    }

    public byte[]? Read(string relativePath)
    {
        if (!_files.TryGetValue(File(relativePath), out TransferFile? file))
        {
            return null;
        }

        try
        {
            return TransferImporter.ReadVerified(_bundle, file);
        }
        catch (InvalidDataException ex)
        {
            Damage ??= ex.Message;
            throw;
        }
    }

    public Stream? OpenRead(string relativePath) =>
        Read(relativePath) is { } bytes ? new MemoryStream(bytes, false) : null;

    public void WriteAtomic(string relativePath, ReadOnlySpan<byte> content) => throw ReadOnly();

    public void WriteAtomic(string relativePath, Action<Stream> write) => throw ReadOnly();

    public void Append(string relativePath, string line) => throw ReadOnly();

    public void Move(string fromRelativePath, string toRelativePath, bool overwrite) => throw ReadOnly();

    public void Delete(string relativePath) => throw ReadOnly();

    public void SetLastWriteTimeUtc(string relativePath, DateTime value) => throw ReadOnly();

    private static InvalidOperationException ReadOnly() =>
        new("A bundle's copy of a store is read-only; an import writes into its own stores.");

    private static bool Inside(string key, string folder) =>
        key.Length > folder.Length + 1 && key[folder.Length] == '/'
                                       && key.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

    // Paths come back with the platform's separator, as the host's own folder lists them.
    private static string Native(string key) => key.Replace('/', System.IO.Path.DirectorySeparatorChar);

    private static string File(string relativePath) =>
        PathSafety.SafeRelativePath(relativePath)?.Replace('\\', '/')
        ?? throw new ArgumentException($"'{relativePath}' is not a file inside the store.", nameof(relativePath));

    private static string Folder(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (relativePath.Length == 0 || relativePath == ".")
        {
            return "";
        }

        return PathSafety.SafeRelativePath(relativePath)?.Replace('\\', '/')
               ?? throw new ArgumentException($"'{relativePath}' is not a folder inside the store.",
                   nameof(relativePath));
    }
}
