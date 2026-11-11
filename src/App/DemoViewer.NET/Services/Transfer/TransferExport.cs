namespace DemoViewer.NET.Services.Transfer;

/// <summary>What the user asked to export.</summary>
/// <param name="AllDemos">Every library demo; <see cref="DemoKeys" /> is ignored.</param>
/// <param name="DemoKeys">
///     With <see cref="AllDemos" /> false, the chosen demos: a content hash (lowercase hex) or a demo path, as the
///     library lists them. A path resolves through the cache row it names.
/// </param>
/// <param name="IncludeDemFiles">Copy the <c>.dem</c> files too.</param>
/// <param name="IncludeUserData">Carry annotations, breakpoints and review-queue entries.</param>
/// <param name="DestinationFolder">The folder the user picked; the bundle folder is created inside it.</param>
public sealed record ExportRequest(
    bool AllDemos,
    IReadOnlyList<string> DemoKeys,
    bool IncludeDemFiles,
    bool IncludeUserData,
    string DestinationFolder)
{
    /// <summary>A request for the whole library.</summary>
    public static ExportRequest All(string destinationFolder, bool includeDemFiles, bool includeUserData = true) =>
        new(true, [], includeDemFiles, includeUserData, destinationFolder);

    /// <summary>A request for <paramref name="demoKeys" />.</summary>
    public static ExportRequest Chosen(IReadOnlyList<string> demoKeys, string destinationFolder,
        bool includeDemFiles, bool includeUserData = true) =>
        new(false, demoKeys, includeDemFiles, includeUserData, destinationFolder);
}

/// <summary>Whether and how a demo goes into the bundle.</summary>
public enum ExportEntryStatus
{
    /// <summary>Hashed and analysed: its data (and its <c>.dem</c> when asked) is exported.</summary>
    Included,

    /// <summary>
    ///     Not hashed yet: no data is exported, since hashing it would write to the source cache. With
    ///     <c>.dem</c> files on, the file itself is still copied as a plain demo.
    /// </summary>
    NotHashed,

    /// <summary>
    ///     The processing queue is working on it. It is still exported, best effort: taken as it stands when the
    ///     job reaches it, and reported as stale or skipped if that turns out short.
    /// </summary>
    InFlight,

    /// <summary>The row or its files are gone; nothing is exported.</summary>
    Missing
}

/// <summary>One demo in an <see cref="ExportPlan" />.</summary>
/// <param name="Sha256">Its content hash, or null when <see cref="Status" /> is <see cref="ExportEntryStatus.NotHashed" /> or unknown.</param>
/// <param name="DemoPath">Its path on this machine, or null when no location is known.</param>
/// <param name="DisplayName">The file name the wizard shows.</param>
/// <param name="Map">The map, for display.</param>
/// <param name="Status">Whether and how it is exported.</param>
/// <param name="DataBytes">The bytes of its data files (record, siblings, extension data, annotations).</param>
/// <param name="DemBytes">The <c>.dem</c>'s bytes when it will be copied, else 0.</param>
public sealed record ExportPlanEntry(
    string? Sha256,
    string? DemoPath,
    string DisplayName,
    string? Map,
    ExportEntryStatus Status,
    long DataBytes,
    long DemBytes)
{
    /// <summary>Everything this entry writes into the bundle.</summary>
    public long TotalBytes => DataBytes + DemBytes;

    /// <summary>Its data goes into the bundle (<see cref="ExportEntryStatus.Included" /> or <see cref="ExportEntryStatus.InFlight" />).</summary>
    public bool ExportsData => Status is ExportEntryStatus.Included or ExportEntryStatus.InFlight;
}

/// <summary>
///     A planned export: what will be written and where, taken from a snapshot of the cache. Planning writes
///     nothing anywhere; <see cref="ITransferExporter.RunAsync" /> executes it.
/// </summary>
/// <param name="Request">What the user asked for.</param>
/// <param name="BundleFolder">The bundle folder to create under <see cref="ExportRequest.DestinationFolder" /> (<see cref="TransferLayout.NewBundleFolder" />).</param>
/// <param name="Entries">One per selected demo, in display order.</param>
/// <param name="SharedBytes">The bytes of the shared files.</param>
/// <param name="Warnings">Findings for the review step, including the target checks.</param>
public sealed record ExportPlan(
    ExportRequest Request,
    string BundleFolder,
    IReadOnlyList<ExportPlanEntry> Entries,
    long SharedBytes,
    IReadOnlyList<TransferWarning> Warnings)
{
    /// <summary>Everything the export writes.</summary>
    public long TotalBytes => Entries.Sum(e => e.TotalBytes) + SharedBytes;

    /// <summary>The largest single file, for the FAT32 check: the biggest <c>.dem</c>, else the biggest data total.</summary>
    public long LargestFileBytes => Entries.Count == 0 ? 0 : Entries.Max(e => Math.Max(e.DemBytes, e.DataBytes));

    /// <summary>No warning blocks the export.</summary>
    public bool CanRun => Warnings.All(w => !w.Blocking);
}

/// <summary>Exports library data to a transfer bundle. Reads the source and writes only inside the bundle folder.</summary>
public interface ITransferExporter
{
    /// <summary>Plans <paramref name="request" /> from a snapshot of the cache and checks the target. Writes nothing but the target probe.</summary>
    Task<ExportPlan> PlanAsync(ExportRequest request, CancellationToken ct);

    /// <summary>
    ///     Writes <paramref name="plan" />'s bundle. The manifest goes last, so a cancelled or failed run leaves a
    ///     folder with no <c>transfer.json</c>, which import refuses. A file the bundle folder already holds with
    ///     the same size and hash is not copied again.
    /// </summary>
    Task<TransferSummary> RunAsync(ExportPlan plan, IProgress<TransferProgress>? progress, CancellationToken ct);
}
