#region

using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.Transfer;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>Stand-ins for the exporter and importer, plus the bundle and library rows the wizard tests share.</summary>
internal static class TransferWizardFakes
{
    public static readonly Action<Action> Inline = a => a();

    public static string Sha(char c) => new(c, 64);

    public static DemoEntry Entry(string file, string map, string players = "s1mple,b1t", long size = 100_000_000) =>
        new()
        {
            FilePath = "/demos/" + file,
            FileName = file,
            Directory = "/demos",
            FileSizeBytes = size,
            Modified = new DateTime(2026, 7, 1, 12, 0, 0),
            MapName = map,
            ServerName = "srv",
            Players = [.. players.Split(',')],
            DurationSeconds = 3000,
            State = DemoIndexState.Indexed
        };

    public static TransferDemo Demo(char sha, string name, string map, bool withDem) => new(
        Sha(sha), name, 1000, [], Map: map,
        Dem: withDem ? new TransferDemFile(TransferLayout.DemFile(Sha(sha)), 1000) : null);

    public static TransferManifest Manifest(params TransferDemo[] demos) => new()
    {
        FormatId = TransferManifest.FormatIdValue,
        AppVersion = "0.10.1",
        CreatedUtc = new DateTime(2026, 11, 11, 15, 30, 0, DateTimeKind.Utc),
        SourceOs = "Windows",
        Options = new TransferManifestOptions(demos.Any(d => d.Dem is not null), true, true, false),
        RulesetIdentities = ["core@1"],
        Extensions = [],
        Demos = demos,
        Shared = [],
        ExtensionData = []
    };

    public static ImportPreview Preview(string folder, IReadOnlyList<ImportPreviewEntry> entries,
        params TransferWarning[] warnings) =>
        new(folder, Manifest([.. entries.Select(e => e.Demo)]), entries, 5000, 3000, warnings);

    public static ImportPreviewEntry Entry(TransferDemo demo, ImportEntryStatus status = ImportEntryStatus.New,
        bool stale = false, bool demHere = false) =>
        new(demo, status, demo.Dem is not null, demHere, stale);

    public static TransferSummary Summary(string folder, params TransferSummaryItem[] items) =>
        new(items, [], folder);
}

/// <summary>Plans what it is told to and records every request; a run waits on <see cref="Gate" /> when set.</summary>
internal sealed class FakeExporter : ITransferExporter
{
    public List<ExportRequest> Requests { get; } = [];

    public List<ExportPlan> Runs { get; } = [];

    public Func<ExportRequest, ExportPlan>? PlanFor { get; set; }

    public Exception? PlanThrows { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public TransferProgress? ReportBeforeGate { get; set; }

    public Func<ExportPlan, TransferSummary>? Result { get; set; }

    public static ExportPlan PlanOf(ExportRequest request, IReadOnlyList<ExportPlanEntry> entries,
        params TransferWarning[] warnings) =>
        new(request, Path.Combine(request.DestinationFolder, "DemoViewer Transfer 2026-11-11 1530"), entries, 10,
            warnings);

    public Task<ExportPlan> PlanAsync(ExportRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (PlanThrows is not null)
        {
            throw PlanThrows;
        }

        return Task.FromResult(PlanFor?.Invoke(request) ?? PlanOf(request,
        [
            new ExportPlanEntry(TransferWizardFakes.Sha('a'), "/demos/a.dem", "a.dem", "de_dust2",
                ExportEntryStatus.Included, 100, request.IncludeDemFiles ? 1000 : 0)
        ]));
    }

    public async Task<TransferSummary> RunAsync(ExportPlan plan, IProgress<TransferProgress>? progress,
        CancellationToken ct)
    {
        Runs.Add(plan);
        if (ReportBeforeGate is not null)
        {
            progress?.Report(ReportBeforeGate);
        }

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct);
        }

        ct.ThrowIfCancellationRequested();
        return Result?.Invoke(plan) ?? TransferWizardFakes.Summary(plan.BundleFolder,
            [.. plan.Entries.Where(e => e.ExportsData).Select(e =>
                new TransferSummaryItem(e.Sha256, e.DisplayName, TransferOutcome.Transferred, null))]);
    }
}

/// <summary>Previews what it is told to, or refuses with a message; a run records its request.</summary>
internal sealed class FakeImporter : ITransferImporter
{
    public Func<string, ImportPreview>? PreviewFor { get; set; }

    public string? Refusal { get; set; }

    public List<ImportRequest> Runs { get; } = [];

    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, a preview waits on it before answering.</summary>
    public TaskCompletionSource? PreviewGate { get; set; }

    public async Task<ImportPreview> PreviewAsync(string bundleFolder, CancellationToken ct)
    {
        if (PreviewGate is not null)
        {
            await PreviewGate.Task.WaitAsync(ct);
        }

        if (Refusal is not null)
        {
            throw new TransferManifestException(Refusal);
        }

        return PreviewFor?.Invoke(bundleFolder) ?? TransferWizardFakes.Preview(bundleFolder,
            [TransferWizardFakes.Entry(TransferWizardFakes.Demo('a', "a.dem", "de_dust2", false))]);
    }

    public async Task<TransferSummary> RunAsync(ImportRequest request, ImportPreview preview,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        Runs.Add(request);
        progress?.Report(new TransferProgress(0, preview.Entries.Count, 0, 0, preview.Entries[0].Demo.OriginalFileName));
        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct);
        }

        ct.ThrowIfCancellationRequested();
        return TransferWizardFakes.Summary(request.BundleFolder,
            [.. preview.Entries.Select(e =>
                new TransferSummaryItem(e.Sha256, e.Demo.OriginalFileName, TransferOutcome.Transferred, null))]);
    }
}
