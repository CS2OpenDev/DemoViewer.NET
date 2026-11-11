#region

using System.IO.Compression;
using System.Text.Json;
using DemoViewer.NET.ViewModels.Diagnostics;

#endregion

namespace DemoViewer.NET.Extensions;

internal sealed partial class ExtensionDemoDataStore
{
    /// <summary>
    ///     Brings one demo's files of one facet in from a transfer bundle: the main payload
    ///     (<c>&lt;sha&gt;.json.gz</c>) and its parts (<c>&lt;sha&gt;.&lt;part&gt;.json.gz</c>). The headers are
    ///     rewritten with no source path, and the index takes the facet under the content hash. A facet this
    ///     machine already holds is replaced only when the import is a successful write and the local one is not,
    ///     or the import is at a higher schema, or the local one was invalidated; it never takes a lower schema.
    /// </summary>
    /// <param name="sha256">The demo's content id, lowercase hex.</param>
    /// <param name="facet">The facet.</param>
    /// <param name="files">The facet's files by file name.</param>
    /// <returns>True when the files were taken; false when the local facet was kept.</returns>
    /// <exception cref="ArgumentException">The content id, facet or a file name is malformed.</exception>
    /// <exception cref="InvalidDataException">A file does not read, belongs elsewhere, or the main payload is missing.</exception>
    public bool Import(string sha256, string facet, IReadOnlyList<KeyValuePair<string, byte[]>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (sha256 is not { Length: 64 } || !sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("A content id is a lowercase-hex SHA-256.", nameof(sha256));
        }

        ExtensionFolders.SafeName(facet);
        List<(string? Part, Header Header, byte[] Content)> parsed = [];
        foreach ((string name, byte[] bytes) in files)
        {
            string? part = PartOf(sha256, name);
            (Header header, byte[] content) = ReadBytes(bytes);
            if (header.Format != CurrentFormat || !string.Equals(header.Facet, facet, StringComparison.Ordinal)
                || !string.Equals(header.Part, part, StringComparison.Ordinal)
                || (header.Sha256 is not null && !string.Equals(header.Sha256, sha256, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"'{name}' is not {facet} data of this demo.");
            }

            parsed.Add((part, header, content));
        }

        if (parsed.FindIndex(p => p.Part is null) is var at && at < 0)
        {
            throw new InvalidDataException($"The {facet} data has no main payload.");
        }

        Header main = parsed[at].Header;

        EnsureLoaded();
        lock (_gate)
        {
            if (Entries().GetValueOrDefault((sha256, facet)) is { } mine && !Takes(mine, main))
            {
                return false;
            }
        }

        // The path is this machine's: the content's listed path when it has one, else none until a folder lists it.
        string demoPath = _library.TryGetByContentId(sha256)?.Path ?? _library.PathOfAnyLocation(sha256) ?? "";
        HashSet<string> written = new(StringComparer.Ordinal);
        foreach ((string? part, Header header, byte[] content) in parsed)
        {
            string file = FileFor(facet, sha256, part);
            WriteFile(file, header with { Sha256 = sha256, DemoPath = demoPath }, content);
            written.Add(file);
        }

        // Parts the imported write did not have belong to the local write it replaces.
        foreach (string stale in FilesOf(facet, sha256).Where(f => !written.Contains(f)))
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.ExtensionStoreWriteFailed(Log, _extensionId, stale, ex.Message);
            }
        }

        IndexFile save;
        lock (_gate)
        {
            Entries()[(sha256, facet)] = new Entry(sha256, facet, demoPath, sha256, main.Schema, main.Fingerprint,
                DemoDataState.Written, main.WrittenAtTicks, 0);
            save = Snapshot();
        }

        Save(save);
        Raise(demoPath.Length > 0 ? demoPath : null);
        return true;
    }

    /// <summary>Stops following the library: for a store made only to import into an extension that is not loaded.</summary>
    public void StopFollowingLibrary() => _library.Changed -= OnLibraryChanged;

    // The import replaces a local facet that is not a settled write, that an older schema wrote, or that was
    // invalidated. Two settled writes at one schema under different fingerprints cannot be told apart here, so
    // the local one stays.
    private static bool Takes(Entry mine, Header theirs)
    {
        if (theirs.Schema != mine.Schema)
        {
            return theirs.Schema > mine.Schema;
        }

        return mine.State != DemoDataState.Written || (mine.Fingerprint is null && theirs.Fingerprint is not null);
    }

    // Null for the main payload's name, the part for a part's, and a throw for any other name.
    private static string? PartOf(string sha256, string name)
    {
        if (string.Equals(name, sha256 + Suffix, StringComparison.Ordinal))
        {
            return null;
        }

        if (name.StartsWith(sha256 + ".", StringComparison.Ordinal) && name.EndsWith(Suffix, StringComparison.Ordinal)
            && name.Length > sha256.Length + 1 + Suffix.Length)
        {
            return ExtensionFolders.SafeName(name[(sha256.Length + 1)..^Suffix.Length]);
        }

        throw new ArgumentException($"'{name}' is not a data file of demo {sha256}.", nameof(name));
    }

    private static (Header Header, byte[] Content) ReadBytes(byte[] bytes)
    {
        try
        {
            using MemoryStream source = new(bytes);
            using GZipStream gzip = new(source, CompressionMode.Decompress);
            using MemoryStream all = new();
            gzip.CopyTo(all);
            byte[] plain = all.ToArray();
            int newline = Array.IndexOf(plain, (byte)'\n');
            if (newline < 0)
            {
                throw new InvalidDataException("No header line.");
            }

            Header header = JsonSerializer.Deserialize<Header>(plain.AsSpan(0, newline), _jsonOptions)
                ?? throw new InvalidDataException("Empty header.");
            return (header, plain[(newline + 1)..]);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The header does not read.", ex);
        }
    }
}
