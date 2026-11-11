namespace DemoViewer.NET.Services.Transfer;

/// <summary>Where a running transfer is, for the wizard's bar and the queue row.</summary>
/// <param name="DemosDone">Demos finished, skipped or failed so far.</param>
/// <param name="DemosTotal">Demos the transfer covers.</param>
/// <param name="BytesDone">Bytes copied so far.</param>
/// <param name="BytesTotal">Bytes the transfer copies in all.</param>
/// <param name="Current">The demo or step being worked on, for display, or null.</param>
public sealed record TransferProgress(int DemosDone, int DemosTotal, long BytesDone, long BytesTotal, string? Current)
{
    /// <summary>0 to 1, by bytes when there are any, else by demos.</summary>
    public double Fraction =>
        BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : DemosTotal > 0 ? Math.Clamp((double)DemosDone / DemosTotal, 0, 1)
        : 0;
}

/// <summary>What happened to one demo, for the summary.</summary>
public enum TransferOutcome
{
    /// <summary>Exported, or imported as new.</summary>
    Transferred,

    /// <summary>Imported into a demo this machine already had, keeping the current side per tier.</summary>
    Merged,

    /// <summary>Left out on purpose: not hashed, already present and current, or kept by the conflict policy.</summary>
    Skipped,

    /// <summary>Transferred, but computed under different rules or extensions, so it will be recomputed.</summary>
    Stale,

    /// <summary>An error stopped it.</summary>
    Failed
}

/// <summary>One line of a <see cref="TransferSummary" />.</summary>
/// <param name="Sha256">The demo's content hash, or null for a demo not hashed yet or a shared file.</param>
/// <param name="DisplayName">The file name the wizard shows.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Reason">Why, in user terms, or null when it went as planned.</param>
public sealed record TransferSummaryItem(string? Sha256, string DisplayName, TransferOutcome Outcome, string? Reason);

/// <summary>The result of an export or import, for the wizard's done step.</summary>
/// <param name="Items">One line per demo (and per shared file that needed a word).</param>
/// <param name="Warnings">Findings raised while running.</param>
/// <param name="BundleFolder">The bundle written (export) or read (import).</param>
/// <param name="Cancelled">The user cancelled part-way; <paramref name="Items" /> covers what finished.</param>
public sealed record TransferSummary(
    IReadOnlyList<TransferSummaryItem> Items,
    IReadOnlyList<TransferWarning> Warnings,
    string BundleFolder,
    bool Cancelled = false)
{
    /// <summary>Exported, or imported as new.</summary>
    public IReadOnlyList<TransferSummaryItem> Transferred => Of(TransferOutcome.Transferred);

    /// <summary>Merged into existing data.</summary>
    public IReadOnlyList<TransferSummaryItem> Merged => Of(TransferOutcome.Merged);

    /// <summary>Left out on purpose.</summary>
    public IReadOnlyList<TransferSummaryItem> Skipped => Of(TransferOutcome.Skipped);

    /// <summary>Transferred but stale on arrival.</summary>
    public IReadOnlyList<TransferSummaryItem> Stale => Of(TransferOutcome.Stale);

    /// <summary>Failed.</summary>
    public IReadOnlyList<TransferSummaryItem> Failed => Of(TransferOutcome.Failed);

    private List<TransferSummaryItem> Of(TransferOutcome outcome) =>
        Items.Where(i => i.Outcome == outcome).ToList();
}
