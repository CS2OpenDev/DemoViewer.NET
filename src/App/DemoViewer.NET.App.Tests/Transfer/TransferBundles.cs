#region

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Transfer;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     Builds transfer bundles by hand from the layout and manifest types, so import is tested against the
///     format rather than against whatever the exporter writes.
/// </summary>
internal sealed class BundleBuilder(string root)
{
    // The cache store reads records with default options.
    private static readonly JsonSerializerOptions _recordJson = new();

    private readonly List<TransferDemo> _demos = [];
    private readonly List<TransferFile> _shared = [];
    private readonly List<TransferExtensionFile> _extensionData = [];

    public string Root { get; } = root;

    /// <summary>Writes a file into the bundle and returns its manifest entry.</summary>
    public TransferFile File(string relative, byte[] bytes)
    {
        string full = TransferLayout.FullPath(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, bytes);
        return new TransferFile(relative, bytes.Length, Sha(bytes));
    }

    public TransferFile Record(DemoCacheRecord record) =>
        File(TransferLayout.CacheFile(record.Sha256!, TransferLayout.DemoFileName(DemoCacheStore.RecordSuffix)),
            SidecarJson.SerializeGzip(record, _recordJson));

    public TransferFile Sibling(string sha, string suffix, string text) =>
        File(TransferLayout.CacheFile(sha, TransferLayout.DemoFileName(suffix)), Encoding.UTF8.GetBytes(text));

    public TransferFile Annotations(string sha, params string[] elementIds) =>
        File(TransferLayout.AnnotationsFile(sha), AnnotationDocument(sha, elementIds));

    public TransferDemFile Dem(string sha, byte[] bytes)
    {
        File(TransferLayout.DemFile(sha), bytes);
        return new TransferDemFile(TransferLayout.DemFile(sha), bytes.Length);
    }

    public void Shared(string fileName, byte[] bytes) => _shared.Add(File(TransferLayout.SharedFile(fileName), bytes));

    /// <summary>A per-demo store file, described in the manifest; list the returned entry among the demo's files.</summary>
    public TransferFile DemoStore(string sha, string extensionId, string storeId, StoreRoot root, string relative,
        byte[] bytes)
    {
        TransferFile file = File(TransferLayout.ExtensionStoreFile(sha, extensionId, storeId, relative), bytes);
        _extensionData.Add(new TransferExtensionFile(file.Path, file.Size, file.Sha256, extensionId, storeId, root,
            StorePortability.PerDemo, relative, sha));
        return file;
    }

    /// <summary>A file of a library-wide store.</summary>
    public void LibraryStore(string extensionId, string storeId, StoreRoot root, string relative, byte[] bytes)
    {
        TransferFile file = File(TransferLayout.LibraryStoreFile(extensionId, storeId, relative), bytes);
        _extensionData.Add(new TransferExtensionFile(file.Path, file.Size, file.Sha256, extensionId, storeId, root,
            StorePortability.LibraryWide, relative, null));
    }

    /// <summary>A file of an extension's own folder under <paramref name="root" />.</summary>
    public void LibraryOwn(string extensionId, StoreRoot root, string relative, byte[] bytes)
    {
        TransferFile file = File(TransferLayout.LibraryOwnFile(extensionId, root, relative), bytes);
        _extensionData.Add(new TransferExtensionFile(file.Path, file.Size, file.Sha256, extensionId, null, root,
            StorePortability.LibraryWide, relative, null));
    }

    public void Demo(string sha, string name, long size, IReadOnlyList<TransferFile> files,
        DemoContentFingerprint? fingerprint = null, TransferDemFile? dem = null,
        IReadOnlyDictionary<string, int>? facetCounts = null) =>
        _demos.Add(new TransferDemo(sha, name, size, files,
            fingerprint is null ? null : TransferFingerprint.From(fingerprint), "de_test", null, dem, facetCounts));

    public TransferManifest Manifest(string? appVersion = null, IReadOnlyList<string>? rulesets = null) => new()
    {
        FormatId = TransferManifest.FormatIdValue,
        AppVersion = appVersion ?? TransferManifest.CurrentAppVersion,
        CreatedUtc = DateTime.UtcNow,
        SourceOs = "test",
        Options = new TransferManifestOptions(_demos.Any(d => d.Dem is not null), true, false, false),
        RulesetIdentities = rulesets ?? [],
        Extensions = [],
        Demos = [.. _demos],
        Shared = [.. _shared],
        ExtensionData = [.. _extensionData]
    };

    public TransferManifest Write(string? appVersion = null, IReadOnlyList<string>? rulesets = null)
    {
        TransferManifest manifest = Manifest(appVersion, rulesets);
        manifest.Write(Root);
        return manifest;
    }

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A parsed record of the content as the source machine stored it, its paths included.</summary>
    public static DemoCacheRecord ParsedRecord(string sha, long size, string map = "de_test")
    {
        DemoCacheRecord record = new()
        {
            Path = @"D:\source\match.dem",
            Size = size,
            ModifiedTicks = 638000000000000000,
            Sha256 = sha,
            Map = map,
            TickRate = 64,
            Locations = [new DemoLocation(@"D:\source\match.dem", true, size, 638000000000000000, 1)]
        };
        DemoCacheStore.StampHeader(record);
        DemoCacheStore.StampParse(record);
        return record;
    }

    /// <summary>A minimal annotation sidecar of the demo holding elements with these ids.</summary>
    public static byte[] AnnotationDocument(string sha, params string[] elementIds) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            demo = new { sha256 = sha, fileName = "match.dem", sizeBytes = 1 },
            elements = elementIds.Select(id => new { id, kind = "stroke" }).ToArray()
        }));

    /// <summary>The element ids of an annotation sidecar, in order.</summary>
    public static List<string> ElementIds(string file)
    {
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(file));
        return [.. doc.RootElement.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("id").GetString()!)];
    }

    /// <summary>An extension data file as the store writes one: a gzipped header line, then the payload.</summary>
    public static byte[] FacetFile(string facet, string? part, int schema, string? fingerprint, string? sha,
        string sourcePath, string payload)
    {
        string header = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Format"] = 1, ["Facet"] = facet, ["Part"] = part, ["Schema"] = schema, ["Fingerprint"] = fingerprint,
            ["Sha256"] = sha, ["DemoPath"] = sourcePath, ["ExtensionVersion"] = "1.0.0", ["WrittenAtTicks"] = 42L
        });
        using MemoryStream bytes = new();
        using (GZipStream gzip = new(bytes, CompressionLevel.Fastest, true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(header + "\n" + payload));
        }

        return bytes.ToArray();
    }
}

/// <summary>Temporary folders for one test, deleted afterwards.</summary>
internal sealed class TempFolders : IDisposable
{
    private readonly List<string> _made = [];

    public string New(string? name = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "dvtransfer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _made.Add(path);
        if (name is null)
        {
            return path;
        }

        string child = Path.Combine(path, name);
        Directory.CreateDirectory(child);
        return child;
    }

    public void Dispose()
    {
        foreach (string path in _made)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
                // A file a background read still holds; the temp folder is the OS's to clear.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
