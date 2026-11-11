#region

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>
///     The <c>transfer.json</c> at a bundle's root, written last so its presence marks a complete bundle. The
///     format has no versions: a bundle opens only in the exact app build that wrote it
///     (<see cref="CheckAppVersion" />). Read strictly: an unknown or missing member, a wrong
///     <see cref="FormatId" />, a malformed hash or an unsafe or case-duplicate path refuses the whole bundle.
/// </summary>
public sealed record TransferManifest
{
    /// <summary>The only <see cref="FormatId" /> a bundle may carry.</summary>
    public const string FormatIdValue = "dv-transfer";

    // The JSON is camelCase, written indented so a user can read it, and read with no tolerance: a bundle
    // from another build is refused by version, so an unknown member means a damaged or foreign file.
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    /// <summary>Always <see cref="FormatIdValue" />.</summary>
    public required string FormatId { get; init; }

    /// <summary>The exact informational version of the app that wrote the bundle.</summary>
    public required string AppVersion { get; init; }

    /// <summary>When the export finished, UTC.</summary>
    public required DateTime CreatedUtc { get; init; }

    /// <summary>The writing machine's OS, for display (<see cref="Environment.OSVersion" />'s description).</summary>
    public required string SourceOs { get; init; }

    /// <summary>The options the export ran with.</summary>
    public required TransferManifestOptions Options { get; init; }

    /// <summary>
    ///     The exporting machine's rulesets that are on, one <c>id:identity</c> each as
    ///     <see cref="TransferEnvironment" /> lists them, for the staleness warning on import.
    /// </summary>
    public required IReadOnlyList<string> RulesetIdentities { get; init; }

    /// <summary>The extensions whose data the bundle carries, with the version that wrote it.</summary>
    public required IReadOnlyList<TransferExtensionVersion> Extensions { get; init; }

    /// <summary>One entry per exported demo.</summary>
    public required IReadOnlyList<TransferDemo> Demos { get; init; }

    /// <summary>The files under <see cref="TransferLayout.SharedDirectory" />.</summary>
    public required IReadOnlyList<TransferFile> Shared { get; init; }

    /// <summary>The informational version of this build, which <see cref="AppVersion" /> must equal to import.</summary>
    public static string CurrentAppVersion { get; } =
        typeof(TransferManifest).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(TransferManifest).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    ///     Null when <paramref name="manifest" /> was written by <paramref name="currentAppVersion" /> (this
    ///     build's <see cref="CurrentAppVersion" /> when null), else the refusal in user terms.
    /// </summary>
    public static string? CheckAppVersion(TransferManifest manifest, string? currentAppVersion = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string current = currentAppVersion ?? CurrentAppVersion;
        return string.Equals(manifest.AppVersion, current, StringComparison.Ordinal)
            ? null
            : $"This bundle was exported by DemoViewer {manifest.AppVersion}; this is {current}. "
              + "Export and import with the same version.";
    }

    /// <summary>The manifest as UTF-8 JSON.</summary>
    public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, _json);

    /// <summary>Writes the manifest to <c>transfer.json</c> under <paramref name="bundleRoot" /> in one atomic replace.</summary>
    public void Write(string bundleRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundleRoot);
        AtomicFile.WriteAllBytes(Path.Combine(bundleRoot, TransferLayout.ManifestFileName), ToJsonBytes());
    }

    /// <summary>Reads and validates <c>transfer.json</c> under <paramref name="bundleRoot" />. The version is not checked here.</summary>
    /// <exception cref="TransferManifestException">The file is missing, unreadable, malformed or unsafe.</exception>
    public static TransferManifest Read(string bundleRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundleRoot);
        string path = Path.Combine(bundleRoot, TransferLayout.ManifestFileName);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new TransferManifestException(
                $"This folder is not a complete transfer bundle: it has no {TransferLayout.ManifestFileName}.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TransferManifestException($"{TransferLayout.ManifestFileName} could not be read: {ex.Message}",
                ex);
        }

        return Parse(bytes);
    }

    /// <summary>Parses and validates manifest JSON. The version is not checked here.</summary>
    /// <exception cref="TransferManifestException">The JSON is malformed or the manifest fails a check.</exception>
    public static TransferManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        TransferManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<TransferManifest>(utf8Json, _json);
        }
        catch (JsonException ex)
        {
            throw new TransferManifestException($"{TransferLayout.ManifestFileName} is damaged: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new TransferManifestException($"{TransferLayout.ManifestFileName} is empty.");
        }

        string? problem = manifest.Validate();
        return problem is null
            ? manifest
            : throw new TransferManifestException($"{TransferLayout.ManifestFileName} is not valid: {problem}");
    }

    /// <summary>Null when every structural check passes, else the first problem.</summary>
    private string? Validate()
    {
        if (!string.Equals(FormatId, FormatIdValue, StringComparison.Ordinal))
        {
            return $"formatId is '{FormatId}', not '{FormatIdValue}'";
        }

        if (string.IsNullOrWhiteSpace(AppVersion))
        {
            return "appVersion is empty";
        }

        HashSet<string> shas = new(StringComparer.Ordinal);
        List<string> paths = [];
        foreach (TransferDemo demo in Demos)
        {
            if (!TransferLayout.IsSha256(demo.Sha256))
            {
                return $"'{demo.Sha256}' is not a lowercase-hex SHA-256";
            }

            if (!shas.Add(demo.Sha256))
            {
                return $"demo {demo.Sha256} is listed twice";
            }

            if (demo.SizeBytes < 0)
            {
                return $"demo {demo.Sha256} has a negative size";
            }

            if (demo.Fingerprint is { } fp && (fp.Size != demo.SizeBytes || fp.WindowBytes <= 0
                                                || !TransferLayout.IsSha256(fp.Head)
                                                || !TransferLayout.IsSha256(fp.Tail)))
            {
                return $"demo {demo.Sha256} has a malformed fingerprint";
            }

            // A demo's files must sit in its own folder, so one entry can never overwrite another's.
            string own = TransferLayout.DemoFolder(demo.Sha256) + "/";
            foreach (TransferFile file in demo.Files)
            {
                string? problem = CheckFile(file);
                if (problem is not null)
                {
                    return problem;
                }

                if (!file.Path.StartsWith(own, StringComparison.Ordinal))
                {
                    return $"'{file.Path}' is outside demo {demo.Sha256}'s folder";
                }

                paths.Add(file.Path);
            }

            if (demo.Dem is { } dem)
            {
                if (!string.Equals(dem.Path, TransferLayout.DemFile(demo.Sha256), StringComparison.Ordinal))
                {
                    return $"demo {demo.Sha256}'s .dem is at '{dem.Path}', not '{TransferLayout.DemFile(demo.Sha256)}'";
                }

                if (dem.Size != demo.SizeBytes)
                {
                    return $"demo {demo.Sha256}'s .dem size does not match the demo";
                }

                paths.Add(dem.Path);
            }
        }

        foreach (TransferFile file in Shared)
        {
            string? problem = CheckFile(file);
            if (problem is not null)
            {
                return problem;
            }

            if (!file.Path.StartsWith(TransferLayout.SharedDirectory + "/", StringComparison.Ordinal))
            {
                return $"'{file.Path}' is outside the shared folder";
            }

            paths.Add(file.Path);
        }

        string? duplicate = PathSafety.FindCaseDuplicate(paths);
        return duplicate is null ? null : $"'{duplicate}' is listed twice, or twice differing only by case";
    }

    private static string? CheckFile(TransferFile file)
    {
        // Paths are stored with '/' only; a backslash or any unsafe segment is refused, not normalised.
        if (PathSafety.SafeRelativePath(file.Path) is null || file.Path.Contains('\\', StringComparison.Ordinal)
                                                          || file.Path.EndsWith('/'))
        {
            return $"'{file.Path}' is not a safe relative path";
        }

        if (file.Size < 0)
        {
            return $"'{file.Path}' has a negative size";
        }

        return TransferLayout.IsSha256(file.Sha256) ? null : $"'{file.Path}' has a malformed SHA-256";
    }
}

/// <summary>The options an export ran with.</summary>
/// <param name="IncludeDemFiles">The bundle carries <c>.dem</c> copies under <see cref="TransferLayout.DemDirectory" />.</param>
/// <param name="IncludeUserData">The bundle carries annotations, breakpoints and review-queue entries.</param>
/// <param name="AllDemos">The whole library was exported, not a chosen set.</param>
public sealed record TransferManifestOptions(bool IncludeDemFiles, bool IncludeUserData, bool AllDemos);

/// <summary>An extension whose data the bundle carries.</summary>
public sealed record TransferExtensionVersion(string Id, string? Version = null);

/// <summary>One exported demo.</summary>
/// <param name="Sha256">Its content hash, lowercase hex.</param>
/// <param name="OriginalFileName">The file name on the source machine, for display and for naming the copied <c>.dem</c>.</param>
/// <param name="SizeBytes">The <c>.dem</c>'s length.</param>
/// <param name="Files">Every data file of the demo in the bundle, under <c>data/&lt;sha&gt;/</c>.</param>
/// <param name="Fingerprint">The cheap identity import reattaches the data with, or null when the source had none.</param>
/// <param name="Map">The map, for display.</param>
/// <param name="MatchDate">When the match was played, UTC, for display.</param>
/// <param name="Dem">The <c>.dem</c> copy, or null when it was not exported.</param>
public sealed record TransferDemo(
    string Sha256,
    string OriginalFileName,
    long SizeBytes,
    IReadOnlyList<TransferFile> Files,
    TransferFingerprint? Fingerprint = null,
    string? Map = null,
    DateTime? MatchDate = null,
    TransferDemFile? Dem = null);

/// <summary>
///     A demo's <see cref="DemoCache.DemoContentFingerprint" /> as the manifest stores it.
/// </summary>
public sealed record TransferFingerprint(long Size, int WindowBytes, string Head, string Tail)
{
    /// <summary>From the cache's fingerprint.</summary>
    public static TransferFingerprint From(DemoCache.DemoContentFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return new TransferFingerprint(fingerprint.Size, fingerprint.WindowBytes, fingerprint.Head, fingerprint.Tail);
    }

    /// <summary>Back to the cache's fingerprint.</summary>
    public DemoCache.DemoContentFingerprint ToFingerprint() => new(Size, WindowBytes, Head, Tail);
}

/// <summary>A file in the bundle.</summary>
/// <param name="Path">Bundle-relative, <c>/</c>-separated (<see cref="TransferLayout" />).</param>
/// <param name="Size">Its length in bytes.</param>
/// <param name="Sha256">Lowercase-hex SHA-256 of its bytes, verified while it is copied in.</param>
public sealed record TransferFile(string Path, long Size, string Sha256);

/// <summary>A <c>.dem</c> copy in the bundle. Its hash is the demo's <see cref="TransferDemo.Sha256" />.</summary>
/// <param name="Path"><see cref="TransferLayout.DemFile" />.</param>
/// <param name="Size">Its length in bytes.</param>
public sealed record TransferDemFile(string Path, long Size);

/// <summary>A transfer manifest that is missing, unreadable, malformed or unsafe. The message is in user terms.</summary>
public sealed class TransferManifestException : Exception
{
    public TransferManifestException()
    {
    }

    public TransferManifestException(string message) : base(message)
    {
    }

    public TransferManifestException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
