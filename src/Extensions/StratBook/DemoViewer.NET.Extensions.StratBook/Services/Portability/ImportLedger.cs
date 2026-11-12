#region

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Portability;

/// <summary>
///     What one Library Transfer import shares across the Strat Book's stores: the user's conflict policy, the
///     report the import shows, and the counts the extension hands back. Each store's import reads the bundle's
///     copy, never writes it, and settles every item against this.
/// </summary>
public sealed class ImportLedger
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> _strict = new();
    private readonly Action<string> _report;

    /// <param name="policy">What to do where both machines hold the same item in different versions.</param>
    /// <param name="report">Adds a line to the import's report.</param>
    public ImportLedger(ImportConflictPolicy policy, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Policy = policy;
        _report = report;
    }

    public ImportConflictPolicy Policy { get; }

    public int Added { get; private set; }

    public int Merged { get; private set; }

    public int Skipped { get; private set; }

    public int Conflicts { get; private set; }

    /// <summary>True once a step found the import cannot go on; the steps after it write nothing.</summary>
    public bool Stopped { get; private set; }

    /// <summary>The counts so far, in the SDK's shape.</summary>
    public ExtensionImportResult Result => new(Added, Merged, Skipped, Conflicts);

    public void CountAdded() => Added++;

    public void CountMerged() => Merged++;

    public void CountSkipped() => Skipped++;

    public void CountConflict() => Conflicts++;

    /// <summary>Adds a line to the import's report.</summary>
    /// <param name="line">The line.</param>
    public void Report(string line) => _report(line);

    /// <summary>Stops the import after the current step, saying why.</summary>
    /// <param name="line">Why, for the report.</param>
    public void Stop(string line)
    {
        Stopped = true;
        Report(line);
    }

    /// <summary>
    ///     A JSON file of the bundle, or null when there is none or it does not read. A file that does not read is
    ///     reported rather than thrown: one bad file must not stop the rest of the import.
    /// </summary>
    /// <param name="staged">The bundle's copy of the store, or null when the bundle has none.</param>
    /// <param name="relativePath">The file under it.</param>
    /// <param name="options">The store's own serializer options.</param>
    public T? ReadJson<T>(IExtensionFolder? staged, string relativePath, JsonSerializerOptions options)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        if (staged is null)
        {
            return null;
        }

        try
        {
            return StoredFile.In(staged, relativePath)?.Read() is { } bytes
                ? JsonSerializer.Deserialize<T>(bytes, Strict(options))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or NotSupportedException)
        {
            Report($"{relativePath} in the bundle could not be read and was left out: {e.Message}");
            return null;
        }
    }

    /// <summary>
    ///     A store's serializer options for reading a bundle: a null where the type declares none
    ///     (<c>"teams": null</c>) does not read, since a bundle may hold what no build of the store ever wrote.
    /// </summary>
    /// <param name="options">The store's own options.</param>
    public static JsonSerializerOptions Strict(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _strict.GetValue(options,
            static o => new JsonSerializerOptions(o) { RespectNullableAnnotations = true });
    }

    /// <summary>As <see cref="Strict(JsonSerializerOptions)" />, for a source-generated type.</summary>
    /// <param name="type">The store's own type info.</param>
    public static JsonTypeInfo<T> Strict<T>(JsonTypeInfo<T> type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return (JsonTypeInfo<T>)Strict(type.Options).GetTypeInfo(typeof(T));
    }

    /// <summary>
    ///     The id a "keep both" copy gets. Derived from the item and the imported version, so a second import of the
    ///     same bundle finds the copy the first one made instead of making another.
    /// </summary>
    /// <param name="id">The item's id on both machines.</param>
    /// <param name="version">What identifies the imported version, such as its serialized form.</param>
    public static Guid BesideId(Guid id, string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("dv-import-beside|" + id.ToString("N") + "|" + version));
        return new Guid(hash.AsSpan(0, 16));
    }
}
